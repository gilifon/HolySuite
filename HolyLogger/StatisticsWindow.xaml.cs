using DXCCManager;
using HolyParser;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HolyLogger
{
    public partial class StatisticsWindow : Window
    {
        private readonly ObservableCollection<QSO> _allQsos;

        public DataAccess Dal { get; set; }

        // Ordered band list (after stripping the "M" suffix)
        private static readonly string[] PivotBands =
            { "160", "80", "60", "40", "30", "20", "17", "15", "12", "10", "6", "2", "70cm", "13cm" };

        // ── column widths (pixels) ────────────────────────────────────────
        // Band[m] | SSB | CW | DIGI | FM | Total-% | Total-Num
        private static readonly double[] ColW = { 70, 50, 50, 50, 50, 55, 58 };

        // Shared across all StatisticsWindow instances — built once per process
        private static readonly EntityResolver _masterResolver = new EntityResolver();
        private static readonly Dictionary<string, BitmapImage> _flagCache = new Dictionary<string, BitmapImage>();

        private List<CountryItem> _workedList;
        private List<CountryItem> _missingList;

        private enum WorkedSort { CountDesc, CountAsc, NameAsc, NameDesc }
        private enum MissingSort { NameAsc, NameDesc }
        private WorkedSort  _workedSort  = WorkedSort.CountDesc;
        private MissingSort _missingSort = MissingSort.NameAsc;

        public StatisticsWindow(ObservableCollection<QSO> qsos)
        {
            InitializeComponent();
            _allQsos = qsos;

            var s = Properties.Settings.Default;

            // Restore size first so the on-screen test below uses the real window size.
            if (s.StatisticsWindowWidth  >= MinWidth)  Width  = s.StatisticsWindowWidth;
            if (s.StatisticsWindowHeight >= MinHeight) Height = s.StatisticsWindowHeight;

            // Restore the last position only if it still lands on a visible monitor. A position
            // saved on a second monitor that's since been turned off or rearranged (e.g. Left=2250
            // when only the primary screen is present) would otherwise open the window in dead
            // space where it can't be seen — which looks exactly like "it didn't remember." When
            // the saved spot is off-screen, fall back to a visible default.
            if (IsPositionOnScreen(s.StatisticsWindowLeft, s.StatisticsWindowTop, Width, Height))
            {
                Left = s.StatisticsWindowLeft;
                Top  = s.StatisticsWindowTop;
            }
            else
            {
                Left = SystemParameters.WorkArea.Left + 60;
                Top  = SystemParameters.WorkArea.Top  + 60;
            }

            ComputeStats();

            // Match country-table scroll heights to the pivot table height whenever the pivot resizes.
            PivotOuterBorder.SizeChanged += (sender, e) =>
            {
                if (e.NewSize.Height > 0)
                {
                    SV_WorkedCountries.Height  = e.NewSize.Height;
                    SV_MissingCountries.Height = e.NewSize.Height;
                }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Never open taller than the screen. The default 680px (or a height restored from a
            // bigger monitor) can exceed a low-resolution or display-scaled screen's work area.
            // The body row is proportional with its own scrollbars, so shrinking to fit keeps the
            // footer and all controls reachable. Nudge up if it would hang off the bottom.
            var work = SystemParameters.WorkArea;
            if (Height > work.Height)
                Height = work.Height;
            if (!double.IsNaN(Top) && Top + Height > work.Bottom)
                Top = Math.Max(work.Top, work.Bottom - Height);
        }

        // ── top-level entry point ─────────────────────────────────────────

        private void ComputeStats()
        {
            int total = _allQsos != null ? _allQsos.Count : 0;
            TB_TotalQSOs.Text = total.ToString();

            if (total == 0)
            {
                TB_UniqueCalls.Text     = "0";
                TB_UniqueCountries.Text = "0";
                TB_DateStart.Text = "—";
                TB_DateEnd.Text   = "—";
                TB_Status.Text          = "No QSOs to analyze.";
                return;
            }

            TB_UniqueCalls.Text = _allQsos
                .Select(q => q.DXCall)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count().ToString();

            TB_UniqueCountries.Text = _allQsos
                .Select(q => !string.IsNullOrEmpty(q.DXCC) ? q.DXCC : q.Country)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count().ToString();

            var dates = _allQsos
                .Where(q => !string.IsNullOrEmpty(q.Date))
                .Select(q => q.Date).OrderBy(d => d).ToList();
            TB_DateStart.Text = dates.Count > 0 ? FormatAdifDate(dates.First()) : "—";
            TB_DateEnd.Text   = dates.Count > 0 ? FormatAdifDate(dates.Last())  : "—";

            BuildPivot();
            BuildCountryTables();

            int needsEdit = _allQsos.Count(q => string.IsNullOrEmpty(q.Band) || string.IsNullOrEmpty(q.Mode));
            if (needsEdit > 0)
            {
                TB_DataQuality.Text = $"⚠  {needsEdit} QSO{(needsEdit == 1 ? "" : "s")} have missing band or mode.";
                BTN_EditProblems.Visibility = Visibility.Visible;
            }
            else
            {
                TB_DataQuality.Text = "";
                BTN_EditProblems.Visibility = Visibility.Collapsed;
            }

            TB_Status.Text = $"Statistics computed for {total} QSO{(total == 1 ? "" : "s")}.";
        }

        // ── pivot table builder ───────────────────────────────────────────

        private void BuildPivot()
        {
            // 1. Accumulate counts
            var counts = new Dictionary<string, Dictionary<string, int>>();
            foreach (var b in PivotBands)
                counts[b] = new Dictionary<string, int>
                    { { "SSB", 0 }, { "CW", 0 }, { "DIGI", 0 }, { "FM", 0 } };

            // Bucket for QSOs whose band is missing or not in PivotBands
            var other = new Dictionary<string, int>
                    { { "SSB", 0 }, { "CW", 0 }, { "DIGI", 0 }, { "FM", 0 } };

            foreach (var q in _allQsos)
            {
                string b = NormalizeBand(q.Band);
                string m = NormalizeMode(q.Mode); // always SSB/CW/DIGI/FM — never null
                if (b != null && counts.ContainsKey(b))
                    counts[b][m]++;
                else
                    other[m]++;
            }

            int totSSB = 0, totCW = 0, totDIGI = 0, totFM = 0;
            foreach (var b in PivotBands)
            {
                totSSB  += counts[b]["SSB"];  totCW   += counts[b]["CW"];
                totDIGI += counts[b]["DIGI"]; totFM   += counts[b]["FM"];
            }
            // Include the "Other" bucket so grand == _allQsos.Count
            totSSB  += other["SSB"];  totCW   += other["CW"];
            totDIGI += other["DIGI"]; totFM   += other["FM"];
            int grand    = totSSB + totCW + totDIGI + totFM;
            int otherTot = other["SSB"] + other["CW"] + other["DIGI"] + other["FM"];
            bool hasOther = otherTot > 0;

            // 2. Build Grid
            //    rows: 2 header rows + N band rows + optional Other row + 2 total rows
            int numBands = PivotBands.Length;
            int numRows  = 2 + numBands + (hasOther ? 1 : 0) + 2;

            var tbl = new Grid();
            foreach (var w in ColW)
                tbl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            for (int i = 0; i < numRows; i++)
                tbl.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Shared brushes
            var headerBg = Br(0xDE, 0xB8, 0x87);   // #DEB887 (tan header)
            var totalBg  = Br(0xEE, 0xF2, 0xF7);   // #EEF2F7 (light blue-grey)
            var yellowBg = Br(0xFF, 0xFF, 0x00);   // yellow  (grand total)
            var evenBg   = Brushes.White;
            var oddBg    = Br(0xC0, 0xD8, 0xF0);   // light blue
            var gridLine = Br(0xAA, 0xAA, 0xAA);     // grey grid lines

            // Cell shorthand
            Border H(string t, int r, int c, int rs = 1, int cs = 1) =>
                Put(tbl, r, c, rs, cs, MkCell(t, headerBg, gridLine, bold: true));

            Border D(string t, Brush bg, int r, int c, bool bold = false,
                     TextAlignment ta = TextAlignment.Center) =>
                Put(tbl, r, c, 1, 1, MkCell(t, bg, gridLine, bold: bold, align: ta));

            // ── row 0: Band[m] │ "mode" (span 4) │ "Total" (span 2) ──────
            H("Band [m]", 0, 0);
            Put(tbl, 0, 1, 1, 4, VL(MkCell("mode",  headerBg, gridLine, bold: true)));
            Put(tbl, 0, 5, 1, 2, VL(MkCell("Total", headerBg, gridLine, bold: true)));

            // ── row 1: sub-headers ────────────────────────────────────────
            H("",       1, 0);
            Put(tbl, 1, 1, 1, 1, VL(MkCell("SSB",   headerBg, gridLine, bold: true)));
            H("CW",     1, 2); H("DIGI",   1, 3); H("FM",     1, 4);
            Put(tbl, 1, 5, 1, 1, VL(MkCell("%",     headerBg, gridLine, bold: true)));
            H("number", 1, 6);

            // ── band rows ─────────────────────────────────────────────────
            string Pct(int n) => grand > 0 && n > 0 ? $"{100.0 * n / grand:F1}%" : "";

            for (int i = 0; i < numBands; i++)
            {
                string b   = PivotBands[i];
                int    r   = 2 + i;
                var    bg  = (Brush)(i % 2 == 0 ? evenBg : oddBg);
                int ssb = counts[b]["SSB"], cw = counts[b]["CW"],
                    digi = counts[b]["DIGI"], fm = counts[b]["FM"];
                int rowTot = ssb + cw + digi + fm;

                if (i == 0)
                {
                    // Black top separator between sub-headers and first data row
                    Put(tbl, r, 0, 1, 1, TL(MkCell(b,            bg, gridLine, align: TextAlignment.Left)));
                    Put(tbl, r, 1, 1, 1, VL(TL(MkCell(N(ssb),    bg, gridLine))));
                    Put(tbl, r, 2, 1, 1, TL(MkCell(N(cw),        bg, gridLine)));
                    Put(tbl, r, 3, 1, 1, TL(MkCell(N(digi),      bg, gridLine)));
                    Put(tbl, r, 4, 1, 1, TL(MkCell(N(fm),        bg, gridLine)));
                    Put(tbl, r, 5, 1, 1, VL(TL(MkCell(Pct(rowTot), bg, gridLine, align: TextAlignment.Right))));
                    Put(tbl, r, 6, 1, 1, TL(MkCell(N(rowTot),    bg, gridLine, bold: rowTot > 0)));
                }
                else
                {
                    D(b,          bg, r, 0, ta: TextAlignment.Left);
                    Put(tbl, r, 1, 1, 1, VL(MkCell(N(ssb),  bg, gridLine)));
                    D(N(cw),      bg, r, 2);
                    D(N(digi),    bg, r, 3);
                    D(N(fm),      bg, r, 4);
                    Put(tbl, r, 5, 1, 1, VL(MkCell(Pct(rowTot), bg, gridLine, align: TextAlignment.Right)));
                    D(N(rowTot),  bg, r, 6, bold: rowTot > 0);
                }
            }

            // ── "Other" row (bands not in PivotBands) ────────────────────
            if (hasOther)
            {
                int r  = 2 + numBands;
                var bg = (Brush)(numBands % 2 == 0 ? evenBg : oddBg);
                D("Other",           bg, r, 0, ta: TextAlignment.Left);
                Put(tbl, r, 1, 1, 1, VL(MkCell(N(other["SSB"]), bg, gridLine)));
                D(N(other["CW"]),    bg, r, 2);
                D(N(other["DIGI"]),  bg, r, 3);
                D(N(other["FM"]),    bg, r, 4);
                Put(tbl, r, 5, 1, 1, VL(MkCell(Pct(otherTot), bg, gridLine, align: TextAlignment.Right)));
                D(N(otherTot),       bg, r, 6, bold: true);
            }

            // ── total footer (2 rows) ─────────────────────────────────────
            int tr1 = 2 + numBands + (hasOther ? 1 : 0);
            int tr2 = tr1 + 1;

            // Wrappers: TL = 2px black top line, VL = 2px black left line
            Border TL(Border inner) => new Border
            {
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(0, 2, 0, 0),
                Child = inner
            };
            Border VL(Border inner) => new Border
            {
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(2, 0, 0, 0),
                Child = inner
            };

            // "Total" label spans both sub-rows — black top separator
            Put(tbl, tr1, 0, 2, 1, TL(MkCell("Total", headerBg, gridLine, bold: true, align: TextAlignment.Left)));

            // sub-row 1: mode percentages — top separator; col 5 also gets left separator
            Put(tbl, tr1, 1, 1, 1, VL(TL(MkCell(Pct(totSSB),  headerBg, gridLine, bold: true))));
            Put(tbl, tr1, 2, 1, 1, TL(MkCell(Pct(totCW),   headerBg, gridLine, bold: true)));
            Put(tbl, tr1, 3, 1, 1, TL(MkCell(Pct(totDIGI), headerBg, gridLine, bold: true)));
            Put(tbl, tr1, 4, 1, 1, TL(MkCell(Pct(totFM),   headerBg, gridLine, bold: true)));
            Put(tbl, tr1, 5, 1, 1, VL(TL(MkCell("100%",    headerBg, gridLine, bold: true))));
            Put(tbl, tr1, 6, 1, 1, TL(MkCell("",           headerBg, gridLine)));

            // sub-row 2: mode counts + grand total (yellow); col 5 gets left separator
            Put(tbl, tr2, 1, 1, 1, VL(MkCell(N(totSSB), headerBg, gridLine, bold: true)));
            D(N(totCW),   headerBg, tr2, 2, bold: true);
            D(N(totDIGI), headerBg, tr2, 3, bold: true);
            D(N(totFM),   headerBg, tr2, 4, bold: true);
            Put(tbl, tr2, 5, 1, 1, VL(MkCell("",           headerBg, gridLine)));
            D(grand > 0 ? grand.ToString() : "", yellowBg, tr2, 6, bold: true);

            PivotBorder.Child = tbl;
        }

        // ── country tables ────────────────────────────────────────────────

        private void BuildCountryTables()
        {
            var workedCounts = _allQsos
                .Where(q => !string.IsNullOrEmpty(q.Country))
                .GroupBy(q => q.Country, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var workedNames = new HashSet<string>(workedCounts.Keys, StringComparer.OrdinalIgnoreCase);

            _workedList = workedCounts.Keys
                .Select(name => new CountryItem
                {
                    Name      = name,
                    Count     = workedCounts[name],
                    FlagImage = GetFlagImage(name),
                }).ToList();

            TB_WorkedHeader.Text = $"Worked Countries ({_workedList.Count})";

            _missingList = _masterResolver.GetAllEntityNames()
                .Where(n => !workedNames.Contains(n))
                .Select(name => new CountryItem
                {
                    Name      = name,
                    FlagImage = GetFlagImage(name),
                }).ToList();

            TB_MissingHeader.Text = $"Missing Countries ({_missingList.Count})";

            TB_SortWorkedName.MouseLeftButtonUp  -= SortWorkedByName;
            TB_SortWorkedName.MouseLeftButtonUp  += SortWorkedByName;
            TB_SortWorkedCount.MouseLeftButtonUp -= SortWorkedByCount;
            TB_SortWorkedCount.MouseLeftButtonUp += SortWorkedByCount;
            TB_SortMissingName.MouseLeftButtonUp -= SortMissingByName;
            TB_SortMissingName.MouseLeftButtonUp += SortMissingByName;

            ApplyWorkedSort();
            ApplyMissingSort();
        }

        private void SortWorkedByName(object sender, MouseButtonEventArgs e)
        {
            _workedSort = _workedSort == WorkedSort.NameAsc ? WorkedSort.NameDesc : WorkedSort.NameAsc;
            ApplyWorkedSort();
        }

        private void SortWorkedByCount(object sender, MouseButtonEventArgs e)
        {
            _workedSort = _workedSort == WorkedSort.CountDesc ? WorkedSort.CountAsc : WorkedSort.CountDesc;
            ApplyWorkedSort();
        }

        private void SortMissingByName(object sender, MouseButtonEventArgs e)
        {
            _missingSort = _missingSort == MissingSort.NameAsc ? MissingSort.NameDesc : MissingSort.NameAsc;
            ApplyMissingSort();
        }

        private void ApplyWorkedSort()
        {
            List<CountryItem> sorted;
            if      (_workedSort == WorkedSort.NameAsc)  sorted = _workedList.OrderBy(c => c.Name).ToList();
            else if (_workedSort == WorkedSort.NameDesc) sorted = _workedList.OrderByDescending(c => c.Name).ToList();
            else if (_workedSort == WorkedSort.CountAsc) sorted = _workedList.OrderBy(c => c.Count).ThenBy(c => c.Name).ToList();
            else                                         sorted = _workedList.OrderByDescending(c => c.Count).ThenBy(c => c.Name).ToList();

            for (int i = 0; i < sorted.Count; i++)
                sorted[i].RowBg = i % 2 == 0 ? (Brush)Brushes.White : Br(0xDC, 0xDC, 0xDC);

            IC_WorkedCountries.ItemsSource = sorted;
            UpdateWorkedSortHeaders();
        }

        private void ApplyMissingSort()
        {
            List<CountryItem> sorted = _missingSort == MissingSort.NameAsc
                ? _missingList.OrderBy(c => c.Name).ToList()
                : _missingList.OrderByDescending(c => c.Name).ToList();

            for (int i = 0; i < sorted.Count; i++)
                sorted[i].RowBg = i % 2 == 0 ? (Brush)Brushes.White : Br(0xDC, 0xDC, 0xDC);

            IC_MissingCountries.ItemsSource = sorted;
            UpdateMissingSortHeaders();
        }

        private void UpdateWorkedSortHeaders()
        {
            TB_SortWorkedName.Text  = _workedSort == WorkedSort.NameAsc  ? "Country ▲"
                                    : _workedSort == WorkedSort.NameDesc ? "Country ▼"
                                    :                                       "Country";
            TB_SortWorkedCount.Text = _workedSort == WorkedSort.CountDesc ? "Count ▼"
                                    : _workedSort == WorkedSort.CountAsc  ? "Count ▲"
                                    :                                        "Count";
        }

        private void UpdateMissingSortHeaders()
        {
            TB_SortMissingName.Text = _missingSort == MissingSort.NameAsc ? "Country ▲" : "Country ▼";
        }

        private static BitmapImage GetFlagImage(string countryName)
        {
            if (!MainWindow.DxccNameToIso.TryGetValue(countryName, out string iso)) return null;
            if (_flagCache.TryGetValue(iso, out BitmapImage cached)) return cached;
            try
            {
                var bm = new BitmapImage(new Uri($"pack://application:,,,/Images/flags/{iso}.png"));
                _flagCache[iso] = bm;
                return bm;
            }
            catch { return null; }
        }

        // ── cell factory ──────────────────────────────────────────────────

        private static Border MkCell(string text, Brush bg, Brush gridLine,
            bool bold = false, TextAlignment align = TextAlignment.Center)
        {
            return new Border
            {
                Background      = bg,
                BorderBrush     = gridLine,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text                = text ?? "",
                    FontSize            = 14,
                    FontWeight          = bold ? FontWeights.Bold : FontWeights.Normal,
                    TextAlignment       = align,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Padding             = new Thickness(align == TextAlignment.Left ? 4 : 2, 2, 2, 2),
                    Foreground          = Brushes.Black
                },
                MinHeight = 20
            };
        }

        private static Border Put(Grid g, int row, int col, int rowSpan, int colSpan, Border cell)
        {
            Grid.SetRow(cell, row);    Grid.SetColumn(cell, col);
            if (rowSpan > 1) Grid.SetRowSpan(cell, rowSpan);
            if (colSpan > 1) Grid.SetColumnSpan(cell, colSpan);
            g.Children.Add(cell);
            return cell;
        }

        // ── helpers ───────────────────────────────────────────────────────

        private static SolidColorBrush Br(byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromRgb(r, g, b));

        // n=0 → empty string (blank cell like the mockup)
        private static string N(int n) => n > 0 ? n.ToString() : "";

        // "160M" → "160", "70CM" → "70cm"
        private static string NormalizeBand(string band)
        {
            if (string.IsNullOrEmpty(band)) return null;
            string b = band.ToUpper().Trim();
            if (b.EndsWith("CM")) return b.ToLower();
            if (b.EndsWith("M"))  return b.Substring(0, b.Length - 1);
            return b;
        }

        // USB/LSB/PH/AM → SSB; blank/unknown → DIGI (never returns null)
        private static string NormalizeMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return "DIGI";
            string m = mode.ToUpper().Trim();
            if (m == "SSB" || m == "USB" || m == "LSB" || m == "PH" || m == "AM") return "SSB";
            if (m == "CW") return "CW";
            if (m == "FM") return "FM";
            return "DIGI";
        }

        private static string FormatAdifDate(string adif)
        {
            if (string.IsNullOrEmpty(adif) || adif.Length < 8) return adif;
            return $"{adif.Substring(0, 4)}-{adif.Substring(4, 2)}-{adif.Substring(6, 2)}";
        }

        // ── problem QSO editor ────────────────────────────────────────────

        private void BTN_EditProblems_Click(object sender, RoutedEventArgs e)
        {
            var badQsos = _allQsos
                .Where(q => string.IsNullOrEmpty(q.Band) || string.IsNullOrEmpty(q.Mode))
                .ToList();

            var editor = new BadQsoEditorWindow(badQsos, Dal)
            {
                Owner = this
            };
            editor.ShowDialog();

            // Refresh stats if any QSOs were saved.
            if (editor.AnySaved)
                ComputeStats();
        }

        // ── window position / size persistence ───────────────────────────

        private void Window_LocationChanged(object sender, EventArgs e)
        {
            // Use WindowState's restore bounds so a position saved while maximized is the real
            // (un-maximized) corner, not the maximized 0,0. Skip NaN that can appear before the
            // window has a position. No "Left >= 0" filter — a second monitor to the left gives
            // valid negative coordinates that must be remembered too.
            double left = WindowState == WindowState.Normal ? Left : RestoreBounds.Left;
            double top  = WindowState == WindowState.Normal ? Top  : RestoreBounds.Top;
            if (double.IsNaN(left) || double.IsNaN(top)) return;

            Properties.Settings.Default.StatisticsWindowLeft = left;
            Properties.Settings.Default.StatisticsWindowTop  = top;
            Properties.Settings.Default.Save();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double width  = WindowState == WindowState.Normal ? Width  : RestoreBounds.Width;
            double height = WindowState == WindowState.Normal ? Height : RestoreBounds.Height;
            if (width  > 0) Properties.Settings.Default.StatisticsWindowWidth  = width;
            if (height > 0) Properties.Settings.Default.StatisticsWindowHeight = height;
            Properties.Settings.Default.Save();
        }

        // True when a window of the given size at (left, top) would still be reachable on some
        // monitor of the current virtual desktop. Mirrors MainWindow.IsPositionOnScreen: requires
        // the title bar to be grabbable rather than the whole window to fit, so a slightly
        // off-bottom/right spot still counts as visible.
        private static bool IsPositionOnScreen(double left, double top, double width, double height)
        {
            if (double.IsNaN(left) || double.IsNaN(top) ||
                double.IsInfinity(left) || double.IsInfinity(top))
                return false;

            double vsLeft   = SystemParameters.VirtualScreenLeft;
            double vsTop    = SystemParameters.VirtualScreenTop;
            double vsRight  = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop  + SystemParameters.VirtualScreenHeight;

            return left >= vsLeft - 10 && top >= vsTop - 10 &&
                   left <= vsRight - 100 && top <= vsBottom - 60;
        }
    }

    internal class CountryItem
    {
        public string Name { get; set; }
        public BitmapImage FlagImage { get; set; }
        public int Count { get; set; }
        public string CountStr => Count > 0 ? Count.ToString() : "";
        public Brush RowBg { get; set; }
    }
}
