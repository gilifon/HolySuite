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
            MaxWidth = SystemParameters.WorkArea.Width;
            DupsGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();

            // Two alternating group backgrounds, both readable with the theme text color.
            Brush bgEven = ThemeManager.Brush("GridRowBg");
            Brush bgOdd  = ThemeManager.Brush("FilterRowBg");
            var keepBrush   = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));   // green
            var deleteBrush = ThemeManager.Brush("Danger");

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

        // yyyyMMdd -> dd-MM-yyyy; unexpected values shown as-is.
        private static string FormatDate(string raw)
        {
            if (!string.IsNullOrWhiteSpace(raw) && raw.Length == 8 &&
                DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
                return d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            return raw;
        }

        // HHmmss -> HH:mm:ss (seconds shown: time is part of the duplicate key).
        private static string FormatTime(string raw)
        {
            if (!string.IsNullOrWhiteSpace(raw) && raw.Length == 6 &&
                DateTime.TryParseExact(raw, "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t))
                return t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
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
