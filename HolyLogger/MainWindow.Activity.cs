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


        // What an activity box should look like when it has nothing to complain about: the form's
        // ordinary input colour, or the edit-mode yellow while a logged QSO is open for editing.
        // Kept here because these boxes have a second background of their own (the pale red below) and
        // the two have to agree on which one wins.
        private Brush activityNormalBg;

        private void SetActivityNormalBackground(Brush background)
        {
            activityNormalBg = background;
            foreach (TextBox box in ActivityBoxes()) ApplyActivityBoxColour(box);
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
        }

        // Pale red while the box holds something that is not a reference; otherwise back to whatever
        // the rest of the form's editable boxes are wearing - white normally, yellow in edit mode.
        private void ApplyActivityBoxColour(TextBox box)
        {
            if (box == null) return;
            string text = (box.Text ?? "").Trim();
            bool ok = text.Length == 0 || IsValidActivityBox(box, text);
            if (!ok) { box.Background = BadReferenceBrush; return; }
            if (activityNormalBg != null) box.Background = activityNormalBg;
            else box.ClearValue(Control.BackgroundProperty);
        }

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
                bad.Add("WWFF \"" + TB_WwffRef.Text.Trim() + "\" - a nature reference looks like 4XFF-0016.");
            return bad;
        }

        // Called just before a QSO is saved. Returns false only when the operator chooses to go back and
        // fix a malformed reference; saying "log it anyway" keeps their typing rather than dropping it.
        private bool ConfirmActivityBeforeSave()
        {
            List<string> bad = ActivityComplaints();
            if (bad.Count == 0) return true;
            string message = (bad.Count == 1 ? "This reference is not in the standard form:" : "These references are not in the standard form:")
                + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine + Environment.NewLine, bad.ToArray())
                + Environment.NewLine + Environment.NewLine
                + "Log the QSO with it as typed anyway?";
            return HolyMessageBox.ShowConfirm(message, "Check the reference", HolyMsgType.Warning, this);
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
        }

        private void ActivityFromQso(QSO qso)
        {
            if (qso == null) { ClearActivityRow(); return; }
            TB_Iota.Text = qso.Iota ?? "";
            TB_SotaRef.Text = qso.SotaRef ?? "";
            TB_PotaRef.Text = qso.PotaRef ?? "";
            TB_WwffRef.Text = qso.WwffRef ?? "";
            CB_ActivitySig.Text = qso.Sig ?? "";
            TB_ActivitySigInfo.Text = qso.SigInfo ?? "";
            ShowActivitySigMeaning();
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
            TB_ActivitySigInfo.Clear();
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
                new KeyValuePair<string, string>(ContestListEntry, "in a contest? read this")
            };
            list.AddRange(OtherActivityWindow.Known);
            CB_ActivitySig.ItemsSource = list;
            CB_ActivitySig.Text = (Properties.Settings.Default.LastActivityProgram ?? "").Trim();

            // Wired AFTER the text above is put in, so filling the box is not mistaken for the operator
            // choosing something.
            CB_ActivitySig.DropDownOpened += ActivitySig_DropDownOpened;
            CB_ActivitySig.SelectionChanged += ActivitySig_SelectionChanged;

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
                TB_ActivitySigHint.Text = OtherActivityWindow.DescriptionOf(typed);

            // The word "Program" shows only while the box is empty - it is a label, not a value.
            if (TB_ActivitySigPlaceholder != null)
                TB_ActivitySigPlaceholder.Visibility = typed.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

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

            const double hintLeft = 335, gap = 6;
            double stopAt = 638;
            if (Btn_TryAgain != null && Btn_TryAgain.Visibility == Visibility.Visible) stopAt = 436;
            else if (Btn_UndoMain != null && Btn_UndoMain.Visibility == Visibility.Visible) stopAt = 542;

            TB_ActivitySigHint.MaxWidth = stopAt - hintLeft - gap;
        }

        // THE ONE LINE ON THE LIST THAT IS NOT A PROGRAM.
        private const string ContestListEntry = "Contest";

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
        private void SetActivityRowVisible(bool visible)
        {
            if (ActivityRow == null) return;
            ActivityRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
