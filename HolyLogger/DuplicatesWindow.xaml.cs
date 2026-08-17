using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HolyParser;

namespace HolyLogger
{
    // ONE DUPLICATE GROUP: the same contact, logged more than once. Members are ordered as they were
    // worked, so the first is the one that stays and every other is a copy.
    public sealed class DupGroup
    {
        public List<QSO> Members = new List<QSO>();

        public QSO Keep { get { return Members[0]; } }
        public IEnumerable<QSO> Extras { get { return Members.Skip(1); } }

        // THE COMMENTS THAT ARE ACTUALLY DIFFERENT, in the order they appear. Blanks are not comments
        // and never count as a difference: a copy with nothing to say disagrees with nobody.
        public List<string> Comments = new List<string>();

        // Two or more different things written on the same contact. Nobody but the operator can say
        // which is right, so a group like this is never removed without asking - it goes to the second
        // step, where the comments are shown side by side.
        public bool NeedsChoice { get { return Comments.Count > 1; } }

        // The comment to give the contact that stays. Filled by the second step from what the operator
        // ticked; for a group that needs no choice it is simply the one comment the group had, which
        // moves across when the contact that stays has none of its own.
        public string ChosenComment;

        // The operator ticked nothing for this group, so it is left exactly as it is - both contacts
        // stay in the log rather than one being removed on a guess.
        public bool Skipped;
    }

    // FINDING THE GROUPS, in the one place both Tools > Remove Duplicates and the Log Fixer ask.
    // "The same contact" is DataAccess.MatchKey and nothing else, so the two windows can never come to
    // different answers about the same log.
    public static class DuplicateScan
    {
        public static List<DupGroup> Find(IEnumerable<QSO> qsos)
        {
            var groups = new List<DupGroup>();
            if (qsos == null) return groups;

            var byKey = new Dictionary<string, DupGroup>(StringComparer.Ordinal);
            var order = new List<DupGroup>();

            foreach (QSO q in qsos)
            {
                if (q == null) continue;
                string key = DataAccess.MatchKey(q);
                if (key == null) continue;      // too incomplete to identify: never grouped with anything

                DupGroup g;
                if (!byKey.TryGetValue(key, out g))
                {
                    g = new DupGroup();
                    byKey[key] = g;
                    order.Add(g);
                }
                g.Members.Add(q);
            }

            foreach (DupGroup g in order)
            {
                if (g.Members.Count < 2) continue;

                // Earliest first. Every member already shares the minute, so this is the seconds and
                // then the order they were written - enough to be the same answer every time it runs.
                g.Members = g.Members
                    .OrderBy(q => (q.Time ?? string.Empty).Trim())
                    .ThenBy(q => q.id)
                    .ToList();

                foreach (QSO q in g.Members)
                {
                    string c = (q.Comment ?? string.Empty).Trim();
                    if (c.Length == 0) continue;
                    if (!g.Comments.Any(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase)))
                        g.Comments.Add(c);
                }

                // One comment in the whole group: nothing to choose between. If the contact that stays
                // is the one without it, it takes it over - otherwise removing a copy would throw away
                // the only note anybody wrote about that contact.
                if (g.Comments.Count == 1) g.ChosenComment = g.Comments[0];

                groups.Add(g);
            }

