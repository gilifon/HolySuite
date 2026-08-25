using System;
using System.Collections.Generic;
using System.Globalization;
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

        // Where the key comes from, and how the window explains getting one.
        public string KeyPageUrl;
        public string KeyPageText;
        public string[] Steps;

        // What it costs, in one line, under the steps.
        public string Price;

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
                      + "out it starts again tomorrow. Nothing is ever charged.",
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
            },

            new AiService
            {
                Name = OpenRouter,
                Label = "OpenRouter - free models, or paid for GPT, Claude and others",
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
                // FREE, AND NAMED. A paid model here would have refused outright for anyone with no
                // credit on the account, which is most people trying this - so the pinned model is
                // one of the free ones. Checked against openrouter.ai/api/v1/models on 2026-08-25.
                DefaultModel = "nvidia/nemotron-3-ultra-550b-a55b:free",
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
                Price = "Free to begin with: on an account with no credit it uses the free models, "
                      + "which cost nothing and have a daily cap of their own. Add credit and it "
                      + "reaches the paid models too - prepaid, so nothing can ever be charged "
                      + "beyond what you put on, and a limit on the key caps it further.",
                Steps = new[]
                {
                    "Open openrouter.ai and make an account.",
                    "Open the key page below and press Create Key.",
                    "Copy the key and paste it in the box. That is enough to start - with no credit "
                    + "on the account it uses the free models, and no card is asked for.",
                    "Only if you want the paid models: add some credit, and give the key a credit "
                    + "limit while you are there. That limit is the most it can ever spend, and this "
                    + "window shows you what is left of it.",
                    "Then name the model in Options - AI Service. anthropic/claude-opus-5 is one of "
                    + "the strongest; anthropic/claude-sonnet-5 costs about half. A question about "
                    + "six QSOs runs to a penny or two.",
                    "An existing ChatGPT or Claude SUBSCRIPTION cannot be used here - those are "
                    + "separate accounts and buy nothing outside their own websites."
                },
                AllowanceUrl = "https://openrouter.ai/api/v1/key",
                TopUpUrl = "https://openrouter.ai/credits",
                TopUpText = "openrouter.ai/credits",
                ReadKey = () => Properties.Settings.Default.AiApiKeyOpenRouter,
                WriteKey = v => Properties.Settings.Default.AiApiKeyOpenRouter = v,
                ReadModel = () => Properties.Settings.Default.AiModelOpenRouter,
                WriteModel = v => Properties.Settings.Default.AiModelOpenRouter = v
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
                        return "Credit left on this key: " + Money(left.Value)
                             + " of " + Money(cap.Value) + ".";

                    // No cap on the key. Saying only what has been spent would read as reassurance;
                    // what he needs to know is that nothing here stops at a number.
                    if (spent.HasValue)
                        return "Spent through this key so far: " + Money(spent.Value)
                             + ". No limit is set on the key, so it can spend whatever credit is on "
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
