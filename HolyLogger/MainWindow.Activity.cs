using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HolyParser;

namespace HolyLogger
{
    // ACTIVITY PROGRAM REFERENCES on the main form.
    //
    // Four programs have a box each because ADIF gives them a field each; every other program -
    // castles, mills, lighthouses, and whatever is founded next year - goes through the Other button
    // into the standard SIG / SIG_INFO pair. That split is the whole design: the list of programs
    // has no end, so only the four the standard names are allowed to take up screen space.
    public partial class MainWindow
    {
        // What each program's reference has to look like, straight out of the ADIF data types.
        // Anchored and upper-case only: every box on the row is CharacterCasing="Upper".
        private static readonly Regex IotaPattern = new Regex(@"^(AF|AN|AS|EU|NA|OC|SA)-\d{3}$", RegexOptions.Compiled);
        private static readonly Regex SotaPattern = new Regex(@"^[A-Z0-9]{1,8}/[A-Z]{2}-\d{3}$", RegexOptions.Compiled);
        private static readonly Regex PotaPattern = new Regex(@"^[A-Z0-9]{1,4}-\d{4,5}(@[A-Z0-9\-]{1,6})?$", RegexOptions.Compiled);
        private static readonly Regex WwffPattern = new Regex(@"^[A-Z0-9]{1,4}FF-\d{4}$", RegexOptions.Compiled);

