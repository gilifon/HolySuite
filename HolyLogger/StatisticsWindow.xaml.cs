using DXCCManager;
using HolyParser;
using Newtonsoft.Json;
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

        // DXCC entity names confirmed on LoTW. Populated from the LoTW confirmation download (or the
        // cached result); drives the Confirmed column (green check / red minus) in the worked list.
        private HashSet<string> _confirmedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private enum WorkedSort { CountDesc, CountAsc, NameAsc, NameDesc, ConfirmedDesc, ConfirmedAsc }
        private enum MissingSort { NameAsc, NameDesc }
        private WorkedSort  _workedSort  = WorkedSort.CountDesc;
        private MissingSort _missingSort = MissingSort.NameAsc;

        // Raised when the user clicks a worked country; the main window opens the Search window
        // filtered by that country.
        public event Action<string> CountrySearchRequested;

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

            LoadConfirmedCache();
            ComputeStats();

            // Match country-table scroll heights to the pivot table height whenever the pivot resizes.
            PivotOuterBorder.SizeChanged += (sender, e) =>
            {
                if (e.NewSize.Height > 0)
                {
                    SV_WorkedCountries.Height  = e.NewSize.Height;
                    SV_MissingCountries.Height = e.NewSize.Height;
                    SV_MissingCQ.Height        = e.NewSize.Height;
                    SV_MissingITU.Height       = e.NewSize.Height;
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
            TB_PivotHeader.Text = "QSOs by Band × Mode\n(" + total + ")";
            TB_CtyVersion.Text = string.IsNullOrEmpty(_masterResolver.Version) ? "—" : _masterResolver.Version;

            // Warn if the country file is overdue for a refresh (e.g. AD1C moved the download URL).
            string ctyWarning = CtyDatService.UpdateWarning();
            if (!string.IsNullOrEmpty(ctyWarning))
            {
                TB_CtyWarning.Text = ctyWarning;
                TB_CtyWarning.Visibility = Visibility.Visible;
                TB_CtyVersion.Foreground = System.Windows.Media.Brushes.OrangeRed;
            }
            else
            {
                TB_CtyWarning.Visibility = Visibility.Collapsed;
            }

            if (total == 0)
            {
                int totalDxcc = _masterResolver.GetAllEntityNames().Count;
                TB_UniqueCalls.Text     = "0";
                TB_UniqueCountries.Text = $"0 / {totalDxcc}";
                TB_MissingDxcc.Text     = totalDxcc.ToString();
                TB_DateStart.Text = "—";
                TB_DateEnd.Text   = "—";
                PopulateMissingZones();
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

            PopulateMissingZones();

            TB_Status.Text = $"Statistics computed for {total} QSO{(total == 1 ? "" : "s")}.";
        }

        // Fills the "Missing CQ Zones" (1..40) and "Missing ITU Zones" (1..90) scrollable lists with
        // the zones not yet present in any QSO, and shows the count in each header.
        private void PopulateMissingZones()
        {
            List<int> missingCq  = MissingZones(40, d => d.CqZone);
            List<int> missingItu = MissingZones(90, d => d.ItuZone);

            IC_MissingCQ.ItemsSource  = ToZoneRows(missingCq);
            IC_MissingITU.ItemsSource = ToZoneRows(missingItu);

            TB_MissingCQHeader.Text  = $"Missing CQ\nZones ({missingCq.Count})";
            TB_MissingITUHeader.Text = $"Missing ITU\nZones ({missingItu.Count})";
        }

        private static List<ZoneRow> ToZoneRows(List<int> zones)
        {
            var rows = new List<ZoneRow>(zones.Count);
            for (int i = 0; i < zones.Count; i++)
                rows.Add(new ZoneRow
                {
                    Zone  = zones[i].ToString(),
                    RowBg = i % 2 == 0 ? ThemeManager.Brush("GridRowBg") : ThemeManager.Brush("GridAltRowBg")
                });
            return rows;
        }

        private class ZoneRow
        {
            public string Zone { get; set; }
            public Brush RowBg { get; set; }
        }

        // Live cty.dat resolution keyed by callsign, so worked-DXCC and zones are computed FRESH from the
        // current country file — never from the possibly-stale stored country/zone fields (that staleness
        // is exactly what produced a wrong count after the DB restore). Cached so each call resolves once.
        private Dictionary<string, DXCC> _resolveCache;
        private DXCC Resolve(string call)
        {
            call = (call ?? string.Empty).Trim();
            if (call.Length == 0 || _masterResolver == null) return null;
            if (_resolveCache == null) _resolveCache = new Dictionary<string, DXCC>(StringComparer.OrdinalIgnoreCase);
            if (!_resolveCache.TryGetValue(call, out var d)) { d = _masterResolver.GetDXCC(call); _resolveCache[call] = d; }
            return d;
        }

        private List<int> MissingZones(int maxZone, Func<DXCC, int> zoneOf)
        {
            var worked = new HashSet<int>();
            if (_allQsos != null)
            {
                foreach (QSO q in _allQsos)
                {
                    var d = Resolve(q.DXCall);
                    if (d == null) continue;
                    int z = zoneOf(d);
                    if (z >= 1 && z <= maxZone) worked.Add(z);
                }
            }
            return Enumerable.Range(1, maxZone).Where(z => !worked.Contains(z)).ToList();
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
            var headerBg = ThemeManager.Brush("GridHeaderBg");
            var totalBg  = ThemeManager.Brush("GridHeaderBg");
            var yellowBg = ThemeManager.Brush("EditFieldBg");    // grand-total highlight
            var evenBg   = ThemeManager.Brush("GridRowBg");
            var oddBg    = ThemeManager.Brush("GridAltRowBg");
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
                BorderBrush = ThemeManager.Brush("ThemeBorderBrush"), BorderThickness = new Thickness(0, 2, 0, 0),
                Child = inner
            };
            Border VL(Border inner) => new Border
            {
                BorderBrush = ThemeManager.Brush("ThemeBorderBrush"), BorderThickness = new Thickness(2, 0, 0, 0),
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
            // Group by the entity RESOLVED live from each callsign (not the stored country string), so the
            // worked/missing DXCC counts always match the current cty.dat entity list and can never drift.
            // Drop "Unknown" — GetDXCC returns that placeholder name (never null) for a callsign no prefix
            // matches, e.g. a retired prefix like T9/4N or an O-for-zero typo. It is not a DXCC entity, and
            // GetAllEntityNames() excludes it, so letting it through would list a 266th "country" while the
            // worked/total box — derived as total minus missing — correctly stayed at 265.
            var workedCounts = _allQsos
                .Select(q => Resolve(q.DXCall)?.Name)
                .Where(n => !string.IsNullOrEmpty(n) && !string.Equals(n, "Unknown", StringComparison.OrdinalIgnoreCase))
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var workedNames = new HashSet<string>(workedCounts.Keys, StringComparer.OrdinalIgnoreCase);

            _workedList = workedCounts.Keys
                .Select(name => new CountryItem
                {
                    Name      = name,
                    Count     = workedCounts[name],
                    FlagImage = GetFlagImage(name),
                }).ToList();

            // Single line now — the LoTW button sits beside it on the same row.
            TB_WorkedHeader.Text = $"Worked Countries ({_workedList.Count})";

            var allDxccEntities = _masterResolver.GetAllEntityNames();
            _missingList = allDxccEntities
                .Where(n => !workedNames.Contains(n))
                .Select(name => new CountryItem
                {
                    Name      = name,
                    FlagImage = GetFlagImage(name),
                }).ToList();

            TB_MissingHeader.Text = $"Missing Countries\n({_missingList.Count})";

            // Top summary boxes: worked-of-total and the missing gap. Derived from the master
            // entity list so worked + missing always equals the total (currently 340 DXCC entities).
            int totalDxcc   = allDxccEntities.Count;
            int missingDxcc = _missingList.Count;
            int workedDxcc  = totalDxcc - missingDxcc;
            TB_UniqueCountries.Text = $"{workedDxcc} / {totalDxcc}";
            TB_MissingDxcc.Text     = missingDxcc.ToString();

            TB_SortWorkedName.MouseLeftButtonUp  -= SortWorkedByName;
            TB_SortWorkedName.MouseLeftButtonUp  += SortWorkedByName;
            TB_SortWorkedCount.MouseLeftButtonUp -= SortWorkedByCount;
            TB_SortWorkedCount.MouseLeftButtonUp += SortWorkedByCount;
            TB_SortWorkedConfirmed.MouseLeftButtonUp -= SortWorkedByConfirmed;
            TB_SortWorkedConfirmed.MouseLeftButtonUp += SortWorkedByConfirmed;
            TB_SortMissingName.MouseLeftButtonUp -= SortMissingByName;
            TB_SortMissingName.MouseLeftButtonUp += SortMissingByName;

            ApplyConfirmedHighlight();   // color rows from the (possibly cached) LoTW-confirmed set
            ApplyWorkedSort();
            ApplyMissingSort();
        }

        // ---- LoTW "confirmed countries" (confirmed on LoTW) ----

        // Restore the last downloaded confirmed-entity set so the colors/count show immediately on open
        // without re-downloading. Stored as a '|'-joined list of DXCC entity names.
        private void LoadConfirmedCache()
        {
            _confirmedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string cached = Properties.Settings.Default.LotwConfirmedEntities;
            if (string.IsNullOrWhiteSpace(cached)) return;
            foreach (var n in cached.Split('|'))
                if (!string.IsNullOrWhiteSpace(n)) _confirmedEntities.Add(n.Trim());

            // Self-heal a bogus total left over from earlier broken-download testing: the confirmed-QSO
            // count can never be below the number of confirmed countries (each country has >=1 confirmed
            // QSO). If it is, drop the incremental marker so the next Check LoTW does a one-time full
            // re-download that recomputes the true total. The cached colors stay until then.
            var s = Properties.Settings.Default;
            if (s.LotwConfirmedQsoCount < _confirmedEntities.Count && !string.IsNullOrWhiteSpace(s.LotwLastQsl))
            {
                s.LotwLastQsl = string.Empty;
                s.Save();
            }
        }

        // Green-highlight the worked rows whose entity is in the confirmed set, and update the count line.
        // Does not re-sort; the caller refreshes the list (BuildCountryTables / the button both do).
        private void ApplyConfirmedHighlight()
        {
            if (_workedList == null) return;
            int confirmed = 0;
            foreach (var item in _workedList)
            {
                item.IsConfirmed = _confirmedEntities.Contains(item.Name);
                if (item.IsConfirmed) confirmed++;
            }

            TB_LotwStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
            TB_LotwStatus.Text = _confirmedEntities.Count == 0
                ? string.Empty
                : $"Confirmed (LoTW): {confirmed} of {_workedList.Count}";

            // Top summary box: confirmed / worked entities (countries).
            TB_ConfirmedDxcc.Text = _confirmedEntities.Count == 0
                ? "—"
                : $"{confirmed} / {_workedList.Count}";

            PopulateConfirmedSummary();
        }

        // The 3-row summary under the worked table: total confirmations, what the LAST check added
        // (new QSLs, and since when), and — the number that matters — new countries.
        private void PopulateConfirmedSummary()
        {
            if (ConfirmSummaryPanel == null) return;
            var s = Properties.Settings.Default;
            if (_confirmedEntities.Count == 0 && s.LotwConfirmedQsoCount == 0)
            {
                ConfirmSummaryPanel.Visibility = Visibility.Collapsed;
                return;
            }
            ConfirmSummaryPanel.Visibility = Visibility.Visible;

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            // Never show a bogus total: it can't be below the confirmed-country count (each country has
            // >=1 confirmed QSO). If it is (stale value from earlier broken downloads), show "—" until a
            // full Check LoTW recomputes it, rather than the misleading small number.
            bool totalKnown = s.LotwConfirmedQsoCount > 0 && s.LotwConfirmedQsoCount >= _confirmedEntities.Count;
            TB_SumTotalQsls.Text = totalKnown ? s.LotwConfirmedQsoCount.ToString("N0", inv) : "—";

            // A full/baseline download has no meaningful "new since last check" delta — everything is
            // pulled at once. Showing the whole set as "new" just duplicates the total, so the two "New"
            // rows show "—" for a full download and only display real deltas after an incremental check.
            bool fullDownload = string.IsNullOrWhiteSpace(s.LotwLastCheckSince);
            var muted = (Brush)ThemeManager.Brush("MutedTextBrush");

            TB_SumNewQsls.Text = fullDownload ? "—" : s.LotwLastNewQsls.ToString(inv);
            TB_SumSince.Text = fullDownload
                ? "   (full download)"
                : $"   (since {s.LotwLastCheckSince})";

            // The "New confirmations" number is a link that opens the list of new QSOs — but only when
            // there actually are some to show. Otherwise it's plain, non-clickable text.
            bool hasNew = !fullDownload && s.LotwLastNewQsls > 0
                          && !string.IsNullOrWhiteSpace(s.LotwLastNewJson);
            LNK_NewQsls.IsEnabled = hasNew;
            if (hasNew)
            {
                LNK_NewQsls.Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));   // link blue
                LNK_NewQsls.TextDecorations = TextDecorations.Underline;
                LNK_NewQsls.Cursor = Cursors.Hand;
            }
            else
            {
                LNK_NewQsls.Foreground = (Brush)ThemeManager.Brush("TextBrush");
                LNK_NewQsls.TextDecorations = null;
                LNK_NewQsls.Cursor = Cursors.Arrow;
            }

            TB_SumNewCountries.Text = fullDownload ? "—" : s.LotwLastNewCountries.ToString(inv);
            TB_SumNewCountries.Foreground = (!fullDownload && s.LotwLastNewCountries > 0)
                ? System.Windows.Media.Brushes.ForestGreen
                : muted;
        }

        // Click the "New confirmations" count -> show the new QSOs (from the last incremental check) with
        // full details, so the operator can see exactly what was just confirmed.
        private void NewQsls_Click(object sender, RoutedEventArgs e)
        {
            List<LotwNewQso> list = null;
            try
            {
                var json = Properties.Settings.Default.LotwLastNewJson;
                if (!string.IsNullOrWhiteSpace(json))
                    list = JsonConvert.DeserializeObject<List<LotwNewQso>>(json);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (list == null || list.Count == 0)
            {
                HolyMessageBox.Show("There are no new confirmations to show.", "LoTW", HolyMsgType.Info, this);
                return;
            }
            ShowNewConfirmationsWindow(list);
        }

        // A small themed grid of the new confirmations (built in code, like the eQSL-queue window). The
        // app's window-chrome hook themes the plain Window automatically.
        private void ShowNewConfirmationsWindow(List<LotwNewQso> list)
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserReorderColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                SelectionMode = DataGridSelectionMode.Single,
                FontSize = 14,
                ItemsSource = list
            };
            grid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();
            ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
            grid.Columns.Add(new DataGridTextColumn { Header = "Callsign", Binding = new System.Windows.Data.Binding("Call"), Width = 130 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Country", Binding = new System.Windows.Data.Binding("Country"), Width = 180 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Date", Binding = new System.Windows.Data.Binding("DateStr"), Width = 90 });
            grid.Columns.Add(new DataGridTextColumn { Header = "UTC", Binding = new System.Windows.Data.Binding("TimeStr"), Width = 60 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Band", Binding = new System.Windows.Data.Binding("Band"), Width = 70 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Mode", Binding = new System.Windows.Data.Binding("Mode"), Width = 70 });

            var win = new Window
            {
                Title = $"New LoTW confirmations ({list.Count})",
                Owner = this,
                Width = 640,
                Height = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                Content = new Border { Padding = new Thickness(10), Child = grid }
            };
            win.ShowDialog();
        }

        private async void BTN_CheckLotw_Click(object sender, RoutedEventArgs e)
        {
            var s = Properties.Settings.Default;
            string user = s.LotwWebUser?.Trim();
            string pass = s.LotwWebPassword;
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                // Same discoverable flow as the Bad-QSO editor: offer to open Options → LoTW rather than
                // leaving the operator wondering why nothing happens. (The download needs the LoTW website
                // login, which is separate from the TQSL certificate used for uploads.)
                bool openOptions = HolyMessageBox.ShowConfirm(
                    "Your LoTW website username and password aren't set, so confirmations can't be downloaded.\n\n" +
                    "(These are separate from the TQSL certificate used for uploading.)\n\n" +
                    "Open Options → LoTW to enter them now?",
                    "LoTW login needed", HolyMsgType.Warning, this);
                if (openOptions)
                {
                    var opts = new OptionsWindow();
                    opts.LotwControlInstance.Dal = Dal;
                    opts.Owner = this;
                    opts.LotwItem.IsSelected = true;
                    opts.ShowDialog();
                }
                return;
            }

            // Incremental sync: the FIRST run has no saved marker, so it pulls everything from 1970 (slow,
            // one time); later runs pass LoTW's EXACT last-QSL timestamp (APP_LoTW_LASTQSL, e.g.
            // "2026-07-17 17:41:36") as qso_qslsince, so LoTW returns only QSLs received AFTER that
            // moment -- no whole-day re-count. New confirmations union into the cached set; a full
            // re-pull only happens when there's no cache yet.
            bool incremental = _confirmedEntities.Count > 0 && !string.IsNullOrWhiteSpace(s.LotwLastQsl);
            string sinceQuery = incremental ? QslSinceValue(s.LotwLastQsl) : "1970-01-01";
            string sinceDisplay = incremental ? PrettySince(s.LotwLastQsl) : string.Empty;

            BTN_CheckLotw.IsEnabled = false;
            TB_LotwStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;   // clear any prior error red
            TB_LotwStatus.Text = incremental
                ? "Checking LoTW for new confirmations…"
                : "Downloading all confirmations from LoTW… (one-time; can take a minute for a large log)";

            // Cover the table with the spinner overlay so the wait doesn't look frozen.
            TB_LotwLoadingText.Text = incremental
                ? "Checking LoTW for new confirmations…"
                : "Downloading confirmations from LoTW…";
            ShowLotwSpinner(true);
            try
            {
                // We do NOT request qso_qsldetail: we match by callsign, so the extra per-record fields
                // would only bloat and slow the download.
                string url = "https://lotw.arrl.org/lotwuser/lotwreport.adi"
                           + "?login=" + Uri.EscapeDataString(user)
                           + "&password=" + Uri.EscapeDataString(pass)
                           + "&qso_query=1&qso_qsl=yes&qso_qslsince=" + Uri.EscapeDataString(sinceQuery);

                string adif;
                // Decompress gzip/deflate — otherwise a compressed response reads back as binary garbage.
                using (var handler = new System.Net.Http.HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
                })
                using (var http = new System.Net.Http.HttpClient(handler))
                {
                    http.Timeout = TimeSpan.FromSeconds(300);   // large accounts can take a while server-side
                    adif = await http.GetStringAsync(url);
                }

                if (adif.IndexOf("Invalid password", StringComparison.OrdinalIgnoreCase) >= 0
                    || adif.IndexOf("login incorrect", StringComparison.OrdinalIgnoreCase) >= 0
                    || adif.IndexOf("<Error>", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    TB_LotwStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                    TB_LotwStatus.Text = "LoTW rejected the login — check your username and password.";
                    return;
                }

                // Sanity-check the payload really is the ADIF report (not an error/login web page, and
                // not unreadable compressed bytes). Catches auth failures whose wording we didn't match.
                bool looksAdif = adif.IndexOf("<eoh>", StringComparison.OrdinalIgnoreCase) >= 0
                              || adif.IndexOf("<eor>", StringComparison.OrdinalIgnoreCase) >= 0
                              || adif.IndexOf("<call:", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!looksAdif)
                {
                    bool looksHtml = adif.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0
                                  || adif.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase) >= 0;
                    TB_LotwStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                    TB_LotwStatus.Text = looksHtml
                        ? "LoTW returned a web page, not data — the login was likely rejected. Check your username and password."
                        : $"LoTW returned no usable data ({adif.Length} chars). Check your LoTW login and try again.";
                    return;
                }

                // Walk the QSL records, resolving each callsign to a DXCC entity via the SAME resolver the
                // worked list uses, so the names line up for highlighting.
                var records = System.Text.RegularExpressions.Regex.Split(
                    adif, "<eor>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                int qslCount = 0;
                var resolvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // On an incremental check, every returned record is a confirmation newer than the marker,
                // so capture their details for the "see the new QSOs" viewer. Skipped on a full download
                // (thousands of records, and they're all "new" — the summary shows them as a baseline).
                var newList = incremental ? new List<LotwNewQso>() : null;
                foreach (var rec in records)
                {
                    string call = ExtractAdifField(rec, "call");
                    if (string.IsNullOrWhiteSpace(call)) continue;
                    qslCount++;

                    string name = _masterResolver.GetDXCC(call)?.Name;
                    if (!string.IsNullOrEmpty(name)
                        && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                        resolvedNames.Add(name);

                    if (newList != null)
                        newList.Add(new LotwNewQso
                        {
                            Call = call.Trim().ToUpperInvariant(),
                            Country = name ?? string.Empty,
                            DateStr = FormatAdifDateDMY(ExtractAdifField(rec, "qso_date")),
                            TimeStr = FormatAdifTime(ExtractAdifField(rec, "time_on")),
                            Band = (ExtractAdifField(rec, "band") ?? string.Empty).ToUpperInvariant(),
                            Mode = (ExtractAdifField(rec, "mode") ?? string.Empty).ToUpperInvariant()
                        });
                }

                // How many NEW countries this check added = growth of the entity set. This is the number
                // that actually matters (unlike raw QSL count, which re-counts same-day confirmations).
                int countriesBefore = _confirmedEntities.Count;

                // Full run replaces the set; incremental run adds the new confirmations to the cached set.
                if (incremental)
                    _confirmedEntities.UnionWith(resolvedNames);
                else
                    _confirmedEntities = resolvedNames;

                int newCountries = incremental
                    ? Math.Max(0, _confirmedEntities.Count - countriesBefore)
                    : _confirmedEntities.Count;   // a full (re)build treats the whole set as "found"

                // Save the confirmed set and LoTW's last-QSL marker for the next incremental run. The
                // total confirmed-QSO count is cumulative: a full run replaces it, an incremental adds
                // the new QSLs (a slight same-day overlap is harmless for this informational figure).
                s.LotwConfirmedEntities = string.Join("|", _confirmedEntities);
                s.LotwConfirmedQsoCount = incremental ? s.LotwConfirmedQsoCount + qslCount : qslCount;
                s.LotwLastNewQsls = qslCount;
                s.LotwLastNewCountries = newCountries;
                s.LotwLastCheckSince = sinceDisplay;   // empty = a full download
                // The list of new confirmations, for the viewer. Cleared on a full download (no delta).
                s.LotwLastNewJson = newList != null ? JsonConvert.SerializeObject(newList) : string.Empty;
                string lastQsl = ExtractAdifField(adif, "app_lotw_lastqsl");
                if (!string.IsNullOrWhiteSpace(lastQsl)) s.LotwLastQsl = lastQsl.Trim();
                s.Save();

                // ApplyConfirmedHighlight sets the "Confirmed (LoTW): N of M" status, colors the rows, and
                // fills the 3-row summary (total / new QSLs since date / new countries) from what we saved.
                ApplyConfirmedHighlight();
                ApplyWorkedSort();   // rebuild the list so the new row colors show
            }
            catch (Exception ex)
            {
                TB_LotwStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                TB_LotwStatus.Text = "LoTW download failed: " + ex.Message;
            }
            finally
            {
                ShowLotwSpinner(false);
                BTN_CheckLotw.IsEnabled = true;
            }
        }

        // Show/hide the download overlay and run (or stop) the spinner's continuous rotation.
        private void ShowLotwSpinner(bool show)
        {
            if (show)
            {
                LotwLoadingOverlay.Visibility = Visibility.Visible;
                var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360,
                    new Duration(TimeSpan.FromSeconds(0.9)))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                SpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
            }
            else
            {
                SpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                LotwLoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        // The exact value to send as qso_qslsince, from LoTW's own APP_LoTW_LASTQSL marker. For a full
        // timestamp we send the moment + 1 second, so the boundary QSL itself is NOT returned again
        // (LoTW's qso_qslsince is inclusive) -- this guarantees "0 new" when nothing arrived since the
        // last check, even if you check twice in a row. Falls back to a full pull (1970) if unrecognized.
        private static string QslSinceValue(string marker)
        {
            marker = (marker ?? string.Empty).Trim();
            if (System.DateTime.TryParseExact(marker, "yyyy-MM-dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt.AddSeconds(1).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            if (System.Text.RegularExpressions.Regex.IsMatch(marker, @"^\d{4}-\d{2}-\d{2}$"))
                return marker;   // legacy date-only marker: use as-is
            return "1970-01-01";
        }

        // Friendly "since" for the summary: just the date as d.M.yyyy (e.g. 17.7.2026), no time. The
        // marker (LotwLastQsl) keeps its full timestamp internally; only this display is shortened.
        private static string PrettySince(string marker)
        {
            marker = (marker ?? string.Empty).Trim();
            var m = System.Text.RegularExpressions.Regex.Match(marker, @"^(\d{4})-(\d{2})-(\d{2})");
            if (!m.Success) return marker;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}.{1}.{2}",
                int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value));
        }

        // Format an ADIF date ("yyyymmdd") as d.M.yyyy for display; passes anything else through.
        private static string FormatAdifDateDMY(string adifDate)
        {
            adifDate = (adifDate ?? string.Empty).Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(adifDate, @"^\d{8}$"))
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}.{1}.{2}",
                    int.Parse(adifDate.Substring(6, 2)), int.Parse(adifDate.Substring(4, 2)), adifDate.Substring(0, 4));
            return adifDate;
        }

        // Format an ADIF time ("hhmm" or "hhmmss") as HH:MM.
        private static string FormatAdifTime(string adifTime)
        {
            adifTime = (adifTime ?? string.Empty).Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(adifTime, @"^\d{4,6}$"))
                return adifTime.Substring(0, 2) + ":" + adifTime.Substring(2, 2);
            return adifTime;
        }

        // Read one ADIF field's value out of a single record. Handles <field:len> and <field:len:type>.
        private static string ExtractAdifField(string record, string field)
        {
            if (string.IsNullOrEmpty(record)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(
                record, "<" + field + @":(\d+)(?::[^>]*)?>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            int len = int.Parse(m.Groups[1].Value);
            int start = m.Index + m.Length;
            if (len <= 0 || start + len > record.Length) return null;
            return record.Substring(start, len).Trim();
        }

        private void SortWorkedByName(object sender, MouseButtonEventArgs e)
        {
            _workedSort = _workedSort == WorkedSort.NameAsc ? WorkedSort.NameDesc : WorkedSort.NameAsc;
            ApplyWorkedSort();
        }

        // Click a worked-country row -> ask the main window to open the Search window for that country.
        private void WorkedCountry_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CountryItem item && !string.IsNullOrWhiteSpace(item.Name))
                CountrySearchRequested?.Invoke(item.Name);
        }

        private void SortWorkedByCount(object sender, MouseButtonEventArgs e)
        {
            _workedSort = _workedSort == WorkedSort.CountDesc ? WorkedSort.CountAsc : WorkedSort.CountDesc;
            ApplyWorkedSort();
        }

        private void SortWorkedByConfirmed(object sender, MouseButtonEventArgs e)
        {
            _workedSort = _workedSort == WorkedSort.ConfirmedDesc ? WorkedSort.ConfirmedAsc : WorkedSort.ConfirmedDesc;
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
            else if (_workedSort == WorkedSort.ConfirmedDesc) sorted = _workedList.OrderByDescending(c => c.IsConfirmed).ThenByDescending(c => c.Count).ThenBy(c => c.Name).ToList();
            else if (_workedSort == WorkedSort.ConfirmedAsc)  sorted = _workedList.OrderBy(c => c.IsConfirmed).ThenByDescending(c => c.Count).ThenBy(c => c.Name).ToList();
            else                                         sorted = _workedList.OrderByDescending(c => c.Count).ThenBy(c => c.Name).ToList();

            for (int i = 0; i < sorted.Count; i++)
                sorted[i].RowBg = i % 2 == 0 ? ThemeManager.Brush("GridRowBg") : ThemeManager.Brush("GridAltRowBg");

            IC_WorkedCountries.ItemsSource = sorted;
            UpdateWorkedSortHeaders();
        }

        private void ApplyMissingSort()
        {
            List<CountryItem> sorted = _missingSort == MissingSort.NameAsc
                ? _missingList.OrderBy(c => c.Name).ToList()
                : _missingList.OrderByDescending(c => c.Name).ToList();

            for (int i = 0; i < sorted.Count; i++)
                sorted[i].RowBg = i % 2 == 0 ? ThemeManager.Brush("GridRowBg") : ThemeManager.Brush("GridAltRowBg");

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
            TB_SortWorkedConfirmed.Text = _workedSort == WorkedSort.ConfirmedDesc ? "Conf. ▼"
                                        : _workedSort == WorkedSort.ConfirmedAsc  ? "Conf. ▲"
                                        :                                            "Conf.";
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
                    Foreground          = ThemeManager.Brush("TextBrush")
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
            SettingsFlush.RequestSave();   // fires per pixel while dragging; debounce the disk write
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double width  = WindowState == WindowState.Normal ? Width  : RestoreBounds.Width;
            double height = WindowState == WindowState.Normal ? Height : RestoreBounds.Height;
            if (width  > 0) Properties.Settings.Default.StatisticsWindowWidth  = width;
            if (height > 0) Properties.Settings.Default.StatisticsWindowHeight = height;
            SettingsFlush.RequestSave();
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

        // LoTW confirmation state, shown in the Confirmed column: green check when confirmed, bold red
        // cross when not. ✓ = check mark, ✗ = ballot X (clearer than a thin minus).
        public bool IsConfirmed { get; set; }
        public string ConfirmedMark => IsConfirmed ? "✓" : "✗";
        public Brush ConfirmedBrush => IsConfirmed
            ? System.Windows.Media.Brushes.ForestGreen
            : (Brush)new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F));   // vivid red, far more visible
        public string ConfirmedTip => IsConfirmed ? "Confirmed on LoTW" : "Not confirmed on LoTW";
    }

    // One newly-confirmed QSO, captured from the last incremental LoTW check for the "see the new
    // QSOs" viewer. Persisted as JSON in Settings.LotwLastNewJson.
    public class LotwNewQso
    {
        public string Call { get; set; }
        public string Country { get; set; }
        public string DateStr { get; set; }
        public string TimeStr { get; set; }
        public string Band { get; set; }
        public string Mode { get; set; }
    }
}
