using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DXCCManager;
using HolyParser;

namespace HolyLogger
{
    // THE ONE QUESTION IN THE LOG FIXER THAT IS NOT A LOOKUP.
    //
    // Most of what Verify reports is settled by a table: a grid that is not a grid, a continent that
    // does not follow its country, a band that does not match its frequency. Those the program knows
    // exactly, and asking anyone else about them would be trading an answer for an opinion.
    //
    // One pile is different. When cty.dat and Club Log disagree about which country a callsign was
    // working from, there is no table left to consult - the two tables ARE the disagreement. The
    // program picks one and says so, and the operator is asked to judge. That is the pile this file
    // is for, and no other.
    //
    // WHAT IT ASKS FOR: not a country. The AI is shown the two candidates that are already on the
    // screen - what the log says, and what HolyLogger proposes - and asked which of THOSE two it
    // believes. A vote between two known answers cannot invent a third, which is the failure mode
    // that matters when a model is asked about a callsign from twenty years ago.
    //
    // WHY IN BATCHES: the free allowance is counted in REQUESTS, not in words. Ten contacts in one
    // request cost one of the day's allowance; ten requests cost ten. The instructions below are the
    // same paragraph every time, and sending it once for ten contacts rather than ten times over is
    // most of what makes this affordable at all.
    internal static class AiCountryVote
    {
        // Ten, and not more. A longer list is not refused, it is answered worse: the replies drift
        // shorter towards the end, and an answer cut off mid-list loses the tail silently. Ten also
        // means a rate limit costs one small batch to redo, not fifty contacts.
        internal const int PerRequest = 10;

        // WHAT THE AI IS ALLOWED TO SAY. Three words, so an answer either parses or is treated as no
        // answer at all. "Probably the log" is not on the list - a maybe belongs in UNSURE with its
        // reason, where it shows on screen as a question rather than as a decision nobody made.
        // WORK OUT THE COUNTRY FIRST. EVERYTHING ELSE FOLLOWS FROM IT.
        //
        // This used to hand the AI two countries and ask which of the two it preferred, and then
        // forbid it to name a third: "the only answers are LOG, SUGGESTED and UNSURE". A question
        // shaped like that cannot produce a finding, only a preference - and when both countries
        // were wrong the program had made it impossible to say so.
        //
        // It is now asked the question the operator actually has: what country WAS this, on that
        // date. Only then is that answer compared with the two on the row. The three verdicts are
        // the three outcomes of one piece of work rather than three opinions about somebody else's.
        //
        // AND ONLY THE CALLSIGN, DATE AND TIME ARE EVIDENCE. Everything else in an old log is
        // unverified - the grid, the zones, the continent are as likely to be wrong as the country,
        // and a log that is wrong twice would otherwise be allowed to vote for itself. His words,
        // 2026-08-25: the only thing we are sure about is the DX callsign and the date and time.
        private const string SystemPrompt =
            "You are checking the country recorded against old amateur radio contacts, for an " +
            "experienced operator going through his log.\n\n" +

            "WORK OUT THE COUNTRY YOURSELF, FIRST. For each numbered contact, decide from the " +
            "CALLSIGN and the DATE AND TIME ALONE what country that station was in when it was " +
            "worked. Only when you have your own answer, compare it with the two countries on " +
            "the row.\n\n" +

            "NOTHING ELSE IN THE LOG IS EVIDENCE. The grid square, the CQ and ITU zones, the " +
            "continent, the band and the mode were typed by somebody and may be as wrong as the " +
            "country you are being asked about. Do not use them, and do not let them talk you " +
            "into an answer. The callsign, the date and the time are the only facts.\n\n" +

            "For each numbered contact you are given: the callsign, the date and time it was " +
            "worked, the country written in the log, the country HolyLogger proposes as a " +
            "correction, and what each of two databases answered for that callsign at that " +
            "moment.\n\n" +

            "cty.dat knows callsign prefixes as they stand TODAY, and nothing about when they " +
            "changed hands. Club Log knows what a particular callsign or prefix WAS between two " +
            "moments - a DXpedition, a special licence, a reissued call - and its records begin " +
            "and end at a time of day, not at midnight.\n\n" +