            return groups;
        }
    }

    // Review window for Tools > Remove Duplicates: shows every duplicate group found in the
    // active log (rows of the same group share a background color; adjacent groups alternate),
    // states exactly what will happen (first of each group is KEPT, the rest DELETED), and lets
    // the operator confirm or back out. No data is touched until the delete button is pressed --
    // the caller performs the actual deletion when ShowDialog returns true.
    public partial class DuplicatesWindow : Window
    {
        // One grid row.
        public class Row
        {
            public int GroupNo { get; set; }
            public string Action { get; set; }        // "KEEP" / "DELETE"
            public Brush ActionBrush { get; set; }
            public Brush RowBackground { get; set; }
            public string Date { get; set; }
            public string Time { get; set; }
            public string DXCall { get; set; }
            public string MyCall { get; set; }
            public string Operator { get; set; }
            public string Freq { get; set; }
            public string Band { get; set; }
            public string Mode { get; set; }
        }

        public DuplicatesWindow(List<List<QSO>> groups)
        {
            InitializeComponent();
            WindowBounds.Attach(this, "Duplicates");   // remember position + size
            MaxWidth = SystemParameters.WorkArea.Width;
            DupsGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();

            // Strongly-contrasting alternating group backgrounds: near-black vs a bright blue, so
            // adjacent duplicate groups are unmistakable. Light schemes use white vs light-blue.
            bool dark = ThemeManager.IsDark;
            Brush bgEven = Frozen(dark ? "#0C0E12" : "#FFFFFF");   // near-black / white
            Brush bgOdd  = Frozen(dark ? "#1E64C8" : "#BBD6F7");   // bright blue / light blue
            // Bright green so KEEP stays readable on both the black and the bright-blue rows.
            var keepBrush   = Frozen("#54D66A");
            var deleteBrush = Frozen(dark ? "#FF6B6B" : "#C62828");

            int extras = 0;
            var rows = new List<Row>();
            for (int g = 0; g < groups.Count; g++)
            {
                Brush bg = (g % 2 == 0) ? bgEven : bgOdd;
                for (int i = 0; i < groups[g].Count; i++)
                {
                    QSO q = groups[g][i];
                    bool keep = i == 0;
                    if (!keep) extras++;
                    rows.Add(new Row
                    {
                        GroupNo = g + 1,
                        Action = keep ? "KEEP" : "DELETE",
                        ActionBrush = keep ? keepBrush : deleteBrush,
                        RowBackground = bg,
                        Date = FormatDate(q.Date),
                        Time = FormatTime(q.Time),
                        DXCall = q.DXCall,
                        MyCall = q.MyCall,
                        Operator = q.Operator,
                        Freq = q.Freq,
                        Band = q.Band,
                        Mode = q.Mode,
                    });
                }
            }
            DupsGrid.ItemsSource = rows;

            TB_Header.Text = $"Found {extras:N0} duplicate QSO(s) in {groups.Count:N0} group(s)";
            TB_Summary.Text = $"{rows.Count:N0} QSOs shown — {groups.Count:N0} will be kept, {extras:N0} deleted.";
            Btn_Delete.Content = $"Delete {extras:N0} Duplicate(s)";
        }

        // ── STEP TWO: the copies whose comments disagree ────────────────────────────────────────
        //
        // One row per contact in each group, so the operator sees WHOSE comment each one is before
        // choosing. Keep is the only thing he can change; everything else is the contact itself.
        public class CommentRow : INotifyPropertyChanged
        {
            public DupGroup Group;
            public QSO Qso;

            // The other rows of the same group, so this one can tell whether it is the last comment
            // standing.
            public List<CommentRow> Siblings;

            public int GroupNo { get; set; }
            public Brush RowBackground { get; set; }
            public string Date { get; set; }
            public string Time { get; set; }
            public string DXCall { get; set; }
            public string Band { get; set; }
            public string Mode { get; set; }
            public string Comment { get; set; }

            private bool keep;
            public bool Keep
            {
                get { return keep; }
                set
                {
                    if (keep == value) return;

                    // A GROUP CAN NEVER END UP WITH NO COMMENT AT ALL. Emptying the last tick would
                    // delete the only thing anybody ever wrote about this contact, and would do it
                    // silently - so the last one standing refuses to come off. The tick is put back
                    // through the dispatcher because the binding is in the middle of pushing this
                    // value down; telling it "no" while it is mid-write leaves the box drawn unticked.
                    if (!value && IsLastTicked())
                    {
                        var d = Application.Current == null ? null : Application.Current.Dispatcher;
                        if (d != null) d.BeginInvoke(new Action(() => Raise("Keep")));
                        return;
                    }

                    keep = value;
                    Raise("Keep");
                }
            }

            private bool IsLastTicked()
            {
                if (Siblings == null) return false;
                int ticked = 0;
                foreach (CommentRow r in Siblings)
                    if (r.keep && r.Comment.Length > 0) ticked++;
                return ticked <= 1;
            }

            // No comment, no box: there is nothing on this row to keep or throw away.
            public Visibility BoxVisibility { get; set; }

            public event PropertyChangedEventHandler PropertyChanged;
            private void Raise(string name)
            {
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name));
            }
        }

        private List<DupGroup> _commentGroups;
        private List<CommentRow> _commentRows;

        // The groups as the operator left them: ChosenComment filled from what he ticked, and Skipped
        // set on any group he ticked nothing in. The caller does the removing, exactly as in step one.
        public List<DupGroup> Resolved { get { return _commentGroups; } }

        public DuplicatesWindow(List<DupGroup> conflicts)
        {
            InitializeComponent();
            WindowBounds.Attach(this, "DuplicateComments");
            MaxWidth = SystemParameters.WorkArea.Width;
            Title = "Remove Duplicates — which comment to keep";

            G_Stage1.Visibility = Visibility.Collapsed;
            G_Stage2.Visibility = Visibility.Visible;
            CommentsGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();

            _commentGroups = conflicts ?? new List<DupGroup>();

            bool dark = ThemeManager.IsDark;
            Brush bgEven = Frozen(dark ? "#0C0E12" : "#FFFFFF");
            Brush bgOdd = Frozen(dark ? "#1E64C8" : "#BBD6F7");

            int extras = 0;
            _commentRows = new List<CommentRow>();
            for (int g = 0; g < _commentGroups.Count; g++)
            {
                DupGroup group = _commentGroups[g];
                Brush bg = (g % 2 == 0) ? bgEven : bgOdd;

                var inThisGroup = new List<CommentRow>();
                for (int i = 0; i < group.Members.Count; i++)
                {
                    QSO q = group.Members[i];
                    if (i > 0) extras++;

                    string comment = (q.Comment ?? string.Empty).Trim();
                    inThisGroup.Add(new CommentRow
                    {
                        Group = group,
                        Qso = q,
                        GroupNo = g + 1,
                        RowBackground = bg,
                        Date = FormatDate(q.Date),
                        Time = FormatTime(q.Time),
                        DXCall = q.DXCall,
                        Band = q.Band,
                        Mode = q.Mode,
                        Comment = comment,
                        BoxVisibility = comment.Length > 0 ? Visibility.Visible : Visibility.Collapsed
                    });
                }

                // THE FIRST COMMENT IS TICKED, so pressing OK without touching anything keeps the
                // oldest of them - a defensible answer rather than none at all. Set before the rows
                // know about each other, so the rule that refuses to untick the last one cannot
                // interfere with setting up the first.
                foreach (CommentRow r in inThisGroup)
                    if (r.Comment.Length > 0) { r.Keep = true; break; }

                foreach (CommentRow r in inThisGroup) r.Siblings = inThisGroup;
                _commentRows.AddRange(inThisGroup);
            }

            CommentsGrid.ItemsSource = _commentRows;

            TB_Header2.Text = string.Format("{0:N0} duplicate{1} in {2:N0} group{3} where the comments differ",
                extras, extras == 1 ? "" : "s", _commentGroups.Count, _commentGroups.Count == 1 ? "" : "s");
            TB_Summary2.Text = "One comment in each group must stay ticked.";
        }

        // What the operator ticked, written back onto the groups. A group with no tick is marked
        // Skipped and the caller removes nothing from it; a group with several is given them joined,
        // in the order they appear on screen, so nothing anybody wrote is thrown away.
        private bool HarvestComments()
        {
            int willRemove = 0;
            foreach (DupGroup g in _commentGroups)
            {
                var ticked = _commentRows
                    .Where(r => r.Group == g && r.Keep && r.Comment.Length > 0)
                    .Select(r => r.Comment)
                    .ToList();

                if (ticked.Count == 0)
                {
                    g.Skipped = true;
                    g.ChosenComment = null;
                    continue;
                }

                g.Skipped = false;
                g.ChosenComment = string.Join(" / ", ticked);
                willRemove += g.Members.Count - 1;
            }

            if (willRemove == 0)
            {
                HolyMessageBox.Show("Nothing is ticked, so nothing would be removed.\n\n"
                    + "Tick the comment you want to keep in a group, then press the button again — "
                    + "or press Cancel to leave all of them alone.",
                    "Remove Duplicates", HolyMsgType.Info, this);
                return false;
            }

            return true;
        }

        private static Brush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        // Once the auto-width columns have measured against their content, shrink the window to the
        // table's real width (plus borders/scrollbar/margins) so it hugs the table instead of being
        // driven wide by the wrapping description line. Capped to the screen work area.
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Step two has a Comment column that takes whatever room is left, so hugging the columns
            // would squeeze the very thing the operator opened the window to read. What it must do
            // instead is open wide enough that the line along the foot is READABLE: the window
            // remembers its size, and a width inherited from somewhere else trimmed that line to
            // "Untouched groups ar..." - a hint that explains nothing is worse than no hint.
            if (_commentGroups != null)
            {
                TB_Summary2.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double footer = TB_Summary2.DesiredSize.Width
                                + Btn_Delete2.Width + Btn_Cancel2.Width
                                + 10          // gap between the two buttons
                                + 28          // the grid's left and right margins
                                + 24;         // window frame, and air after the text

                // AND THE COMMENT ITSELF, which is the entire point of this screen. It is the column
                // that takes whatever is left over, so a window opened at someone else's remembered
                // width squeezed it to about forty pixels and stood the comment on end, one letter to
                // a line. The eight columns before it are measured and a readable width demanded for
                // the ninth, rather than a total guessed at.
                double before = 0;
                for (int i = 0; i < CommentsGrid.Columns.Count - 1; i++)
                    before += CommentsGrid.Columns[i].ActualWidth;

                double table = before
                               + 420         // room for a comment worth reading
                               + 22          // the vertical scrollbar
                               + 2           // the grid's own border
                               + 28          // the grid's left and right margins
                               + 8;          // window frame

                double need = Math.Min(Math.Max(footer, table), SystemParameters.WorkArea.Width);
                if (Width < need) Width = need;
                if (MinWidth < need) MinWidth = need;
                return;
            }

            double columns = 0;
            foreach (var c in DupsGrid.Columns) columns += c.ActualWidth;
            if (columns <= 0) return;

            // grid border (2) + vertical scrollbar (~20) + window margins (28) + frame (~4).
            double target = columns + 54;
            double max = SystemParameters.WorkArea.Width;
            Width = System.Math.Max(MinWidth, System.Math.Min(target, max));
        }

        // yyyyMMdd -> dd-MM-yyyy; unexpected values shown as-is.
        private static string FormatDate(string raw)
        {
            if (!string.IsNullOrWhiteSpace(raw) && raw.Length == 8 &&
                DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
                return d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            return raw;
        }

        // HHmmss -> HH:mm (time is matched to the minute, so seconds aren't shown).
        private static string FormatTime(string raw)
        {
            if (!string.IsNullOrWhiteSpace(raw) && raw.Length >= 4 &&
                DateTime.TryParseExact(raw.Substring(0, Math.Min(6, raw.Length)).PadRight(6, '0'),
                    "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t))
                return t.ToString("HH:mm", CultureInfo.InvariantCulture);
            return raw;
        }

        private void Btn_Delete_Click(object sender, RoutedEventArgs e)
        {
            // The grid may still be holding an edit that has not been pushed back to the row.
            try { CommentsGrid.CommitEdit(DataGridEditingUnit.Row, true); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (_commentGroups != null && !HarvestComments()) return;
            DialogResult = true;
        }

        private void Btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
