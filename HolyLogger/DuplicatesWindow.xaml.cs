using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using HolyParser;

namespace HolyLogger
{
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
            DialogResult = true;
        }

        private void Btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