            "A DATE BEATS A PREFIX RULE - WHILE IT LASTS. When you are given a Club Log record " +
            "with dates and the contact falls INSIDE them, that record is the answer, and a " +
            "prefix rule that holds today does not overrule it.\n\n" +

            "WHEN THE CONTACT FALLS OUTSIDE THE RECORD, SET THAT RECORD ASIDE AND WORK THE " +
            "COUNTRY OUT YOURSELF. Outside its dates the record says nothing at all about this " +
            "contact. Decide what the callsign was by what YOU know of callsign allocation on " +
            "that date - the country the prefix and the call area belong to, and what a " +
            "suffix like /1 or /P means about where the operator was.\n\n" +

            "DO NOT SIMPLY TAKE WHAT A DATABASE SAYS INSTEAD. Both of them carry leftovers of " +
            "expired operations against ordinary callsigns: cty.dat still lists K4A as Puerto " +
            "Rico because of a week in 2014, though a 1x1 callsign like K4A in any other year " +
            "is an ordinary mainland United States special-event call. A database entry is " +
            "evidence, not an answer - if you know it is the residue of an operation that had " +
            "ended, say so and answer accordingly.\n\n" +
            "Answer with ONE LINE PER NUMBER, nothing before them and nothing after:\n" +
            "<number>: LOG - <reason in at most fifteen words>\n" +
            "<number>: SUGGESTED - <reason in at most fifteen words>\n" +
            "<number>: NEITHER - <the country you believe it was, and why, at most fifteen words>\n" +
            "<number>: UNSURE - <what you would need to know, at most fifteen words>\n\n" +

            "LOG means your answer is the country already written in the log, so nothing should " +
            "be changed. SUGGESTED means your answer is the correction HolyLogger proposes. " +
            "NEITHER means you worked out a country and it is not either of them - name it. " +
            "UNSURE means you could not work out the country from the callsign, date and " +
            "time.\n\n" +

            "Rules:\n" +
            "- Answer every number you were given, in the same order, and no others.\n" +
            "- Take your time and reason it out properly before you write anything. This is not " +
            "a race: a wrong country goes into a log somebody has kept for thirty years.\n" +
            "- BEFORE YOU WRITE EACH LINE, READ YOUR OWN REASON BACK AND CHECK IT SUPPORTS YOUR " +
            "VERDICT. Answering SUGGESTED while your reason argues for the country in the log " +
            "is worse than useless - it is a wrong answer wearing the evidence for the right " +
            "one. If the reason and the verdict do not match, the reason is right: change the " +
            "verdict.\n" +
            "- If a date range is your evidence, check the QSO date really falls inside it " +
            "before you answer, and outside it if that is what you are arguing.\n" +
            "- Plain text. No markdown, no headings, no bullet characters.\n" +
            "- UNSURE and NEITHER are proper answers and cost nothing. A guess dressed as a " +
            "decision costs the operator a wrong country in a log he has kept for twenty " +
            "years.\n" +
            "- Your reason must name the EVIDENCE, not the conclusion: the prefix rule, the " +
            "licence, or the Club Log date range you used. Club Log holds SV9CUF/1 as Crete " +
            "from 1998 to 2004 is a reason; it is Crete is not.\n" +
            "- If you cannot name such a piece of evidence, the answer is UNSURE.\n" +
            "- Never invent an expedition, a licence or a date you do not actually know.";

        // ONE CONTACT'S WORTH OF QUESTION, as the Log Fixer knows it: the QSO itself, and the two
        // countries the screen is already showing above one another.
        internal sealed class Question
        {
            public QSO Qso;
            public string Logged;
            public string Suggested;
        }

        // WHAT IT VOTED FOR. Kept as what was said rather than as a decision already made: Backs is
        // the vote, Reason is its sentence, and the sentence is what the operator actually reads
        // before he trusts the vote.
        internal enum Backs { Nothing, Log, Suggested, Neither, Unsure }

        internal sealed class Answer
        {
            public Backs Backs;
            public string Reason;
        }

