using HolyParser;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace HolyLogger
{
    public class LotwUploadResult
    {
        public bool Success { get; set; }
        public int Uploaded { get; set; }
        public int SkippedNoBand { get; set; }
        public int SkippedPreviouslySigned { get; set; }
        public int ExitCode { get; set; }
        // True when TQSL processed the file but sent nothing to ARRL (all duplicates, or no QSOs).
        public bool NothingUploaded { get; set; }
        // Full TQSL stdout+stderr, for writing to a diagnostic log file.
        public string Detail { get; set; }
        public string ErrorMessage { get; set; }
    }

    public static class LotwUploader
    {
        // Signs the temporary ADIF with TQSL and uploads it to LoTW in one step (TQSL -u).
        // TQSL authenticates the upload with the callsign certificate — the LoTW website
        // login is NOT used here. Returns a result with the count of previously-signed QSOs
        // that LoTW skipped. Throws only on real errors (bad cert/password/location, no
        // connection, rejected upload).
        public static async Task<LotwUploadResult> SignAndUploadAsync(
            string tqslPath,
            string stationLocation,
            string password,
            string inputAdiPath,
            IProgress<string> progress = null,
            int totalQsos = 0)
        {
            if (!File.Exists(tqslPath))
                throw new FileNotFoundException($"TQSL executable not found: {tqslPath}");

            // -u      : sign AND upload directly to LoTW (no separate output file)
            // -a all  : process all QSOs, including ones previously signed (LoTW dedupes server-side)
            // -d      : batch / headless — suppress GUI dialog boxes
            // -x      : exit immediately when done
            var args = new StringBuilder();
            args.Append("-x -a all -d -u ");
            if (!string.IsNullOrWhiteSpace(password))
                args.Append($"-p \"{password}\" ");
            args.Append($"-l \"{stationLocation}\" \"{inputAdiPath}\"");

            var psi = new ProcessStartInfo
            {
                FileName = tqslPath,
                Arguments = args.ToString(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            string tqslOutput;
            int exitCode;
            using (var proc = new Process { StartInfo = psi })
            using (var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(10)))
            {
                proc.Start();

                // If TQSL hangs (e.g. hidden dialog, network stall), kill it after 10 minutes so
                // the app is never left frozen waiting forever.
                timeout.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch { } });

                // Drain stdout silently to prevent the process from blocking on a full stdout buffer.
                var drainStdout = proc.StandardOutput.ReadToEndAsync();

                // Read stderr line-by-line so we can count QSO blocks and report progress.
                // TQSL outputs one block per QSO it reports on (CALL: / QSO_DATE: / …).
                var sb = new StringBuilder();
                int qsosSeen = 0;
                string line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                {
                    sb.AppendLine(line);
                    if (line.StartsWith("CALL:", StringComparison.OrdinalIgnoreCase))
                    {
                        qsosSeen++;
                        if (qsosSeen % 50 == 0)
                        {
                            // Clamp so a duplicate's extra CALL: line can't push the count past the total.
                            int shown = totalQsos > 0 ? Math.Min(qsosSeen, totalQsos) : qsosSeen;
                            string label = totalQsos > 0
                                ? $"Signing QSO {shown:N0} / {totalQsos:N0}"
                                : $"Signing QSO {shown:N0}…";
                            progress?.Report(label);
                        }
                    }
                }
                string stdout = await drainStdout;
                await Task.Run(() => proc.WaitForExit());
                if (timeout.IsCancellationRequested)
                    throw new TimeoutException("TQSL did not complete within 10 minutes and was stopped.");
                exitCode = proc.ExitCode;
                tqslOutput = sb.ToString() + stdout;
            }

            // Surface the most common configuration problems with a clear message.
            if (tqslOutput.IndexOf("Certificate Expired", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception("Your LoTW certificate has expired. Please renew it in TQSL.");
            if (tqslOutput.IndexOf("Unknown Station Location", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tqslOutput.IndexOf("station location", StringComparison.OrdinalIgnoreCase) >= 0 && exitCode != 0 && exitCode != 9)
                throw new Exception($"Unknown station location \"{stationLocation}\". Check the name in TQSL.");
            if (tqslOutput.IndexOf("Invalid Password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tqslOutput.IndexOf("bad password", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception("Invalid TQSL certificate password.");

            int prevSigned = CountOccurrences(tqslOutput, "Previously Signed QSO") +
                             CountOccurrences(tqslOutput, "Duplicate");

            // TQSL exit codes (tqsl.cpp):
            //   0 = QSOs were signed and uploaded
            //   8 = no QSOs in the file matched / nothing to process
            //   9 = all QSOs were duplicates and were SUPPRESSED — nothing sent to LoTW
            // 8 and 9 are not errors, but they mean nothing actually reached ARRL.
            switch (exitCode)
            {
                case 0:
                    return new LotwUploadResult
                    {
                        Success = true,
                        SkippedPreviouslySigned = prevSigned,
                        ExitCode = exitCode,
                        NothingUploaded = false,
                        Detail = tqslOutput
                    };
                case 8:
                case 9:
                    return new LotwUploadResult
                    {
                        Success = true,
                        SkippedPreviouslySigned = prevSigned,
                        ExitCode = exitCode,
                        NothingUploaded = true,
                        Detail = tqslOutput
                    };
                case 1:
                    throw new Exception("The upload was cancelled.");
                case 2:
                    throw new Exception("LoTW rejected the upload. " + Snippet(tqslOutput));
                case 3:
                    throw new Exception("Unexpected response from the LoTW server. " + Snippet(tqslOutput));
                case 11:
                    throw new Exception("Could not connect to the LoTW server. Check your internet connection and try again.");
                default:
                    throw new Exception($"TQSL upload failed (exit code {exitCode}). " + Snippet(tqslOutput));
            }
        }

        private static string Snippet(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            text = text.Trim();
            return text.Length > 400 ? text.Substring(0, 400) + "…" : text;
        }

        private static int CountOccurrences(string text, string phrase)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(phrase, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                idx += phrase.Length;
            }
            return count;
        }

        // Writes a minimal ADIF file for a list of QSOs.
        // QSOs with no Band AND no Freq are skipped (TQSL rejects them as "Invalid contact").
        // Returns the number of skipped QSOs; the caller is responsible for deleting the file.
        public static int WriteAdif(IList<QSO> qsos, string path, IProgress<string> progress = null)
        {
            int total = qsos.Count;
            int written = 0;
            int skipped = 0;
            using (var sw = new StreamWriter(path, false, Encoding.ASCII))
            {
                sw.WriteLine("HolyLogger LoTW export");
                sw.WriteLine("<ADIF_VER:5>3.1.0");
                sw.WriteLine("<EOH>");
                foreach (var q in qsos)
                {
                    if (string.IsNullOrWhiteSpace(q.Band) && string.IsNullOrWhiteSpace(q.Freq))
                    {
                        skipped++;
                        continue;
                    }
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
                    written++;
                    if (written % 500 == 0)
                        progress?.Report($"Preparing QSO {written:N0} / {total:N0}");
                }
            }
            progress?.Report($"Preparing QSO {written:N0} / {total:N0}");
            return skipped;
        }

        private static void WriteField(StreamWriter sw, string tag, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            sw.Write($"<{tag}:{value.Length}>{value} ");
        }
    }
}
