using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HolyLogger
{
    // THE THREE NUMBERS EVERY OPERATOR LOOKS AT BEFORE CALLING CQ: the A index, the K index and the
    // solar flux. Read straight from NOAA's Space Weather Prediction Center, which needs no account,
    // no key and no registration - the same three feeds The Holy Cluster reads, so its bars and ours
    // can never disagree about what the sun is doing.
    //
    //   K index  /products/noaa-planetary-k-index.json  - every 3 hours, the planetary Kp
    //   A index  /text/daily-geomagnetic-indices.txt    - once a day, the planetary A
    //   SFI      /json/f107_cm_flux.json                - the 10.7 cm radio flux
    //
    // NOTHING HERE THROWS AT THE CALLER. The sun is not worth an error dialog: a failed read leaves
    // the last good numbers standing, and the retry a minute later usually fixes it. An operator with
    // no internet still gets his log.
    internal static class SolarDataService
    {
        private const string Host = "https://services.swpc.noaa.gov";
        private const string KIndexPath = "/products/noaa-planetary-k-index.json";
        private const string AIndexPath = "/text/daily-geomagnetic-indices.txt";
        private const string SfiPath = "/json/f107_cm_flux.json";

        // ONCE AN HOUR, which is what The Holy Cluster's own server does, and asking oftener would be
        // taking from NOAA without learning anything: the K index is a three-hour figure, the A index
        // is one a day, and the flux is measured a few times a day.
        public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(1);

        // After a failed read, though, try again in a minute rather than in an hour - a laptop that was
        // asleep or a link that was down should not leave the bars empty for the rest of the session.
        public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

        // Whether the last attempt got anything at all. The caller's timer reads it to decide which of
        // the two intervals to wait.
        public static bool LastReadSucceeded { get; private set; }

        public sealed class Reading
        {
            public double? AIndex;
            public double? KIndex;
            public double? Sfi;

            // When these numbers were read, not when they were measured. Null until a read succeeds.
            public DateTime? ReadAtUtc;

            public bool HasAny { get { return AIndex.HasValue || KIndex.HasValue || Sfi.HasValue; } }
        }

        // The last numbers read. Replaced whole, never edited in place, so a reader on another thread
        // sees one consistent set rather than half of two.
        private static Reading _latest = new Reading();
        public static Reading Latest { get { return _latest; } }

        // Raised on whatever thread did the reading - a listener that touches the screen must marshal.
        public static event Action<Reading> Updated;

        // EVERY await IN HERE IS ConfigureAwait(false). Without it the reply comes back to whichever
        // thread asked - the screen thread, when the cluster opens - and the parsing, three files of
        // it, is done there. None of this work has any business on the thread that draws the window.
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static int _reading;   // 0/1: one read at a time, however many callers ask

        public static async Task RefreshAsync()
        {
            if (Interlocked.Exchange(ref _reading, 1) == 1) return;   // one already running
            try
            {
                var next = new Reading
                {
                    AIndex = _latest.AIndex,
                    KIndex = _latest.KIndex,
                    Sfi = _latest.Sfi,
                    ReadAtUtc = _latest.ReadAtUtc
                };

                bool anyRead = false;

                double? k = await ReadKIndexAsync().ConfigureAwait(false);
                if (k.HasValue) { next.KIndex = k; anyRead = true; }

                double? a = await ReadAIndexAsync().ConfigureAwait(false);
                if (a.HasValue) { next.AIndex = a; anyRead = true; }

                double? sfi = await ReadSfiAsync().ConfigureAwait(false);
                if (sfi.HasValue) { next.Sfi = sfi; anyRead = true; }

                if (anyRead) next.ReadAtUtc = DateTime.UtcNow;
                LastReadSucceeded = anyRead;

                _latest = next;

                var handler = Updated;
                if (handler != null)
                {
                    try { handler(next); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
            }
            catch (Exception ex)
            {
                LastReadSucceeded = false;
                // It will try again in a minute. Worth a line in the log, nothing more.
                Log.Warn("Solar data could not be read: " + ex.Message);
            }
            finally { Interlocked.Exchange(ref _reading, 0); }
        }

        // [{"time_tag":"2026-08-19T15:00:00","Kp":2.00,...}, ...] - newest last, but sorted here rather
        // than trusted, because "the last one in the file" is a promise NOAA never made.
        private static async Task<double?> ReadKIndexAsync()
        {
            try
            {
                string body = await Http.GetStringAsync(Host + KIndexPath).ConfigureAwait(false);
                var rows = JArray.Parse(body);

                DateTime bestAt = DateTime.MinValue;
                double? best = null;

                foreach (var row in rows.OfType<JObject>())
                {
                    DateTime at;
                    double value;
                    if (!TryTime(row["time_tag"], out at)) continue;
                    if (!TryNumber(row["Kp"], out value)) continue;
                    if (value < 0) continue;              // NOAA's "not measured"
                    if (at < bestAt) continue;
                    bestAt = at; best = value;
                }
                return best;
            }
            catch (Exception ex) { Log.Warn("K index: " + ex.Message); return null; }
        }

        // Columns of text, one line a day. The planetary A is the FOURTH block on the line - after the
        // date, the Fredericksburg A and its eight K figures, and the estimated A and its eight:
        //
        //   2026 08 18    20  4 5 3 2 3 2 4 3    31  5 6 5 3 2 2 3 4    25   4.00 5.00 ...
        //   ^date         ^Fredericksburg A      ^estimated A           ^planetary A
        //
        // Blocks are separated by three or more spaces; the figures inside a block are separated by
        // one or two, and can run together when negative (2-1-1), which is why the split cannot simply
        // be on whitespace. -1 means "not measured yet" and is skipped, so the newest REAL value wins.
        private static async Task<double?> ReadAIndexAsync()
        {
            try
            {
                string body = await Http.GetStringAsync(Host + AIndexPath).ConfigureAwait(false);
                var dataLine = new Regex(@"^\d{4}\s+\d{2}\s+\d{2}\s+");

                DateTime bestAt = DateTime.MinValue;
                double? best = null;

                foreach (string raw in body.Split('\n'))
                {
                    string line = raw.Trim();
                    if (!dataLine.IsMatch(line)) continue;

                    string[] blocks = Regex.Split(line, @"\s{3,}");
                    if (blocks.Length < 4) continue;

                    DateTime at;
                    if (!DateTime.TryParseExact(blocks[0].Trim(), "yyyy MM dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out at))
                        continue;

                    string first = blocks[3].Trim().Split(' ')[0];
                    double value;
                    if (!double.TryParse(first, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) continue;
                    if (value < 0) continue;              // not measured yet today
                    if (at < bestAt) continue;
                    bestAt = at; best = value;
                }
                return best;
            }
            catch (Exception ex) { Log.Warn("A index: " + ex.Message); return null; }
        }

        // [{"time_tag":"2026-08-19T17:00:00","flux":1.26e+002,...}, ...] - newest FIRST in this one,
        // which is exactly why the newest is found by its time rather than by its position.
        private static async Task<double?> ReadSfiAsync()
        {
            try
            {
                string body = await Http.GetStringAsync(Host + SfiPath).ConfigureAwait(false);
                var rows = JArray.Parse(body);

                DateTime bestAt = DateTime.MinValue;
                double? best = null;

                foreach (var row in rows.OfType<JObject>())
                {
                    DateTime at;
                    double value;
                    if (!TryTime(row["time_tag"], out at)) continue;
                    if (!TryNumber(row["flux"], out value)) continue;
                    if (value < 0) continue;
                    if (at < bestAt) continue;
                    bestAt = at; best = value;
                }
                return best;
            }
            catch (Exception ex) { Log.Warn("SFI: " + ex.Message); return null; }
        }

        private static bool TryTime(JToken token, out DateTime when)
        {
            when = DateTime.MinValue;
            if (token == null) return false;
            return DateTime.TryParse((string)token, CultureInfo.InvariantCulture,
                                     DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when);
        }

        // NOAA writes its flux as 1.260000000000000e+002, which only parses with the invariant culture
        // and NumberStyles.Float - and this program runs on machines whose comma is a decimal point.
        private static bool TryNumber(JToken token, out double value)
        {
            value = 0;
            if (token == null) return false;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                value = (double)token;
                return true;
            }
            return double.TryParse((string)token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