        // THE WHOLE ERRAND. Questions in, answers out, keyed by their place in the list that came in.
        // A number that never came back is simply absent - the caller leaves that row untouched, which
        // is the honest thing to do with a contact nobody answered about.
        //
        // `say` is how the window keeps the operator company: "request 2 of 5" is the difference
        // between a program working and a program hung.
        internal static async Task<Dictionary<int, Answer>> AskAsync(
            IList<Question> questions, Action<string> say, CancellationToken cancel,
            Action<int, Answer> answered = null)
        {
            var answers = new Dictionary<int, Answer>();
            if (questions == null || questions.Count == 0) return answers;

            int batches = (questions.Count + PerRequest - 1) / PerRequest;
            int settled = 0;

            for (int b = 0; b < batches; b++)
            {
                cancel.ThrowIfCancellationRequested();

                int from = b * PerRequest;
                int count = Math.Min(PerRequest, questions.Count - from);

                // "REQUEST 1 OF 1" TELLS HIM NOTHING HE WANTED TO KNOW. Ten contacts go in each
                // request, so most runs are a single one, and counting to one is a strange way of
                // saying how much work there is. When there is only the one, the number of contacts
                // is the figure worth showing; when there are several, the count of requests is.
                // NAMED, NOT "THE AI". Which model is answering is the difference between 5-1 and
                // 4-2 on the same six QSOs, and it is the thing he is choosing between - so the line
                // he watches for a minute or two says which one he is watching.
                string who = AiServices.Current.Model;

                // Marked for bold. The window turns **...** into bold text; anywhere else the
                // markers are simply part of a sentence nobody is reading with a magnifying glass.
                string whoBold = "**" + who + "**";

                if (say != null)
                    say(batches == 1
                        ? count + " QSO" + (count == 1 ? " is" : "s are") + " verified by " + whoBold
                        : "Asking " + whoBold + " - request " + (b + 1) + " of " + batches + "...");

                // EVERY ANSWER THE MOMENT IT IS WRITTEN, not the whole list at the end.
                //
                // The reply is one line per contact, so a finished line IS a finished verdict. Read
                // as they arrive, the first row can be coloured while the model is still writing
                // about the second - and the count below is real progress, made of answers, not a
                // clock counting seconds beside a window that will not move.
                Action<string> onLine = raw =>
                {
                    int index; Answer answer;
                    if (!ReadOne(raw, from, count, out index, out answer)) return;

                    // First answer for a number wins, exactly as when the reply is read whole.
                    if (answers.ContainsKey(index)) return;
                    answers[index] = answer;
                    settled++;

                    if (answered != null) answered(index, answer);
                    if (say != null)
                        say(whoBold + " has answered " + settled + " of " + questions.Count + "...");
                };

                string input = Describe(questions, from, count);

                // TIMED, BECAUSE "IT FELT SLOW" IS NOT EVIDENCE. The same question answered in two
                // seconds in a browser and seemed to take twenty here - and between the answer
                // arriving and the operator seeing it, this program also writes a report of several
                // hundred kilobytes. One of those is the wait; guessing which has cost enough time.
                var clock = System.Diagnostics.Stopwatch.StartNew();

                string reply = await AiQsoCheck.AskAsync(SystemPrompt, input, cancel, say, onLine)
                                               .ConfigureAwait(false);

                Log.Warn("AI country vote: the service answered in " + clock.ElapsedMilliseconds
                         + " ms (" + count + " QSO(s), question " + input.Length + " chars)");

                // AND READ WHOLE AFTERWARDS AS WELL. A line the stream broke in a place nobody
                // expected, or an answer the model wrote without a newline after it, is picked up
                // here - and anything already settled above is left alone, so nothing is counted or
                // painted twice.
                Read(reply, from, count, answers);

                // FEWER ANSWERS THAN CONTACTS, AND NOTHING TO SHOW FOR IT.
                //
                // A model that stops after three of six is not an error - the request succeeded, the
                // words arrived, and the log stays silent. So the operator is told three came back
                // unanswered and there is no way on earth to find out why: what it wrote is not kept
                // anywhere. The reply goes in the log when it falls short, and only then, because it
                // is the one case where the answer itself is the evidence.
                int got = 0;
                for (int i = 0; i < count; i++) if (answers.ContainsKey(from + i)) got++;

                if (got < count)
                    Log.Warn("AI country vote: asked about " + count + " contact(s) and got " + got
                             + " answer(s). The reply as it arrived: " + Cut(reply));
            }

            return answers;
        }

