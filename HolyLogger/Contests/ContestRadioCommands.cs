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
        // The radio shown when the window was last closed, so it opens on that radio again rather than
        // whichever one happens to be first in the file.
        [JsonProperty("last_radio")] public string LastRadio { get; set; }
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
        // by CAT. Left out: receivers (IC-R8500, AR8600...), HF-only radios, and the uncertain
        // (Elecraft K3 with a 2m module, SDR programs). Agreed with the operator 2026-09-19.
        //
        // Also left out (2026-09-29, his rule: no radio on the list without commands from its maker):
        // IC-970D and IC-275H - their Icom manuals have no command table at all, only "see the CT-17
        // manual" - and TH-F6A / TH-F7E - Kenwood's instruction manual has no PC commands, and the only
        // command list found is a ham's own (K9DCI), not Kenwood's.
        public static readonly IReadOnlyList<string> VhfUhfRadios = new[]
        {
            "IC- 820", "IC- 821", "IC-821PST",
            "IC-7000", "IC-7000v2", "IC-705", "IC-705-DATA",
            "IC-706", "IC-706 MKII", "IC-706 MKIIG",
            "IC-7100", "IC-7100-DATA-FIL1", "IC-7100e4", "IC-7100e4-DATA",
            "IC-910", "IC-9100", "IC-9100v2", "IC-9700", "IC-9700-DATA", "IC-9700-SAT",
            "ID-5100A",
            "FT-100 D", "FT-817", "FT-847", "FT-857", "FT-897", "FT-991", "FT-991-DATA", "FT-991A",
            "TS-2000",
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
            Ts2000("TS-2000"),
            Ic9700("IC-9700"),
            Ic9700("IC-9700-DATA"),
            Ic9700("IC-9700-SAT"),
            Ic7000("IC-7000"),
            Ic7000("IC-7000v2"),
            Ic705("IC-705"),
            Ic705("IC-705-DATA"),
            Ic820("IC- 820"),
            Ic821("IC- 821"),
            Ic821("IC-821PST"),
            Ic706("IC-706"),
            Ic706Mk2("IC-706 MKII", "4E"),
            Ic706Mk2("IC-706 MKIIG", "58"),
            Ic9100("IC-9100"),
            Ic9100("IC-9100v2"),
            Ft817("FT-817"),
            Ft847("FT-847"),
            Ft100("FT-100 D"),
        };

        // IC-820H, CI-V address 42. From Icom's own IC-820H INSTRUCTION MANUAL, "Remote jack (CI-V)
        // information" COMMAND TABLE (scanned manual, read off the page image):
        //   07 alone = VFO mode.   06 05 = FM (the table's mode list has no narrow-FM code at all).
        //   0F 10 = Simplex selection.
        // The table has NO tone, tone squelch or menu command (no 16, no 1A), so No Tone, No TSQL and
        // No Auto Repeater stay EMPTY - they cannot be reached over CAT. NOT YET CHECKED ON A RADIO.
        private static RadioCommandSet Ic820(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "FE FE 42 E0 07 FD",
            FmWide = "FE FE 42 E0 06 05 FD",
            NoAutoRepeater = "",
            Simplex = "FE FE 42 E0 0F 10 FD",
            NoTone = "",
            NoTsql = "",
        };

        // IC-821H, CI-V address 4C. From Icom's own IC-821H INSTRUCTION MANUAL, COMMAND TABLE - the same
        // commands as the IC-820H: 07 = VFO mode, 06 05 = FM (no narrow code), 0F 10 = simplex; no tone,
        // tone squelch or menu command, so those three stay EMPTY. OmniRig's IC-821PST file is the same
        // IC-821 (made for PstRotator), so it gets the same commands. NOT YET CHECKED ON A RADIO.
        private static RadioCommandSet Ic821(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "FE FE 4C E0 07 FD",
            FmWide = "FE FE 4C E0 06 05 FD",
            NoAutoRepeater = "",
            Simplex = "FE FE 4C E0 0F 10 FD",
            NoTone = "",
            NoTsql = "",
        };

        // IC-706 (the first model), CI-V address 48. From Icom's own IC-706 INSTRUCTION MANUAL,
        // COMMAND TABLE: 07 = VFO mode; 06 05 = FM - the table says "Add 02 to select narrow IF
        // filters", so 06 05 without it is the normal (not narrow) filter. Its 0F command has only
        // Split OFF/ON - NO simplex/duplex codes - and there is no tone or menu command, so Simplex,
        // No Tone, No TSQL and No Auto Repeater stay EMPTY. NOT YET CHECKED ON A RADIO.
        private static RadioCommandSet Ic706(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "FE FE 48 E0 07 FD",
            FmWide = "FE FE 48 E0 06 05 FD",
            NoAutoRepeater = "",
            Simplex = "",
            NoTone = "",
            NoTsql = "",
        };

        // IC-706MKII (CI-V 4E) and IC-706MKIIG (CI-V 58), each from its OWN Icom instruction manual.
        //   Address: each manual's set-mode item "CI-V ADDRESS" shows the default - 4EH (MKII) and 58H
        //     (MKIIG). The MKII manual's data-format drawing still prints 48 (the old IC-706's), and the
        //     MKIIG's text still says 4EH; the set-mode default is what the radio really starts with.
        //   07 alone = VFO mode (MKII "VFO mode", MKIIG "Set to VFO").
        //   06 05 00 = FM, normal filter. Both manuals' footnote: "when normal or narrow operation is
        //     available, add 00 for normal operation or 01 for narrow"; and both filter tables list FM
        //     as Normal / Narrow only - so 00 is the wider of the two.
        // MKIIG only: 0F 10 = Simplex mode; 16 42 / 16 43 = TONE / TSQL setting. The MKIIG table does
        // NOT print the data value that means OFF for 16 42 / 16 43, so No Tone and No TSQL are left
        // EMPTY rather than assuming 00. The MKII's 0F has only Split OFF/ON and it has no 16 command,
        // so its Simplex is EMPTY too. Neither table has a menu (1A) command, so No Auto Repeater is
        // EMPTY on both. NOT YET CHECKED ON A RADIO.
        private static RadioCommandSet Ic706Mk2(string name, string civ) => new RadioCommandSet
        {
            Radio = name,
            Vfo = $"FE FE {civ} E0 07 FD",
            FmWide = $"FE FE {civ} E0 06 05 00 FD",
            NoAutoRepeater = "",
            Simplex = civ == "58" ? "FE FE 58 E0 0F 10 FD" : "",
            NoTone = "",
            NoTsql = "",
        };

        // IC-9100, CI-V address 7C. From Icom's own IC-9100 INSTRUCTION MANUAL, "18 CONTROL COMMAND":
        //   07 = Select VFO mode (listed as a command of its own, above 07 00 VFO A).
        //   0F 10 = Set simplex operation.
        //   16 42 00 / 16 43 00 = Repeater tone OFF / Tone squelch OFF.
        //   1A 05 0019 00 = Auto Repeater OFF.
        // FM wide is left EMPTY, as on the IC-9700: the mode command takes FIL1/FIL2/FIL3, and the
        // manual never says which one is wide for FM. NOT YET CHECKED ON A RADIO.
        private static RadioCommandSet Ic9100(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "FE FE 7C E0 07 FD",
            FmWide = "",
            NoAutoRepeater = "FE FE 7C E0 1A 05 00 19 00 FD",
            Simplex = "FE FE 7C E0 0F 10 FD",
            NoTone = "FE FE 7C E0 16 42 00 FD",
            NoTsql = "FE FE 7C E0 16 43 00 FD",
        };

        // FT-817. Five raw bytes, opcode LAST, P1 first (the same order as the FT-857). From Yaesu's own
        // FT-817 OPERATING MANUAL, "Opcode Command Chart":
        //   07, P1 = 08 : FM (the chart lists no FM-N code)      -> "08 00 00 00 07"
        //   09, P1 = 89 : SIMPLEX                                 -> "89 00 00 00 09"
        //   0A, P1 = 8A : CTCSS/DCS OFF (tone and decoder both)   -> "8A 00 00 00 0A"
        // VFO and No Auto Repeater EMPTY: the only VFO opcode is "VFO-A/B 81 Toggle", and there is no
        // command that writes a menu item, so ARS cannot be reached. NOT YET CHECKED ON A RADIO.
        private static RadioCommandSet Ft817(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "",
            FmWide = "08 00 00 00 07",
            NoAutoRepeater = "",
            Simplex = "89 00 00 00 09",
            NoTone = "8A 00 00 00 0A",
            NoTsql = "8A 00 00 00 0A",
        };

        // FT-847. Five raw bytes, D1 first, opcode (P1) last. From Yaesu's own FT-847 OPERATING MANUAL,
        // "Opcode Command Chart"; each command's P1 picks MAIN / SAT RX / SAT TX VFO, and MAIN is used:
        //   Operating Mode, D1 = 08 : FM (88 = FM(N)), P1 = 07 : MAIN VFO   -> "08 00 00 00 07"
        //   Repeater Shift, D1 = 89 : Simplex, opcode 09                    -> "89 00 00 00 09"
        //   CTCSS/DCS Mode, D1 = 8A : CTCSS/DCS OFF, P1 = 0A : MAIN VFO     -> "8A 00 00 00 0A"
        // The FT-847 answers CAT only after "CAT ON" (00 00 00 00 00); OmniRig's FT-847 file already
        // sends that when it starts. VFO and No Auto Repeater EMPTY: the chart has no VFO/memory command
        // and no menu command. NOT YET CHECKED ON A RADIO.
        private static RadioCommandSet Ft847(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "",
            FmWide = "08 00 00 00 07",
            NoAutoRepeater = "",
            Simplex = "89 00 00 00 09",
            NoTone = "8A 00 00 00 0A",
            NoTsql = "8A 00 00 00 0A",
        };

        // FT-100 / FT-100D. Five raw bytes, opcode last - but UNLIKE the FT-817/857/847, P1 is the FOURTH
        // byte (the manual's own example: Split ON = 00 00 00 01 01). From Yaesu's own FT-100 OPERATING
        // MANUAL, "Opcode Command Chart" (13 opcodes):
        //   05, P1 = 00 : VFO Mode, VFO-A (a real select, not a toggle)  -> "00 00 00 00 05"
        //   0C, P1 = 06 : FM (07 is W-FM)                                 -> "00 00 00 06 0C"
        //   84, P1 = 00 : Repeater Shift, Simplex                         -> "00 00 00 00 84"
        //   92, P1 = 00 : CTCSS/DCS OFF (tone and decoder both)           -> "00 00 00 00 92"
        // No Auto Repeater EMPTY: ARS is menu 44/45 and the chart has no menu command.
        // NOT YET CHECKED ON A RADIO.
        private static RadioCommandSet Ft100(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "00 00 00 00 05",
            FmWide = "00 00 00 06 0C",
            NoAutoRepeater = "",
            Simplex = "00 00 00 00 84",
            NoTone = "00 00 00 00 92",
            NoTsql = "00 00 00 00 92",
        };

        // TS-2000. Text commands, each ending in ';', from Kenwood's own TS-2000 INSTRUCTION MANUAL,
        // "21 APPENDIX" command reference: FR0;FT0; = select VFO A as both the RX and TX source (i.e.
        // leave Memory/Call mode); MD4; = FM, sent together with the filter width command so FM is
        // already on before the simplex command runs (OS is "valid only in FM mode"); FW0001; = DSP
        // receive filter WIDE (0000=Narrow); OS0; = simplex; TO0; = TONE (encode) OFF; CT0; = CTCSS
        // (the TSQL function on this radio) OFF - kept apart from TO, unlike the Icoms' single command.
        // NOT YET CHECKED ON A RADIO.
        //
        // No Auto Repeater is left EMPTY. Menu 43 ("Auto repeater offset") does exist (page 34), but the
        // manual gives no example of the EX command's string for that menu, only the command's general
        // format and a worked example for menu 00 - a guessed EX string was rejected by the operator.
        private static RadioCommandSet Ts2000(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "FR0;FT0;",
            FmWide = "MD4;FW0001;",
            NoAutoRepeater = "",
            Simplex = "OS0;",
            NoTone = "TO0;",
            NoTsql = "CT0;",
        };

        // IC-9700, CI-V address A2. From Icom's IC-9700 CI-V REFERENCE GUIDE command table:
        //   07 00 = Select the VFO mode, VFO A - UNLIKE the ID-5100/IC-7100/IC-910, this radio's "07"
        //     is a group header with no command of its own; "00" (Select VFO A) is what is actually sent.
        //   0F 10 = Set the simplex operation.
        //   16 42 00 / 16 43 00 = Repeater tone OFF / Tone squelch OFF.
        //   1A 05 0047 00 = Auto Repeater OFF (menu "SET > Function > Auto Repeater", 00=OFF, counted
        //     off the guide's own sequential menu-number list, since the two-column PDF put the numbers
        //     and their descriptions in different columns).
        // FM wide is left EMPTY: mode 06 05 takes an FIL1/FIL2/FIL3 filter byte, but the guide never
        // says which of the three is "wide" for FM (unlike the IC-7100's guide, which does). NOT YET
        // CHECKED ON A RADIO.
        private static RadioCommandSet Ic9700(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "FE FE A2 E0 07 00 FD",
            FmWide = "",
            NoAutoRepeater = "FE FE A2 E0 1A 05 00 47 00 FD",
            Simplex = "FE FE A2 E0 0F 10 FD",
            NoTone = "FE FE A2 E0 16 42 00 FD",
            NoTsql = "FE FE A2 E0 16 43 00 FD",
        };

        // IC-7000, CI-V default address 70h. From Icom's own IC-7000 INSTRUCTION MANUAL, "17 CONTROL
        // COMMAND" section (its own command table, not borrowed from another radio):
        //   07 alone = Select VFO mode (same bare command as the ID-5100/IC-7100/IC-910).
        //   0F 10 = Select simplex operation.
        //   16 42 00 / 16 43 00 = Repeater tone OFF / Tone squelch OFF.
        //   1A 05 0060 00 = "auto repeater set", 0=OFF (the table's own name for the setting).
        // FM wide is left EMPTY: command 06 (mode select) lists no filter-width sub-byte for FM at all,
        // and the only wide/narrow SSB-bandwidth commands found (1A 05 0003 etc.) are for SSB, not FM.
        // NOT YET CHECKED ON A RADIO.
        private static RadioCommandSet Ic7000(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "FE FE 70 E0 07 FD",
            FmWide = "",
            NoAutoRepeater = "FE FE 70 E0 1A 05 00 60 00 FD",
            Simplex = "FE FE 70 E0 0F 10 FD",
            NoTone = "FE FE 70 E0 16 42 00 FD",
            NoTsql = "FE FE 70 E0 16 43 00 FD",
        };

        // IC-705, CI-V address A4. From Icom's IC-705 CI-V REFERENCE GUIDE command table:
        //   07 00 = Select the VFO mode, VFO A (same group-header/sub-command split as the IC-9700).
        //   0F 10 = Set the simplex operation.
        //   16 42 00 / 16 43 00 = Repeater tone OFF / Tone squelch OFF.
        //   1A 05 0049 00 = Auto Repeater OFF ("Send/read the Auto Repeater setting", 00=OFF; menu
        //     number counted off the guide's own sequential list, same method as the IC-9700's).
        // FM wide is left EMPTY, for the same reason as the IC-9700: no FIL1/2/3 filter is documented
        // as "wide" for FM, and the radio's separate WFM mode (06) is FM broadcast reception, not a
        // wider voice filter. NOT YET CHECKED ON A RADIO.
        private static RadioCommandSet Ic705(string name) => new RadioCommandSet
        {
            Radio = name,
            Vfo = "FE FE A4 E0 07 00 FD",
            FmWide = "",
            NoAutoRepeater = "FE FE A4 E0 1A 05 00 49 00 FD",
            Simplex = "FE FE A4 E0 0F 10 FD",
            NoTone = "FE FE A4 E0 16 42 00 FD",
            NoTsql = "FE FE A4 E0 16 43 00 FD",
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

        public static void Save(string contestId, string lastRadio, IEnumerable<RadioCommandSet> sets)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var setup = new ContestRadioSetup
                {
                    ContestId = contestId,
                    LastRadio = string.IsNullOrWhiteSpace(lastRadio) ? null : lastRadio.Trim(),
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
