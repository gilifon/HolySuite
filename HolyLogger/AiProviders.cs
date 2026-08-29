using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HolyLogger
{
    // WHICH AI, AND WHAT IT COSTS THE OPERATOR TO GET AT IT.
    //
    // The feature began with one service because one was enough to build it. Then the free allowance
    // turned out to be twenty checks a day, and twenty is not a log's worth - a hundred and thirty
    // findings would eat a week of it. So the service became a choice.
    //
    // WHAT IS NOT HERE, and deliberately: one service per company. Writing a request shape, a reply
    // shape and an error shape for Claude, and again for OpenAI, and again for the next one, is a
    // debt that comes due every time one of them changes something. OpenRouter is one account and
    // one key that reaches all of them, so the second entry below buys the whole field for the cost
    // of one.
    //
    // AND THE THING EVERY OPERATOR GETS WRONG: a ChatGPT or Claude subscription is not an API key.
    // They are separate accounts with separate billing, and twenty pounds a month for the website
    // buys nothing here. That sentence is in the guidance for every paid service, because without it
    // the first thing a subscriber does is paste his password and wonder why it fails.
    internal sealed class AiService
    {
        // Stored in the settings file, so it must never be reworded - the label is what changes.
        public string Name;

        // What the operator picks from. Says the price in the same breath as the name, because for
        // most people the price IS the choice.
        public string Label;

        // The same service named inside a sentence, where the price and the dashes of the label
        // would read as noise.
        public string ShortName;

        public string Endpoint;
        public string DefaultModel;

        // TWO SHAPES, AND ONLY TWO. Google speaks its own; everyone else has settled on the shape
        // OpenAI started, which is what OpenRouter serves. A third would need a third branch, and
        // that is the cost this file exists to avoid paying twice.
        public bool ChatShape;

        // Bearer for everyone but Google, which wants its own header.
        public bool Bearer;

        // THE MODELS THIS SERVICE IS KNOWN TO ANSWER WITH, WRITTEN OUT.
        //
        // The model box was a blank line under the words "leave empty for nvidia/nemotron...". An
        // operator who wanted a better answer had to know that names like anthropic/claude-opus-5
        // exist, that they belong in that box, and that they are spelled exactly so - three things
        // the program knew and did not say. He was being asked to guess.
        //
        // So the box offers the names, each with what it costs, and still takes anything typed by
        // hand for a model that is not on the list. Every entry is "id|what it is".
        public string[] ModelChoices;

        // Where the key comes from, and how the window explains getting one.
        public string KeyPageUrl;
        public string KeyPageText;
        public string[] Steps;

        // What it costs, in one line, under the steps.
        public string Price;

        // WHERE THE SERVICE PUBLISHES THE MODELS IT ACTUALLY HAS TODAY. Empty for a service with no
        // such address, and then the written-in list is all there is.
        public string ModelsUrl;

        // True when that address needs the operator's key. OpenRouter's is open to anybody; Google's
        // is not, so it can only be asked once a key has been pasted.
        public bool ModelsUrlNeedsKey;

        // WHERE THE ACCOUNT CAN BE ASKED WHAT IS LEFT, and where more is put on. Empty for a service
        // that has nothing to spend - the free one's allowance is counted by the calendar, not by
        // money, and there is nothing to show him but a number he cannot act on.
        public string AllowanceUrl;
        public string TopUpUrl;
        public string TopUpText;

        // The key and the model live in settings under their own names, so switching services and
        // back does not make anyone paste a key again.
        public Func<string> ReadKey;
        public Action<string> WriteKey;
        public Func<string> ReadModel;
        public Action<string> WriteModel;

        public string Key { get { return (ReadKey() ?? string.Empty).Trim(); } }

        public string Model
        {
            get
            {
                // A MODEL NAME THAT CAN BE CORRECTED WITHOUT A NEW BUILD. Model names are retired on
                // somebody else's schedule, and when one goes the feature stops working for everybody
                // until the next release. Kept in settings, a wrong name is a line in a file.
                string chosen = (ReadModel() ?? string.Empty).Trim();
                return chosen.Length > 0 ? chosen : DefaultModel;
            }
        }
    }

    internal static class AiServices
    {
        internal const string Gemini = "Gemini";
        internal const string OpenRouter = "OpenRouter";

        internal static readonly AiService[] All =
        {
            new AiService
            {
                Name = OpenRouter,
                Label = "OpenRouter - paid, reaches GPT, Claude and Gemini",
                ShortName = "OpenRouter",
                Endpoint = "https://openrouter.ai/api/v1/chat/completions",
                // A NAMED MODEL, NOT "auto". The chooser was picked to survive model names being
                // retired on somebody else's schedule - a fair worry, and it cost more than it
                // saved: auto picks a DIFFERENT model per request, so two runs over the same QSOs
                // were two different AIs and could not be compared. The same six came back 5-1-0,
                // then 4-2-0, then 3-2-1, and the model was one of the reasons why.
                //
                // A name that is retired is still a line in the settings file, which is what
                // AiModelOpenRouter is for - the operator can put a working one in without waiting
                // for a new build. That was always the real answer to the worry.
                // THE DEFAULT IS A PAID ONE NOW. It was a free model, chosen so that a key with no
                // credit still answered - but the free ones answered worse, and a wrong country is
                // not a bargain. Anyone who does not want to pay has Google Gemini and its own free
                // allowance; anyone here has already put credit on the account.
                DefaultModel = "anthropic/claude-sonnet-5",
                ChatShape = true,
                Bearer = true,
                KeyPageUrl = "https://openrouter.ai/keys",
                KeyPageText = "openrouter.ai/keys",
                // WHAT IT ACTUALLY COSTS, WHICH IS NOT WHAT THIS SAID. It read "paid, per check ...
                // there is no free allowance to fall back on", and then an operator with no credit
                // and no card on the account got answer after answer. He had not been charged and
                // could not be: with an empty balance the service uses its FREE models, which have a
                // daily cap instead of a price. A description that sends somebody to fetch a credit
                // card he does not need is worse than no description.
                Price = "Paid, and prepaid: the credit goes on the account first and nothing can ever "
                      + "be charged beyond it. A run over a few QSOs costs a few cents. Put a limit "
                      + "on the API key as well and that limit is the most it can spend, whatever "
                      + "happens here.",
                // THE CREDIT COMES FIRST. This service is the only one offered now, and it answers
                // nothing at all on an empty account - so "add credit" is step two, not an optional
                // step five for people who want something better. The order here is the order an
                // operator has to do it in, and nothing is listed before it is needed.
                Steps = new[]
                {
                    "Open openrouter.ai and make an account.",
                    "Put credit on the account: Credits, then Add Credits. Five dollars is plenty - "
                    + "a question about six QSOs costs a few cents.",
                    "It is prepaid, so that sum is the ceiling. Leave Auto Top-Up alone and nothing "
                    + "can ever be charged beyond what you put on.",
                    "Open the API key page below and press + New Key.",
                    "Give the API key a credit limit while you are there, if you want a smaller ceiling "
                    + "still. This window shows you what is left of it.",
                    "Copy the API key and paste it in the box, then choose a model below.",
                    "An existing ChatGPT or Claude SUBSCRIPTION cannot be used here - those are "
                    + "separate accounts and buy nothing outside their own websites."
                },
                ModelsUrl = "https://openrouter.ai/api/v1/models",
                ModelsUrlNeedsKey = false,
                AllowanceUrl = "https://openrouter.ai/api/v1/key",
                TopUpUrl = "https://openrouter.ai/credits",
                TopUpText = "openrouter.ai/credits",
                ReadKey = () => Properties.Settings.Default.AiApiKeyOpenRouter,
                WriteKey = v => Properties.Settings.Default.AiApiKeyOpenRouter = v,
                // NO FREE MODELS HERE, AND THE REASON IS MEASURED. Put the same six QSOs to all of
                // them: the four paid models came back with the same verdict on every row and the
                // same rule quoted for it, while the free ones disagreed on two rows each. Offering
                // a model that answers worse, beside better ones, is offering somebody a way to be
                // told the wrong country about his own log.
                //
                // Nobody is shut out - Google Gemini is still there with its own free allowance -
                // and any name at all can still be typed into the box by hand.
                ModelChoices = new[]
                {
                    "anthropic/claude-opus-5|Anthropic, the dearest of these",
                    "anthropic/claude-sonnet-5|Anthropic, cheaper than Opus",
                    "openai/gpt-5.2|OpenAI",
                    "google/gemini-3.7-flash|Google, no daily allowance to run out",
                },
                ReadModel = () => Properties.Settings.Default.AiModelOpenRouter,
                WriteModel = v => Properties.Settings.Default.AiModelOpenRouter = v
            },

            // ── AND A WAY IN WITH NO CREDIT CARD ────────────────────────────────────────────────
            //
            // Taken out once, and put back on 2026-08-29 for the reason it was written in the first
            // place: an operator who will not put money on an account is otherwise offered nothing at
            // all, and nothing at all is worse than a small allowance.
            //
            // Both objections stand and are said out loud rather than hidden: the free allowance is
            // about twenty checks a day, which one real log uses up, and on the same six QSOs it did
            // not agree with the paid models that agreed with each other. It is second in the list,
            // and its own words say what it is. What it is not is a locked door.
            new AiService
            {
                Name = Gemini,
                Label = "Google Gemini - free, no credit card",
                ShortName = "Google Gemini",
                Endpoint = "https://generativelanguage.googleapis.com/v1beta/interactions",
                DefaultModel = "gemini-3.7-flash",
                ChatShape = false,
                Bearer = false,
                KeyPageUrl = "https://aistudio.google.com/apikey",
                KeyPageText = "aistudio.google.com/apikey",
                Price = "Free. The allowance is small - around twenty checks a day - and when it runs "
                      + "out it starts again tomorrow. Nothing is ever charged. On the same QSOs it "
                      + "does not always agree with the paid models, so on a country you care about "
                      + "it is worth asking one of those as well.",
                Steps = new[]
                {
                    "Sign in with any Google account - a Gmail address is one.",
                    "Open the key page below and press Create API key.",
                    "Copy the key it shows you and paste it in the box.",
                    "No credit card is asked for at any point."
                },
                ReadKey = () => Properties.Settings.Default.AiApiKey,
                WriteKey = v => Properties.Settings.Default.AiApiKey = v,
                ReadModel = () => Properties.Settings.Default.AiModelGemini,
                WriteModel = v => Properties.Settings.Default.AiModelGemini = v
            }
        };

        // THE ONE IN USE. An unknown name in the settings file - an older version's, a typed mistake -
        // falls back to the free one rather than to nothing, because a program that cannot name its
        // service simply stops working and says nothing useful about why.
        internal static AiService Current
        {
            get
            {
                string want = (Properties.Settings.Default.AiProvider ?? string.Empty).Trim();
                AiService found = All.FirstOrDefault(
                    s => string.Equals(s.Name, want, StringComparison.OrdinalIgnoreCase));
                return found ?? All[0];
            }
        }

        // THE OTHER SERVICES THAT ALREADY HAVE A KEY, named for the middle of a sentence.
        //
        // Each service keeps its own key, which is what lets an operator try the paid one for an
        // afternoon and go back without pasting anything again - but it also means that picking a
        // different service in the list leaves yesterday's key saved and unused. Told flatly that
        // there is "no key", he will believe the program has lost the one he remembers pasting. So
        // whoever says that can name the ones that do have one, and he can see it was not lost.
        internal static string WithKeysExcept(AiService current)
        {
            var names = All.Where(s => !ReferenceEquals(s, current) && s.Key.Length > 0)
                           .Select(s => s.ShortName ?? s.Name)
                           .ToList();

            if (names.Count == 0) return string.Empty;
            if (names.Count == 1) return names[0];
            return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
        }

        internal static void Choose(string name)
        {
            if (All.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Properties.Settings.Default.AiProvider = name;
                Properties.Settings.Default.Save();
            }
        }

        // The whole registration story for one service, as the window shows it: numbered steps, then
        // what it costs. The link is added by the window, which is the only place that can make one
        // clickable.
        internal static IEnumerable<string> Guidance(AiService service)
        {
            if (service == null) yield break;
            int n = 1;
            foreach (string step in service.Steps) yield return (n++) + ". " + step;
        }
    }

    // THE MODELS A SERVICE REALLY HAS TODAY, NOT THE ONES IT HAD WHEN THIS WAS BUILT.
    //
    // A list written into the program is right on the day it ships and wrong ever after: models are
    // retired on somebody else's schedule, and the good one released next month is one this program
    // would never mention. An operator installing a two-year-old copy would be choosing from a menu
    // of ghosts.
    //
    // So the list is fetched, exactly the way cty.dat and the Club Log database already are: kept in
    // the folder beside them, stamped with the day it was taken, refreshed at most once a day, and
    // fallen back on when there is no answer. What is written into the program becomes the last
    // resort rather than the only truth.
    internal static class AiModelList
    {
        private static readonly HttpClient Http =
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // Beside cty.dat and clublog_cty.xml, for the reason they are there: it is the folder that
        // survives an update and belongs to this operator.
        private static string FileFor(AiService service)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "4Z1KD", "HolyLogger");
            Directory.CreateDirectory(folder);

            string name = "ai_models_" + (service.Name ?? "service").ToLowerInvariant() + ".txt";
            return Path.Combine(folder, name);
        }

        private static string StampFor(AiService service) { return FileFor(service) + ".stamp"; }

        // ONCE A DAY IS OFTEN ENOUGH. Models do not come and go by the hour, and an operator who
        // opens Options five times in an evening should not fetch the list five times.
        private static bool WorthRefreshing(AiService service)
        {
            try
            {
                string stamp = StampFor(service);
                if (!File.Exists(stamp)) return true;

                DateTime taken;
                if (!DateTime.TryParse(File.ReadAllText(stamp).Trim(),
                                       CultureInfo.InvariantCulture,
                                       DateTimeStyles.RoundtripKind, out taken)) return true;

                return (DateTime.UtcNow - taken) > TimeSpan.FromDays(1);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return true; }
        }

        /// <summary>
        /// The choices to offer for this service: today's list from the service when one can be had,
        /// yesterday's from disk when it cannot, and the built-in list when there is neither. Each
        /// entry is "id|what it is", the same shape as AiService.ModelChoices.
        /// </summary>
        internal static async Task<string[]> ChoicesAsync(AiService service)
        {
            if (service == null) return new string[0];

            try
            {
                if (WorthRefreshing(service))
                {
                    string[] fetched = await FetchAsync(service).ConfigureAwait(false);
                    fetched = KeepTheShortList(service, fetched);

                    if (fetched != null && fetched.Length > 0)
                    {
                        Save(service, fetched);
                        return fetched;
                    }
                }

                string[] kept = Read(service);
                if (kept != null && kept.Length > 0) return kept;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return service.ModelChoices ?? new string[0];
        }

        // A SHORT LIST, CHECKED AGAINST WHAT REALLY EXISTS.
        //
        // The service publishes hundreds of models, and hundreds is not a choice - it is a wall an
        // operator walks away from. So the names offered are the few worth offering, written down
        // here, and the fetched list is used to CHECK them rather than to replace them: a name that
        // has been retired quietly disappears, and the price beside each one is today's price rather
        // than whatever was true when this was built.
        //
        // The result is a list that is both short and never out of date - which is the whole reason
        // the fetch exists.
        //
        // Anything typed by hand still works. This governs what is offered, not what is allowed.
        private static string[] KeepTheShortList(AiService service, string[] fetched)
        {
            if (fetched == null || fetched.Length == 0) return fetched;

            var wanted = new List<string>();
            foreach (string choice in service.ModelChoices ?? new string[0])
            {
                int bar = choice.IndexOf('|');
                wanted.Add((bar < 0 ? choice : choice.Substring(0, bar)).Trim());
            }
            if (wanted.Count == 0) return fetched;

            // Kept in the order they are written above, not the order the service happened to send
            // them: free first, then paid, cheapest decision at the top.
            var kept = new List<string>();
            foreach (string want in wanted)
            {
                foreach (string live in fetched)
                {
                    int bar = live.IndexOf('|');
                    string id = (bar < 0 ? live : live.Substring(0, bar)).Trim();

                    if (!string.Equals(id, want, StringComparison.OrdinalIgnoreCase)) continue;

                    kept.Add(live);
                    break;
                }
            }

            // Every one of them gone is not a shortened list, it is a broken one - and then the
            // whole live list is better than nothing.
            return kept.Count > 0 ? kept.ToArray() : fetched;
        }

        private static async Task<string[]> FetchAsync(AiService service)
        {
            string url = (service.ModelsUrl ?? string.Empty).Trim();
            if (url.Length == 0) return null;

            string key = service.Key;
            if (service.ModelsUrlNeedsKey && key.Length == 0) return null;

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (service.ModelsUrlNeedsKey)
                {
                    if (service.Bearer)
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    else
                        request.Headers.Add("x-goog-api-key", key);
                }

                HttpResponseMessage reply = await Http.SendAsync(request).ConfigureAwait(false);
                if (!reply.IsSuccessStatusCode)
                {
                    Log.Warn("AI models list: " + service.Name + " answered " + (int)reply.StatusCode);
                    return null;
                }

                string text = await reply.Content.ReadAsStringAsync().ConfigureAwait(false);
                return service.ChatShape ? ReadOpenRouter(text) : ReadGemini(text);
            }
        }

        // FREE ONES FIRST. They are what most operators want and what the program starts on; a paid
        // model is a decision, and a decision belongs below the thing that costs nothing.
        private static string[] ReadOpenRouter(string json)
        {
            var free = new List<string>();
            var paid = new List<string>();

            var all = JObject.Parse(json)["data"] as JArray;
            foreach (JToken m in all ?? new JArray())
            {
                string id = (string)m["id"];
                if (string.IsNullOrWhiteSpace(id)) continue;

                double inPrice = Price(m["pricing"], "prompt");
                double outPrice = Price(m["pricing"], "completion");

                if (inPrice <= 0 && outPrice <= 0)
                {
                    free.Add(id + "|free");
                    continue;
                }

                // WHAT ONE QUESTION COSTS, not dollars per million tokens - an operator can decide
                // on "about 10 cents" where "$5 per million tokens" tells him nothing at all.
                //
                // THE OUTPUT FIGURE INCLUDES THINKING, and thinking is most of it. The first guess
                // here was 300 output tokens, from counting the six short lines the model writes,
                // and it was wrong by a factor of ten: four real runs cost $0.49, about 12 cents
                // each, because the program asks for thinking_level "high" and every thinking
                // token is billed as output. Measured, not imagined.
                double run = (inPrice * 3000.0) + (outPrice * 4500.0);
                paid.Add(id + "|paid, about " + Money(run) + " a run");
            }

            free.Sort(StringComparer.OrdinalIgnoreCase);
            paid.Sort(StringComparer.OrdinalIgnoreCase);

            free.AddRange(paid);
            return free.ToArray();
        }

        private static double Price(JToken pricing, string field)
        {
            try
            {
                if (pricing == null) return 0;

                JToken v = pricing[field];
                if (v == null || v.Type == JTokenType.Null) return 0;

                double parsed;
                return double.TryParse((string)v ?? "0", NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return 0; }
        }

        private static string Money(double run)
        {
            if (run <= 0) return "nothing";
            if (run < 0.01) return "under a cent";
            if (run < 1.0) return Math.Round(run * 100).ToString(CultureInfo.InvariantCulture) + " cents";
            return "$" + run.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // Gemini answers with names carrying a "models/" prefix that the request itself must not
        // have, so it is taken off here rather than by whoever reads the list.
        private static string[] ReadGemini(string json)
        {
            var found = new List<string>();

            var all = JObject.Parse(json)["models"] as JArray;
            foreach (JToken m in all ?? new JArray())
            {
                string name = (string)m["name"];
                if (string.IsNullOrWhiteSpace(name)) continue;

                int slash = name.LastIndexOf('/');
                string id = slash >= 0 ? name.Substring(slash + 1) : name;

                string what = (string)m["displayName"];
                found.Add(id + (string.IsNullOrWhiteSpace(what) ? string.Empty : "|" + what));
            }

            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found.ToArray();
        }

        private static void Save(AiService service, string[] choices)
        {
            try
            {
                File.WriteAllLines(FileFor(service), choices);
                File.WriteAllText(StampFor(service),
                                  DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static string[] Read(AiService service)
        {
            try
            {
                string file = FileFor(service);
                return File.Exists(file) ? File.ReadAllLines(file) : null;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }
    }

    // WHAT IS LEFT TO SPEND, ASKED OF THE ACCOUNT ITSELF.
    //
    // The one worry a paid service gives an operator is the one no error message answers in time:
    // how much of my money has this used. The account will say - a paid service keeps a running
    // total and will hand it over for the asking - so it is asked, and the answer is put where he is
    // already looking, under the name of the service.
    //
    // A LIMIT ON THE KEY IS THE FIXED PRICE. Without one the key can spend whatever credit sits on
    // the account; with one it cannot spend past it, whatever this program does. The sentence below
    // says which of the two he has, because that is the whole difference between knowing the worst
    // case and hoping about it.
    //
    // SILENT WHEN IT CANNOT BE SURE. Every failure here - no answer, a shape that was not expected,
    // a service with nothing to report - returns an empty string, and the window shows no line at
    // all. A wrong number about somebody's money is worse than no number.
    internal static class AiAllowance
    {
        // Its own client, and a short fuse: this is a courtesy line under a heading, and a window
        // must never sit waiting on it.
        private static readonly HttpClient Http =
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        internal static async Task<string> DescribeAsync(AiService service)
        {
            if (service == null) return string.Empty;

            string url = (service.AllowanceUrl ?? string.Empty).Trim();
            string key = service.Key;
            if (url.Length == 0 || key.Length == 0) return string.Empty;

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

                    HttpResponseMessage reply = await Http.SendAsync(request).ConfigureAwait(false);
                    string text = await reply.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!reply.IsSuccessStatusCode)
                    {
                        Log.Warn("AI allowance: the service answered " + (int)reply.StatusCode);
                        return string.Empty;
                    }

                    JToken data = JObject.Parse(text)["data"];
                    if (data == null) return string.Empty;

                    double? cap = Amount(data["limit"]);
                    double? left = Amount(data["limit_remaining"]);
                    double? spent = Amount(data["usage"]);

                    // THE CAP HE SET, AND WHAT IS LEFT OF IT. This is the case worth having: a
                    // number that cannot be exceeded, and how much of it is still his.
                    if (cap.HasValue && left.HasValue)
                        return "Credit left on this API key: " + Money(left.Value)
                             + " of " + Money(cap.Value) + ".";

                    // No cap on the key. Saying only what has been spent would read as reassurance;
                    // what he needs to know is that nothing here stops at a number.
                    if (spent.HasValue)
                        return "Spent through this API key so far: " + Money(spent.Value)
                             + ". No limit is set on the API key, so it can spend whatever credit is on "
                             + "the account.";

                    return string.Empty;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return string.Empty; }
        }

        // Null when the field is absent or null - which is exactly what an account with no limit set
        // sends back, and must not be read as a limit of zero.
        private static double? Amount(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return null;
            try { return value.Value<double>(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // A SUM OF MONEY THE WAY A PERSON WRITES ONE. Two decimals, and a dot for the decimal point
        // whatever the machine's own habit is - these come back from the service as plain numbers.
        // A real amount smaller than a cent is called that rather than rounded away to "$0.00",
        // which would read as nothing left.
        private static string Money(double value)
        {
            if (value > 0 && value < 0.005) return "under $0.01";
            return "$" + value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