        // THE BATCH IN WORDS. Numbered from one WITHIN the batch, because a model asked to answer
        // "37 to 46" answers 1 to 10 often enough to matter; the offset is put back on this side,
        // where it cannot be got wrong.
        private static string Describe(IList<Question> questions, int from, int count)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Here are " + count + " contacts. Answer each by its number.");

            for (int i = 0; i < count; i++)
            {
                Question q = questions[from + i];
                QSO qso = q.Qso;

                sb.AppendLine();
                sb.AppendLine((i + 1) + ".");
                sb.AppendLine("- Callsign: " + Text(qso.DXCall));
                sb.AppendLine("- Worked on: " + Text(qso.Date) + " at " + Text(qso.Time) + " UTC");
                // NO BAND, NO MODE, NO GRID, NO ZONES. They were typed by somebody years ago and
                // are no more trustworthy than the country being questioned; none of them bears on
                // which country a callsign was in anyway. Sending them would only invite a log that
                // is wrong twice to vote for itself.
                sb.AppendLine("- The country written in the log: " + Text(q.Logged));
                sb.AppendLine("- The country HolyLogger proposes: " + Text(q.Suggested));

                foreach (string line in Databases(qso)) sb.AppendLine(line);
            }

            return sb.ToString();
        }

        // EVERYTHING THE PROGRAM KNOWS ABOUT THIS CALLSIGN, NOT ITS CONCLUSIONS.
        //
        // This used to send two sentences - "cty.dat matched CQ8 which is Azores", "Club Log matched
        // CQ which is Portugal" - and keep the rest to itself. The rest is the argument. HolyLogger
        // holds, for every callsign, the longer prefix Club Log DOES have and the exact dates it
        // applied between; it knew that CQ8 means Azores only from 2009 and never said so, while
        // asking an AI about a 1991 QSO to work that out from memory.
        //
        // It could not, and it showed: across six runs of the same six QSOs, five rows came back
        // identical every time and CQ8M moved four different ways - twice a guess one way, twice the
        // other, twice an honest "I would need the 1991 licence". The one row we withheld the
        // evidence for was the only unstable row in the set.
        //
        // So everything goes. Whether a record is against this EXACT callsign or merely its prefix,
        // whether it is a historic record, the near-miss prefix with its from and to dates, and the
        // entity numbers - all of it in plain lines the model can compare with the QSO's own date
        // instead of remembering.
        private static IEnumerable<string> Databases(QSO q)
        {
            var lines = new List<string>();
            string call = Text(q.DXCall);
            if (call.Length == 0) return lines;

            CountryLookup.Explanation x;
            try
            {
                x = CountryLookup.Shared.Explain(call, CountryLookup.QsoDate(q.Date, q.Time));
            }
            catch (Exception swallowed)
            {
                // One contact short of its paragraph is still a contact worth asking about.
                Log.Swallow(swallowed);
                return lines;
            }
            if (x == null) return lines;

            lines.Add("- cty.dat says: " + x.CtySays);
            lines.Add("- Club Log says: " + x.ClubSays);

            // HOW MUCH OF THE CALLSIGN EACH ONE RECOGNISED. "Club Log matched CQ" and "cty.dat
            // matched CQ8" are two different strengths of answer, and which of them matched more of
            // the callsign is often the whole of the argument.
            if (Text(x.CtyMatched).Length > 0)
                lines.Add("- cty.dat matched the prefix: " + Text(x.CtyMatched)
                          + (x.CtyCode > 0 ? "  (entity " + x.CtyCode + ")" : string.Empty));

            if (Text(x.ClubMatched).Length > 0)
                lines.Add("- Club Log matched: " + Text(x.ClubMatched)
                          + (x.ClubCode > 0 ? "  (entity " + x.ClubCode + ")" : string.Empty));

            // A RECORD AGAINST THIS VERY CALLSIGN beats one read off a prefix, and the model cannot
            // tell the two apart unless it is told which it has.
            if (x.ClubExactCall)
                lines.Add("- Club Log holds a record for this EXACT callsign, not merely its prefix.");

            if (x.ClubHistoric)
                lines.Add("- Club Log's record for it is a historic one, tied to dates.");

            // THE FACT THAT WAS BEING WITHHELD. Club Log holds a longer prefix that did NOT apply on
            // this QSO's date - which is precisely why its answer came back shorter than cty.dat's.
            // Without the dates the model is left to remember when a prefix changed hands; with them
            // there is nothing to remember.
            if (Text(x.ClubNearKey).Length > 0)
            {
                string from = x.ClubNearFrom == DateTime.MinValue
                    ? "the beginning"
                    : x.ClubNearFrom.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

                string to = x.ClubNearTo == DateTime.MaxValue || x.ClubNearTo == DateTime.MinValue
                    ? "now"
                    : x.ClubNearTo.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

                lines.Add("- Club Log ALSO holds " + Text(x.ClubNearKey) + " as "
                          + Text(x.ClubNearName)
                          + (x.ClubNearCode > 0 ? " (entity " + x.ClubNearCode + ")" : string.Empty)
                          + ", but only from " + from + " to " + to
                          + " - which does NOT cover this contact's date.");
            }

            foreach (string note in x.ExtraNotes)
                lines.Add("- Club Log note: " + note.Replace("**", string.Empty));

            return lines;
        }

