using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DXCCManager;
using HolyParser;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HolyLogger
{
    // ONE QSO, READ BY AN AI, AND NOTHING WRITTEN BACK.
    //
    // The right-click item "Ask AI to check this QSO" ends up here. The QSO's fields are described in
    // plain words, sent to Anthropic's Messages API, and whatever comes back is shown to the operator
    // as a short report. The log is never touched - this is the same rule the Verify tool follows:
    // report, do not change. What to do about a remark is the operator's business.
    //
    // WHY AN AI AT ALL, when the program already checks prefixes, bands and modes by itself: those
    // checks are exact and free, and they stay where they are. This is for the rest - a callsign that
    // looks like a country it is not, a DXpedition worked on a date it was not on the air, a grid that
    // sits in the wrong country, a comment that contradicts the fields. Judgement rather than lookup.
    //
    // WHAT LEAVES THE COMPUTER: the fields of the ONE QSO the operator asked about, nothing else. No
    // log, no callsign list, no password. The window says so before the first call.
    internal static class AiQsoCheck
    {
        // WHICH SERVICE, AND WHERE ITS ADDRESS AND MODEL LIVE: AiProviders.cs. This file no longer
        // knows the name of any company. It knows two request shapes, and asks the chosen service
        // which of them it speaks.
        private static AiService Service { get { return AiServices.Current; } }

        // One client for the life of the program: a new HttpClient per call leaks sockets.
        private static readonly HttpClient Http = BuildClient();

        private static HttpClient BuildClient()
        {
            // .NET Framework does not turn TLS 1.2 on by itself on every machine, and the API speaks
            // nothing older. The rest of the program does the same before its web calls.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // TEN MINUTES, not three. Thinking at "high" has taken over two minutes for six QSOs,
            // and a batch of ten with more to weigh up can take longer - a timeout that fires while
            // the model is still working throws away the answer AND the allowance spent on it.
            return new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        }

        internal static string ApiKey
        {
            get { return Service.Key; }
        }

        // PER SERVICE, NOT ONE BOX SHARED. Each keeps its own key, so trying the paid one for an
        // afternoon and going back to the free one does not mean pasting a key again from a page the
        // operator has to sign into first.
        internal static bool HasKey
        {
            get { return ApiKey.Length > 0; }
        }

        // Where the operator gets a key. A CLICKABLE LINK in the window, not an address to copy by
        // hand - an operator who has to retype an address into a browser is an operator who gives up
        // on the feature. Which address it is now depends on the service he picked.
        internal static string KeyPageUrl { get { return Service.KeyPageUrl; } }

        // Said in three pieces because the middle one is the link.
        internal static string KeyHelpBefore
        {
            get { return "The check is done by " + Service.Label + ". It needs a key of your own. Get one at "; }
        }
        internal static string KeyHelpLinkText { get { return Service.KeyPageText; } }
        internal static string KeyHelpAfter
        {
            get { return ", then paste it below. It is kept on this computer only."; }
        }

        /// <summary>
        /// Asks the AI about one QSO. Returns the report as plain text. Throws with a readable
        /// message when the service refuses or cannot be reached - the window shows that message.
        /// </summary>
        // WAITING OUT A PER-MINUTE LIMIT INSTEAD OF REPORTING IT.
        //
        // The free allowance is counted per minute as well as per day, and checking three or four QSOs
        // one after another is enough to trip the per-minute one. Google answers 429 and says how long
        // to wait - usually a handful of seconds. Telling the operator to come back later, when the
        // service has just told us it will be ready in eight seconds, is making him do the waiting.
        // So a short wait is taken here, once, and only when the service itself named the delay.
        // A MINUTE AND A QUARTER. The free allowance is twenty requests A MINUTE, so the longest
        // wait it can ever ask for is the rest of that minute - and the ones it actually asks for
        // run to fifty-eight seconds. Forty-five stopped just short of the waits worth sitting out,
        // and sent the operator away with an error over less than a minute.
        private const int LongestAutoWaitSeconds = 75;

        internal static Task<string> CheckAsync(QSO qso, CancellationToken cancel)
        {
            return AskAsync(SystemPrompt, Describe(qso), cancel);
        }

        // THE ONE WAY IN AND OUT. Everything that asks the AI anything comes through here: the single
        // QSO report, and the Log Fixer asking which of two countries it believes. Both want the same
        // handling of a service that says "not so fast" - wait the time it names, once, and then give
        // up rather than sit there retrying while the operator watches a still window.
        //
        // `say` is optional and is how a caller with a button or a status line can show the wait. A
        // silent minute is indistinguishable from a hung program, and the wait here runs to fifty-
        // eight seconds.
        // `onLine` IS WHAT MAKES THE WAIT VISIBLE. Give it a callback and the answer is asked for as a
        // stream: the service sends the words as the model writes them, and every finished LINE is
        // handed over the moment it lands. A caller whose answer is a list - one verdict per contact -
        // can then colour the first row while the model is still writing the second, which is real
        // progress rather than a clock ticking beside a window that does not move.
        //
        // Costs nothing extra. The price is per token and the tokens are the same ones; streaming only
        // changes when they arrive.
        //
        // Leave it null and everything behaves exactly as it did: one request, one answer at the end.
        internal static async Task<string> AskAsync(string system, string input, CancellationToken cancel,
                                                    Action<string> say = null, Action<string> onLine = null)
        {
            try
            {
                return await SendAsync(system, input, cancel, onLine).ConfigureAwait(false);
            }
            catch (RateLimited limited)
            {
                if (limited.RetryAfterSeconds <= 0 || limited.RetryAfterSeconds > LongestAutoWaitSeconds)
                    throw new Exception(limited.Message);

                if (say != null)
                    say("The free allowance needs " + limited.RetryAfterSeconds + " seconds - waiting...");

                await Task.Delay(TimeSpan.FromSeconds(limited.RetryAfterSeconds + 1), cancel)
                          .ConfigureAwait(false);
                try
                {
                    // A REFUSAL ARRIVES AS A STATUS, BEFORE A WORD OF THE ANSWER DOES - so nothing was
                    // handed to onLine on the attempt that failed, and asking again cannot repeat a
                    // line the caller has already been given.
                    return await SendAsync(system, input, cancel, onLine).ConfigureAwait(false);
                }
                catch (RateLimited again)
                {
                    throw new Exception(again.Message);
                }
            }
        }

        // A 429 with the service's own advice about it, so the caller can decide whether waiting is
        // worth it. Not shown to the operator as-is - CheckAsync turns it into a plain message.
        private class RateLimited : Exception
        {
            public readonly int RetryAfterSeconds;
            public RateLimited(string message, int retryAfterSeconds) : base(message)
            {
                RetryAfterSeconds = retryAfterSeconds;
            }
        }

        private static async Task<string> SendAsync(string system, string input, CancellationToken cancel,
                                                    Action<string> onLine)
        {
            if (string.IsNullOrWhiteSpace(input)) throw new ArgumentNullException("input");
            if (!HasKey) throw new InvalidOperationException("No API key has been entered yet.");

            AiService service = Service;

            // ZERO. Facts, not invention: temperature is how much freedom the model has to pick a
            // less likely word, and every scrap of it here buys nothing and costs repeatability. At
            // 0.2 the same six QSOs came back 5-1-0, then 4-2-0, then 3-2-1 - three different answers
            // to one question, which is not a second opinion, it is a coin. The same number either
            // way: it is the one setting both request shapes agree on.
            const double Temperature = 0.0;

            string body = service.ChatShape
                ? JsonConvert.SerializeObject(new
                {
                    model = service.Model,
                    // THE SHAPE EVERYONE BUT GOOGLE SPEAKS. The instructions are the first message
                    // rather than a field of their own, which is the only real difference here.
                    messages = new object[]
                    {
                        new { role = "system", content = system },
                        new { role = "user", content = input }
                    },
                    temperature = Temperature
                })
                : JsonConvert.SerializeObject(new
                {
                    model = service.Model,
                    system_instruction = system,
                    input = input,
                    generation_config = new
                    {
                        temperature = Temperature,
                        // HIGH. THE OPERATOR'S CHOICE, MADE WITH THE PRICE IN FRONT OF HIM.
                        //
                        // Thinking is where the time goes: six QSOs took 142 seconds here while a
                        // browser doing none answered in two, and the report in between cost 23 ms.
                        // Cutting it to "minimal" would buy back nearly all of that.
                        //
                        // He would rather wait. A wrong country written into a log kept for thirty
                        // years outlives any amount of waiting, and three AIs have already been
                        // caught voting against their own stated reasons at lower settings. So this
                        // asks for the most thought the endpoint offers, and the window is built to
                        // sit out the wait: a spinner, a count of seconds, and a Stop button.
                        thinking_level = "high"
                    },
                    // ASKED FOR IN SO MANY WORDS, rather than left to a default.
                    //
                    // Google serves this API in tiers now. "flex" has a stated target of ONE TO
                    // FIFTEEN MINUTES, and the 142 seconds measured here sits inside that band -
                    // while the same question in a browser came back in two. The documentation says
                    // an omitted tier means standard, but it does not say what a FREE key is served
                    // as, and best-effort is what free usually means. So it is named.
                    //
                    // If nothing changes, the free tier itself is the ceiling, and that is worth
                    // knowing too - it is the difference between a bug and a price.
                    service_tier = "standard"
                });

            // ASKED FOR AS A STREAM ONLY WHEN SOMEBODY IS LISTENING LINE BY LINE. Added to the body
            // after it is built rather than written into both shapes above: it is the same one word
            // for either service, and duplicating it into two anonymous objects would mean two places
            // to forget it.
            // STREAMING IS OFF WHILE WE FIND OUT WHY GEMINI GOES QUIET.
            //
            // The same question answered in three seconds in a browser and timed out three times from
            // here, after the streaming change and nothing else. So this is switched off in one place
            // rather than unpicked: with it false, `onLine` is dropped and the request is exactly the
            // one that worked before - one question, one answer at the end.
            const bool AskForAStream = false;

            if (onLine != null && AskForAStream)
            {
                try
                {
                    JObject shaped = JObject.Parse(body);
                    shaped["stream"] = true;
                    body = shaped.ToString(Formatting.None);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); onLine = null; }
            }
            else
            {
                onLine = null;
            }

            SavePrompt(system, input, service);

            using (var request = new HttpRequestMessage(HttpMethod.Post, service.Endpoint))
            {
                // The key goes in a HEADER, never in the address. A key in the URL is written into
                // every proxy log and browser history it passes through.
                if (service.Bearer)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
                else
                    request.Headers.Add("x-goog-api-key", ApiKey);
                request.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                HttpResponseMessage response;
                try
                {
                    // ResponseHeadersRead, or SendAsync waits for the last byte before it returns and
                    // there is nothing left to stream.
                    response = await Http.SendAsync(request,
                            onLine != null ? HttpCompletionOption.ResponseHeadersRead
                                           : HttpCompletionOption.ResponseContentRead,
                            cancel)
                        .ConfigureAwait(false);
                }
                catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new Exception("The AI did not answer in time. Try again.");
                }
                catch (HttpRequestException e)
                {
                    throw new Exception("Could not reach the AI service: " + e.Message);
                }

                // A REFUSAL IS SHORT AND IS READ WHOLE, streaming or not: the status arrives with the
                // headers, so this is decided before a word of any answer has been asked for.
                string text = response.IsSuccessStatusCode && onLine != null
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                // WHAT THE SERVICE ITSELF SAID WHEN IT REFUSED. The operator is shown a sentence in
                // plain words, and that sentence is a guess made from Google's wording - the whole
                // difference between "wait half a minute" and "come back tomorrow" hangs on whether
                // the words "per day" appear in it. So the answer as it arrived goes into the log,
                // where it can be read afterwards instead of remembered. Nothing private is in it:
                // the key travels in a header, never in the body that comes back.
                if (!response.IsSuccessStatusCode)
                    Log.Warn("AI QSO check: the service answered " + (int)response.StatusCode
                             + ": " + FirstPart(text));

                if ((int)response.StatusCode == 429)
                    throw new RateLimited(ExplainFailure(response.StatusCode, text),
                                          RetryAfterSeconds(response, text));

                if (!response.IsSuccessStatusCode)
                    throw new Exception(ExplainFailure(response.StatusCode, text));

                if (onLine != null)
                    return await ReadStreamAsync(response, service.ChatShape, onLine, cancel)
                                 .ConfigureAwait(false);

                return ReadReport(text);
            }
        }

        // EVERY QUESTION, KEPT AS A FILE.
        //
        // The whole worth of this feature turns on how the question is worded, and until now the
        // wording lived only inside the program - so an operator who wanted to put the same question
        // to another AI, or to see for himself what his log was being asked about, had no way to get
        // at it. Now each question is written out beside the reports, exactly as it was sent: the
        // instructions first, then the contacts.
        //
        // Never allowed to stop a check. A folder that cannot be written to is a nuisance; a check
        // that refuses to run because of it would be a fault.
        private static void SavePrompt(string system, string input, AiService service)
        {
            try
            {
                var sb = new StringBuilder();
                string rule = new string('=', 78);

                sb.AppendLine(rule);
                sb.AppendLine("HolyLogger - the question put to the AI");
                sb.AppendLine(DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm:ss"));
                if (service != null)
                    sb.AppendLine("Asked of " + service.ShortName + " (" + service.Model + ")");
                sb.AppendLine(rule);
                sb.AppendLine();
                sb.AppendLine("THE INSTRUCTIONS (sent as the system prompt)");
                sb.AppendLine(rule);
                sb.AppendLine();
                sb.AppendLine(system ?? string.Empty);
                sb.AppendLine();
                sb.AppendLine(rule);
                sb.AppendLine("THE QUESTION (sent as one message after the instructions)");
                sb.AppendLine(rule);
                sb.AppendLine();
                sb.AppendLine(input ?? string.Empty);

                // Seconds in the name, because a run of several batches asks several questions
                // within the same minute and each of them is worth keeping on its own.
                string name = "holylogger_ai_prompt_"
                            + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".txt";

                File.WriteAllText(Path.Combine(Reports.Folder, name), sb.ToString());
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // WHAT THE AI IS ASKED TO BE. Short lines, ordinary words, and no invented certainty - a
        // report that pads itself out with "looks fine" for every field is a report nobody reads
        // twice.
        private const string SystemPrompt =
            "You are checking ONE amateur radio log entry (a QSO) for mistakes, for an experienced " +
            "operator. You are given the fields as they are stored in his log; blank fields are simply " +
            "not there.\n\n" +
            "Look for things that do not hold together: a callsign whose prefix does not match the " +
            "country or the zones recorded, a frequency that is not in the band named (or not in an " +
            "amateur band at all), a mode that does not belong on that frequency, a grid square that is " +
            "not in that country, an RST that does not fit the mode, a date or time that cannot be " +
            "right, a comment that contradicts the fields, an obviously mistyped callsign, a special " +
            "or expedition callsign that was not active on that date.\n\n" +
            "THE COUNTRY, WHICH IS THE FIELD THAT GOES WRONG MOST. At the end you are given what each " +
            "of the two country databases answered for this callsign on this QSO's own date and time, " +
            "and which of them the program decided to follow. cty.dat knows callsign prefixes as they " +
            "stand today; Club Log knows what a particular callsign WAS between two moments - a " +
            "DXpedition, a special licence - and its records begin and end at a time of day, not at " +
            "midnight. When the two disagree, say in your own words what you understand each of them " +
            "to be saying, which one you would follow for THIS contact, and why - the date and time of " +
            "the QSO against the window Club Log gives is usually what settles it. Say plainly when " +
            "the program's own recommendation is the wrong one.\n\n" +
            "Rules for your answer:\n" +
            "- Plain text. No markdown, no headings, no bullets characters other than a leading dash.\n" +
            // THE LABELS ARE ORDINARY ENGLISH, because an operator reading his own log should not have
            // to work out what a word means before he can read the line. "VERDICT" was the first
            // version and it is a courtroom's word, not a radio one.
            "- One line per remark, shortest first: start each with PROBLEM, CHECK or NOTE.\n" +
            "- Only remark on what is worth his time. Do not list the fields that are fine.\n" +
            "- The country is the exception: when the two databases disagree, always say what you " +
            "understood from each and which you would follow, even if the program already chose right.\n" +
            "- If you are not sure, say so in the line itself rather than stating it as fact.\n" +
            "- Never invent a fact about a callsign or an expedition you do not actually know, and " +
            "never call a date wrong merely because it is later than what you know of the world.\n" +
            "- Finish with one last line beginning 'IN SHORT: ' and at most twelve words.\n" +
            "- If nothing is wrong, the whole answer is that IN SHORT line.";

        // The QSO in words. Only the fields that carry something - an empty field tells the AI
        // nothing and costs tokens - and the names are spelled as an operator says them, not as the
        // database column is called.
        private static string Describe(QSO q)
        {
            var lines = new List<string>();
            Add(lines, "Station worked (DX callsign)", q.DXCall);
            Add(lines, "My callsign", q.MyCall);
            Add(lines, "Operator", q.Operator);
            Add(lines, "Date", ReadableDate(q.Date));
            Add(lines, "Time UTC", ReadableTime(q.Time));
            Add(lines, "Band", q.Band);
            Add(lines, "Frequency (MHz)", q.Freq);
            Add(lines, "Mode", q.Mode);
            Add(lines, "Submode", q.SUBMode);
            Add(lines, "RST sent", q.RST_SENT);
            Add(lines, "RST received", q.RST_RCVD);
            Add(lines, "Country the program resolved", q.Country);
            Add(lines, "DXCC entity code", q.DxccCode > 0 ? q.DxccCode.ToString(CultureInfo.InvariantCulture) : null);
            Add(lines, "Continent", q.Continent);
            Add(lines, "CQ zone", q.CQZone);
            Add(lines, "ITU zone", q.ITUZone);
            Add(lines, "State", q.State);
            Add(lines, "His grid square", q.DXLocator);
            Add(lines, "My grid square", q.MyLocator);
            Add(lines, "His name", q.Name);
            Add(lines, "His QTH", q.Qth);
            Add(lines, "IOTA", q.Iota);
            Add(lines, "SOTA", q.SotaRef);
            Add(lines, "POTA", q.PotaRef);
            Add(lines, "WWFF", q.WwffRef);
            Add(lines, "Propagation mode", q.PROP_MODE);
            Add(lines, "Satellite", q.SAT_NAME);
            Add(lines, "Contest exchange sent", q.STX);
            Add(lines, "Contest exchange received", q.SRX);
            Add(lines, "Contest", q.ContestId);
            Add(lines, "Comment", q.Comment);
            Add(lines, "Soapbox", q.SOAPBOX);

            lines.AddRange(WhatTheDatabasesSay(q));

            // WHAT DAY IT IS, said first.
            //
            // An AI knows nothing that happened after it was trained, and it has no clock. Without
            // this line it reads a date it has never heard of as one that has not happened yet - a
            // QSO logged last week came back as "WRONG: the date is in the future". Told the day, it
            // can judge what it is actually being asked: a date AHEAD of today is the operator's
            // mistake, and a date behind it is simply a contact it does not remember.
            return "Today is " + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                   + " (UTC), so anything up to that day has already happened.\n"
                   + "Your own knowledge may end well before today - if a date is in the past and you "
                   + "do not recognise the operation, say nothing about it rather than calling it wrong.\n\n"
                   + "Here is the QSO:\n" + string.Join("\n", lines);
        }

        // THE LOG'S OWN SPELLING IS NOT ANYBODY'S. Dates are stored as yyyyMMdd and times as HHmmss,
        // and "20260818" was read by the AI as a number rather than a day. Written out plainly there
        // is nothing left to interpret.
        private static string ReadableDate(string yyyymmdd)
        {
            string digits = (yyyymmdd ?? string.Empty).Trim();
            if (digits.Length != 8) return digits;
            DateTime parsed;
            if (!DateTime.TryParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out parsed))
                return digits;
            // "18 August 2026" - no order of numbers to guess at.
            return parsed.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture);
        }

        private static string ReadableTime(string hhmmss)
        {
            string digits = (hhmmss ?? string.Empty).Trim();
            if (digits.Length == 6)
                return digits.Substring(0, 2) + ":" + digits.Substring(2, 2) + ":" + digits.Substring(4, 2);
            if (digits.Length == 4)
                return digits.Substring(0, 2) + ":" + digits.Substring(2, 2);
            return digits;
        }

        private static void Add(List<string> lines, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            lines.Add("- " + label + ": " + value.Trim());
        }

        // THE TWO COUNTRY DATABASES, IN THE AI'S HANDS TOO.
        //
        // The country is the field they argue about, and the argument is exactly where a second
        // opinion is worth having: cty.dat knows callsign prefixes as they stand, Club Log knows what
        // a particular callsign WAS between two moments - a DXpedition, a special licence - and when
        // they disagree the program has to pick one. Showing the AI what each said, and what the
        // program made of it, is what lets it say "Club Log is right here and here is why" instead of
        // guessing at the country from the callsign like everybody else.
        //
        // Asked with the QSO's own date AND time, the same way the rest of the program asks (see
        // CountryLookup.QsoDate) - four hours in a day decided Swains Island against American Samoa.
        private static IEnumerable<string> WhatTheDatabasesSay(QSO q)
        {
            var lines = new List<string>();
            string call = (q.DXCall ?? string.Empty).Trim();
            if (call.Length == 0) return lines;

            CountryLookup.Explanation x;
            try
            {
                x = CountryLookup.Shared.Explain(call, CountryLookup.QsoDate(q.Date, q.Time));
            }
            catch (Exception swallowed)
            {
                // The AI can still check everything else; a missing paragraph is not a failed check.
                Log.Swallow(swallowed);
                return lines;
            }
            if (x == null) return lines;

            lines.Add("");
            lines.Add("What the two country databases say about this callsign on this QSO's date and time:");
            lines.Add("- cty.dat: " + x.CtySays);
            lines.Add("- Club Log: " + x.ClubSays);
            foreach (string note in x.ExtraNotes)
                lines.Add("- Club Log note: " + note.Replace("**", string.Empty));
            lines.Add("- " + x.Recommends + "  (this is the country HolyLogger would write into the log)");
            lines.Add("- The log currently holds: "
                      + (string.IsNullOrWhiteSpace(q.Country) ? "nothing" : q.Country.Trim())
                      + (q.DxccCode > 0 ? " (" + q.DxccCode.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty));
            return lines;
        }

        // The answer carries the finished text in "output_text". The long way round - walking the
        // steps and picking the text blocks out - is kept as a fallback, because a service that adds
        // a step type tomorrow should not leave the operator staring at an empty window.
        // THE ANSWER READ AS IT IS WRITTEN.
        //
        // Both services stream the same way - server-sent events, one "data:" line per piece, and the
        // literal [DONE] at the end - and differ only in which corner of the JSON the new words sit
        // in. So the plumbing is written once here and the difference is a single method below.
        //
        // WHY LINES AND NOT PIECES. The pieces arrive at whatever size the service felt like sending;
        // half a word is common. A LINE, though, is a whole thought in every answer this program asks
        // for - one verdict, one contact - so the pieces are collected until a newline says the
        // thought is finished, and only then is it handed on. The caller never sees half an answer.
        //
        // The whole text is returned as well, exactly as the non-streaming path returns it, so a
        // caller that also wants to read the answer through at the end can.
        private static async Task<string> ReadStreamAsync(HttpResponseMessage response, bool chatShape,
                                                          Action<string> onLine, CancellationToken cancel)
        {
            var whole = new StringBuilder();     // everything the model wrote
            var pending = new StringBuilder();   // the line it is in the middle of writing
            var body = new StringBuilder();      // the answer exactly as it came, streamed or not
            int given = 0;

            // HOW THE STREAM ENDED, which is the difference between two faults that look identical
            // from the outside. An answer that stops after one verdict of six was either cut off in
            // the middle - the connection dropped, and [DONE] never came - or the model decided it
            // had finished, in which case [DONE] arrived and the fault is upstream of us. Guessing
            // between the two costs a day; the flag costs nothing.
            bool ended = false;
            string last = string.Empty;

            DateTime began = DateTime.UtcNow;

            using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            {
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();

                    // A WAIT WITH AN END TO IT.
                    //
                    // The three-minute limit on the request stops covering anything once the answer
                    // starts arriving: the request itself finished the moment the headers landed, and
                    // everything after that is this loop. A service that sends a header and then goes
                    // quiet leaves the program here for ever - it was watched doing it for four
                    // hundred and sixty seconds, spinner turning, nothing on the screen and no way
                    // out but killing the program.
                    //
                    // So the silence is timed. Not the whole answer - a model that is writing
                    // steadily may take minutes over a long batch and that is fine - but the GAP
                    // between one piece and the next, which is never long while anybody is alive at
                    // the other end.
                    Task<string> next = reader.ReadLineAsync();
                    Task first = await Task.WhenAny(next, Task.Delay(Silence, cancel)).ConfigureAwait(false);

                    if (first != next)
                    {
                        // The read is abandoned, not waited for: the stream is about to be disposed
                        // under it. Its failure is looked at and dropped so it cannot come back later
                        // as an exception nobody is expecting.
                        Forget(next);

                        throw new Exception("The AI stopped sending in the middle of its answer, and "
                            + "nothing more came for " + (int)Silence.TotalSeconds + " seconds. Nothing "
                            + "has been written to your log. Press Check with AI again and it will ask "
                            + "about the ones it never answered.");
                    }

                    string sse = await next.ConfigureAwait(false);
                    if (sse == null) break;

                    // AND A LIMIT ON THE WHOLE ERRAND, in case it never falls silent for long enough
                    // to trip the one above but never finishes either.
                    if (DateTime.UtcNow - began > WholeAnswer)
                        throw new Exception("The AI has been answering for over "
                            + (int)WholeAnswer.TotalMinutes + " minutes and has not finished. Nothing "
                            + "has been written to your log. Press Check with AI again to ask about "
                            + "the ones it never answered.");

                    body.AppendLine(sse);

                    // Anything that is not a data line is not the answer: the "event:" lines naming
                    // the kind of event, the blank lines between events, and the comments one of the
                    // services sends every few seconds purely to hold the connection open. That last
                    // one is not JSON, and handing it to a parser is how a stream ends in an error
                    // over a keep-alive.
                    if (!sse.StartsWith("data:", StringComparison.Ordinal)) continue;

                    string payload = sse.Substring("data:".Length).Trim();
                    if (payload.Length == 0) continue;
                    if (payload == "[DONE]") { ended = true; break; }

                    last = payload;

                    string piece = PieceOf(payload, chatShape);
                    if (piece.Length == 0) continue;

                    whole.Append(piece);

                    foreach (char c in piece)
                    {
                        if (c != NewLine) { pending.Append(c); continue; }
                        if (HandOver(onLine, pending)) given++;
                    }
                }
            }

            // The last line usually arrives without a newline after it, and it is an answer like any
            // other - dropping it would lose one contact's verdict every single time.
            if (HandOver(onLine, pending)) given++;

            // NOT A STREAM AFTER ALL, AND NOT A DEAD END EITHER.
            //
            // A service that never learnt the word "stream" answers in one piece, the ordinary way -
            // no "data:" lines, nothing for the reader above to find - and returning the empty string
            // it collected would throw the whole answer away and report nothing at all, which is a
            // far worse failure than simply not showing progress. So when nothing came through as a
            // stream, what did arrive is read as an ordinary reply.
            if (whole.Length == 0)
            {
                Log.Warn("AI: asked for a stream and got none - reading the answer whole instead.");
                return ReadReport(body.ToString());
            }

            if (given == 0)
                Log.Warn("AI: the answer streamed but held no finished line.");

            // Said whatever the answer looked like, because "it stopped early" is worth knowing even
            // when enough lines happened to arrive: it is the same fault, caught on a good day.
            if (!ended)
                Log.Warn("AI: the stream ended without [DONE] - the answer was cut short. "
                         + "The last thing it sent: " + FirstPart(last));

            return Tidy(whole.ToString());
        }

        // Its own name, because a newline written as a character literal inside this file has been
        // mangled by a tool once already and the compiler cannot always tell.
        private const char NewLine = '\n';

        // The longest gap between two pieces of an answer that is taken for "still working", and the
        // longest the whole answer may take however steadily it arrives.
        private static readonly TimeSpan Silence = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan WholeAnswer = TimeSpan.FromMinutes(5);

        // A task nobody is going to wait for. Its failure is read and thrown away, so an abandoned
        // read of a stream that has since been disposed cannot surface later as an exception out of
        // nowhere.
        private static void Forget(Task task)
        {
            if (task == null) return;
            task.ContinueWith(t => { var ignored = t.Exception; },
                              TaskContinuationOptions.OnlyOnFaulted);
        }

        private static bool HandOver(Action<string> onLine, StringBuilder pending)
        {
            string line = pending.ToString().Trim();
            pending.Length = 0;

            if (line.Length == 0 || onLine == null) return false;

            // A CALLER'S MISTAKE MUST NOT KILL THE STREAM. Whatever is done with a finished line -
            // painting a row, counting an answer - happens on somebody else's side of the fence, and
            // a throw from there would abandon the rest of the answer that is still arriving.
            try { onLine(line); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return true;
        }

        // WHERE THE NEW WORDS SIT, which is the only thing the two services do differently here.
        // Everything else in the stream - the status updates, a step beginning, the token counts at
        // the end - carries no words and is passed over in silence.
        private static string PieceOf(string payload, bool chatShape)
        {
            try
            {
                var o = JObject.Parse(payload);

                if (chatShape)
                {
                    var choices = o["choices"] as JArray;
                    if (choices == null || choices.Count == 0) return string.Empty;

                    JToken delta = choices[0]["delta"];
                    return delta == null ? string.Empty : ((string)delta["content"] ?? string.Empty);
                }

                JToken d = o["delta"];
                if (d == null) return string.Empty;
                if (!string.Equals((string)d["type"], "text", StringComparison.Ordinal)) return string.Empty;
                return (string)d["text"] ?? string.Empty;
            }
            catch (Exception swallowed)
            {
                // One unreadable piece is one gap in the words, not a reason to give up on the rest.
                Log.Swallow(swallowed);
                return string.Empty;
            }
        }

        private static string ReadReport(string json)
        {
            try
            {
                var root = JObject.Parse(json);

                // THE OTHER SHAPE'S ANSWER, WHICH IS THE SIMPLER OF THE TWO: one message, and the
                // words are in its content. Tried first and only when the service speaks it, so a
                // reply that happens to carry both cannot be read the wrong way round.
                if (Service.ChatShape)
                {
                    var choices = root["choices"] as JArray;
                    if (choices != null && choices.Count > 0)
                    {
                        var message = choices[0]["message"];
                        string said = message == null ? null : (string)message["content"];
                        if (!string.IsNullOrWhiteSpace(said)) return Tidy(said);
                    }
                    return "The AI answered in a form this program did not understand.";
                }

                string direct = (string)root["output_text"];
                if (!string.IsNullOrWhiteSpace(direct))
                    return Tidy(direct);

                var sb = new StringBuilder();
                var steps = root["steps"] as JArray;
                if (steps != null)
                {
                    foreach (var step in steps)
                    {
                        var blocks = step["content"] as JArray;
                        if (blocks == null) continue;
                        foreach (var block in blocks)
                        {
                            if ((string)block["type"] != "text") continue;
                            string text = (string)block["text"];
                            if (string.IsNullOrWhiteSpace(text)) continue;
                            if (sb.Length > 0) sb.Append("\r\n");
                            sb.Append(text.Trim());
                        }
                    }
                }

                string report = sb.ToString();
                return report.Length == 0 ? "The AI had nothing to say about this QSO." : Tidy(report);
            }
            catch (Exception e)
            {
                Log.Warn("AI QSO check: could not read the answer: " + e.GetType().Name + ": " + e.Message);
                return "The AI answered in a form this program did not understand.";
            }
        }

        // HOW LONG THE SERVICE ITSELF SAYS TO WAIT. Google answers a 429 with a Retry-After header, or
        // with a RetryInfo block in the body carrying "retryDelay": "8s". Zero means it said nothing,
        // and then nothing is waited out - a made-up delay is just a slower failure.
        private static int RetryAfterSeconds(HttpResponseMessage response, string payload)
        {
            try
            {
                if (response.Headers.RetryAfter != null && response.Headers.RetryAfter.Delta.HasValue)
                    return (int)response.Headers.RetryAfter.Delta.Value.TotalSeconds;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    payload ?? string.Empty, "\"retryDelay\"\\s*:\\s*\"(\\d+)(?:\\.\\d+)?s\"");
                int seconds;
                if (match.Success && int.TryParse(match.Groups[1].Value, out seconds)) return seconds;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // AND WHEN IT IS ONLY IN THE SENTENCE. The refusal that actually arrives carries no
            // retryDelay field at all - the wait is written into the English: "Please retry in
            // 57.653013552s". Read out of the prose it is still the service's own number, which was
            // the whole point of asking; ignoring it left the program guessing zero and giving up on
            // a wait of under a minute.
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    payload ?? string.Empty,
                    "retry in\\s+(\\d+)(?:\\.\\d+)?\\s*s",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                int seconds;
                if (match.Success && int.TryParse(match.Groups[1].Value, out seconds)) return seconds;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return 0;
        }

        // Line endings a WPF text box shows properly: the service sends bare newlines.
        private static string Tidy(string text)
        {
            return text.Trim().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        // A refusal is a sentence or two; a page of HTML from something in the way is not, and a log
        // file is not the place to keep it whole.
        private static string FirstPart(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return "(nothing at all)";
            string one = payload.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return one.Length <= 1500 ? one : one.Substring(0, 1500) + " ...(cut)";
        }

        // THE SERVER'S REFUSALS, IN WORDS THE OPERATOR CAN ACT ON. An HTTP number on its own tells
        // him nothing about what to do next.
        // THE NUMBER THE SERVICE NAMED, IF IT NAMED ONE. Google writes it into the refusal as
        // "limit: 20, model: gemini-3.7-flash", and an operator told his allowance is twenty can
        // decide what to do about it. Told only that it "ran out", he can decide nothing.
        // A PHRASE, NOT A SENTENCE - it is dropped into the middle of the refusal below.
        private static string Limit(string detail)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    detail ?? string.Empty,
                    "limit:\\s*(\\d+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                    return " of " + m.Groups[1].Value + " requests a day";
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return string.Empty;
        }

        // WHEN THE FREE ALLOWANCE COMES BACK, ON HIS OWN CLOCK. Google counts its day in California
        // and starts the allowance again at midnight THERE - which is the middle of the morning in
        // Israel. So an operator who used it up at eleven at night and comes back after breakfast
        // has had a whole night, but not a whole Google day, and "starts again tomorrow" sent him
        // away believing the program was broken. It happened, on 2026-08-25: refused at 08:21 with
        // ten idle minutes behind it, while in California it was still the previous evening.
        //
        // The moment is worked out and named instead. Empty when the machine has no Pacific zone to
        // convert with - then nothing is claimed about the hour.
        private static string WhenAllowanceReturns()
        {
            try
            {
                var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
                DateTime there = TimeZoneInfo.ConvertTime(DateTime.Now, pacific);
                DateTime here = TimeZoneInfo.ConvertTime(there.Date.AddDays(1), pacific, TimeZoneInfo.Local);

                if (here.Date == DateTime.Now.Date) return "at " + here.ToString("HH:mm") + " today";
                if (here.Date == DateTime.Now.Date.AddDays(1)) return "at " + here.ToString("HH:mm") + " tomorrow";
                return "at " + here.ToString("HH:mm") + " on " + here.ToString("d MMMM");
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return string.Empty; }
        }

        // WHERE MORE CREDIT GOES ON, IN THE CHOSEN SERVICE'S OWN WORDS. Named by the service and not
        // written in here, because this file knows no company by name and is worth keeping that way.
        private static string TopUp()
        {
            string where = (Service.TopUpText ?? string.Empty).Trim();
            return where.Length > 0 ? where : "the service's own website";
        }

        private static string ExplainFailure(HttpStatusCode status, string payload)
        {
            string detail = string.Empty;
            try
            {
                var root = JObject.Parse(payload ?? string.Empty);
                detail = (string)(root["error"] != null ? root["error"]["message"] : null) ?? string.Empty;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            switch ((int)status)
            {
                case 401:
                case 403:
                    return "The API key was not accepted. Check that it was copied whole, and that it "
                           + "is still valid at aistudio.google.com/apikey.";
                case 429:
                    // BY THE TIME THIS REACHES HIM IT IS THE DAY'S ALLOWANCE, NOT THE MINUTE'S.
                    //
                    // Google refuses with a number but never says which window it belongs to, so this
                    // message used to hedge - "if waiting a minute does not clear it, the allowance is
                    // a daily one" - and left the operator to find out by sitting there. He does not
                    // have to: AskAsync has ALREADY waited out any short delay the service named and
                    // asked a second time. A refusal that gets this far has survived that wait, so
                    // waiting another minute is not the answer and the message must not suggest it.
                    //
                    // What it names instead is the hour the allowance returns, in his own clock.
                    //
                    // AND NOTHING ABOUT SWITCHING SERVICES. It used to end "or choose a different AI
                    // service in the check window" - advice naming a window he would have had to go
                    // and find. Whoever shows this message offers the chooser itself if it can; a
                    // sentence cannot.
                    string returns = WhenAllowanceReturns();
                    return "The AI service refused: the free allowance" + Limit(detail)
                           + " has been used up. It starts again "
                           + (returns.Length > 0 ? returns : "at midnight California time")
                           + ", because the allowance runs on California time, not on yours.";
                case 402:
                    // THE CREDIT IS GONE, AND THAT IS THE CAP DOING ITS WORK.
                    //
                    // This is not a fault to apologise for. A prepaid account cannot spend money it
                    // was never given, which is the whole reason a paid service is offered at all to
                    // somebody who did not want a bill that grows with use. So the message says the
                    // one thing he needs to hear first - nothing more has been charged - and only
                    // then where to put more on.
                    //
                    // AND NOTHING ABOUT SWITCHING SERVICES, for the same reason as the refusal
                    // below: whoever shows this offers the chooser itself if it can.
                    return "The credit on the account is used up. Nothing more has been charged - the "
                           + "account only ever spends what you put on it. Put more on at "
                           + TopUp() + ".";
                case 400:
                    return "The AI service refused the request"
                           + (detail.Length > 0 ? ": " + detail : ".");
                default:
                    if ((int)status >= 500)
                        return "The AI service had a problem of its own (" + (int)status + "). Try again shortly.";
                    return "The AI service answered " + (int)status
                           + (detail.Length > 0 ? ": " + detail : ".");
            }
        }
    }
}
