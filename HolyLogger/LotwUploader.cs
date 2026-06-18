using HolyParser;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace HolyLogger
{
    public class LotwUploadResult
    {
        public bool Success { get; set; }
        public int Uploaded { get; set; }
        public string ErrorMessage { get; set; }
    }

    public static class LotwUploader
    {
        private const string UploadUrl = "https://lotw.arrl.org/lotwuser/upload";

        // Signs the temporary ADIF with TQSL and uploads the resulting .tq8 to LoTW.
        // On success returns true; on failure throws with a descriptive message.
        public static async Task<bool> SignAndUploadAsync(
            string tqslPath,
            string stationLocation,
            string password,
            string inputAdiPath,
            string outputTq8Path)
        {
            if (!File.Exists(tqslPath))
                throw new FileNotFoundException($"TQSL executable not found: {tqslPath}");

            // Build argument list; omit -p entirely when no password is configured.
            // -a all  : upload regardless of duplicate status (TQSL 2.x requires an explicit mode value)
            // -d      : suppress GUI dialog boxes (headless / batch mode)
            // -x      : exit immediately after processing
            var args = new StringBuilder();
            args.Append("-x -a all -d ");
            if (!string.IsNullOrWhiteSpace(password))
                args.Append($"-p \"{password}\" ");
            args.Append($"-l \"{stationLocation}\" -o \"{outputTq8Path}\" \"{inputAdiPath}\"");

            var psi = new ProcessStartInfo
            {
                FileName = tqslPath,
                Arguments = args.ToString(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = new Process { StartInfo = psi })
            {
                proc.Start();
                string stderr = await proc.StandardError.ReadToEndAsync();
                await Task.Run(() => proc.WaitForExit());

                if (proc.ExitCode != 0)
                {
                    string msg = string.IsNullOrWhiteSpace(stderr) ? $"Exit code {proc.ExitCode}" : stderr.Trim();
                    if (msg.Contains("Certificate Expired"))
                        throw new Exception("Your LoTW certificate has expired. Please renew it in TQSL.");
                    if (msg.Contains("Unknown Station Location"))
                        throw new Exception($"Unknown station location \"{stationLocation}\". Check the name in TQSL.");
                    if (msg.Contains("Invalid Password") || msg.Contains("bad password"))
                        throw new Exception("Invalid TQSL certificate password.");
                    throw new Exception($"TQSL signing failed: {msg}");
                }
            }

            if (!File.Exists(outputTq8Path))
                throw new Exception("TQSL completed but the signed .tq8 file was not created.");

            // Upload the signed binary to the ARRL gateway.
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            using (var form = new MultipartFormDataContent())
            using (var fs = File.OpenRead(outputTq8Path))
            using (var sc = new StreamContent(fs))
            {
                sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                form.Add(sc, "tqsfld", Path.GetFileName(outputTq8Path));

                HttpResponseMessage response = await http.PostAsync(UploadUrl, form);
                string body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    throw new Exception($"ARRL server returned HTTP {(int)response.StatusCode}.");

                if (body.Contains("Certificate Expired"))
                    throw new Exception("ARRL rejected the upload: your LoTW certificate has expired.");
                if (body.Contains("Unknown Station Location"))
                    throw new Exception($"ARRL rejected the upload: unknown station location \"{stationLocation}\".");
                if (body.Contains("Invalid Password"))
                    throw new Exception("ARRL rejected the upload: invalid certificate password.");

                if (!body.Contains("File queued") && !body.Contains("Upload ID"))
                    throw new Exception($"Unexpected response from ARRL:\n{body.Substring(0, Math.Min(body.Length, 300))}");
            }

            return true;
        }

        // Writes a minimal ADIF file for a list of QSOs and returns the path.
        // The caller is responsible for deleting the file.
        public static string WriteAdif(IEnumerable<QSO> qsos, string path)
        {
            using (var sw = new StreamWriter(path, false, Encoding.ASCII))
            {
                sw.WriteLine("HolyLogger LoTW export");
                sw.WriteLine("<ADIF_VER:5>3.1.0");
                sw.WriteLine("<EOH>");
                foreach (var q in qsos)
                {
                    WriteField(sw, "CALL", q.DXCall);
                    WriteField(sw, "QSO_DATE", q.Date?.Replace("-", ""));
                    WriteField(sw, "TIME_ON", q.Time?.Replace(":", ""));
                    WriteField(sw, "BAND", q.Band);
                    WriteField(sw, "MODE", q.Mode);
                    if (!string.IsNullOrWhiteSpace(q.SUBMode))
                        WriteField(sw, "SUBMODE", q.SUBMode);
                    if (!string.IsNullOrWhiteSpace(q.MyCall))
                        WriteField(sw, "STATION_CALLSIGN", q.MyCall);
                    if (!string.IsNullOrWhiteSpace(q.MyLocator))
                        WriteField(sw, "MY_GRIDSQUARE", q.MyLocator);
                    if (!string.IsNullOrWhiteSpace(q.RST_SENT))
                        WriteField(sw, "RST_SENT", q.RST_SENT);
                    if (!string.IsNullOrWhiteSpace(q.RST_RCVD))
                        WriteField(sw, "RST_RCVD", q.RST_RCVD);
                    sw.WriteLine("<EOR>");
                }
            }
            return path;
        }

        private static void WriteField(StreamWriter sw, string tag, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            sw.Write($"<{tag}:{value.Length}>{value} ");
        }
    }
}