        // READING THE REPLY. Anything that is not a numbered line beginning with one of the three
        // words is ignored rather than argued with - a model that opens with "Sure, here you go" has
        // not answered anything, and the line is no loss.
        private static readonly Regex Line = new Regex(
            @"^\s*(\d{1,3})\s*[:.)\-]\s*(LOG|SUGGESTED|NEITHER|UNSURE)\b\s*[-:,]?\s*(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void Read(string reply, int from, int count, Dictionary<int, Answer> into)
        {
            if (string.IsNullOrWhiteSpace(reply)) return;

            foreach (string raw in reply.Replace("\r\n", "\n").Split('\n'))
            {
                int index; Answer answer;
                if (!ReadOne(raw, from, count, out index, out answer)) continue;

                // First answer for a number wins. A model that answers the same number twice has
                // changed its mind mid-list, and the second thought is not better evidence.
                if (!into.ContainsKey(index)) into[index] = answer;
            }
        }

        // ONE LINE, ON ITS OWN. Split out of the loop above so a line arriving from a stream can be
        // read the instant it lands, by exactly the same rules as one read afterwards out of the
        // finished reply. Two readers would be two chances to disagree about what an answer is.
        internal static bool ReadOne(string raw, int from, int count, out int index, out Answer answer)
        {
            index = -1;
            answer = null;

            Match m = Line.Match(raw ?? string.Empty);
            if (!m.Success) return false;

            int n;
            if (!int.TryParse(m.Groups[1].Value, out n)) return false;

            // Numbered within the batch, so anything outside 1..count is the model losing count
            // rather than an answer about a contact we asked about.
            if (n < 1 || n > count) return false;

            string word = m.Groups[2].Value.ToUpperInvariant();
            answer = new Answer
            {
                // NEITHER IS ITS OWN ANSWER, not a kind of doubt. The AI worked the country out and
                // it is not the one in the log NOR the one HolyLogger proposes - which is the single
                // most useful thing it can say, and the prompt before today forbade it outright.
                Backs = word == "LOG" ? Backs.Log
                      : word == "SUGGESTED" ? Backs.Suggested
                      : word == "NEITHER" ? Backs.Neither
                      : Backs.Unsure,
                Reason = m.Groups[3].Value.Trim()
            };
            index = from + n - 1;
            return true;
        }

        // Enough of the reply to see what shape it was in, not so much that one bad run fills the
        // log file. Newlines flattened so the whole thing stays on one line where it can be found.
        private static string Cut(string reply)
        {
            string one = (reply ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (one.Length == 0) return "(nothing at all)";
            return one.Length <= 2000 ? one : one.Substring(0, 2000) + " ...(cut)";
        }

        private static string Text(string s) { return (s ?? string.Empty).Trim(); }
    }
}
