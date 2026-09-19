using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace HolyLogger.Contests
{
    // The commands that put one radio model into the state a simplex FM contest (Sukkot) is worked in:
    // VFO mode, simplex, no tone, no tone squelch. OmniRig does frequency and mode for every radio, but
    // not these, so each radio needs its own raw CAT commands - sent as OmniRig custom commands.
    //
    // Radio is OmniRig's own name for the radio (its RigType, the .ini file name: "ID-5100A"), because
    // that is what the connected rig is matched against. A command is written the way the Channels and
    // CW code already send them: hex bytes with spaces for Icom ("FE FE 8C E0 07 FD"), plain text for
    // the others ("VX0;"). An empty command is simply not sent.
    //
    // No Tone and No TSQL are kept apart even though on the ID-5100 they are one command: another radio
    // may need two. When the two are the same it is sent once.
    public class RadioCommandSet
    {
        [JsonProperty("radio")] public string Radio { get; set; } = "";
        [JsonProperty("vfo")] public string Vfo { get; set; } = "";
        [JsonProperty("simplex")] public string Simplex { get; set; } = "";
        [JsonProperty("no_tone")] public string NoTone { get; set; } = "";
        [JsonProperty("no_tsql")] public string NoTsql { get; set; } = "";

        [JsonIgnore]
        public bool IsEmpty => string.IsNullOrWhiteSpace(Radio) && string.IsNullOrWhiteSpace(Vfo)
                               && string.IsNullOrWhiteSpace(Simplex) && string.IsNullOrWhiteSpace(NoTone)
                               && string.IsNullOrWhiteSpace(NoTsql);

        // The commands to send, in order, with the empty ones left out and No TSQL dropped when it is the
        // same command as No Tone.
        public List<string> CommandsToSend()
        {
            var list = new List<string>();
            foreach (string c in new[] { Vfo, Simplex, NoTone })
                if (!string.IsNullOrWhiteSpace(c)) list.Add(c.Trim());
            if (!string.IsNullOrWhiteSpace(NoTsql) && !SameCommand(NoTsql, NoTone)) list.Add(NoTsql.Trim());
            return list;
        }

        private static bool SameCommand(string a, string b)
            => string.Equals(Squash(a), Squash(b), StringComparison.OrdinalIgnoreCase);

        private static string Squash(string s) => new string((s ?? "").Where(ch => !char.IsWhiteSpace(ch)).ToArray());
    }

    // What the file holds: the contest the commands are for, and the radios' commands.
    public class ContestRadioSetup
    {
        [JsonProperty("contest_id")] public string ContestId { get; set; }
        [JsonProperty("radios")] public List<RadioCommandSet> Radios { get; set; } = new List<RadioCommandSet>();
    }

    public static class ContestRadioCommands
    {
        // Its own file rather than a setting: it is a list of radios, not a preference, and a profile
        // switch must not swap it out.
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HolyLogger", "contest_radio_commands.json");

        // The radios offered in Contest Radio Setup: the OmniRig radio files (their exact names, spaces
        // and all) of transceivers that transmit on 2m, 70cm or both. OmniRig's files do not say which
        // bands a radio has, so the list is fixed here. Every OmniRig file is a radio OmniRig controls
        // by CAT (checked: even the TH-F6A/TH-F7E handhelds carry 19 CAT commands). Left out: receivers
        // (IC-R8500, AR8600...), HF-only radios, and the uncertain (Elecraft K3 with a 2m module, SDR
        // programs). Agreed with the operator 2026-09-19.
        public static readonly IReadOnlyList<string> VhfUhfRadios = new[]
        {
            "IC- 820", "IC- 821", "IC-821PST", "IC- 970D", "IC-275H",
            "IC-7000", "IC-7000v2", "IC-705", "IC-705-DATA",
            "IC-706", "IC-706 MKII", "IC-706 MKIIG",
            "IC-7100", "IC-7100-DATA-FIL1", "IC-7100e4", "IC-7100e4-DATA",
            "IC-910", "IC-9100", "IC-9100v2", "IC-9700", "IC-9700-DATA", "IC-9700-SAT",
            "ID-5100A",
            "FT-100 D", "FT-817", "FT-847", "FT-857", "FT-897", "FT-991", "FT-991-DATA", "FT-991A",
            "TS-2000", "TH-F6A", "TH-F7E",
        };

        // The radio files really in OmniRig right now (names without .ini), read fresh on each call, so a
        // file the operator renamed or deleted in OmniRig is seen at once. Null when no OmniRig folder is
        // found - then nothing can be checked.
        public static HashSet<string> OmniRigRigFiles()
        {
            HashSet<string> files = null;
            foreach (string pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                try
                {
                    string dir = Path.Combine(pf, "Afreet", "OmniRig", "Rigs");
                    if (!Directory.Exists(dir)) continue;
                    if (files == null) files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string f in Directory.GetFiles(dir, "*.ini"))
                        files.Add(Path.GetFileNameWithoutExtension(f));
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            return files;
        }

        // What a first run starts with. The models are the ID-5100A/E and the ID-5100D - there is no
        // plain "ID-5100" - so OmniRig's ID-5100.ini is not offered. CI-V address 8C.
        private static List<RadioCommandSet> Defaults() => new List<RadioCommandSet>
        {
            Id5100("ID-5100A"),
        };

        private static RadioCommandSet Id5100(string name) => new RadioCommandSet
        {
            Radio = name,
            // 07 alone = "Select the VFO mode" (Icom's ID-50 CI-V guide; no ID-5100 guide was found).
            // 07 00 - VFO A on the HF radios - was tried first and left the ID-5100 in memory mode.
            // 07 alone was checked on his ID-5100A on 2026-09-19.
            Vfo = "FE FE 8C E0 07 FD",
            Simplex = "FE FE 8C E0 0F 10 FD",
            NoTone = "FE FE 8C E0 16 5D 00 FD",
            NoTsql = "FE FE 8C E0 16 5D 00 FD",
        };

        // The contest the operator picked in the window: the commands go out when a log of THIS contest
        // is opened, and for no other. Sukkot until he picks another - it is the contest this was built for.
        public const string DefaultContestId = "SUKKOT";

        // The contests offered in the window's Contest box: those with 2m or 70cm among their bands, since
        // every radio here is a VHF/UHF one.
        public static List<Contest> VhfUhfContests()
            => ContestService.All
                   .Where(c => c.Bands != null && c.Bands.Any(b => string.Equals(b, "2m", StringComparison.OrdinalIgnoreCase)
                                                                || string.Equals(b, "70cm", StringComparison.OrdinalIgnoreCase)))
                   .ToList();

        public static ContestRadioSetup Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new ContestRadioSetup { ContestId = DefaultContestId, Radios = Defaults() };

                string json = File.ReadAllText(FilePath).TrimStart();
                // The first version of the file held the radios alone, with no contest.
                if (json.StartsWith("[", StringComparison.Ordinal))
                    return new ContestRadioSetup
                    {
                        ContestId = DefaultContestId,
                        Radios = JsonConvert.DeserializeObject<List<RadioCommandSet>>(json) ?? new List<RadioCommandSet>()
                    };

                var setup = JsonConvert.DeserializeObject<ContestRadioSetup>(json) ?? new ContestRadioSetup();
                if (setup.Radios == null) setup.Radios = new List<RadioCommandSet>();
                return setup;
            }
            catch (Exception swallowed)
            {
                Log.Swallow(swallowed);
                return new ContestRadioSetup { ContestId = DefaultContestId, Radios = Defaults() };
            }
        }

        public static void Save(string contestId, IEnumerable<RadioCommandSet> sets)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var setup = new ContestRadioSetup
                {
                    ContestId = contestId,
                    Radios = sets.Where(s => s != null && !s.IsEmpty).ToList()
                };
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(setup, Formatting.Indented));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The commands for the radio OmniRig says is connected, or null when there are none.
        public static RadioCommandSet FindFor(ContestRadioSetup setup, string rigType)
        {
            if (setup?.Radios == null || string.IsNullOrWhiteSpace(rigType)) return null;
            return setup.Radios.FirstOrDefault(s => string.Equals((s.Radio ?? "").Trim(), rigType.Trim(),
                                                                  StringComparison.OrdinalIgnoreCase));
        }

        // A command made only of hex digits and spaces is meant as bytes, and every byte needs two
        // digits: "FE FE 8C E0 7 00 FD" would otherwise go out as the TEXT "FE FE 8C...", since that is
        // how a command that is not all two-digit bytes is sent. Returns false for such a typo.
        public static bool LooksValid(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return true;
            string t = command.Trim();
            if (!t.All(ch => Uri.IsHexDigit(ch) || ch == ' ')) return true;   // text command (Kenwood/Yaesu)
            string[] parts = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 && parts.All(p => p.Length == 2);
        }
    }
}
