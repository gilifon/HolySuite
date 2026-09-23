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
        // Wide FM, not FM-N: a narrow radio hears a wide station badly, and both are "FM" to OmniRig.
        [JsonProperty("fm_wide")] public string FmWide { get; set; } = "";
        // Auto Repeater puts a duplex shift back on by itself on the repeater part of the band, which
        // undoes Simplex - so it is switched off before the shift is cleared.
        [JsonProperty("no_auto_repeater")] public string NoAutoRepeater { get; set; } = "";

        [JsonIgnore]
        public bool IsEmpty => string.IsNullOrWhiteSpace(Radio) && string.IsNullOrWhiteSpace(Vfo)
                               && string.IsNullOrWhiteSpace(Simplex) && string.IsNullOrWhiteSpace(NoTone)
                               && string.IsNullOrWhiteSpace(NoTsql) && string.IsNullOrWhiteSpace(FmWide)
                               && string.IsNullOrWhiteSpace(NoAutoRepeater);

        // The commands to send, in order, with the empty ones left out and No TSQL dropped when it is the
        // same command as No Tone. Auto Repeater off comes before Simplex, or it would shift the radio
        // again straight after.
        public List<string> CommandsToSend()
        {
            var list = new List<string>();
            foreach (string c in new[] { Vfo, FmWide, NoAutoRepeater, Simplex, NoTone })
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

        // Where OmniRig keeps its .ini files, if that folder actually exists on this machine - so a
        // message can tell the operator exactly where to put a new one instead of saying "OmniRig's
        // Rigs folder" and leaving him to hunt for it.
        public static string OmniRigRigsFolder()
        {
            foreach (string pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                try
                {
                    string dir = Path.Combine(pf, "Afreet", "OmniRig", "Rigs");
                    if (Directory.Exists(dir)) return dir;
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            return null;
        }

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
            Ic7100("IC-7100"),
            Ic7100("IC-7100-DATA-FIL1"),
            Ic7100("IC-7100e4"),
            Ic7100("IC-7100e4-DATA"),
            Ft991("FT-991"),
            Ft991("FT-991-DATA"),
            Ft991("FT-991A"),
            Ft857("FT-857"),
            Ft897("FT-897"),
            Ic910("IC-910"),
        };

        // IC-7100, CI-V address 88. Every command below is from Icom's IC-7100 ADVANCED INSTRUCTIONS,
        // section 20 (Control command): 07 = select the VFO mode; 06 + 05 01 = FM with FIL1 (the wide
        // filter; FIL2 is the narrow one); 0F 10 = set simplex; 16 42 00 = repeater tone OFF;
        // 16 43 00 = tone squelch OFF. NOT YET CHECKED ON A RADIO.
        //
        // Auto Repeater IS in the IC-7100's command table, unlike the ID-5100's: 1A 05 0020, "0=OFF,
        // 1=ON(DUP), 2=ON(DUP,TONE)". The manual calls the function U.S.A./Korea only, but the setting
        // is addressed all the same, and a radio that does not have it simply ignores the command.
        private static RadioCommandSet Ic7100(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "FE FE 88 E0 07 FD",
            FmWide = "FE FE 88 E0 06 05 01 FD",
            NoAutoRepeater = "FE FE 88 E0 1A 05 00 20 00 FD",
            Simplex = "FE FE 88 E0 0F 10 FD",
            NoTone = "FE FE 88 E0 16 42 00 FD",
            NoTsql = "FE FE 88 E0 16 43 00 FD",
        };

        // FT-991 / FT-991A. Text commands, each ending in ';', from Yaesu's FT-991 CAT OPERATION
        // REFERENCE MANUAL: MD0 + 4 = FM (B would be FM-N); OS0 + 0 = simplex, which the manual says
        // works in FM only - so it is sent after the mode; CT0 + 0 = CTCSS OFF, which is both the tone
        // and the tone squelch on this radio, so the same command stands in both boxes.
        //
        // Auto Repeater is Yaesu's ARS, and it is two menu items - 084 for 144 MHz and 085 for 430 MHz
        // - so the box holds both commands one after the other. NOT YET CHECKED ON A RADIO.
        //
        // VFO is left empty ON PURPOSE: the FT-991's only V/M command (VM;) is the front-panel key,
        // which TOGGLES between VFO and memory - sending it could put the radio INTO memory mode.
        private static RadioCommandSet Ft991(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "",
            FmWide = "MD04;",
            NoAutoRepeater = "EX0840;EX0850;",
            Simplex = "OS00;",
            NoTone = "CT00;",
            NoTsql = "CT00;",
        };

        // FT-857 / FT-857D. A DIFFERENT, OLDER protocol from the FT-991's: five raw bytes per command,
        // hex, with no ';' and no opcode letters - the last byte is the opcode, the other four are its
        // parameters (unused ones are 00). From Yaesu's own FT-857D OPERATING MANUAL, "CAT OPERATION"
        // appendix, the 16-row opcode chart:
        //   07 = Operating Mode, P1 in byte 1: 08 = FM, 88 = FM-N  -> FM wide = "08 00 00 00 07"
        //   09 = Repeater Offset, P1 = 89 for SIMPLEX               -> Simplex = "89 00 00 00 09"
        //        (F9 is Repeater Offset FREQUENCY - the manual's own example is "05, 43, 21, 00, [F9] =
        //        5.4321 MHz" - so the old "89 00 00 00 F9" set a 89.000000 MHz shift instead of simplex.)
        //   0A = CTCSS/DCS Mode, P1 = 8A for OFF - the ONE command that turns off both the tone encoder
        //        and the tone/DCS decoder, so No Tone and No TSQL are the same command here, as on the
        //        ID-5100 -> "8A 00 00 00 0A"
        // NOT YET CHECKED ON A RADIO.
        //
        // VFO and No Auto Repeater are left EMPTY. The FT-857 does have ARS (menu items 002/003), but
        // its whole CAT protocol is these 16 fixed opcodes - there is no "write a menu item" command
        // like the FT-991's EX, so ARS cannot be reached over CAT at all. And its only VFO-related
        // opcode (81) toggles between VFO-A and VFO-B; it says nothing about leaving memory mode, so
        // sending it is as likely to move the wrong way as the right one (same reasoning as the FT-991).
        // IC-910(H), CI-V address 60. From Icom's own IC-910H INSTRUCTION MANUAL, "Control command"
        // section (command table read column by column, each command's number vertically centered
        // over its own sub-commands - reconstructed from the PDF's text coordinates, not guessed):
        //   07 alone = Select the VFO mode (same bare command as the ID-5100 and IC-7100).
        //   06 04 = Set FM. UNLIKE the ID-5100/IC-7100, the table's mode command (06) lists only
        //     00=LSB, 01=USB, 03=CW, 04=FM - no narrow-FM sub-command at all, so there is nothing more
        //     specific to send; this only guarantees the mode is FM, not a filter width.
        //   0F 10 = Set simplex operation (same as the ID-5100).
        //   16 42 00 / 16 43 00 = subaudible tone OFF / tone squelch OFF - two separate commands here,
        //     unlike the ID-5100's single 16 5D.
        // NOT YET CHECKED ON A RADIO.
        //
        // No Auto Repeater: EMPTY. The IC-910H is a fixed base station, and its command table has
        // nothing resembling the auto-repeater-shift feature the handheld/mobile Icoms have - repeater
        // duplex is set by hand (commands 0C/0D/0F), never automatically.
        private static RadioCommandSet Ic910(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "FE FE 60 E0 07 FD",
            FmWide = "FE FE 60 E0 06 04 FD",
            NoAutoRepeater = "",
            Simplex = "FE FE 60 E0 0F 10 FD",
            NoTone = "FE FE 60 E0 16 42 00 FD",
            NoTsql = "FE FE 60 E0 16 43 00 FD",
        };

        private static RadioCommandSet Ft857(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "",
            FmWide = "08 00 00 00 07",
            NoAutoRepeater = "",
            Simplex = "89 00 00 00 09",
            NoTone = "8A 00 00 00 0A",
            NoTsql = "8A 00 00 00 0A",
        };

        // FT-897 / FT-897D. The same five-raw-bytes protocol as the FT-857, but the values below were read
        // from the FT-897's OWN manual (page 62, "Opcode Command Chart", 17 opcodes), not copied from it:
        //   07 = Operating Mode, "P1 = 08 : FM", "P1 = 88 : FMN"   -> FM wide = "08 00 00 00 07"
        //   09 = Repeater Offset, "P1 = 89 : SIMPLEX"              -> Simplex = "89 00 00 00 09"
        //        (F9 is a DIFFERENT opcode, Repeater Offset FREQUENCY, whose P1~P4 are frequency digits.)
        //   0A = CTCSS/DCS Mode, "P1 = 8A : OFF" - the one command that turns off both the tone and the
        //        tone/DCS decoder, so No Tone and No TSQL are the same command -> "8A 00 00 00 0A"
        // NOT YET CHECKED ON A RADIO.
        //
        // VFO and No Auto Repeater are EMPTY, for the same reasons as the FT-857: the chart's only VFO
        // entry is "VFO-A/B 81 Toggle" - a toggle says nothing about leaving memory mode - and there is
        // no command that writes a menu item, so ARS cannot be reached over CAT at all.
        private static RadioCommandSet Ft897(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "",
            FmWide = "08 00 00 00 07",
            NoAutoRepeater = "",
            Simplex = "89 00 00 00 09",
            NoTone = "8A 00 00 00 0A",
            NoTsql = "8A 00 00 00 0A",
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
            // 06 = send operating mode, then the mode and the filter: FM is 05 01, FM-N is 05 02
            // (ID-5100A/E full manual, section 13, "Operating mode"). Not checked on the radio yet.
            FmWide = "FE FE 8C E0 06 05 01 FD",
            // AUTO REPEATER IS LEFT EMPTY ON PURPOSE. The ID-5100's CI-V table has no 1A command at
            // all, so the setting cannot be changed over CAT; and the menu item itself exists only in
            // the U.S.A. and Korean versions - the European ID-5100E, which he has, has no such
            // setting. The column stays for a radio that can do it.
            NoAutoRepeater = "",
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
                FillNewCommandsFromDefaults(setup.Radios);
                return setup;
            }
            catch (Exception swallowed)
            {
                Log.Swallow(swallowed);
                return new ContestRadioSetup { ContestId = DefaultContestId, Radios = Defaults() };
            }
        }

        // The commands HolyLogger ships for a radio, or null when it ships none. Used when the operator
        // picks a radio his file has nothing for: a setup made before that radio was added still gets
        // them, without old rows he deleted coming back.
        public static RadioCommandSet DefaultFor(string radio)
        {
            string name = (radio ?? "").Trim();
            if (name.Length == 0) return null;
            return Defaults().FirstOrDefault(d => string.Equals((d.Radio ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase));
        }

        // EVERY box a saved radio leaves empty is filled from the list HolyLogger ships for it. A file
        // written before FM wide existed gets that one; a row that is only a name - picked in the window
        // and never typed into - gets the lot, instead of showing five empty boxes and one filled in.
        // Anything the operator typed himself is left exactly as it is.
        private static void FillNewCommandsFromDefaults(List<RadioCommandSet> radios)
        {
            foreach (var saved in radios)
            {
                var d = Defaults().FirstOrDefault(x => string.Equals((x.Radio ?? "").Trim(), (saved.Radio ?? "").Trim(),
                                                                     StringComparison.OrdinalIgnoreCase));
                if (d == null) continue;
                if (string.IsNullOrWhiteSpace(saved.Vfo)) saved.Vfo = d.Vfo;
                if (string.IsNullOrWhiteSpace(saved.FmWide)) saved.FmWide = d.FmWide;
                if (string.IsNullOrWhiteSpace(saved.NoAutoRepeater)) saved.NoAutoRepeater = d.NoAutoRepeater;
                if (string.IsNullOrWhiteSpace(saved.Simplex)) saved.Simplex = d.Simplex;
                if (string.IsNullOrWhiteSpace(saved.NoTone)) saved.NoTone = d.NoTone;
                if (string.IsNullOrWhiteSpace(saved.NoTsql)) saved.NoTsql = d.NoTsql;
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
                    // A row with a name and no commands is not worth keeping: it only shadows the
                    // commands HolyLogger ships for that radio the next time the window opens.
                    Radios = sets.Where(s => s != null && !s.IsEmpty && s.CommandsToSend().Count > 0).ToList()
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
