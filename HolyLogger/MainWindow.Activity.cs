using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HolyParser;

namespace HolyLogger
{
    // ACTIVITY PROGRAMME REFERENCES on the main form.
    //
    // Four programmes have a box each because ADIF gives them a field each; every other programme -
    // castles, mills, lighthouses, and whatever is founded next year - goes through the Other button
    // into the standard SIG / SIG_INFO pair. That split is the whole design: the list of programmes
    // has no end, so only the four the standard names are allowed to take up screen space.
    public partial class MainWindow
    {
        // What each programme's reference has to look like, straight out of the ADIF data types.
        // Anchored and upper-case only: every box on the row is CharacterCasing="Upper".
        private static readonly Regex IotaPattern = new Regex(@"^(AF|AN|AS|EU|NA|OC|SA)-\d{3}$", RegexOptions.Compiled);
        private static readonly Regex SotaPattern = new Regex(@"^[A-Z0-9]{1,8}/[A-Z]{2}-\d{3}$", RegexOptions.Compiled);
        private static readonly Regex PotaPattern = new Regex(@"^[A-Z0-9]{1,4}-\d{4,5}(@[A-Z0-9\-]{1,6})?$", RegexOptions.Compiled);
        private static readonly Regex WwffPattern = new Regex(@"^[A-Z0-9]{1,4}FF-\d{4}$", RegexOptions.Compiled);

        // The pale red a box wears while what is in it is not a valid reference. Not the theme's Danger
        // brush: that one is for text, and behind 16pt characters it is far too dark to read through.
        private static readonly Brush BadReferenceBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xE1, 0xE1));

        // Whatever the Other button last collected, held here until the QSO is logged.
        private string activitySig;
        private string activitySigInfo;

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

        // Which programme a lone reference belongs to, or null when it is not a reference at all. The
        // four formats cannot be confused with each other, which is what lets the Verify Log tool offer
        // to move a reference out of a comment without having to ask the operator which programme it is.
        public static string ProgrammeOf(string reference)
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
            qso.Sig = (activitySig ?? "").Trim();
            qso.SigInfo = (activitySigInfo ?? "").Trim();
        }

        private void ActivityFromQso(QSO qso)
        {
            if (qso == null) { ClearActivityRow(); return; }
            TB_Iota.Text = qso.Iota ?? "";
            TB_SotaRef.Text = qso.SotaRef ?? "";
            TB_PotaRef.Text = qso.PotaRef ?? "";
            TB_WwffRef.Text = qso.WwffRef ?? "";
            activitySig = qso.Sig ?? "";
            activitySigInfo = qso.SigInfo ?? "";
            UpdateOtherActivityButton();
        }

        private void ClearActivityRow()
        {
            TB_Iota.Clear();
            TB_SotaRef.Clear();
            TB_PotaRef.Clear();
            TB_WwffRef.Clear();
            activitySig = null;
            activitySigInfo = null;
            UpdateOtherActivityButton();
        }

        // The button carries what it holds, so an "other" programme is visible on the form without
        // opening anything. It goes back to reading "Other..." when there is nothing set.
        private void UpdateOtherActivityButton()
        {
            if (Btn_OtherActivity == null) return;
            string sig = (activitySig ?? "").Trim();
            string info = (activitySigInfo ?? "").Trim();
            if (sig.Length == 0 && info.Length == 0)
            {
                Btn_OtherActivity.Content = "Other…";
                Btn_OtherActivity.FontWeight = FontWeights.Normal;
                Btn_OtherActivity.ToolTip = "Any other programme - castles, mills, lighthouses. Shows what is set once you choose one.";
                return;
            }
            string shown = (sig + " " + info).Trim();
            Btn_OtherActivity.Content = shown;
            Btn_OtherActivity.FontWeight = FontWeights.Bold;
            Btn_OtherActivity.ToolTip = "This QSO carries " + shown + ". Click to change or clear it.";
        }

        private void Btn_OtherActivity_Click(object sender, RoutedEventArgs e)
        {
            var w = new OtherActivityWindow(activitySig, activitySigInfo) { Owner = this };
            if (w.ShowDialog() != true) return;
            activitySig = w.Programme;
            activitySigInfo = w.Reference;
            UpdateOtherActivityButton();
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