        // The pale red a box wears while what is in it is not a valid reference. Not the theme's Danger
        // brush: that one is for text, and behind 16pt characters it is far too dark to read through.
        private static readonly Brush BadReferenceBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xE1, 0xE1));

        // My Fauna's own, stronger red, asked for by eye: the pale one above was too easy to miss on
        // the box that must be filled before an activation starts. Still light enough for black text.
        private static readonly Brush MyWwffMissingBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x9E, 0x9E));


        // What an activity box should look like when it has nothing to complain about: the form's
        // ordinary input colour, or the edit-mode yellow while a logged QSO is open for editing.
        // Kept here because these boxes have a second background of their own (the pale red below) and
        // the two have to agree on which one wins.
        private Brush activityNormalBg;

        private void SetActivityNormalBackground(Brush background)
        {
            activityNormalBg = background;
            foreach (TextBox box in ActivityBoxes()) ApplyActivityBoxColour(box);
            ApplyMyWwffColour();
        }

        private IEnumerable<TextBox> ActivityBoxes()
        {
            yield return TB_Iota;
            yield return TB_SotaRef;
            yield return TB_PotaRef;
            yield return TB_WwffRef;
        }

        public static bool IsValidIota(string s) { return IotaPattern.IsMatch((s ?? "").Trim()); }
        public static bool IsValidSota(string s) { return SotaPattern.IsMatch((s ?? "").Trim()); }
        public static bool IsValidWwff(string s) { return WwffPattern.IsMatch((s ?? "").Trim()); }

        // POTA is the one that can hold a LIST: "K-0001,US-4578" is one QSO inside two parks, which the
        // standard allows and which really happens where parks overlap. Every item has to be a park.
        public static bool IsValidPota(string s)
        {
            string t = (s ?? "").Trim();
            if (t.Length == 0) return false;
            foreach (string part in t.Split(','))
            {
                if (!PotaPattern.IsMatch(part.Trim())) return false;
            }
            return true;
        }

        // Which program a lone reference belongs to, or null when it is not a reference at all. The
        // four formats cannot be confused with each other, which is what lets the Verify Log tool offer
        // to move a reference out of a comment without having to ask the operator which program it is.
        public static string ProgramOf(string reference)
        {
            string t = (reference ?? "").Trim().ToUpperInvariant();
            if (t.Length == 0) return null;
            if (IsValidIota(t)) return "IOTA";
            if (IsValidSota(t)) return "SOTA";
            if (IsValidWwff(t)) return "WWFF";
            if (IsValidPota(t)) return "POTA";
            return null;
        }

        // Live checking as the operator types: a box holding something that is not a reference goes
        // pale red. Nothing is blocked here - the complaint, if any, comes once at Add time.
        private void ActivityBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyActivityBoxColour(sender as TextBox);
            if (sender == TB_WwffRef) UpdateParkSuggestions(TB_WwffRef);
        }

        // ── A PARK TYPED IN EXCHANGE, IN A WWFF ACTIVATION ────────────────────────────────────────
        //
        // In WWFF the exchange that matters is the other station's park, and it is natural to type it
        // in Exchange. At Add it is moved to the WWFF box - corrected when it is a near miss (4XFF16 ->
        // 4XFF-0016) - so it is saved as WWFF_REF and not as a loose SRX_STRING. Anything that is not a
        // park stays in Exchange exactly as before. Nothing is moved when the WWFF box already holds a
        // different park: which one is right is the operator's call, not the program's.
        //
        // LIVE, TOO: while it is typed, Exchange is copied into the WWFF box on every keystroke, so the
        // operator SEES where it is going. The WWFF box can still be typed in directly.
        private bool WwffExchangeOn
        {
            get { return IsWwffActivity && ActivityRow != null && ActivityRow.Visibility == Visibility.Visible; }
        }

        private void Exchange_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Only the operator's own typing is copied. Exchange is also written by the program - emptied
            // by Clear and by the move at Add, filled when a QSO is opened for editing - and none of
            // those may overwrite the WWFF box.
            if (movingExchange || !WwffExchangeOn || !TB_Exchange.IsKeyboardFocusWithin) return;
            string ex = (TB_Exchange.Text ?? "").Trim();
            if (!string.Equals(TB_WwffRef.Text, ex, StringComparison.Ordinal)) TB_WwffRef.Text = ex;

            // "Worked before" sits under the callsign and covers part of Exchange; once a park is
            // being typed there it has been read, and it is in the way.
            if (L_Legal != null) L_Legal.Visibility = Visibility.Hidden;
            UpdateParkSuggestions(TB_Exchange);
        }

        private void MoveExchangeParkToWwff()
        {
            if (!WwffExchangeOn) return;
            string ex = (TB_Exchange.Text ?? "").Trim().ToUpperInvariant();
            if (ex.Length == 0) return;
            string have = (TB_WwffRef.Text ?? "").Trim();
            // Typed over in the WWFF box after Exchange: that box wins, Exchange is left alone.
            if (have.Length > 0 && !string.Equals(have, ex, StringComparison.OrdinalIgnoreCase)) return;
            // A near miss is corrected; anything else goes as typed, and the WWFF box's own check
            // (pale red, and the "how to fix" question at Add) speaks for it.
            string park = IsValidWwff(ex) ? ex : GuessWwff(ex);
            TB_WwffRef.Text = park ?? ex;
            // F1 can be pressed with the cursor still in Exchange, so the live copy must not answer
            // this emptying by emptying the WWFF box as well.
            movingExchange = true;
            try { TB_Exchange.Text = ""; }
            finally { movingExchange = false; }
        }

        private bool movingExchange;

        // Pale red while the box holds something that is not a reference; otherwise back to whatever
        // the rest of the form's editable boxes are wearing - white normally, yellow in edit mode.
        private void ApplyActivityBoxColour(TextBox box)
        {
            if (box == null) return;
            string text = (box.Text ?? "").Trim();
            bool ok = text.Length == 0 || IsValidActivityBox(box, text);
            if (box == TB_WwffRef)
            {
                // The park's NAME on hover once it is on the list; orange and a warning when the
                // format is right but no such park exists.
                WwffDirectory.Park park = ok && text.Length > 0 ? WwffDirectory.Find(text) : null;
                bool unknown = ok && text.Length > 0 && WwffDirectory.IsKnown(text) == false;
                box.ToolTip = park != null ? park.Ref + " - " + park.Name
                    : unknown ? ParkNotListedTip(text)
                    : WwffBoxTip;
                if (unknown) { box.Background = UnknownParkBrush; return; }
            }
            if (!ok) { box.Background = BadReferenceBrush; return; }
            if (activityNormalBg != null) box.Background = activityNormalBg;
            else box.ClearValue(Control.BackgroundProperty);
        }

        private const string WwffBoxTip = "World Wide Flora and Fauna: the nature-reserve reference - 4XFF-0016";

        private bool IsValidActivityBox(TextBox box, string text)
        {
            if (box == TB_Iota) return IsValidIota(text);
            if (box == TB_SotaRef) return IsValidSota(text);
            if (box == TB_PotaRef) return IsValidPota(text);
            if (box == TB_WwffRef) return IsValidWwff(text);
            return true;
        }

        // Everything on the row that is filled in but malformed, said in the operator's words. Empty
        // when the row is fine, which is the normal case.
        private List<string> ActivityComplaints()
        {
            var bad = new List<string>();
            if (!string.IsNullOrWhiteSpace(TB_Iota.Text) && !IsValidIota(TB_Iota.Text))
                bad.Add("IOTA \"" + TB_Iota.Text.Trim() + "\" - an island reference looks like EU-005: two letters for the continent, then three digits.");
            if (!string.IsNullOrWhiteSpace(TB_SotaRef.Text) && !IsValidSota(TB_SotaRef.Text))
                bad.Add("SOTA \"" + TB_SotaRef.Text.Trim() + "\" - a summit reference looks like W2/WE-003.");
            if (!string.IsNullOrWhiteSpace(TB_PotaRef.Text) && !IsValidPota(TB_PotaRef.Text))
                bad.Add("POTA \"" + TB_PotaRef.Text.Trim() + "\" - a park reference looks like K-0001. Two parks at once are written K-0001,US-4578.");
            if (!string.IsNullOrWhiteSpace(TB_WwffRef.Text) && !IsValidWwff(TB_WwffRef.Text))
                bad.Add(WwffHowToFix("WWFF", TB_WwffRef.Text));
            if (IsWwffActivity && !string.IsNullOrWhiteSpace(TB_ActivitySigInfo.Text) && !IsValidWwff(TB_ActivitySigInfo.Text))
                bad.Add(WwffHowToFix("My Fauna", TB_ActivitySigInfo.Text));
            return bad;
        }

        // HOW TO FIX A WWFF PARK, not just "wrong". Says how one is written, and when the mistake is a
        // usual one - no dash, the zeros left out, one F, a space - gives the number it was surely
        // meant to be: "4XFF16" -> 4XFF-0016.
        private static string WwffHowToFix(string boxName, string typed)
        {
            string t = (typed ?? "").Trim();
            string text = boxName + " \"" + t + "\" is not a park number."
                + Environment.NewLine + "Write it as: country, FF, a dash, four digits - 4XFF-0016.";
            string guess = GuessWwff(t);
            // A statement, not "did you mean ...?": the dialog ends with its own yes/no question, and a
            // second question above it would leave "Yes" answering the wrong one.
            if (guess != null) text += Environment.NewLine + "It is probably " + guess + ".";
            return text;
        }

        private static readonly Regex WwffLoosePattern =
            new Regex(@"^([A-Z0-9]{1,4}?)F{1,2}[\s\-_/.]*(\d{1,4})$", RegexOptions.Compiled);

        // The park a mistyped reference was meant to be, or null when there is no safe guess.
        public static string GuessWwff(string typed)
        {
            string t = (typed ?? "").Trim().ToUpperInvariant();
            Match m = WwffLoosePattern.Match(t);
            if (!m.Success) return null;
            string guess = m.Groups[1].Value + "FF-" + m.Groups[2].Value.PadLeft(4, '0');
            return IsValidWwff(guess) && guess != t ? guess : null;
        }

        // Called just before a QSO is saved. Returns false only when the operator chooses to go back and
        // fix a malformed reference; saying "log it anyway" keeps their typing rather than dropping it.
        private bool ConfirmActivityBeforeSave()
        {
            MoveExchangeParkToWwff();

            // MY PARK MISSING. Asked, never blocked: the operator may still be setting up when the
            // contact of the day calls, and that contact must be loggable - the park can be added
            // later in the QSO editor.
            if (IsWwffActivity && string.IsNullOrWhiteSpace(TB_ActivitySigInfo.Text))
            {
                bool logAnyway = HolyMessageBox.ShowConfirm(
                    "Your park is not typed in My Fauna."
                    + Environment.NewLine + Environment.NewLine
                    + "Type it there, e.g. 4XFF-0016, so this QSO counts for your activation.",
                    "My Fauna is empty", HolyMsgType.Warning, this, 0,
                    "Log anyway", "Type my park");
                if (!logAnyway)
                {
                    TB_ActivitySigInfo.Focus();
                    return false;
                }
            }

            List<string> bad = ActivityComplaints();
            if (bad.Count == 0) return true;
            string message = (bad.Count == 1 ? "This reference is not in the standard form:" : "These references are not in the standard form:")
                + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine + Environment.NewLine, bad.ToArray())
                + Environment.NewLine + Environment.NewLine
                + "Log the QSO with it as typed anyway?";
            // fitLongestLine: the WWFF lines end on the example park, and a wrap there left "4XFF-0016"
            // alone on a line of its own (measured).
            return HolyMessageBox.ShowConfirm(message, "Check the reference", HolyMsgType.Warning, this,
                fitLongestLine: true);
        }

        private void ActivityToQso(QSO qso)
        {
            if (qso == null) return;
            qso.Iota = TB_Iota.Text.Trim();
            qso.SotaRef = TB_SotaRef.Text.Trim();
            qso.PotaRef = TB_PotaRef.Text.Trim();
            qso.WwffRef = TB_WwffRef.Text.Trim();
            // Straight off the form now, like the four above. They used to be held in two variables that
            // only the Other window ever wrote.
            qso.Sig = (CB_ActivitySig.Text ?? "").Trim();
            qso.SigInfo = (TB_ActivitySigInfo.Text ?? "").Trim();

            // WWFF HAS ITS OWN ADIF FIELDS, so it is never written as SIG: the box beside it is MY park,
            // MY_WWFF_REF. The other station's park stays in the WWFF box above (WWFF_REF).
            qso.MyWwffRef = "";
            if (IsWwffActivity)
            {
                qso.MyWwffRef = (TB_ActivitySigInfo.Text ?? "").Trim();
                qso.Sig = "";
                qso.SigInfo = "";
            }
        }

        private void ActivityFromQso(QSO qso)
        {
            if (qso == null) { ClearActivityRow(); return; }
            TB_Iota.Text = qso.Iota ?? "";
            TB_SotaRef.Text = qso.SotaRef ?? "";
            TB_PotaRef.Text = qso.PotaRef ?? "";
            TB_WwffRef.Text = qso.WwffRef ?? "";
            if (!string.IsNullOrWhiteSpace(qso.MyWwffRef))
            {
                myWwffRef = qso.MyWwffRef.Trim();
                CB_ActivitySig.Text = WwffListEntry;
                TB_ActivitySigInfo.Text = myWwffRef;
            }
            else
            {
                CB_ActivitySig.Text = qso.Sig ?? "";
                TB_ActivitySigInfo.Text = qso.SigInfo ?? "";
            }
            ShowActivitySigMeaning();
        }

        // ── MY FAUNA ──────────────────────────────────────────────────────────────────────────────
        //
        // While Activity is WWFF the box beside the list is MY park. It is kept apart from what the box
        // holds for any other program - switching to castles must not log my park as a castle - and it
        // survives Clear and a restart, because it stays the same for the whole activation.
        private string myWwffRef = (Properties.Settings.Default.LastMyWwffRef ?? "").Trim();
        private bool wwffModeShown;

        private void ApplyWwffMode()
        {
            if (TB_ActivitySigInfo == null || TB_MyWwffLabel == null) return;
            bool wwff = IsWwffActivity;
            if (wwff == wwffModeShown) return;
            wwffModeShown = wwff;
            if (L_WwffRef != null) L_WwffRef.FontWeight = wwff ? FontWeights.Bold : FontWeights.Normal;
            if (wwff) WwffDirectory.EnsureLoaded(this, RefreshWwffColours);

            if (wwff)
            {
                // Label, box and hint all 8px further right than the plain row, asked for by eye so
                // "My Fauna" does not crowd the list's chevron.
                TB_MyWwffLabel.Visibility = Visibility.Visible;
                System.Windows.Controls.Canvas.SetLeft(TB_MyWwffLabel, 185);
                System.Windows.Controls.Canvas.SetLeft(TB_ActivitySigInfo, 233);
                System.Windows.Controls.Canvas.SetLeft(TB_ActivitySigHint, 343);
                TB_ActivitySigInfo.Width = 102;
                TB_ActivitySigInfo.MaxLength = 20;
                TB_ActivitySigInfo.ToolTip = "Your own WWFF park, e.g. 4XFF-0016";
                TB_ActivitySigInfo.Text = myWwffRef;

                // No park yet: the cursor goes straight to where it is typed. After the list has
                // closed, or the list takes the focus back.
                if (myWwffRef.Length == 0)
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try { if (IsWwffActivity) TB_ActivitySigInfo.Focus(); }
                        catch (Exception swallowed) { Log.Swallow(swallowed); }
                    }), System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                TB_MyWwffLabel.Visibility = Visibility.Collapsed;
                System.Windows.Controls.Canvas.SetLeft(TB_ActivitySigInfo, 177);
                System.Windows.Controls.Canvas.SetLeft(TB_ActivitySigHint, 335);
                TB_ActivitySigInfo.Width = 150;
                TB_ActivitySigInfo.MaxLength = 60;
                TB_ActivitySigInfo.ToolTip = "The reference within that activity, e.g. OK-00234";
                TB_ActivitySigInfo.Text = "";
            }
            ApplyMyWwffColour();
        }

        private void ActivitySigInfo_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsWwffActivity && wwffModeShown)
            {
                myWwffRef = (TB_ActivitySigInfo.Text ?? "").Trim();
                try
                {
                    if (!string.Equals(Properties.Settings.Default.LastMyWwffRef, myWwffRef, StringComparison.Ordinal))
                    {
                        Properties.Settings.Default.LastMyWwffRef = myWwffRef;
                        SettingsFlush.RequestSave();
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            ApplyMyWwffColour();
            UpdateParkSuggestions(TB_ActivitySigInfo);
        }

        // Pale red while My Fauna holds something that is not a WWFF park, like the other boxes on the row.
        private void ApplyMyWwffColour()
        {
            if (TB_ActivitySigInfo == null) return;
            string text = (TB_ActivitySigInfo.Text ?? "").Trim();
            // EMPTY IS RED TOO: an activation without my park is not an activation - the WWFF log needs
            // it on every QSO - so the empty box says "type me" until it is filled.
            bool bad = IsWwffActivity && (text.Length == 0 || !IsValidWwff(text));
            if (IsWwffActivity)
            {
                // The red box explains itself on hover, with the corrected number when there is one.
                string guess = bad && text.Length > 0 ? GuessWwff(text) : null;
                TB_ActivitySigInfo.ToolTip = !bad
                    ? "Your own WWFF park, e.g. 4XFF-0016"
                    : text.Length == 0
                        ? "Type your own WWFF park here, e.g. 4XFF-0016"
                        : "Write it as: country, FF, a dash, four digits - 4XFF-0016"
                          + (guess != null ? Environment.NewLine + "It is probably " + guess : "");
            }
            if (bad) { TB_ActivitySigInfo.Background = MyWwffMissingBrush; return; }
            if (IsWwffActivity)
            {
                WwffDirectory.Park park = WwffDirectory.Find(text);
                if (park != null) TB_ActivitySigInfo.ToolTip = "Your park: " + park.Ref + " - " + park.Name;
                else if (WwffDirectory.IsKnown(text) == false)
                {
                    TB_ActivitySigInfo.ToolTip = ParkNotListedTip(text);
                    TB_ActivitySigInfo.Background = UnknownParkBrush;
                    return;
                }
            }
            if (activityNormalBg != null) TB_ActivitySigInfo.Background = activityNormalBg;
            else TB_ActivitySigInfo.ClearValue(Control.BackgroundProperty);
        }

        // THE PROGRAM SURVIVES A CLEAR. Everything else on this row belongs to the contact just logged
        // and goes; the program is what the OPERATOR is doing - working a castle, a lighthouse - and it
        // stays true until they say otherwise. Clearing it after every QSO would mean choosing it again
        // for every QSO of the same activation. Clear it by picking the blank at the top of the list, or
        // by selecting a different program.
        private void ClearActivityRow()
        {
            TB_Iota.Clear();
            TB_SotaRef.Clear();
            TB_PotaRef.Clear();
            TB_WwffRef.Clear();
            // My Fauna is MY park for the whole activation, so it stays, like the program itself.
            if (!IsWwffActivity) TB_ActivitySigInfo.Clear();
            ShowActivitySigMeaning();
        }

        // The program list, filled once, from the same place the Other window and the QSO editor use,
        // with the last program used put back into the box - the same activation usually goes on across
        // sessions, so the answer given yesterday is still the right one this morning.
        private void FillActivitySigList()
        {
            if (CB_ActivitySig == null) return;

            // A blank line at the top, because the box now keeps what it holds: without a way to choose
            // NOTHING, the only way back out of a program would be to select all of it and delete it.
            // CONTEST IS ON THE LIST, AND IT IS NOT AN ACTIVITY.
            // Second line, under the blank: the blank is the way OUT of a programme and is used far
            // more often, so it keeps the top.
            // Operators read "Activity" as "the thing I am taking part in", saw no contest on the list,
            // and logged a contest without ever making a contest log - so the exchange had nowhere to
            // go. The word they were looking for is now where they look for it, and choosing it says
            // where a contest really starts. Nothing is ever logged from this line: the box goes
            // straight back to what it held before (ActivitySig_SelectionChanged).
            var list = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("", "No Activity was selected"),
                new KeyValuePair<string, string>(ContestListEntry, "in a contest? read this"),
                // Right under Contest. Picking it says "I am activating a WWFF park": the Radio
                // Control Panel's band buttons then go to the WWFF frequencies (IsWwffActivity).
                new KeyValuePair<string, string>(WwffListEntry, "World Wide Flora and Fauna activation")
            };
            list.AddRange(OtherActivityWindow.Known);
            CB_ActivitySig.ItemsSource = list;
            CB_ActivitySig.Text = (Properties.Settings.Default.LastActivityProgram ?? "").Trim();

            // Wired AFTER the text above is put in, so filling the box is not mistaken for the operator
            // choosing something.
            CB_ActivitySig.DropDownOpened += ActivitySig_DropDownOpened;
            CB_ActivitySig.SelectionChanged += ActivitySig_SelectionChanged;
            TB_Exchange.TextChanged += Exchange_TextChanged;
            WireParkSuggest(TB_WwffRef);
            WireParkSuggest(TB_ActivitySigInfo);
            WireParkSuggest(TB_Exchange);

            ShowActivitySigMeaning();
        }

        // Remembered as it changes, so the box comes back filled next time the program starts.
        private void RememberActivityProgram()
        {
            try
            {
                string now = (CB_ActivitySig.Text ?? "").Trim();
                // The Contest line is a signpost, not a program. It sits in the box for the instant it
                // takes to put the old value back, and remembering it would bring it back at the next
                // start as though it had been chosen.
                if (string.Equals(now, ContestListEntry, StringComparison.OrdinalIgnoreCase)) return;
                if (string.Equals(Properties.Settings.Default.LastActivityProgram, now, StringComparison.Ordinal)) return;
                Properties.Settings.Default.LastActivityProgram = now;
                SettingsFlush.RequestSave();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // WHAT THE SHORT NAME MEANS, beside the box. "ARLHS" is not something to have to remember, and
        // three of the eight names on the list are lighthouses. The drop-down spells each one out while
        // it is open; this keeps the answer on screen after it has closed. Silent for a name nothing
        // recognises - a program founded next year is perfectly allowed here, and saying nothing is the
        // truthful response to one we have never heard of.
        private void ShowActivitySigMeaning()
        {
            string typed = (CB_ActivitySig == null ? "" : CB_ActivitySig.Text ?? "").Trim();

            if (TB_ActivitySigHint != null)
                TB_ActivitySigHint.Text = string.Equals(typed, WwffListEntry, StringComparison.OrdinalIgnoreCase)
                    ? "WWFF activation"
                    : OtherActivityWindow.DescriptionOf(typed);

            // The word "Program" shows only while the box is empty - it is a label, not a value.
            if (TB_ActivitySigPlaceholder != null)
                TB_ActivitySigPlaceholder.Visibility = typed.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            ApplyWwffMode();

            UpdateActivityHintWidth();
        }

        // AS MUCH ROOM AS THE ROW HAS SPARE. The hint shares its line with Try Again and Undo, and both
        // of those are Collapsed in normal use - so a fixed limit that always left space for them meant
        // "World Castles Award" was cut to "World Castl..." on a row that was two thirds empty.
        // The limit now follows whichever button is actually showing:
        //   Try Again visible -> stop at its left edge, 436
        //   only Undo visible -> stop at its left edge, 542
        //   neither           -> the row's own right edge, 638 (the right edge of the WWFF box above)
        // less a 6px gap in each case, so the words never touch a key. Anything still too long keeps the
        // ellipsis it always had, and the drop-down spells every name out in full.
        private void UpdateActivityHintWidth()
        {
            if (TB_ActivitySigHint == null) return;

            const double gap = 6;
            // 335 normally, 343 while My Fauna is showing (ApplyWwffMode moves it).
            double hintLeft = System.Windows.Controls.Canvas.GetLeft(TB_ActivitySigHint);
            if (double.IsNaN(hintLeft)) hintLeft = 335;
            double stopAt = 638;
            if (Btn_TryAgain != null && Btn_TryAgain.Visibility == Visibility.Visible)
            {
                // ITS REAL LEFT EDGE, NOT THE 436 IT WAS DRAWN AT. The key now carries the number of
                // stations waiting and is made wider for it, growing leftwards (SizeTryAgainKey), so
                // a fixed 436 here would let the hint run under a key that had moved.
                double keyLeft = System.Windows.Controls.Canvas.GetLeft(Btn_TryAgain);
                stopAt = double.IsNaN(keyLeft) ? 436 : keyLeft;
            }
            else if (Btn_UndoMain != null && Btn_UndoMain.Visibility == Visibility.Visible) stopAt = 542;

            TB_ActivitySigHint.MaxWidth = Math.Max(0, stopAt - hintLeft - gap);
        }

        // THE ONE LINE ON THE LIST THAT IS NOT A PROGRAM.
        private const string ContestListEntry = "Contest";

        private const string WwffListEntry = "WWFF";

        /// <summary>True while Activity is WWFF - the Radio Control Panel then uses the WWFF frequencies.</summary>
        internal bool IsWwffActivity
        {
            get
            {
                string now = CB_ActivitySig == null ? "" : (CB_ActivitySig.Text ?? "").Trim();
                return string.Equals(now, WwffListEntry, StringComparison.OrdinalIgnoreCase);
            }
        }

        // What the box held before the list was opened, so choosing Contest can put it back. Taken when
        // the list opens rather than from the selection's RemovedItems: the box is editable, and a typed
        // value has no item to be removed.
        private string activitySigBeforeDropDown = "";

        // Set while the old value is being put back, so that put-back is not read as a fresh choice.
        private bool explainingContest;

        private void ActivitySig_DropDownOpened(object sender, EventArgs e)
        {
            activitySigBeforeDropDown = (CB_ActivitySig.Text ?? "").Trim();
        }

        // Choosing Contest EXPLAINS; it does not select. The word is on the list because that is where
        // people look for it, and what it does is send them to the one place a contest actually starts.
        private void ActivitySig_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (explainingContest) return;

            // THE ITEM THAT WAS JUST PICKED, not the box's text. On an editable ComboBox the text is
            // still the OLD value while SelectionChanged is being raised - the box is written a moment
            // later - so asking the box what it holds here answers about the value being replaced.
            if (e == null || e.AddedItems == null || e.AddedItems.Count == 0) return;
            if (!(e.AddedItems[0] is KeyValuePair<string, string>)) return;
            var picked = (KeyValuePair<string, string>)e.AddedItems[0];
            if (!string.Equals(picked.Key, ContestListEntry, StringComparison.OrdinalIgnoreCase)) return;

            explainingContest = true;
            string putBack = activitySigBeforeDropDown;

            // AFTER the drop-down has finished closing itself. Putting the old text back from inside the
            // selection that is still being raised leaves the ComboBox arguing with itself over which
            // value is current, and the window would open with the list still on screen over it.
            Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    CB_ActivitySig.SelectedItem = null;
                    CB_ActivitySig.Text = putBack;
                    ShowActivitySigMeaning();
                    ExplainContestLog();
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                finally { explainingContest = false; }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // Where a contest really starts - and the two buttons that start it, not a description of one.
        // The yellow key is the Log Manager's own "Create New Contest Log", the same colours and the
        // same job. Beside it, in green, the answer for somebody who made that log yesterday: opening
        // the Log Manager on the list of logs he already has. Without it the window spoke only to
        // operators with no contest log at all, and everybody else was left looking for a menu.
        private void ExplainContestLog()
        {
            int answer = HolyMessageBox.ShowChoice(
                "This list is for activity programs - castles, mills, lighthouses. A contest is not one of them."
                + Environment.NewLine + Environment.NewLine
                + "A contest needs a log of its own. Make a new one with the yellow button, or open a contest log you already have with the green one."
                + Environment.NewLine + Environment.NewLine
                + "The contest name then shows at the top of this window, and the exchange boxes appear.",
                "Working a contest?", HolyMsgType.Info, this,
                "Create New Contest Log", "Select existing Log", "Not now",
                // The Log Manager button's own three colours (ViewLogsWindow.xaml).
                "#FFC107", "#1A1A1A", "#1565C0",
                // The dark green the word "Contest" wears in the list this window was opened from.
                "#1B5E20", "#FFFFFF", "#1565C0");

            if (answer == 1) CreateNewContestLog(this);
            else if (answer == 2) OpenExistingContestLog();
        }

        // "I already have one" - so show him HIS CONTEST LOGS and nothing else. And if there are none,
        // say so rather than opening a window filtered down to an empty table, which reads as a fault
        // in the program. The offer to make one is repeated there because it is the only thing left to
        // do: a message that says "you have none" and stops is a dead end.
        private void OpenExistingContestLog()
        {
            int contestLogs = 0;
            try
            {
                foreach (var log in dal.GetLogs())
                    if (!string.IsNullOrEmpty(log.EventType)) contestLogs++;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (contestLogs > 0) { OpenLogManager(null, true); return; }

            bool make = HolyMessageBox.ShowConfirm(
                "There is no contest log here yet - every log you have is a general one."
                + Environment.NewLine + Environment.NewLine
                + "Make one now and pick your contest?",
                "No contest log", HolyMsgType.Info, this, 0,
                "Create New Contest Log", "Not now",
                "#FFC107", "#1A1A1A", "#1565C0");

            if (make) CreateNewContestLog(this);
        }

        private void ActivitySig_TextChanged(object sender, TextChangedEventArgs e)
        {
            ShowActivitySigMeaning();
            RememberActivityProgram();
            try { radioPanel?.ShowWwffHint(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ESC MUST NOT THROW THE PROGRAM AWAY. A WPF ComboBox treats Escape as "undo what I typed" and
        // puts back whatever was selected before - and the main window treats it as "clear the entry" -
        // so a chosen programme could vanish from a key pressed for something else entirely. Here Escape
        // does one thing only: close the list if it is open. The value stays either way, and the box is
        // cleared the way everything else on the form is cleared, by Clear (F9).
        private void ActivitySig_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;

            ComboBox box = sender as ComboBox;
            if (box != null && box.IsDropDownOpen) box.IsDropDownOpen = false;
            e.Handled = true;
        }

        // A click anywhere on the box opens the list, not only on the 10px chevron - the same rule the
        // RST boxes follow, and for the same reason. On the way UP, because opening it on the way down
        // is undone by the ComboBox's own handling of the release (measured; see RST_PreviewMouseLeftButtonUp).
        private void ActivitySig_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ComboBox box = sender as ComboBox;
            if (box == null || box.Items.Count == 0) return;

            Point p = e.GetPosition(box);
            if (p.X < 0 || p.Y < 0 || p.X > box.ActualWidth || p.Y > box.ActualHeight) return;

            box.IsDropDownOpen = !box.IsDropDownOpen;
            e.Handled = true;
        }

        // Contest mode has no room for this row - the contest layout already reaches the bottom of the
        // form - and no use for it either: in a contest the exchange is the contest's own. Hiding it
        // leaves every contest position exactly as it was before the row existed.
        // ── PARK SUGGESTIONS ─────────────────────────────────────────────────────────────────────────
        //
        // The DX callsign's Suggest, for parks: typing in WWFF, in My Fauna, or in Exchange during a
        // WWFF activation opens a list of matching parks with their names under the box. Up/Down
        // move, Enter or Tab or a click takes one, Esc closes. The list is built in code, one popup
        // shared by the three boxes.
        private System.Windows.Controls.Primitives.Popup parkPopup;
        private ListBox parkList;
        private Border parkFrame;
        private static readonly Brush ParkListTypingBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFD));
        private TextBox parkTarget;
        private bool fillingPark;
        private const int MaxParkSuggestions = 30;

        private void WireParkSuggest(TextBox box)
        {
            box.PreviewKeyDown += ParkBox_PreviewKeyDown;
            box.LostKeyboardFocus += (s, e) => { if (parkTarget == box) CloseParkSuggestions(); };
        }

        private bool ParkSuggestApplies(TextBox box)
        {
            if (box == TB_WwffRef) return true;
            // My Fauna and Exchange are parks only while the activity is WWFF.
            return WwffExchangeOn && (box == TB_ActivitySigInfo || box == TB_Exchange);
        }

        private void UpdateParkSuggestions(TextBox box)
        {
            if (fillingPark || box == null) return;
            if (!box.IsKeyboardFocusWithin || !ParkSuggestApplies(box)) { if (parkTarget == box) CloseParkSuggestions(); return; }

            if (!WwffDirectory.Loaded)
            {
                // First use: fetch the list, and answer this same typing once it is here.
                WwffDirectory.EnsureLoaded(this, delegate { RefreshWwffColours(); UpdateParkSuggestions(box); });
                return;
            }
            WwffDirectory.EnsureLoaded(this, RefreshWwffColours);   // a no-op unless the list is old

            string typed = (box.Text ?? "").Trim();
            List<WwffDirectory.Park> matches = WwffDirectory.Suggest(typed, MaxParkSuggestions);
            // Nothing to offer, or the box already holds exactly the one park: no list.
            if (matches.Count == 0 || (matches.Count == 1 && string.Equals(matches[0].Ref, typed, StringComparison.OrdinalIgnoreCase)))
            {
                CloseParkSuggestions();
                return;
            }

            EnsureParkPopup();
            parkTarget = box;
            parkPopup.PlacementTarget = box;
            parkList.ItemsSource = matches;
            // Very light blue while what is typed is not yet a whole park, white once it is. Light
            // blue, not the box's pale red (tried, and he did not like it) - and lighter than the
            // #7FBFFF highlight, so the row Up/Down is on still stands out.
            parkFrame.Background = IsValidWwff(typed) && WwffDirectory.IsKnown(typed) != false
                ? Brushes.White : ParkListTypingBrush;
            parkList.SelectedIndex = 0;
            parkList.ScrollIntoView(parkList.SelectedItem);
            parkPopup.IsOpen = true;
        }

        private void EnsureParkPopup()
        {
            if (parkPopup != null) return;

            // Not focusable, list or rows: the cursor has to stay in the box being typed in.
            // ITS OWN HIGHLIGHT, the callsign list's blue. A list that never has the focus paints its
            // selected row in the "inactive" colour, and that was measured at #F2F2F2 - white on
            // white - so Up/Down moved the selection and nobody could see it move. #7FBFFF: a shade darker than
            // the callsign list's #A9D4FF, asked for by eye.
            var rowStyle = (Style)System.Windows.Markup.XamlReader.Parse(
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListBoxItem'>" +
                "<Setter Property='Focusable' Value='False'/>" +
                "<Setter Property='Foreground' Value='Black'/>" +
                "<Setter Property='Template'><Setter.Value>" +
                "<ControlTemplate TargetType='ListBoxItem'>" +
                "<Border x:Name='Bd' Background='Transparent' Padding='4,2,8,2'><ContentPresenter/></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsSelected' Value='True'><Setter TargetName='Bd' Property='Background' Value='#7FBFFF'/></Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate></Setter.Value></Setter></Style>");

            parkList = new ListBox
            {
                FontSize = 16,
                MaxHeight = 330,
                MinWidth = 260,
                MaxWidth = 560,
                Focusable = false,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemContainerStyle = rowStyle
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(parkList, ScrollBarVisibility.Disabled);
            parkList.PreviewMouseLeftButtonUp += (s, e) =>
            {
                var item = ItemsControl.ContainerFromElement(parkList, e.OriginalSource as DependencyObject) as ListBoxItem;
                if (item == null) return;
                TakePark(item.DataContext as WwffDirectory.Park);
                e.Handled = true;
            };

            parkPopup = new System.Windows.Controls.Primitives.Popup
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = true,
                AllowsTransparency = true,
                Child = parkFrame = new Border
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    Background = Brushes.White,
                    Child = parkList
                }
            };
        }

        private void CloseParkSuggestions()
        {
            if (parkPopup != null) parkPopup.IsOpen = false;
            parkTarget = null;
        }

        private void ParkBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (parkPopup == null || !parkPopup.IsOpen || parkTarget != sender || parkList.Items.Count == 0) return;
            switch (e.Key)
            {
                case System.Windows.Input.Key.Down:
                    parkList.SelectedIndex = Math.Min(parkList.SelectedIndex + 1, parkList.Items.Count - 1);
                    parkList.ScrollIntoView(parkList.SelectedItem);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Up:
                    parkList.SelectedIndex = Math.Max(parkList.SelectedIndex - 1, 0);
                    parkList.ScrollIntoView(parkList.SelectedItem);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Enter:
                case System.Windows.Input.Key.Tab:
                    TakePark(parkList.SelectedItem as WwffDirectory.Park);
                    // Enter is taken by the list; Tab still moves on to the next box as usual.
                    if (e.Key == System.Windows.Input.Key.Enter) e.Handled = true;
                    break;
                case System.Windows.Input.Key.Escape:
                    CloseParkSuggestions();
                    e.Handled = true;
                    break;
            }
        }

        private void TakePark(WwffDirectory.Park park)
        {
            TextBox box = parkTarget;
            CloseParkSuggestions();
            if (park == null || box == null) return;
            fillingPark = true;
            try
            {
                box.Text = park.Ref;
                box.CaretIndex = box.Text.Length;
            }
            finally { fillingPark = false; }
        }

        // ── A PARK THAT IS NOT ON THE LIST ───────────────────────────────────────────────────────────
        //
        // Right format, but no such park (or a deleted one) - a typo the format check cannot see,
        // like GIFF-1001 for GIFF-0101. Orange, not red: the list is refreshed monthly, and a park
        // added last week may truly exist; the tooltip says which list it was checked against.
        private static readonly Brush UnknownParkBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x80));

        private static string ParkNotListedTip(string reference)
        {
            return reference + " is not on the WWFF park list. Check the number.";
        }

        private void RefreshWwffColours()
        {
            ApplyActivityBoxColour(TB_WwffRef);
            ApplyMyWwffColour();
        }

        private void SetActivityRowVisible(bool visible)
        {
            if (ActivityRow == null) return;
            ActivityRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ── THE WWFF PARK LIST, AND SUGGESTIONS FROM IT ─────────────────────────────────────────────────
    //
    // The official directory (wwff.co/wwff-data/wwff_directory.csv, read 2026-09-29: 68,861 rows -
    // 67,453 active, 1,374 deleted, 33 "national"; every real reference matches WwffPattern). It is
    // 24 MB, of which only the reference, the name and the locator are wanted, so it is boiled down
    // to a ~3 MB text file beside the log database and read from there. Refreshed when older than
    // RefreshDays, in the background, only once somebody actually uses WWFF - nobody who never
    // activates a park pays for the download.
    internal static class WwffDirectory
    {
        private const string Url = "https://wwff.co/wwff-data/wwff_directory.csv";
        private const int RefreshDays = 30;

        internal sealed class Park
        {
            public string Ref;
            public string Name;
            public string Locator;
            public override string ToString() { return Ref + "   " + Name; }
        }

        private static List<Park> _parks;                   // sorted by Ref, ordinal
        private static Dictionary<string, Park> _byRef;
        private static int _busy;                            // 1 while a load/download runs
        private static readonly List<Action> _waiting = new List<Action>();

        public static bool Loaded { get { return _parks != null; } }

        /// <summary>The list as it stands on disk, for Help > About: the number of parks and the day
        /// it was downloaded. False when it has never been downloaded.</summary>
        public static bool OnDisk(out int parks, out DateTime updated)
        {
            parks = 0;
            updated = DateTime.MinValue;
            try
            {
                string path = LocalPath;
                if (!System.IO.File.Exists(path)) return false;
                updated = System.IO.File.GetLastWriteTime(path);
                List<Park> loaded = _parks;
                if (loaded != null) parks = loaded.Count;
                else foreach (string line in System.IO.File.ReadLines(path)) if (line.Length > 0) parks++;
                return parks > 0;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        private static string LocalPath
        {
            get
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return System.IO.Path.Combine(appData, fvi.CompanyName, fvi.ProductName, "wwff_parks.txt");
            }
        }

        /// <summary>Loads the list (and downloads it when missing or old) off the UI thread; onReady
        /// runs on the window's thread once it is there. Safe to call as often as you like.</summary>
        public static void EnsureLoaded(Window owner, Action onReady)
        {
            string path = LocalPath;
            bool fresh = false;
            try { fresh = System.IO.File.Exists(path) && (DateTime.Now - System.IO.File.GetLastWriteTime(path)).TotalDays < RefreshDays; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            if (Loaded && (fresh || _triedDownload)) return;

            if (onReady != null) lock (_waiting) _waiting.Add(onReady);
            if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) return;

            System.Threading.Tasks.Task.Run(delegate
            {
                try
                {
                    // What is on disk first, so suggestions work at once - and offline.
                    if (!Loaded && System.IO.File.Exists(path)) Use(ReadLocal(path));
                    if (!fresh && !_triedDownload)
                    {
                        // Once per run: a failed download is not retried on every keystroke.
                        _triedDownload = true;
                        List<Park> downloaded = Download();
                        // A short answer is a moved or broken link, never the real list.
                        if (downloaded != null && downloaded.Count > 10000)
                        {
                            WriteLocal(path, downloaded);
                            Use(downloaded);
                        }
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                finally { System.Threading.Interlocked.Exchange(ref _busy, 0); }

                Action[] calls;
                lock (_waiting) { calls = _waiting.ToArray(); _waiting.Clear(); }
                if (owner != null && calls.Length > 0)
                    owner.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        foreach (Action a in calls)
                            try { a(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                    }));
            });
        }

        private static bool _triedDownload;

        private static void Use(List<Park> parks)
        {
            parks.Sort((a, b) => string.CompareOrdinal(a.Ref, b.Ref));
            var byRef = new Dictionary<string, Park>(StringComparer.OrdinalIgnoreCase);
            foreach (Park p in parks) byRef[p.Ref] = p;
            _byRef = byRef;
            _parks = parks;
        }

        private static List<Park> ReadLocal(string path)
        {
            var list = new List<Park>();
            foreach (string line in System.IO.File.ReadAllLines(path, System.Text.Encoding.UTF8))
            {
                string[] f = line.Split('\t');
                if (f.Length < 2) continue;
                list.Add(new Park { Ref = f[0], Name = f[1], Locator = f.Length > 2 ? f[2] : "" });
            }
            return list;
        }

        private static void WriteLocal(string path, List<Park> parks)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            var sb = new System.Text.StringBuilder(parks.Count * 48);
            foreach (Park p in parks)
                sb.Append(p.Ref).Append('\t').Append(p.Name).Append('\t').Append(p.Locator).Append('\n');
            string tmp = path + ".new";
            System.IO.File.WriteAllText(tmp, sb.ToString(), System.Text.Encoding.UTF8);
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.File.Move(tmp, path);
        }

        // Only the parks one can still work: "active", and the 33 "national" ones. Deleted parks and
        // the few junk rows ("Select-0001") are dropped.
        private static List<Park> Download()
        {
            try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            using (var stream = http.GetStreamAsync(Url).GetAwaiter().GetResult())
            using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
            {
                var list = new List<Park>();
                List<string> header = ReadCsvRow(reader);
                if (header == null) return null;
                int iRef = header.IndexOf("reference"), iStatus = header.IndexOf("status"),
                    iName = header.IndexOf("name"), iLoc = header.IndexOf("iaruLocator");
                if (iRef < 0 || iStatus < 0 || iName < 0) return null;

                List<string> row;
                while ((row = ReadCsvRow(reader)) != null)
                {
                    if (row.Count <= Math.Max(iRef, Math.Max(iStatus, iName))) continue;
                    string status = row[iStatus].Trim();
                    if (status != "active" && status != "national") continue;
                    string reference = row[iRef].Trim().ToUpperInvariant();
                    if (!MainWindow.IsValidWwff(reference)) continue;
                    string loc = iLoc >= 0 && iLoc < row.Count ? row[iLoc].Trim() : "";
                    if (loc == "-") loc = "";
                    list.Add(new Park
                    {
                        Ref = reference,
                        Name = row[iName].Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim(),
                        Locator = loc
                    });
                }
                return list;
            }
        }

        // One CSV record: commas between fields, a field in double quotes may hold commas, line
        // breaks and "" for a quote. Null at the end of the file.
        private static List<string> ReadCsvRow(System.IO.TextReader r)
        {
            int c = r.Peek();
            if (c < 0) return null;
            var fields = new List<string>();
            var field = new System.Text.StringBuilder();
            bool quoted = false;
            while (true)
            {
                c = r.Read();
                if (c < 0) { fields.Add(field.ToString()); return fields; }
                char ch = (char)c;
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (r.Peek() == '"') { r.Read(); field.Append('"'); }
                        else quoted = false;
                    }
                    else field.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') { fields.Add(field.ToString()); field.Clear(); }
                else if (ch == '\n') { fields.Add(field.ToString().TrimEnd('\r')); return fields; }
                else field.Append(ch);
            }
        }

        /// <summary>True/false once the list is loaded; null before, when nothing can be said.</summary>
        public static bool? IsKnown(string reference)
        {
            Dictionary<string, Park> byRef = _byRef;
            if (byRef == null) return null;
            return byRef.ContainsKey((reference ?? "").Trim());
        }

        public static Park Find(string reference)
        {
            Dictionary<string, Park> byRef = _byRef;
            Park p;
            return byRef != null && byRef.TryGetValue((reference ?? "").Trim(), out p) ? p : null;
        }

        /// <summary>Parks whose reference starts with what was typed; when that finds few and the
        /// typing has letters, parks whose NAME holds it as well ("HULA").</summary>
        public static List<Park> Suggest(string typed, int max)
        {
            var result = new List<Park>();
            List<Park> parks = _parks;
            string t = (typed ?? "").Trim().ToUpperInvariant();
            if (parks == null || t.Length == 0) return result;

            int lo = 0, hi = parks.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (string.CompareOrdinal(parks[mid].Ref, t) < 0) lo = mid + 1; else hi = mid;
            }
            for (int i = lo; i < parks.Count && result.Count < max; i++)
            {
                if (!parks[i].Ref.StartsWith(t, StringComparison.Ordinal)) break;
                result.Add(parks[i]);
            }

            bool hasLetters = false;
            foreach (char ch in t) if (char.IsLetter(ch)) { hasLetters = true; break; }
            if (result.Count < max && t.Length >= 3 && hasLetters)
            {
                foreach (Park p in parks)
                {
                    if (result.Count >= max) break;
                    if (p.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 && !result.Contains(p))
                        result.Add(p);
                }
            }
            return result;
        }
    }
}
