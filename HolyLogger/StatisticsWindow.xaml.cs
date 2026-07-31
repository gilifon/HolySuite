using DXCCManager;
using HolyParser;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HolyLogger
{
    public partial class StatisticsWindow : Window
    {
        // Not readonly: re-read from the database after a Check so the per-QSO confirmation flags (which
        // the zone lists read) are live, without the operator having to reopen the window.
        private ObservableCollection<QSO> _allQsos;

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

        // DXCC entity names confirmed by the SELECTED confirmation source. Populated from that source's
        // cached download result; drives the Confirmed column (tick) in the worked list. Reloaded from a
        // different cache whenever the source folder changes (see LoadConfirmedCache / _source).
        private HashSet<string> _confirmedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The confirmation source whose analysis the window is currently showing - one folder each in the
        // vertical tab strip. "Worked" is the plain log with no confirmation overlay; the rest color the
        // worked list by that service's confirmations. Only LoTW and QRZ are wired in this first step.
        private enum ConfSource { Worked, Lotw, Qrz, Eqsl, Clublog, Paper }
        private ConfSource _source = ConfSource.Worked;

        // Cancels the running Check (download + marking) when the operator presses Stop. A fresh source
        // is created at the start of each Check.
        private System.Threading.CancellationTokenSource _checkCts;

        private void BTN_StopCheck_Click(object sender, RoutedEventArgs e)
        {
            try { _checkCts?.Cancel(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            BTN_StopCheck.IsEnabled = false;
            TB_LotwLoadingSub.Text = "Stopping…";
        }

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
            //
            // The saved height is never allowed BELOW what the XAML asks for. A size stored before the
            // content grew would otherwise win over the new default and clip whatever was added at the
            // bottom - which is exactly what happened when the "Get All Confirmations" row was added to
            // the LoTW box: the XAML height went up, the saved height overwrote it, and the button ended
            // up jammed against the frame. Growing the window is still remembered; only shrinking it
            // past the content is refused.
            double contentHeight = Height;   // the XAML value, i.e. what this window's content needs
            if (s.StatisticsWindowWidth  >= MinWidth)  Width  = s.StatisticsWindowWidth;
            if (s.StatisticsWindowHeight >= MinHeight) Height = Math.Max(s.StatisticsWindowHeight, contentHeight);

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
            BuildSourceFolders();
            BuildLeftViewFolders();
            ApplyLeftView();

            // Match country-table scroll heights to the pivot table height whenever the pivot resizes.
            PivotOuterBorder.SizeChanged += (sender, e) =>
            {
                if (e.NewSize.Height > 0)
                {
                    _tableHeight = e.NewSize.Height;
                    SV_WorkedCountries.Height  = _tableHeight;
                    SV_MissingCountries.Height = _tableHeight;
                    AdjustZoneHeights();
                }
            };
            // The zone lists (CQ/ITU) live in the right column, UNDER the LoTW panel slot. On the LoTW
            // folder that slot is taller than the tiles, so the zones start lower; shrink them by that
            // extra offset so their BOTTOMS line up with the country lists. When the slot height changes
            // (folder switch shows/hides the panel), recompute.
            LotwSlot.SizeChanged += (snd, ev) => AdjustZoneHeights();
        }

        private double _tableHeight;

        private void AdjustZoneHeights()
        {
            if (_tableHeight <= 0 || SV_MissingCQ == null) return;
            double extra = 0;
            try { extra = Math.Max(0, LotwSlot.ActualHeight - TilesRow.ActualHeight); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            double h = Math.Max(80, _tableHeight - extra);
            SV_MissingCQ.Height  = h;
            SV_MissingITU.Height = h;
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

        // The QSOs the open folder is about: every QSO on the Worked folder, and only the ones that
        // service has confirmed on any other. Everything on the left of the window - both tiles, the
        // date range and the QSO table - is counted from this, so standing on the LoTW folder answers
        // "what have I got confirmed at LoTW" rather than repeating the whole log six times over.
        private List<QSO> SourceQsos()
        {
            if (_allQsos == null) return new List<QSO>();
            if (_source == ConfSource.Worked) return _allQsos.ToList();
            return _allQsos.Where(q => q != null && IsAchievedForSource(q)).ToList();
        }

        // Repaints everything that depends on which source folder is open. Cheap enough to run on every
        // folder change: one pass over the log to filter, then the pivot's own pass.
        private void ApplySourceCounts()
        {
            if (TB_TotalQSOs == null) return;
            List<QSO> qsos = SourceQsos();

            TB_TotalQSOs.Text = qsos.Count.ToString("N0");

            _uniqueCallsText = qsos
                .Select(q => q.DXCall)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count().ToString("N0");
            ApplyUniqueTile();

            // The dates follow too, so the range is the span of the QSOs actually being counted - the
            // first and last CONFIRMED contact on a confirmation folder, not the first and last logged.
            List<string> dates = qsos
                .Where(q => !string.IsNullOrEmpty(q.Date))
                .Select(q => q.Date).OrderBy(d => d, StringComparer.Ordinal).ToList();
            TB_DateStart.Text = dates.Count > 0 ? FormatAdifDate(dates[0]) : "—";
            TB_DateEnd.Text = dates.Count > 0 ? FormatAdifDate(dates[dates.Count - 1]) : "—";

            BuildPivot(qsos);

            TB_PivotHeader.Text = "QSOs by Bands & Mode"
                + (_source == ConfSource.Worked ? "" : " — " + SourceTitle(_source))
                + "\n(" + qsos.Count.ToString("N0") + ")";
        }

        private void ComputeStats()
        {
            int total = _allQsos != null ? _allQsos.Count : 0;

            // Name of the log these statistics are for, shown top-left. Wraps in the UI, so a long name
            // is shown in full.
            try
            {
                var dal = DataAccess.GetInstance();
                string logName = dal?.GetLogName(dal.ActiveLogId);
                TB_LogName.Text = string.IsNullOrWhiteSpace(logName) ? "(unnamed log)" : logName;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // Warn if the country file is overdue for a refresh (e.g. AD1C moved the download URL). The
            // cty.dat version tile was removed from the window; the warning still surfaces a stale file.
            string ctyWarning = CtyDatService.UpdateWarning();
            if (!string.IsNullOrEmpty(ctyWarning))
            {
                TB_CtyWarning.Text = ctyWarning;
                TB_CtyWarning.Visibility = Visibility.Visible;
            }
            else
            {
                TB_CtyWarning.Visibility = Visibility.Collapsed;
            }

            if (total == 0)
            {
                int totalDxcc = _masterResolver.GetAllEntityNames().Count;
                TB_TotalQSOs.Text       = "0";
                _uniqueCallsText        = "0";
                _countryCountText       = "0";
                ApplyUniqueTile();
                TB_UniqueCountries.Text = $"0 / {totalDxcc}";
                TB_MissingDxcc.Text     = totalDxcc.ToString();
                TB_DateStart.Text = "—";
                TB_DateEnd.Text   = "—";
                PopulateMissingZones();
                TB_Status.Text          = "No QSOs to analyze.";
                return;
            }

            TB_UniqueCountries.Text = _allQsos
                .Select(q => !string.IsNullOrEmpty(q.DXCC) ? q.DXCC : q.Country)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count().ToString();

            ApplySourceCounts();     // the tiles, the dates and the QSO table, for the open folder
            BuildCountryPivot();
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

        // adifDate is the QSO's own date (yyyyMMdd). Pass it whenever it is known: Club Log resolves a
        // callsign against the date it was worked, which is the only way an entity that no longer issues
        // its prefix (Serbia's 4N, Bosnia's T9) counts at all. Omit it only for a live "today" answer.
        // Both sides of a confirmation match must be resolved the same way, or a confirmed country could
        // be matched under one name and counted under another.
        private DXCC Resolve(string call, string adifDate = null)
        {
            call = (call ?? string.Empty).Trim();
            if (call.Length == 0 || _masterResolver == null) return null;
            if (_resolveCache == null) _resolveCache = new Dictionary<string, DXCC>(StringComparer.OrdinalIgnoreCase);
            string key = call + "|" + (adifDate ?? string.Empty).Trim();
            if (!_resolveCache.TryGetValue(key, out var d))
            {
                d = CountryLookup.Shared.Resolve(call,
                        adifDate == null ? DateTime.UtcNow : CountryLookup.QsoDate(adifDate));
                _resolveCache[key] = d;
            }
            return d;
        }

        private List<int> MissingZones(int maxZone, Func<DXCC, int> zoneOf)
        {
            var achieved = new HashSet<int>();
            if (_allQsos != null)
            {
                foreach (QSO q in _allQsos)
                {
                    if (!IsAchievedForSource(q)) continue;   // confirmation folders count only confirmed QSOs
                    var d = Resolve(q.DXCall, q.Date);
                    if (d == null) continue;
                    int z = zoneOf(d);
                    if (z >= 1 && z <= maxZone) achieved.Add(z);
                }
            }
            return Enumerable.Range(1, maxZone).Where(z => !achieved.Contains(z)).ToList();
        }

        // ── pivot table builder ───────────────────────────────────────────

        // qsos is the set for the OPEN FOLDER (see SourceQsos): the whole log on Worked, only that
        // service's confirmed contacts on any other.
        private void BuildPivot(List<QSO> qsos)
        {
            // 1. Accumulate counts
            var counts = new Dictionary<string, Dictionary<string, int>>();
            foreach (var b in PivotBands)
                counts[b] = new Dictionary<string, int>
                    { { "SSB", 0 }, { "CW", 0 }, { "DIGI", 0 }, { "FM", 0 } };

            // Bucket for QSOs whose band is missing or not in PivotBands
            var other = new Dictionary<string, int>
                    { { "SSB", 0 }, { "CW", 0 }, { "DIGI", 0 }, { "FM", 0 } };

            foreach (var q in qsos)
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

        // ── countries by band and mode ─────────────────────────────────────
        //
        // The QSO pivot above answers "how many contacts"; this one answers "how many COUNTRIES", for
        // whichever confirmation folder is selected. Same shape, one column fewer.
        //
        // There is no percentage pair here, and that is deliberate. A QSO sits on exactly one band in
        // exactly one mode, so its percentages split 100% between the cells. A COUNTRY sits in every
        // cell it was worked in - 15m SSB and 20m CW and 40m CW - so percentages would add to several
        // hundred and invite the reasonable question "how can 99.6% and 23.5% come to more than 100%".
        // The counts say the same thing without the trap.
        private static readonly double[] CountryColW = { 70, 61, 61, 61, 61, 69 };

        private void BuildCountryPivot()
        {
            if (CountryPivotBorder == null) return;

            // A set per cell, not a counter: the same country worked ten times on 20m SSB is one country.
            var cell = new Dictionary<string, HashSet<string>>();
            var bandTotal = new Dictionary<string, HashSet<string>>();
            var modeTotal = new Dictionary<string, HashSet<string>>();
            var grand = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> Bucket(Dictionary<string, HashSet<string>> d, string key)
            {
                HashSet<string> set;
                if (!d.TryGetValue(key, out set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); d[key] = set; }
                return set;
            }

            foreach (QSO q in _allQsos ?? new ObservableCollection<QSO>())
            {
                if (q == null || !IsAchievedForSource(q)) continue;

                // Resolved live from the callsign and the QSO's own date, exactly as the worked/missing
                // lists do, so this table can never disagree with the tiles beside it.
                string country = Resolve(q.DXCall, q.Date)?.Name;
                if (string.IsNullOrEmpty(country) || string.Equals(country, "Unknown", StringComparison.OrdinalIgnoreCase))
                    continue;

                string b = NormalizeBand(q.Band);
                if (b == null || Array.IndexOf(PivotBands, b) < 0) b = "Other";
                string m = NormalizeMode(q.Mode);

                Bucket(cell, b + "|" + m).Add(country);
                Bucket(bandTotal, b).Add(country);
                Bucket(modeTotal, m).Add(country);
                grand.Add(country);
            }

            int Count(Dictionary<string, HashSet<string>> d, string key)
            {
                HashSet<string> set;
                return d.TryGetValue(key, out set) ? set.Count : 0;
            }

            bool hasOther = Count(bandTotal, "Other") > 0;
            int numBands = PivotBands.Length;
            int numRows = 2 + numBands + (hasOther ? 1 : 0) + 1;

            var tbl = new Grid();
            foreach (double w in CountryColW)
                tbl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            for (int i = 0; i < numRows; i++)
                tbl.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Brush headerBg = ThemeManager.Brush("GridHeaderBg");
            Brush yellowBg = ThemeManager.Brush("EditFieldBg");
            Brush evenBg = ThemeManager.Brush("GridRowBg");
            Brush oddBg = ThemeManager.Brush("GridAltRowBg");
            Brush gridLine = Br(0xAA, 0xAA, 0xAA);
            Brush black = ThemeManager.Brush("ThemeBorderBrush");

            Border TL(Border inner) => new Border { BorderBrush = black, BorderThickness = new Thickness(0, 2, 0, 0), Child = inner };
            Border VL(Border inner) => new Border { BorderBrush = black, BorderThickness = new Thickness(2, 0, 0, 0), Child = inner };

            // Row 0: Band [m] | "mode" over the four mode columns | "Total"
            Put(tbl, 0, 0, 1, 1, MkCell("Band [m]", headerBg, gridLine, bold: true));
            Put(tbl, 0, 1, 1, 4, VL(MkCell("mode", headerBg, gridLine, bold: true)));
            Put(tbl, 0, 5, 1, 1, VL(MkCell("Total", headerBg, gridLine, bold: true)));

            // Row 1: sub-headers
            Put(tbl, 1, 0, 1, 1, MkCell("", headerBg, gridLine, bold: true));
            Put(tbl, 1, 1, 1, 1, VL(MkCell("SSB", headerBg, gridLine, bold: true)));
            Put(tbl, 1, 2, 1, 1, MkCell("CW", headerBg, gridLine, bold: true));
            Put(tbl, 1, 3, 1, 1, MkCell("DIGI", headerBg, gridLine, bold: true));
            Put(tbl, 1, 4, 1, 1, MkCell("FM", headerBg, gridLine, bold: true));
            Put(tbl, 1, 5, 1, 1, VL(MkCell("countries", headerBg, gridLine, bold: true)));

            void BandRow(string label, int r, Brush bg, bool topLine)
            {
                int ssb = Count(cell, label + "|SSB"), cw = Count(cell, label + "|CW");
                int digi = Count(cell, label + "|DIGI"), fm = Count(cell, label + "|FM");
                int tot = Count(bandTotal, label);
                Border Wrap(Border inner) => topLine ? TL(inner) : inner;
                Put(tbl, r, 0, 1, 1, Wrap(MkCell(label, bg, gridLine, align: TextAlignment.Left)));
                Put(tbl, r, 1, 1, 1, VL(Wrap(MkCell(N(ssb), bg, gridLine))));
                Put(tbl, r, 2, 1, 1, Wrap(MkCell(N(cw), bg, gridLine)));
                Put(tbl, r, 3, 1, 1, Wrap(MkCell(N(digi), bg, gridLine)));
                Put(tbl, r, 4, 1, 1, Wrap(MkCell(N(fm), bg, gridLine)));
                Put(tbl, r, 5, 1, 1, VL(Wrap(MkCell(N(tot), bg, gridLine, bold: tot > 0))));
            }

            for (int i = 0; i < numBands; i++)
                BandRow(PivotBands[i], 2 + i, (Brush)(i % 2 == 0 ? evenBg : oddBg), i == 0);
            if (hasOther)
                BandRow("Other", 2 + numBands, (Brush)(numBands % 2 == 0 ? evenBg : oddBg), false);

            // Footer: one row only - the mode totals are counts, and there is no percentage row.
            int fr = 2 + numBands + (hasOther ? 1 : 0);
            Put(tbl, fr, 0, 1, 1, TL(MkCell("Total", headerBg, gridLine, bold: true, align: TextAlignment.Left)));
            Put(tbl, fr, 1, 1, 1, VL(TL(MkCell(N(Count(modeTotal, "SSB")), headerBg, gridLine, bold: true))));
            Put(tbl, fr, 2, 1, 1, TL(MkCell(N(Count(modeTotal, "CW")), headerBg, gridLine, bold: true)));
            Put(tbl, fr, 3, 1, 1, TL(MkCell(N(Count(modeTotal, "DIGI")), headerBg, gridLine, bold: true)));
            Put(tbl, fr, 4, 1, 1, TL(MkCell(N(Count(modeTotal, "FM")), headerBg, gridLine, bold: true)));
            Put(tbl, fr, 5, 1, 1, VL(TL(MkCell(grand.Count > 0 ? grand.Count.ToString() : "", yellowBg, gridLine, bold: true))));

            CountryPivotBorder.Child = tbl;

            // Feed the tile from the same count the table just made, so the two can never disagree.
            _countryCountText = grand.Count.ToString("N0");
            ApplyUniqueTile();

            if (TB_CountryPivotHeader != null)
                TB_CountryPivotHeader.Text = "Countries by Bands & Mode — " + SourceTitle(_source)
                                             + "\n(" + grand.Count.ToString("N0") + ")";

            // An empty table is not a fault - it means that service has confirmed nothing in this log -
            // so say which it is rather than leaving a blank grid to be puzzled over.
            if (TB_CountryPivotNote != null)
                TB_CountryPivotNote.Text = grand.Count == 0
                    ? "Nothing is marked as confirmed by " + SourceTitle(_source) + " in this log, so there is nothing to count yet."
                    : "A country counts once in every cell it belongs to, so the rows and columns do not add up to the total — one country worked on two bands is still one country.";
        }

        // WHAT the left-hand table counts. The source strip opposite says WHICH SOURCE; this one says
        // whether you are looking at contacts or countries. Two small strips beat one long one: six
        // sources times two views would be twelve tabs on a single line.
        private enum LeftView { Qso, Dxcc }
        private LeftView _leftView = LeftView.Qso;

        // The second tile shows one or the other, so both values are kept and the tile is repainted when
        // the folder changes: unique callsigns worked (QSO folder) or countries (DXCC folder). The
        // country figure is whatever the DXCC table last counted, so tile and table always agree.
        private string _uniqueCallsText = "0";
        private string _countryCountText = "0";

        private void BuildLeftViewFolders()
        {
            if (LB_LeftView == null || LB_LeftView.Items.Count > 0) return;
            LB_LeftView.Items.Add(new ListBoxItem
            {
                Content = "QSO", Tag = LeftView.Qso,
                ToolTip = "How many CONTACTS, by band and mode"
            });
            LB_LeftView.Items.Add(new ListBoxItem
            {
                Content = "DXCC", Tag = LeftView.Dxcc,
                ToolTip = "How many COUNTRIES, by band and mode, for the source folder selected on the right"
            });
            LB_LeftView.SelectedIndex = 0;
            ApplyLeftViewColour();
        }

        // The left tabs and page wear the colour of the source folder open on the right. The left table
        // belongs to that source - DXCC on the eQSL folder counts eQSL's countries - so it would be
        // confusing for the two halves to be different colours.
        private void ApplyLeftViewColour()
        {
            System.Windows.Media.Brush tint = SourceBackground(_source);
            if (SV_LeftContent != null) SV_LeftContent.Background = tint;
            if (LB_LeftView == null) return;
            foreach (object o in LB_LeftView.Items)
            {
                ListBoxItem item = o as ListBoxItem;
                if (item != null) item.Background = tint;
            }
        }

        private void LB_LeftView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(LB_LeftView.SelectedItem is ListBoxItem item) || !(item.Tag is LeftView v)) return;
            _leftView = v;
            ApplyLeftView();
        }

        private void ApplyLeftView()
        {
            if (QsoViewPanel == null || DxccViewPanel == null) return;
            bool qso = _leftView == LeftView.Qso;
            QsoViewPanel.Visibility = qso ? Visibility.Visible : Visibility.Collapsed;
            DxccViewPanel.Visibility = qso ? Visibility.Collapsed : Visibility.Visible;
            if (!qso) BuildCountryPivot();   // it follows the source folder, so rebuild on the way in
            ApplyUniqueTile();
        }

        // The middle tile belongs to whichever folder is open: "Unique Calls" counts callsigns, which
        // means nothing on the DXCC folder, so there it becomes "Countries" and shows what the table
        // below it totals.
        private void ApplyUniqueTile()
        {
            if (TB_UniqueCalls == null || TB_UniqueCallsLabel == null) return;
            bool qso = _leftView == LeftView.Qso;
            TB_UniqueCallsLabel.Text = qso ? "Unique Calls" : "Countries";
            TB_UniqueCalls.Text = qso ? _uniqueCallsText : _countryCountText;
        }

        // The folder's name as the operator sees it on its tab.
        private static string SourceTitle(ConfSource s)
        {
            switch (s)
            {
                case ConfSource.Lotw: return "LoTW";
                case ConfSource.Qrz: return "QRZ";
                case ConfSource.Eqsl: return "eQSL";
                case ConfSource.Clublog: return "Club Log";
                case ConfSource.Paper: return "Paper QSL";
                default: return "Worked";
            }
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
                .Select(q => Resolve(q.DXCall, q.Date)?.Name)
                .Where(n => !string.IsNullOrEmpty(n) && !string.Equals(n, "Unknown", StringComparison.OrdinalIgnoreCase))
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            _workedList = workedCounts.Keys
                .Select(name => new CountryItem
                {
                    Name      = name,
                    Count     = workedCounts[name],
                    FlagImage = GetFlagImage(name),
                }).ToList();

            // Single line now — the LoTW button sits beside it on the same row.
            TB_WorkedHeader.Text = $"Worked Countries ({_workedList.Count})";

            // The Missing list is source-aware, so it is built in its own method that the folder switch
            // also calls. The tiles are set by ApplyConfirmedHighlight from the same _missingList.
            RebuildMissingCountries();

            TB_SortWorkedName.MouseLeftButtonUp  -= SortWorkedByName;
            TB_SortWorkedName.MouseLeftButtonUp  += SortWorkedByName;
            TB_SortWorkedCount.MouseLeftButtonUp -= SortWorkedByCount;
            TB_SortWorkedCount.MouseLeftButtonUp += SortWorkedByCount;
            TB_SortWorkedConfirmed.MouseLeftButtonUp -= SortWorkedByConfirmed;
            TB_SortWorkedConfirmed.MouseLeftButtonUp += SortWorkedByConfirmed;
            TB_SortMissingName.MouseLeftButtonUp -= SortMissingByName;
            TB_SortMissingName.MouseLeftButtonUp += SortMissingByName;

            ApplyConfirmedHighlight();   // sets the tiles + colors the worked rows for the current source
            ApplyWorkedSort();
        }

        // Rebuilds the Missing Countries list for the CURRENT folder, so it always matches the Missing
        // tile: on the Worked folder it is the entities never contacted; on a confirmation folder it is
        // the entities not confirmed by that source (all DXCC minus that source's confirmed set).
        private void RebuildMissingCountries()
        {
            var allDxccEntities = _masterResolver.GetAllEntityNames();
            HashSet<string> achieved = _source == ConfSource.Worked
                ? new HashSet<string>(_workedList.Select(c => c.Name), StringComparer.OrdinalIgnoreCase)
                : _confirmedEntities;

            _missingList = allDxccEntities
                .Where(n => !achieved.Contains(n))
                .Select(name => new CountryItem { Name = name, FlagImage = GetFlagImage(name) })
                .ToList();

            TB_MissingHeader.Text = $"Missing Countries\n({_missingList.Count})";
            ApplyMissingSort();
        }

        // Whether a QSO counts as "achieved" for the current folder - i.e. removes its entity/zone from
        // the Missing lists. On the Worked folder any logged QSO counts; on a confirmation folder only a
        // QSO confirmed by that source counts. Reads the per-QSO confirmation flags.
        private bool IsAchievedForSource(QSO q)
        {
            switch (_source)
            {
                case ConfSource.Lotw:    return q.LotwQslRcvd == 1;
                case ConfSource.Qrz:     return q.QrzQslRcvd == 1;
                case ConfSource.Eqsl:    return q.EqslQslRcvd == 1;
                case ConfSource.Clublog: return q.ClublogQslRcvd == 1;
                case ConfSource.Paper:   return q.PaperQslRcvd == 1;
                default:                 return true;
            }
        }

        // ---- LoTW "confirmed countries" (confirmed on LoTW) ----

        // Fills the vertical folder strip with the confirmation sources this operator uses. "Worked"
        // (the plain log, no confirmation) is always first; LoTW / QRZ appear when their service is in
        // use, or when a past download left a cached confirmed set to show. Selecting index 0 lands on
        // Worked and fires the first RefreshForSource.
        private void BuildSourceFolders()
        {
            var s = Properties.Settings.Default;
            LB_Source.Items.Clear();
            AddSourceFolder(ConfSource.Worked, "Worked");
            if (s.UseLotwService || !string.IsNullOrWhiteSpace(s.LotwConfirmedEntities))
                AddSourceFolder(ConfSource.Lotw, "LoTW");
            if (s.UseQrzLogbook || !string.IsNullOrWhiteSpace(s.QrzConfirmedEntities))
                AddSourceFolder(ConfSource.Qrz, "QRZ");
            // eQSL is configured by the per-callsign accounts table, so show the folder when an account
            // exists (or the service is on, or a past download left a cache).
            bool hasEqsl = false;
            try { hasEqsl = (DataAccess.GetInstance()?.GetEqslAccounts().Count ?? 0) > 0; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            if (s.UseEqslService || hasEqsl || !string.IsNullOrWhiteSpace(s.EqslConfirmedEntities))
                AddSourceFolder(ConfSource.Eqsl, "eQSL");
            // Club Log is a single account (e-mail + password), so show the folder when the service is on
            // or a past download left a cached confirmed set.
            if (s.UseClublogService || !string.IsNullOrWhiteSpace(s.ClublogConfirmedEntities))
                AddSourceFolder(ConfSource.Clublog, "Club Log");
            // Paper QSL is manual (no service to configure), so it is ALWAYS available.
            AddSourceFolder(ConfSource.Paper, "Paper QSL");
            LB_Source.SelectedIndex = 0;   // Worked; fires LB_Source_SelectionChanged -> RefreshForSource
        }

        private void AddSourceFolder(ConfSource src, string label)
        {
            LB_Source.Items.Add(new ListBoxItem { Content = label, Tag = src, Background = SourceBackground(src) });
        }

        // The light tint that identifies each confirmation source - used both for the folder tab and for
        // the per-source content area, so the two always match. (eQSL / Club Log / Paper are listed for
        // when those folders are added.)
        private static readonly Dictionary<string, System.Windows.Media.Brush> _sourceBrushes =
            new Dictionary<string, System.Windows.Media.Brush>();

        // The tab / page colours reuse the app's OWN defined background colours, so the folders match the
        // rest of the UI and follow any scheme customization: Worked = the on-radio-frequency green
        // (RowOnFreqBg), LoTW = the LoTW-user yellow (RowLotwBg), QRZ = the main-form blue (FormBg).
        // eQSL uses the fixed Msg-button purple; Club Log a light brown.
        private static System.Windows.Media.Brush SourceBackground(ConfSource src)
        {
            switch (src)
            {
                case ConfSource.Lotw:    return ThemeManager.Brush("RowLotwBg");     // LoTW-user yellow
                case ConfSource.Qrz:     return ThemeManager.Brush("FormBg");         // main-form blue
                case ConfSource.Eqsl:    return HexBrush("#E6CCFF");                  // purple (the Msg buttons)
                case ConfSource.Clublog: return HexBrush("#EAD9BF");                  // light brown
                case ConfSource.Paper:   return HexBrush("#FFFFFF");                  // white
                default:                 return ThemeManager.Brush("RowOnFreqBg");    // Worked = on-frequency green
            }
        }

        private static System.Windows.Media.Brush HexBrush(string hex)
        {
            if (_sourceBrushes.TryGetValue(hex, out var cached)) return cached;
            var b = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString(hex);
            b.Freeze();
            _sourceBrushes[hex] = b;
            return b;
        }

        private void LB_Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(LB_Source.SelectedItem is ListBoxItem item) || !(item.Tag is ConfSource src)) return;
            _source = src;
            RefreshForSource();
        }

        // Re-reads the active log's QSOs so their (now updated) confirmation flags are current, then
        // repaints the folder. Called after a Check so every value stays live - nothing needs a reopen.
        private void ReloadQsosAfterCheck()
        {
            try
            {
                var dal = DataAccess.GetInstance();
                if (dal != null) _allQsos = dal.GetQSOsForLog(dal.ActiveLogId);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            _resolveCache = null;   // rebuilt lazily against the fresh list
            RefreshForSource();
        }

        // Called by the log windows when a Paper QSL checkbox is ticked/unticked, so the Paper QSL folder
        // recomputes its confirmed countries live - the "no button needed" behaviour. Updates the matching
        // QSO in our own list (the grid may hold a different instance after a reload) and repaints only
        // when the Paper QSL folder is the one on screen.
        public void NotifyPaperQslChanged(int qsoId, bool confirmed)
        {
            try
            {
                if (_allQsos != null)
                    foreach (var q in _allQsos)
                        if (q != null && q.id == qsoId) { q.PaperQslRcvd = confirmed ? 1 : 0; break; }
                if (_source == ConfSource.Paper) RefreshForSource();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Repaints the tables for the selected source: load that source's confirmed cache, recolor the
        // worked list, and show only that source's download button (none on the Worked folder).
        private void RefreshForSource()
        {
            LoadConfirmedCache();
            RebuildMissingCountries();   // Missing Countries list for this source
            ApplySourceCounts();         // tiles, dates and the QSO table, for this source
            BuildCountryPivot();         // countries by band and mode, for this source
            PopulateMissingZones();      // Missing CQ / ITU zones for this source
            ApplyConfirmedHighlight();   // tiles read the freshly-built _missingList
            ApplyWorkedSort();

            // Tint the per-source content area to match the selected folder's colour.
            if (SV_SourceContent != null) SV_SourceContent.Background = SourceBackground(_source);

            // ...and tint the LEFT page and its two tabs to the same colour, so the whole window reads as
            // one open folder rather than two unrelated halves wearing different colours.
            ApplyLeftViewColour();

            // Only "Check LoTW Updates" (incremental) lives in the header; the per-source full-download
            // buttons live in the summary frame and are toggled by PopulateConfirmedSummary.
            if (BTN_CheckLotw != null)
                BTN_CheckLotw.Visibility = _source == ConfSource.Lotw ? Visibility.Visible : Visibility.Collapsed;
        }

        // Restore the last downloaded confirmed-entity set so the colors/count show immediately on open
        // without re-downloading. Stored as a '|'-joined list of DXCC entity names.
        private void LoadConfirmedCache()
        {
            _confirmedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Paper QSL has nothing to download - it is manually marked per QSO - so its confirmed set is
            // computed LIVE from the log itself: the entities of the QSOs the operator ticked. This is what
            // makes it recalculate automatically the moment a paper-QSL checkbox changes, with no button.
            if (_source == ConfSource.Paper)
            {
                if (_allQsos != null)
                    foreach (var q in _allQsos)
                    {
                        if (q == null || q.PaperQslRcvd != 1) continue;
                        string name = Resolve(q.DXCall, q.Date)?.Name;
                        if (!string.IsNullOrEmpty(name) && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                            _confirmedEntities.Add(name);
                    }
                return;
            }

            // The Worked folder has no confirmation overlay; each other folder reads its own cache.
            string cached =
                _source == ConfSource.Lotw    ? Properties.Settings.Default.LotwConfirmedEntities :
                _source == ConfSource.Qrz     ? Properties.Settings.Default.QrzConfirmedEntities  :
                _source == ConfSource.Eqsl    ? Properties.Settings.Default.EqslConfirmedEntities :
                _source == ConfSource.Clublog ? Properties.Settings.Default.ClublogConfirmedEntities :
                string.Empty;
            if (string.IsNullOrWhiteSpace(cached)) return;
            foreach (var n in cached.Split('|'))
                if (!string.IsNullOrWhiteSpace(n)) _confirmedEntities.Add(n.Trim());

            // Self-heal a bogus total left over from earlier broken-download testing: the confirmed-QSO
            // count can never be below the number of confirmed countries (each country has >=1 confirmed
            // QSO). If it is, drop the incremental marker so the next Check LoTW does a one-time full
            // re-download that recomputes the true total. The cached colors stay until then. LoTW-only:
            // the incremental marker belongs to the LoTW download.
            if (_source != ConfSource.Lotw) return;
            var s = Properties.Settings.Default;
            if (s.LotwConfirmedQsoCount < _confirmedEntities.Count && !string.IsNullOrWhiteSpace(s.LotwLastQsl))
            {
                s.LotwLastQsl = string.Empty;
                s.Save();
            }
        }

        // Green-highlight the worked rows whose entity is in the confirmed set, and update the count line.
        // Does not re-sort; the caller refreshes the list (BuildCountryTables / the button both do).
        // Count of distinct DELETED entities confirmed by the current source, from the codes that
        // source's last download stored.
        private int CountConfirmedDeleted()
        {
            string csv =
                _source == ConfSource.Lotw    ? Properties.Settings.Default.LotwConfirmedDeletedCodes :
                _source == ConfSource.Qrz     ? Properties.Settings.Default.QrzConfirmedDeletedCodes  :
                _source == ConfSource.Eqsl    ? Properties.Settings.Default.EqslConfirmedDeletedCodes :
                _source == ConfSource.Clublog ? Properties.Settings.Default.ClublogConfirmedDeletedCodes :
                string.Empty;
            if (string.IsNullOrWhiteSpace(csv)) return 0;
            var set = new HashSet<int>();
            foreach (var part in csv.Split(','))
                if (int.TryParse(part.Trim(), out int c)) set.Add(c);
            return set.Count;
        }

        // The current source's display name, for the confirmed tile and status line.
        private string SourceName =>
            _source == ConfSource.Lotw    ? "LoTW" :
            _source == ConfSource.Qrz     ? "QRZ" :
            _source == ConfSource.Eqsl    ? "eQSL" :
            _source == ConfSource.Clublog ? "Club Log" :
            _source == ConfSource.Paper   ? "Paper QSL" : "Worked";

        private void ApplyConfirmedHighlight()
        {
            if (_workedList == null) return;
            // The Worked folder has no confirmation source, so its Conf. column is blank (not all-crosses).
            bool showConf = _source != ConfSource.Worked;
            int confirmed = 0;
            foreach (var item in _workedList)
            {
                item.ShowConfirmation = showConf;
                item.IsConfirmed = _confirmedEntities.Contains(item.Name);
                if (item.IsConfirmed) confirmed++;
            }

            // How many DELETED entities are confirmed (from the DXCC codes LoTW returned, stored by the
            // download). ALWAYS shown - including "0 deleted" - because an operator needs to see the
            // figure to trust it, and a deleted-entity total is a thing many operators actively track.
            // The deleted count is not part of "of N" (that is the current worked list), so it reads as
            // a separate clause.
            int deletedConfirmed = CountConfirmedDeleted();

            // Kept to one line to fit the worked column (wrapping would misalign the four column
            // headers, which share a fixed height). "of N" is dropped because the worked-countries
            // header right above already shows that total.
            TB_LotwStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
            TB_LotwStatus.Text = (_source == ConfSource.Worked || _confirmedEntities.Count == 0)
                ? string.Empty
                : $"Confirmed ({SourceName}): {confirmed} active,  {deletedConfirmed} deleted";

            // The three source tiles. Confirmed and Missing PARTITION all DXCC entities (out of the full
            // 340): Confirmed = entities confirmed by this source, Missing = 340 - Confirmed. "Worked,
            // not confirmed" is the chaseable SUBSET of Missing - entities already contacted but not yet
            // confirmed here (worked - confirmed) - so it is not a third partition, just a highlight.
            int workedDxcc = _workedList.Count;                                  // 265 - entities contacted
            int totalDxcc  = _masterResolver.GetAllEntityNames().Count;         // 340 - all DXCC entities
            // The Missing tile ALWAYS reads the Missing Countries list, so tile and list can never
            // disagree (that mismatch is the bug this fixes). The list itself is source-aware.
            int missingCount = _missingList != null ? _missingList.Count : 0;

            // The Confirmed tile only makes sense for a confirmation source; the Worked folder is the
            // plain log, so it shows just Worked / DXCC and Missing DXCC.
            if (TileConfirmed != null)
                TileConfirmed.Visibility = _source == ConfSource.Worked ? Visibility.Collapsed : Visibility.Visible;

            if (_source == ConfSource.Worked)
            {
                // Plain log folder: Worked = worked / total; Missing = never contacted. The tiles spell out
                // "Countries" (the number is a country count); the source is named by the folder tab.
                TB_WorkedTileLabel.Text = "Worked Countries";
                TB_UniqueCountries.Text = $"{workedDxcc} / {totalDxcc}";
                TB_MissingTileLabel.Text = "Missing Countries";
                TB_MissingDxcc.Text = missingCount.ToString();
            }
            else
            {
                // Confirmation folder: Confirmed + Missing partition all 340. Worked-not-confirmed is
                // the chaseable subset (contacted but not confirmed here). Labels spell out "Countries";
                // the source name is dropped here because the folder tab (and status line) already show it.
                TB_ConfirmedTileLabel.Text = "Confirmed Countries";
                TB_WorkedTileLabel.Text = "Worked Countries, not confirmed";
                TB_MissingTileLabel.Text = "Missing Countries";
                TB_ConfirmedDxcc.Text = $"{confirmed} / {totalDxcc}";
                TB_UniqueCountries.Text = Math.Max(0, workedDxcc - confirmed).ToString();
                TB_MissingDxcc.Text = missingCount.ToString();
            }

            PopulateConfirmedSummary();
        }

        // The summary frame, shown on EVERY confirmation folder (LoTW / QRZ / eQSL) so the pages look
        // alike. Kept the same 4-row height everywhere; QRZ/eQSL have no incremental "check", so their
        // "New" rows read "—". On the Worked folder there is no confirmation, so the frame is kept but
        // made invisible (Hidden, not Collapsed) - it still reserves its space so the zone lists below it
        // line up with the other folders.
        private void PopulateConfirmedSummary()
        {
            if (ConfirmSummaryPanel == null) return;
            var s = Properties.Settings.Default;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var muted = (Brush)ThemeManager.Brush("MutedTextBrush");

            // The current source's full-download button; only its own button shows in the frame.
            if (BTN_GetAllConfirmations != null) BTN_GetAllConfirmations.Visibility = _source == ConfSource.Lotw    ? Visibility.Visible : Visibility.Collapsed;
            if (BTN_CheckQrz != null)           BTN_CheckQrz.Visibility           = _source == ConfSource.Qrz     ? Visibility.Visible : Visibility.Collapsed;
            if (BTN_CheckEqsl != null)          BTN_CheckEqsl.Visibility          = _source == ConfSource.Eqsl    ? Visibility.Visible : Visibility.Collapsed;
            if (BTN_CheckClublog != null)       BTN_CheckClublog.Visibility       = _source == ConfSource.Clublog ? Visibility.Visible : Visibility.Collapsed;

            // Worked and Paper QSL have no downloaded summary (Paper is manual). Keep the frame's SPACE
            // (Hidden) so the zone lists still line up with the download folders.
            if (_source == ConfSource.Worked || _source == ConfSource.Paper)
            {
                ConfirmSummaryPanel.Visibility = Visibility.Hidden;
                return;
            }
            ConfirmSummaryPanel.Visibility = Visibility.Visible;

            var dal = DataAccess.GetInstance();

            if (_source == ConfSource.Lotw)
            {
                TB_SumConfirmedLabel.Text = "Confirmed at LoTW";
                // Never show a bogus total: it can't be below the confirmed-country count. If it is (stale
                // value from earlier broken downloads), show "—" until a full check recomputes it.
                bool totalKnown = s.LotwConfirmedQsoCount > 0 && s.LotwConfirmedQsoCount >= _confirmedEntities.Count;
                TB_SumTotalQsls.Text = totalKnown ? s.LotwConfirmedQsoCount.ToString("N0", inv) : "—";

                int matched = 0;
                try { if (dal != null) matched = dal.GetLotwConfirmedCount(dal.ActiveLogId); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                TB_SumMatchedInLog.Text = matched.ToString("N0", inv);

                bool fullDownload = string.IsNullOrWhiteSpace(s.LotwLastCheckSince);
                TB_SumNewQsls.Text = fullDownload ? "—" : s.LotwLastNewQsls.ToString(inv);
                TB_SumSince.Text = fullDownload ? "   (full download)" : $"   (since {s.LotwLastCheckSince})";
                bool hasNew = !fullDownload && s.LotwLastNewQsls > 0 && !string.IsNullOrWhiteSpace(s.LotwLastNewJson);
                StyleSummaryLink(LNK_NewQsls, hasNew, muted);

                TB_SumNewCountries.Text = fullDownload ? "—" : s.LotwLastNewCountries.ToString(inv);
                bool hasNewCountry = !fullDownload && s.LotwLastNewCountries > 0 && !string.IsNullOrWhiteSpace(s.LotwLastNewJson);
                StyleSummaryLink(LNK_NewCountries, hasNewCountry, muted);
            }
            else
            {
                // QRZ / eQSL / Club Log: full-download only.
                //   Row 0 "Confirmed on <service>" = how many the SERVICE reported on the last download
                //     (stored count), independent of the log - the same meaning LoTW's row has. This is
                //     what tells you "the service says 48", even when none are in your log.
                //   Row 1 "Matched in this log"    = how many of those actually landed on a QSO in the log
                //     open now (read live from the database).
                // The two "New" rows keep the frame the same height, showing "—" (no incremental check).
                int total = 0, matched = 0;
                try
                {
                    if (_source == ConfSource.Qrz)          total = s.QrzConfirmedQsoCount;
                    else if (_source == ConfSource.Clublog) total = s.ClublogConfirmedQsoCount;
                    else                                    total = s.EqslConfirmedQsoCount;

                    if (dal != null)
                    {
                        if (_source == ConfSource.Qrz)          matched = dal.GetQrzConfirmedCount(dal.ActiveLogId);
                        else if (_source == ConfSource.Clublog) matched = dal.GetClublogConfirmedCount(dal.ActiveLogId);
                        else                                    matched = dal.GetEqslConfirmedCount(dal.ActiveLogId);
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                TB_SumConfirmedLabel.Text = $"Confirmed on {SourceName}";
                TB_SumTotalQsls.Text = total.ToString("N0", inv);
                TB_SumMatchedInLog.Text = matched.ToString("N0", inv);
                TB_SumNewQsls.Text = "—";
                TB_SumSince.Text = "   (full download)";
                TB_SumNewCountries.Text = "—";
                StyleSummaryLink(LNK_NewQsls, false, muted);
                StyleSummaryLink(LNK_NewCountries, false, muted);
            }
        }

        private static void StyleSummaryLink(System.Windows.Documents.Hyperlink link, bool active, Brush muted)
        {
            link.IsEnabled = active;
            if (active)
            {
                link.Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));   // link blue
                link.TextDecorations = TextDecorations.Underline;
                link.Cursor = Cursors.Hand;
            }
            else
            {
                link.Foreground = muted;
                link.TextDecorations = null;
                link.Cursor = Cursors.Arrow;
            }
        }

        // ONLY the confirmations that belong to the log now open.
        //
        // Every service reports for the whole ACCOUNT, not for one callsign: LoTW's report carries no
        // callsign in the request at all, so an operator with certificates for 4Z5SL, 4X2XMAS and
        // 4Z73SL gets all three back in one file. Handing the lot to the matcher meant thousands of
        // confirmations for OTHER stations were tried against this log, failed, and were then counted
        // and reported as "unmatched" - which reads as something being wrong when nothing is.
        //
        // Compared by IDENTITY, so 4Z5SL/6 counts as 4Z5SL. A confirmation that names no station at all
        // is KEPT: it cannot be attributed either way, and dropping it would silently lose whatever a
        // service that does not report the station callsign sends us.
        private List<DataAccess.LotwConfirmation> ForThisLog(
            IEnumerable<DataAccess.LotwConfirmation> all, out int otherStations)
        {
            otherStations = 0;
            var kept = new List<DataAccess.LotwConfirmation>();
            if (all == null) return kept;

            string logCall = string.Empty;
            try
            {
                var dal = DataAccess.GetInstance();
                if (dal != null) dal.GetLogIdentity(dal.ActiveLogId, out logCall, out _);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // No identity set on this log: nothing to scope by, so behave exactly as before.
            if (string.IsNullOrWhiteSpace(logCall)) return all.ToList();

            foreach (var c in all)
            {
                string station = c?.StationCallsign;
                if (string.IsNullOrWhiteSpace(station) || CallsignIdentity.Same(station, logCall)) kept.Add(c);
                else otherStations++;
            }
            return kept;
        }

        // The confirmations that actually matched a QSO in the log = everything downloaded MINUS the
        // unmatched list the marker returns. Used to build each source's confirmed-country cache from
        // real matches, so the "Confirmed (X)" tile never counts entities the log has no confirmed QSO
        // for (matched by reference: the unmatched list holds the very same objects).
        private static List<DataAccess.LotwConfirmation> MatchedOnly(
            IEnumerable<DataAccess.LotwConfirmation> all, List<DataAccess.LotwConfirmation> unmatched)
        {
            if (all == null) return new List<DataAccess.LotwConfirmation>();
            if (unmatched == null || unmatched.Count == 0) return all.ToList();
            var skip = new HashSet<DataAccess.LotwConfirmation>(unmatched);
            return all.Where(c => !skip.Contains(c)).ToList();
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
            ShowNewConfirmationsWindow(list, $"New LoTW confirmations ({list.Count})");
        }

        // Click the "New countries" count -> show only the QSOs that gave a new country (same table).
        private void NewCountries_Click(object sender, RoutedEventArgs e)
        {
            List<LotwNewQso> list = null;
            try
            {
                var json = Properties.Settings.Default.LotwLastNewJson;
                if (!string.IsNullOrWhiteSpace(json))
                    list = JsonConvert.DeserializeObject<List<LotwNewQso>>(json);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            var newCountryQsos = list?.Where(q => q.IsNewCountry).ToList() ?? new List<LotwNewQso>();
            if (newCountryQsos.Count == 0)
            {
                HolyMessageBox.Show("There are no new-country QSOs to show.", "LoTW", HolyMsgType.Info, this);
                return;
            }
            ShowNewConfirmationsWindow(newCountryQsos, $"New countries ({newCountryQsos.Count})");
        }

        // The new confirmations as a regular table: one row per QSO, every ADIF field a column, in the
        // order LoTW sent them (matching a raw ADIF viewer). Built in code; the app's window-chrome hook
        // themes the plain Window automatically.
        private void ShowNewConfirmationsWindow(List<LotwNewQso> list, string title)
        {
            // Ordered union of every ADIF field across the records (first-seen == ADIF order).
            var columns = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var q in list)
                if (q.Fields != null)
                    foreach (var f in q.Fields)
                        if (seen.Add(f.Field)) columns.Add(f.Field);

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserReorderColumns = true,
                HeadersVisibility = DataGridHeadersVisibility.All,   // row-number headers too, like a "Line" column
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                SelectionMode = DataGridSelectionMode.Single,
                FontSize = 13
            };
            grid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();
            ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
            grid.LoadingRow += (s, ev) => ev.Row.Header = (ev.Row.GetIndex() + 1).ToString();

            if (columns.Count > 0)
            {
                // One dictionary per row; every column pre-filled so indexer bindings never KeyNotFound.
                var rows = new List<Dictionary<string, string>>();
                foreach (var q in list)
                {
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (q.Fields != null)
                        foreach (var f in q.Fields) d[f.Field] = f.Value;
                    foreach (var c in columns) if (!d.ContainsKey(c)) d[c] = string.Empty;
                    rows.Add(d);
                }
                foreach (var c in columns)
                    grid.Columns.Add(new DataGridTextColumn
                    {
                        Header = c,
                        Binding = new System.Windows.Data.Binding("[" + c + "]"),
                        Width = DataGridLength.Auto
                    });
                grid.ItemsSource = rows;
            }
            else
            {
                // Older cached list (captured before full-field support): fall back to the summary columns.
                grid.Columns.Add(new DataGridTextColumn { Header = "Callsign", Binding = new System.Windows.Data.Binding("Call"), Width = 130 });
                grid.Columns.Add(new DataGridTextColumn { Header = "Country", Binding = new System.Windows.Data.Binding("Country"), Width = 180 });
                grid.Columns.Add(new DataGridTextColumn { Header = "Date", Binding = new System.Windows.Data.Binding("DateStr"), Width = 90 });
                grid.Columns.Add(new DataGridTextColumn { Header = "UTC", Binding = new System.Windows.Data.Binding("TimeStr"), Width = 60 });
                grid.Columns.Add(new DataGridTextColumn { Header = "Band", Binding = new System.Windows.Data.Binding("Band"), Width = 70 });
                grid.Columns.Add(new DataGridTextColumn { Header = "Mode", Binding = new System.Windows.Data.Binding("Mode"), Width = 70 });
                grid.ItemsSource = list;
            }

            var win = new Window
            {
                Title = title,
                Owner = this,
                Width = 920,
                Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                Content = new Border { Padding = new Thickness(10), Child = grid }
            };
            win.ShowDialog();
        }

        // Set by "Get All Confirmations" so the next check asks LoTW for EVERYTHING instead of only what
        // has arrived since last time. One-shot: cleared as soon as the check reads it.
        private bool _forceFullDownload;

        // Writes the confirmations that matched no QSO, each followed by what the log holds for that
        // same callsign. Desktop file, same place as the other diagnostics.
        private static void WriteUnmatchedReport(int total, int matched, List<DataAccess.LotwConfirmation> unmatched,
                                                 int otherStations = 0)
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "lotw_unmatched_confirmations.txt");

            var dal = DataAccess.GetInstance();
            var text = new System.Text.StringBuilder();
            text.AppendLine($"LoTW confirmations that matched no QSO — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine(new string('=', 78));
            if (otherStations > 0)
            {
                text.AppendLine($"For your OTHER callsigns, set aside : {otherStations:N0}");
                text.AppendLine("  (LoTW reports the whole account; those belong in their own logs)");
            }
            text.AppendLine($"Confirmations for this log       : {total:N0}");
            text.AppendLine($"Matched to a QSO                 : {matched:N0}");
            text.AppendLine($"NOT matched                      : {(unmatched?.Count ?? 0):N0}");
            text.AppendLine();
            text.AppendLine("For each one, 'LoTW sent' is the confirmation; 'log has' is every QSO in the");
            text.AppendLine("database with that callsign. Compare band / mode / date / station callsign.");
            text.AppendLine();

            // The "log has" lookup is one query per entry, so only the first block gets it. A few hundred
            // examples are more than enough to see the pattern, and running ~2,400 extra queries on the
            // UI thread would add a long freeze for no extra insight.
            const int detailed = 200;

            if (unmatched != null)
            {
                int i = 0;
                foreach (var c in unmatched)
                {
                    text.AppendLine($"LoTW sent : {c.Call,-12} {c.Band,-6} {c.Mode,-6} {c.QsoDate,-10} station={c.StationCallsign}");
                    if (i < detailed)
                    {
                        var inLog = dal?.DescribeQsosForCallsign(c.Call) ?? new List<string>();
                        if (inLog.Count == 0)
                            text.AppendLine("  log has : (no QSO with this callsign at all)");
                        else
                            foreach (string line in inLog) text.AppendLine("  log has : " + line);
                        text.AppendLine();
                    }
                    if (i == detailed)
                        text.AppendLine($"  …(the rest are listed without the log comparison)\n");
                    i++;
                }
            }

            System.IO.File.WriteAllText(path, text.ToString(), System.Text.Encoding.UTF8);
        }

        // Downloads the complete confirmation history and marks every confirmed QSO.
        //
        // The ordinary check is incremental - it asks for confirmations since the last one - so it can
        // never fill in the years already confirmed before this feature existed. This runs the very same
        // code with the "since" date reset to the beginning, so there is one download path, not two.
        private void BTN_GetAllConfirmations_Click(object sender, RoutedEventArgs e)
        {
            int already = 0;
            try { already = DataAccess.GetInstance()?.GetLotwConfirmedCount() ?? 0; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (!HolyMessageBox.ShowConfirm(
                    "Download your COMPLETE LoTW confirmation history and mark every confirmed QSO?\n\n" +
                    $"Marked so far: {already:N0} QSO(s).\n\n" +
                    "This takes longer than the normal check - it asks for every confirmation you have " +
                    "ever received, not just the new ones. It is only needed once; after that the normal " +
                    "Check LoTW Updates keeps things up to date.",
                    "Get All LoTW Confirmations", HolyMsgType.Info, this))
                return;

            _forceFullDownload = true;
            BTN_CheckLotw_Click(sender, e);
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
            // one time); later runs pass the DATE of the last QSL received (LotwLastQsl) as qso_qslsince.
            // IMPORTANT: LoTW's qso_qslsince honors only the DATE, not the time -- so it re-returns every
            // QSL received on that same day. We therefore query FROM that date (inclusive) and de-dupe
            // same-day repeats against LotwSeenKeysJson below, instead of trusting a timestamp+1s (which
            // a date-only filter silently ignores, causing the same QSLs to be re-counted every check).
            // Require the de-dupe key set too: its absence means either a first-ever run or an upgrade
            // from the old timestamp scheme (whose total may be inflated by same-day re-counts). Either
            // way, do one full re-download to reset the total cleanly and seed the de-dupe set; every
            // check after that is incremental.
            bool incremental = !_forceFullDownload
                               && _confirmedEntities.Count > 0
                               && !string.IsNullOrWhiteSpace(s.LotwLastQsl)
                               && !string.IsNullOrWhiteSpace(s.LotwSeenKeysJson);
            _forceFullDownload = false;   // one-shot: only the click that set it gets the full run
            string sinceQuery = incremental ? MarkerDate(s.LotwLastQsl) : "1970-01-01";
            string sinceDisplay = incremental ? PrettySince(s.LotwLastQsl) : string.Empty;

            BTN_CheckLotw.IsEnabled = false;
            LB_Source.IsEnabled = false;   // no folder switching mid-download/mark (it would block the UI)
            _checkCts = new System.Threading.CancellationTokenSource();
            var ct = _checkCts.Token;
            BTN_StopCheck.IsEnabled = true;
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
                // qso_mydetail=yes is REQUIRED, not optional: it makes LoTW include STATION_CALLSIGN on
                // every record - the callsign WE logged the QSO under. Without it that field is empty
                // (verified: 0 of 1926 records carry it plain, 1926 of 1926 with the flag), and the
                // marker below then had nothing to scope by, so a confirmation for 4Z5SL could match a
                // completely different operator's QSO in another log that merely shared the same
                // call+band+mode+date. That is how an imported friend's log got 24 QSOs wrongly ticked.
                // qso_qsldetail=yes adds the WORKED station's entity to each record: <DXCC> (the ARRL
                // entity code), <COUNTRY>, and APP_LoTW_DXCC_ENTITY_STATUS (Current / Deleted). This is
                // the authoritative, DATE-CORRECT entity - a 1985 East-Germany QSO comes back as East
                // Germany, not modern Germany - which our own cty.dat resolver (current-only) cannot
                // give. It is what lets the confirmations be split into active vs deleted entities.
                string url = "https://lotw.arrl.org/lotwuser/lotwreport.adi"
                           + "?login=" + Uri.EscapeDataString(user)
                           + "&password=" + Uri.EscapeDataString(pass)
                           + "&qso_query=1&qso_qsl=yes&qso_mydetail=yes&qso_qsldetail=yes&qso_qslsince=" + Uri.EscapeDataString(sinceQuery);

                string adif;
                // Decompress gzip/deflate — otherwise a compressed response reads back as binary garbage.
                using (var handler = new System.Net.Http.HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
                })
                using (var http = new System.Net.Http.HttpClient(handler))
                {
                    http.Timeout = TimeSpan.FromSeconds(300);   // large accounts can take a while server-side

                    // Stream the reply rather than take it whole, and count <eor> records as they arrive,
                    // so the overlay shows a real "Downloaded N confirmations…" climbing instead of a
                    // spinner. ResponseHeadersRead hands us the body stream before it has all arrived; the
                    // AutomaticDecompression above means the stream we read is already un-gzipped.
                    var sb = new System.Text.StringBuilder();
                    int eor = 0;
                    using (var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct))
                    using (var stream = await resp.Content.ReadAsStreamAsync())
                    using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
                    {
                        char[] buf = new char[16384];
                        string carry = string.Empty;   // last 4 chars, to catch an <eor> split across reads
                        int n;
                        while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            sb.Append(buf, 0, n);

                            string hay = carry + new string(buf, 0, n);
                            int idx = 0;
                            while ((idx = hay.IndexOf("<eor>", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                            {
                                eor++;
                                idx += 5;
                            }
                            carry = hay.Length >= 4 ? hay.Substring(hay.Length - 4) : hay;

                            TB_LotwLoadingText.Text = $"Downloaded {eor:N0} confirmations from LoTW…";
                        }
                    }
                    adif = sb.ToString();
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

                // Everything from here - splitting the reply into records, resolving each callsign, and
                // marking the log - is CPU/DB work that used to run on the UI thread, freezing the window
                // for over a minute on a full download (the debugger raised ContextSwitchDeadlock). It now
                // runs on a background thread, reporting a running count so the operator sees a number
                // climb instead of a stalled spinner. The DXCC resolver is read-only after load, so
                // concurrent lookups are safe.
                int eohIdx = adif.IndexOf("<eoh>", StringComparison.OrdinalIgnoreCase);
                string recordsBody = eohIdx >= 0 ? adif.Substring(eohIdx + 5) : adif;

                string boundaryDate = incremental ? MarkerDate(s.LotwLastQsl) : string.Empty;
                var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (incremental && !string.IsNullOrWhiteSpace(s.LotwSeenKeysJson))
                {
                    try
                    {
                        foreach (var k in JsonConvert.DeserializeObject<List<string>>(s.LotwSeenKeysJson) ?? new List<string>())
                            seenKeys.Add(k);
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }

                // Snapshot the confirmed-country set so the "is this a NEW country?" test inside the loop
                // reads a private copy off the UI thread rather than the live field.
                var confirmedSnapshot = new HashSet<string>(_confirmedEntities, StringComparer.OrdinalIgnoreCase);

                // Report progress back to the loading overlay, with a PHASE label so the wording tracks
                // what the program is actually doing - reading the reply, then matching it to the log -
                // instead of one misleading line that stays put through the whole thing. Progress<T>
                // captures the UI context, so the text update lands on the UI thread automatically.
                var progress = new Progress<(string label, int done, int total)>(p =>
                {
                    TB_LotwLoadingText.Text = p.total > 0
                        ? $"{p.label}…  {p.done:N0} of {p.total:N0}"
                        : $"{p.label}…  {p.done:N0}";
                    TB_LotwLoadingSub.Text = p.label == "Matching to your log"
                        ? "Marking each confirmed QSO in your logs."
                        : "Reading the confirmations LoTW sent back.";
                });

                LotwRunResult result = await Task.Run(() =>
                    ProcessLotwConfirmations(recordsBody, incremental, boundaryDate, seenKeys, confirmedSnapshot, progress, ct));

                // Locals the UI-thread tail below still expects.
                int qslCount = result.QslCount;
                var resolvedNames = result.ResolvedNames;
                var newList = result.NewList;
                int newCount = result.NewCount;
                string maxRxDate = result.MaxRxDate;
                var newSeenKeys = result.NewSeenKeys;
                int markedConfirmed = result.MarkedConfirmed;

                // The marking went into the database; the log grid is showing QSO objects read when the
                // log was opened. Re-read them so the ticks appear now rather than after a restart.
                if (markedConfirmed > 0)
                {
                    try
                    {
                        (Application.Current.Windows.OfType<MainWindow>().FirstOrDefault())?.ReloadActiveLogQsos();
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }

                // How many NEW countries this check added = growth of the entity set.
                int countriesBefore = _confirmedEntities.Count;

                // Full run replaces the set; incremental run adds the new confirmations to the cached set.
                if (incremental)
                    _confirmedEntities.UnionWith(resolvedNames);
                else
                    _confirmedEntities = resolvedNames;

                int newCountries = incremental
                    ? Math.Max(0, _confirmedEntities.Count - countriesBefore)
                    : _confirmedEntities.Count;   // a full (re)build treats the whole set as "found"

                // Persist. The total adds only genuinely-new QSLs on an incremental run (no same-day
                // re-count), or the whole set on a full run. The marker is a DATE (matching qso_qslsince),
                // plus the de-dupe key set for that date.
                s.LotwConfirmedEntities = string.Join("|", _confirmedEntities);
                s.LotwConfirmedQsoCount = incremental ? s.LotwConfirmedQsoCount + newCount : qslCount;
                s.LotwLastNewQsls = incremental ? newCount : qslCount;
                s.LotwLastNewCountries = newCountries;
                s.LotwLastCheckSince = sinceDisplay;   // empty = a full download
                // The list of new confirmations, for the viewer. Cleared on a full download (no delta).
                s.LotwLastNewJson = newList != null ? JsonConvert.SerializeObject(newList) : string.Empty;
                if (!string.IsNullOrWhiteSpace(maxRxDate)) s.LotwLastQsl = maxRxDate;   // stored as a date
                s.LotwSeenKeysJson = JsonConvert.SerializeObject(newSeenKeys);

                // Confirmed DELETED entities, by DXCC code. Full run replaces the set; an incremental
                // run adds to it (a deleted entity newly confirmed since last check). Stored as codes so
                // re-confirming the same entity never inflates the count.
                var deletedCodes = new HashSet<int>(result.ConfirmedDeletedCodes);
                if (incremental && !string.IsNullOrWhiteSpace(s.LotwConfirmedDeletedCodes))
                    foreach (var part in s.LotwConfirmedDeletedCodes.Split(','))
                        if (int.TryParse(part.Trim(), out int old)) deletedCodes.Add(old);
                s.LotwConfirmedDeletedCodes = string.Join(",", deletedCodes);
                s.Save();

                // Re-read the log so QSO confirmation flags are live (the zone lists read them), then
                // repaint the folder - RefreshForSource rebuilds the Missing lists, sets the tiles/status,
                // colors the rows, and fills the 3-row summary. No manual reopen needed.
                ReloadQsosAfterCheck();

                // A full download (Get All Confirmations, or a first-ever check) ends with a summary,
                // so the operator knows it finished and can SEE that every log was updated, not only the
                // one open now. Left off an incremental check, which would nag on each routine run.
                if (!incremental)
                    ShowFullDownloadSummary(qslCount, markedConfirmed);
            }
            catch (OperationCanceledException)
            {
                TB_LotwStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
                TB_LotwStatus.Text = "";
                HolyMessageBox.Show("LoTW update stopped — no changes were made.",
                    "LoTW confirmations", HolyMsgType.Info, this);
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
                LB_Source.IsEnabled = true;
                _checkCts?.Dispose();
                _checkCts = null;
            }
        }

        // The QRZ side of the confirmation feature, triggered by the Check QRZ button next to Check LoTW.
        // QRZ's confirmed set is a DIFFERENT, usually larger universe than LoTW's (QRZ confirms a contact
        // when the other operator also logged it on QRZ), so it fills its own qrz_qsl_rcvd column and its
        // own tick and is never mixed with LoTW. One button, always a full fetch: the confirmed set is
        // small and cheap to download, so there is no incremental mode to get subtly wrong.
        private async void BTN_CheckQrz_Click(object sender, RoutedEventArgs e)
        {
            string key = Properties.Settings.Default.qrz_api_key?.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                // Same discoverable flow as the LoTW button: offer to open the right Options page rather
                // than silently doing nothing.
                bool openOptions = HolyMessageBox.ShowConfirm(
                    "Your QRZ.com Logbook API key isn't set, so QRZ confirmations can't be downloaded.\n\n" +
                    "Open Options → QRZ Logbook to enter it now?",
                    "QRZ API key needed", HolyMsgType.Warning, this);
                if (openOptions)
                {
                    var opts = new OptionsWindow();
                    opts.Owner = this;
                    opts.QRZItem.IsSelected = true;
                    opts.ShowDialog();
                }
                return;
            }

            BTN_CheckQrz.IsEnabled = false;
            LB_Source.IsEnabled = false;   // no folder switching mid-download/mark (it would block the UI)
            _checkCts = new System.Threading.CancellationTokenSource();
            var ct = _checkCts.Token;
            BTN_StopCheck.IsEnabled = true;
            TB_LotwLoadingText.Text = "Downloading confirmations from QRZ…";
            TB_LotwLoadingSub.Text = "Reading your confirmed QSOs from QRZ.com.";
            ShowLotwSpinner(true);
            try
            {
                QrzLogbookService.QrzFetchResult fetch = await QrzLogbookService.FetchConfirmationsAsync(key, ct);

                if (fetch.NetworkError)
                {
                    HolyMessageBox.Show(
                        "Couldn't reach QRZ.com. Check your internet connection and try again.",
                        "QRZ confirmations", HolyMsgType.Warning, this);
                    return;
                }
                if (!fetch.Ok)
                {
                    string why = string.IsNullOrWhiteSpace(fetch.Reason) ? "the request was rejected" : fetch.Reason;
                    HolyMessageBox.Show(
                        "QRZ.com did not accept the request: " + why + ".\n\n" +
                        "Reading confirmations needs a valid QRZ Logbook API key, and some accounts need a " +
                        "QRZ subscription to read logbook data. Check Options → QRZ Logbook.",
                        "QRZ confirmations", HolyMsgType.Warning, this);
                    return;
                }

                var confirmations = fetch.Confirmations ?? new List<DataAccess.LotwConfirmation>();

                // Full authoritative rebuild: clear existing QRZ marks, then re-apply. Run off the UI
                // thread because the DB marking takes the connection lock and can run long on a big log.
                TB_LotwLoadingText.Text = $"Marking {confirmations.Count:N0} confirmed QSO(s)…";
                TB_LotwLoadingSub.Text = "Matching each QRZ confirmation to your logs.";
                TB_LotwLoadingText.Text = $"Marking QRZ confirmations…  0 of {confirmations.Count:N0}";
                var markProgress = new Progress<int>(done =>
                    TB_LotwLoadingText.Text = $"Marking QRZ confirmations…  {done:N0} of {confirmations.Count:N0}");
                List<DataAccess.LotwConfirmation> unmatched = null;
                int otherStations;
                confirmations = ForThisLog(confirmations, out otherStations);   // this log's callsign only
                int marked = await Task.Run(() =>
                    Dal.MarkQrzConfirmed(confirmations, true, ((IProgress<int>)markProgress).Report, ct, out unmatched));

                // The marks went into the database; the open grid is showing QSO objects read when the
                // log was opened, so re-read them for the QRZ ticks to appear now rather than on restart.
                if (marked > 0)
                {
                    try { (Application.Current.Windows.OfType<MainWindow>().FirstOrDefault())?.ReloadActiveLogQsos(); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }

                // Cache the QRZ confirmed-entity set and the deleted-entity codes, mirroring what the LoTW
                // download stores. This is what the Statistics window's QRZ folder reads to color the
                // worked list and show "Confirmed (QRZ): N active, M deleted" - independent of LoTW.
                // Built from the confirmations that actually MATCHED a QSO in the log (not the raw
                // download), so the country tile can never claim entities the log has no confirmed QSO for.
                var qrzMatched = MatchedOnly(confirmations, unmatched);
                var qrzNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var qrzDeleted = new HashSet<int>();
                foreach (var c in qrzMatched)
                {
                    string name = Resolve(c.Call, c.QsoDate)?.Name;
                    if (!string.IsNullOrEmpty(name) && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                        qrzNames.Add(name);
                    if (DXCCManager.DeletedEntities.IsDeleted(c.DxccCode)) qrzDeleted.Add(c.DxccCode);
                }
                var qs = Properties.Settings.Default;
                qs.QrzConfirmedEntities = string.Join("|", qrzNames);
                qs.QrzConfirmedDeletedCodes = string.Join(",", qrzDeleted);
                qs.QrzConfirmedQsoCount = confirmations.Count;   // what QRZ reported (frame "Confirmed on QRZ")
                qs.Save();

                // Re-read the log so the QSO confirmation flags (which the zone lists use) are live, then
                // repaint the current folder - no manual reopen needed.
                ReloadQsosAfterCheck();

                ShowQrzDownloadSummary(fetch.Count);
            }
            catch (OperationCanceledException)
            {
                HolyMessageBox.Show("QRZ update stopped — no changes were made.",
                    "QRZ confirmations", HolyMsgType.Info, this);
            }
            catch (Exception ex)
            {
                HolyMessageBox.Show("QRZ download failed: " + ex.Message,
                    "QRZ confirmations", HolyMsgType.Warning, this);
            }
            finally
            {
                ShowLotwSpinner(false);
                BTN_CheckQrz.IsEnabled = true;
                LB_Source.IsEnabled = true;
                _checkCts?.Dispose();
                _checkCts = null;
            }
        }

        // QRZ counterpart of ShowFullDownloadSummary: reports the download and shows, per log, that the
        // marks reached every log holding a matching QSO - not only the one open now.
        private void ShowQrzDownloadSummary(int downloaded)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"Downloaded {downloaded:N0} confirmed QSO(s) from QRZ.com.");
            text.AppendLine();

            try
            {
                var perLog = Dal?.GetQrzConfirmedCountsByLog() ?? new List<KeyValuePair<string, int>>();
                int totalMarked = perLog.Sum(p => p.Value);

                if (perLog.Count == 0)
                {
                    text.AppendLine("No QSO in any of your logs matched a QRZ confirmation yet.");
                }
                else if (perLog.Count == 1)
                {
                    text.AppendLine($"{totalMarked:N0} QSO(s) in your log are now marked confirmed on QRZ.");
                }
                else
                {
                    text.AppendLine($"{totalMarked:N0} QSO(s) are now marked QRZ-confirmed, across all your logs:");
                    text.AppendLine();
                    foreach (var p in perLog)
                        text.AppendLine($"    • {p.Key}:  {p.Value:N0}");
                    text.AppendLine();
                    text.AppendLine("A confirmation belongs to the contact, so every log holding a matching " +
                                    "QSO - under any of your station callsigns - was updated, not only the one open now.");
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            HolyMessageBox.Show(text.ToString().TrimEnd(), "QRZ confirmations updated", HolyMsgType.Info, this);
        }

        // The eQSL side of the confirmation feature. eQSL is per-callsign, so this loops over every eQSL
        // account (Options ▸ eQSL) and downloads that account's In Box (received cards). eQSL's download
        // carries no <DXCC>, so the deleted-entity split is resolved from the callsign via cty.dat and is
        // only approximate. Always a full rebuild.
        private async void BTN_CheckEqsl_Click(object sender, RoutedEventArgs e)
        {
            List<EqslAccount> accounts;
            try { accounts = Dal?.GetEqslAccounts() ?? new List<EqslAccount>(); }
            catch (Exception ex) { HolyMessageBox.Show("Couldn't read eQSL accounts: " + ex.Message, "eQSL confirmations", HolyMsgType.Warning, this); return; }

            accounts = accounts.Where(a => !string.IsNullOrWhiteSpace(a.Username) && !string.IsNullOrWhiteSpace(a.Password)).ToList();
            if (accounts.Count == 0)
            {
                bool openOptions = HolyMessageBox.ShowConfirm(
                    "No eQSL account with a user name and password is set, so eQSL confirmations can't be downloaded.\n\n" +
                    "Open Options → eQSL to add one now?",
                    "eQSL account needed", HolyMsgType.Warning, this);
                if (openOptions)
                {
                    var opts = new OptionsWindow();
                    opts.Owner = this;
                    opts.EqslItem.IsSelected = true;
                    opts.ShowDialog();
                }
                return;
            }

            BTN_CheckEqsl.IsEnabled = false;
            LB_Source.IsEnabled = false;   // no folder switching mid-download/mark (it would block the UI)
            _checkCts = new System.Threading.CancellationTokenSource();
            var ct = _checkCts.Token;
            BTN_StopCheck.IsEnabled = true;
            ShowLotwSpinner(true);
            try
            {
                // Download each account's In Box; stamp the account callsign on every confirmation so the
                // match is scoped to that operator.
                var all = new List<DataAccess.LotwConfirmation>();
                var failed = new List<string>();
                int idx = 0;
                foreach (var acct in accounts)
                {
                    ct.ThrowIfCancellationRequested();
                    idx++;
                    TB_LotwLoadingText.Text = $"Downloading eQSL In Box… ({idx} of {accounts.Count})";
                    TB_LotwLoadingSub.Text = $"Account {acct.Callsign}";
                    var r = await EqslConfirmationService.FetchInboxAsync(acct.Username, acct.Password, acct.Callsign, ct);
                    if (r.Ok) all.AddRange(r.Confirmations);
                    else if (r.NetworkError) { failed.Add($"{acct.Callsign}: no connection"); }
                    else failed.Add($"{acct.Callsign}: {r.Reason}");
                }

                if (all.Count == 0)
                {
                    string why = failed.Count > 0 ? "\n\n" + string.Join("\n", failed) : "";
                    HolyMessageBox.Show("No eQSL confirmations were downloaded." + why,
                        "eQSL confirmations", HolyMsgType.Warning, this);
                    return;
                }

                TB_LotwLoadingText.Text = $"Marking eQSL confirmations…  0 of {all.Count:N0}";
                TB_LotwLoadingSub.Text = "Matching each eQSL confirmation to your logs.";
                // Live counter: MarkConfirmedCore reports its running count; Progress<int> marshals each
                // update back to the UI thread so the number climbs instead of just a spinner turning.
                var markProgress = new Progress<int>(done =>
                    TB_LotwLoadingText.Text = $"Marking eQSL confirmations…  {done:N0} of {all.Count:N0}");
                List<DataAccess.LotwConfirmation> unmatched = null;
                int otherStations;
                all = ForThisLog(all, out otherStations);   // this log's callsign only
                int marked = await Task.Run(() =>
                    Dal.MarkEqslConfirmed(all, true, ((IProgress<int>)markProgress).Report, ct, out unmatched));

                if (marked > 0)
                {
                    try { (Application.Current.Windows.OfType<MainWindow>().FirstOrDefault())?.ReloadActiveLogQsos(); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }

                // Cache the eQSL confirmed-entity set (resolved from callsigns). eQSL sends no <DXCC> and
                // the cty.dat resolver exposes only the (current) entity name, not a code, so the deleted-
                // entity split isn't available for eQSL - left empty (a known caveat, not a bug). Built from
                // the confirmations that MATCHED a QSO in the log, so the tile never over-counts countries.
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in MatchedOnly(all, unmatched))
                {
                    string name = Resolve(c.Call, c.QsoDate)?.Name;
                    if (!string.IsNullOrEmpty(name) && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
                }
                var s = Properties.Settings.Default;
                s.EqslConfirmedEntities = string.Join("|", names);
                s.EqslConfirmedDeletedCodes = string.Empty;
                s.EqslConfirmedQsoCount = all.Count;   // what eQSL reported (frame "Confirmed on eQSL")
                s.Save();

                ReloadQsosAfterCheck();
                ShowEqslDownloadSummary(all.Count, failed);
            }
            catch (OperationCanceledException)
            {
                // The marking rolls its transaction back on cancel, so nothing was changed.
                HolyMessageBox.Show("eQSL update stopped — no changes were made.",
                    "eQSL confirmations", HolyMsgType.Info, this);
            }
            catch (Exception ex)
            {
                HolyMessageBox.Show("eQSL download failed: " + ex.Message,
                    "eQSL confirmations", HolyMsgType.Warning, this);
            }
            finally
            {
                ShowLotwSpinner(false);
                BTN_CheckEqsl.IsEnabled = true;
                LB_Source.IsEnabled = true;
                _checkCts?.Dispose();
                _checkCts = null;
            }
        }

        private void ShowEqslDownloadSummary(int downloaded, List<string> failed)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"Downloaded {downloaded:N0} received eQSL(s) from your In Box.");
            text.AppendLine();
            try
            {
                var perLog = Dal?.GetEqslConfirmedCountsByLog() ?? new List<KeyValuePair<string, int>>();
                int totalMarked = perLog.Sum(p => p.Value);
                if (perLog.Count == 0)
                    text.AppendLine("No QSO in any of your logs matched an eQSL confirmation yet.");
                else if (perLog.Count == 1)
                    text.AppendLine($"{totalMarked:N0} QSO(s) in your log are now marked confirmed on eQSL.");
                else
                {
                    text.AppendLine($"{totalMarked:N0} QSO(s) are now marked eQSL-confirmed, across all your logs:");
                    text.AppendLine();
                    foreach (var p in perLog)
                        text.AppendLine($"    • {p.Key}:  {p.Value:N0}");
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (failed != null && failed.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Some accounts could not be downloaded:");
                foreach (var f in failed) text.AppendLine("    • " + f);
            }
            HolyMessageBox.Show(text.ToString().TrimEnd(), "eQSL confirmations updated", HolyMsgType.Info, this);
        }

        // The Club Log side of the confirmation feature. Club Log is a single account (e-mail + password),
        // but getadif.php is per-callsign, so this loops over every station callsign the operator used and
        // downloads that call's whole-log export, keeping the QSOs Club Log reports confirmed
        // (QSL_RCVD = Y/V). Unlike eQSL, Club Log DOES send <DXCC>, so the deleted-entity split is exact.
        // Always a full rebuild.
        private async void BTN_CheckClublog_Click(object sender, RoutedEventArgs e)
        {
            var s0 = Properties.Settings.Default;
            string email = s0.ClublogEmail?.Trim();
            string password = s0.ClublogPassword;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                bool openOptions = HolyMessageBox.ShowConfirm(
                    "Your Club Log e-mail and password aren't set, so Club Log confirmations can't be downloaded.\n\n" +
                    "Open Options → Club Log to enter them now?",
                    "Club Log account needed", HolyMsgType.Warning, this);
                if (openOptions)
                {
                    var opts = new OptionsWindow();
                    opts.Owner = this;
                    opts.ClublogItem.IsSelected = true;
                    opts.ShowDialog();
                }
                return;
            }

            // A Club Log account belongs to ONE operator, so we only ever ask about THIS operator's own
            // callsign - the personal callsign in Settings. We must NOT loop over every my_callsign in the
            // database: a shared/club machine holds other operators' logs too, and asking Club Log about a
            // friend's call under your login is both wrong and pointless (Club Log rejects it).
            string myCall = s0.my_callsign?.Trim();
            if (string.IsNullOrWhiteSpace(myCall))
            {
                HolyMessageBox.Show("Your own callsign isn't set (Options → General), so there is nothing to download from Club Log.",
                    "Club Log confirmations", HolyMsgType.Warning, this);
                return;
            }
            var calls = new List<string> { myCall };

            BTN_CheckClublog.IsEnabled = false;
            LB_Source.IsEnabled = false;   // no folder switching mid-download/mark (it would block the UI)
            _checkCts = new System.Threading.CancellationTokenSource();
            var ct = _checkCts.Token;
            BTN_StopCheck.IsEnabled = true;
            ShowLotwSpinner(true);
            try
            {
                var all = new List<DataAccess.LotwConfirmation>();
                var failed = new List<string>();
                int idx = 0;
                foreach (var call in calls)
                {
                    ct.ThrowIfCancellationRequested();
                    idx++;
                    TB_LotwLoadingText.Text = $"Downloading Club Log export… ({idx} of {calls.Count})";
                    TB_LotwLoadingSub.Text = $"Callsign {call}";
                    var r = await ClublogService.FetchLogAsync(email, password, call, ct);
                    if (r.Ok) all.AddRange(r.Confirmations);
                    else if (r.NetworkError) failed.Add($"{call}: no connection");
                    else failed.Add($"{call}: {r.Reason}");
                }

                if (all.Count == 0)
                {
                    string why = failed.Count > 0 ? "\n\n" + string.Join("\n", failed) : "";
                    HolyMessageBox.Show("No Club Log confirmations were downloaded." + why,
                        "Club Log confirmations", HolyMsgType.Warning, this);
                    return;
                }

                TB_LotwLoadingText.Text = $"Marking Club Log confirmations…  0 of {all.Count:N0}";
                TB_LotwLoadingSub.Text = "Matching each Club Log confirmation to your logs.";
                var markProgress = new Progress<int>(done =>
                    TB_LotwLoadingText.Text = $"Marking Club Log confirmations…  {done:N0} of {all.Count:N0}");
                List<DataAccess.LotwConfirmation> unmatched = null;
                int otherStations;
                all = ForThisLog(all, out otherStations);   // this log's callsign only
                int marked = await Task.Run(() =>
                    Dal.MarkClublogConfirmed(all, true, ((IProgress<int>)markProgress).Report, ct, out unmatched));

                if (marked > 0)
                {
                    try { (Application.Current.Windows.OfType<MainWindow>().FirstOrDefault())?.ReloadActiveLogQsos(); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }

                // Cache the Club Log confirmed-entity set and deleted-entity codes. Club Log sends <DXCC>,
                // so the active/deleted split is exact (same as QRZ / LoTW, not the eQSL approximation).
                // Built from the confirmations that MATCHED a QSO in the log, so the "Confirmed (Club Log)"
                // country tile agrees with the marked-QSO count instead of counting the whole download.
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var deleted = new HashSet<int>();
                foreach (var c in MatchedOnly(all, unmatched))
                {
                    string name = Resolve(c.Call, c.QsoDate)?.Name;
                    if (!string.IsNullOrEmpty(name) && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
                    if (DXCCManager.DeletedEntities.IsDeleted(c.DxccCode)) deleted.Add(c.DxccCode);
                }
                s0.ClublogConfirmedEntities = string.Join("|", names);
                s0.ClublogConfirmedDeletedCodes = string.Join(",", deleted);
                s0.ClublogConfirmedQsoCount = all.Count;   // what Club Log reported (frame "Confirmed on Club Log")
                s0.Save();

                ReloadQsosAfterCheck();
                ShowClublogDownloadSummary(all.Count, failed);
            }
            catch (OperationCanceledException)
            {
                HolyMessageBox.Show("Club Log update stopped — no changes were made.",
                    "Club Log confirmations", HolyMsgType.Info, this);
            }
            catch (Exception ex)
            {
                HolyMessageBox.Show("Club Log download failed: " + ex.Message,
                    "Club Log confirmations", HolyMsgType.Warning, this);
            }
            finally
            {
                ShowLotwSpinner(false);
                BTN_CheckClublog.IsEnabled = true;
                LB_Source.IsEnabled = true;
                _checkCts?.Dispose();
                _checkCts = null;
            }
        }

        private void ShowClublogDownloadSummary(int downloaded, List<string> failed)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"Downloaded {downloaded:N0} confirmed QSO(s) from Club Log.");
            text.AppendLine();
            try
            {
                var perLog = Dal?.GetClublogConfirmedCountsByLog() ?? new List<KeyValuePair<string, int>>();
                int totalMarked = perLog.Sum(p => p.Value);
                if (perLog.Count == 0)
                    text.AppendLine("No QSO in any of your logs matched a Club Log confirmation yet.");
                else if (perLog.Count == 1)
                    text.AppendLine($"{totalMarked:N0} QSO(s) in your log are now marked confirmed on Club Log.");
                else
                {
                    text.AppendLine($"{totalMarked:N0} QSO(s) are now marked Club Log-confirmed, across all your logs:");
                    text.AppendLine();
                    foreach (var p in perLog)
                        text.AppendLine($"    • {p.Key}:  {p.Value:N0}");
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (failed != null && failed.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Some callsigns could not be downloaded:");
                foreach (var f in failed) text.AppendLine("    • " + f);
            }
            HolyMessageBox.Show(text.ToString().TrimEnd(), "Club Log confirmations updated", HolyMsgType.Info, this);
        }

        // Reports the outcome of a full confirmation download, and shows plainly that the marks reached
        // EVERY log - the per-log breakdown makes the cross-log effect visible instead of leaving the
        // operator to wonder whether their other logs were touched.
        private void ShowFullDownloadSummary(int downloaded, int matched)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"Downloaded {downloaded:N0} confirmation(s) from LoTW.");
            text.AppendLine();

            try
            {
                var perLog = DataAccess.GetInstance()?.GetLotwConfirmedCountsByLog()
                             ?? new List<KeyValuePair<string, int>>();
                int totalMarked = perLog.Sum(p => p.Value);

                if (perLog.Count == 0)
                {
                    text.AppendLine("No QSO in any of your logs matched a confirmation yet.");
                }
                else if (perLog.Count == 1)
                {
                    text.AppendLine($"{totalMarked:N0} QSO(s) in your log are now marked confirmed.");
                }
                else
                {
                    text.AppendLine($"{totalMarked:N0} QSO(s) are now marked confirmed, across all your logs:");
                    text.AppendLine();
                    foreach (var p in perLog)
                        text.AppendLine($"    • {p.Key}:  {p.Value:N0}");
                    text.AppendLine();
                    text.AppendLine("A confirmation belongs to the contact, so every log that holds a matching " +
                                    "QSO - under any of your station callsigns - was updated, not only the one open now.");
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            HolyMessageBox.Show(text.ToString().TrimEnd(), "LoTW confirmations updated", HolyMsgType.Info, this);
        }

        // Everything ProcessLotwConfirmations produces that the UI-thread tail of the check needs.
        private class LotwRunResult
        {
            public int QslCount;
            public HashSet<string> ResolvedNames;
            public List<LotwNewQso> NewList;
            public int NewCount;
            public string MaxRxDate;
            public List<string> NewSeenKeys;
            public int MarkedConfirmed;

            // How many of the downloaded confirmations belong to a DIFFERENT station callsign of yours
            // and were therefore set aside rather than tried against this log. Reported so the operator
            // can see WHY the download was bigger than the number matched.
            public int OtherStationConfirmations;

            // Distinct DXCC entity CODES confirmed, split by whether the entity is deleted. Taken from
            // LoTW's own <DXCC> per record (date-correct), so a deleted entity is counted as deleted
            // even though our cty.dat resolver would map its callsign to the modern parent.
            public HashSet<int> ConfirmedActiveCodes = new HashSet<int>();
            public HashSet<int> ConfirmedDeletedCodes = new HashSet<int>();
        }

        // The heavy half of the LoTW check, run on a background thread (see the caller). Splits the
        // reply into records, resolves each callsign, works out which confirmations are new, marks the
        // matching QSOs in the database, and writes the unmatched-diagnostic file - reporting a running
        // record count through `progress` so the overlay can show a climbing number.
        //
        // Touches no UI. It reads the DXCC resolver (safe: read-only after load) and a caller-supplied
        // SNAPSHOT of the confirmed-country set, never the live field.
        private LotwRunResult ProcessLotwConfirmations(
            string recordsBody, bool incremental, string boundaryDate,
            HashSet<string> seenKeys, HashSet<string> confirmedSnapshot,
            IProgress<(string label, int done, int total)> progress,
            System.Threading.CancellationToken ct)
        {
            var records = System.Text.RegularExpressions.Regex.Split(
                recordsBody, "<eor>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int totalRecords = records.Length;

            var result = new LotwRunResult
            {
                ResolvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                NewList = incremental ? new List<LotwNewQso>() : null,
                NewSeenKeys = new List<string>(),
                MaxRxDate = boundaryDate
            };

            var confirmations = new List<DataAccess.LotwConfirmation>();
            int seen = 0;

            foreach (var rec in records)
            {
                string call = ExtractAdifField(rec, "call");
                if (string.IsNullOrWhiteSpace(call)) continue;
                result.QslCount++;

                // One record does three jobs; do them together so the record is parsed once.
                string band = (ExtractAdifField(rec, "band") ?? string.Empty).Trim();
                string mode = (ExtractAdifField(rec, "mode") ?? string.Empty).Trim();
                string qsoDate = (ExtractAdifField(rec, "qso_date") ?? string.Empty).Trim();
                string rxDate = QslRcvdDate(rec);
                string key = QsoKey(rec);

                string name = Resolve(call, qsoDate)?.Name;
                if (!string.IsNullOrEmpty(name)
                    && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                    result.ResolvedNames.Add(name);

                // Classify by LoTW's own entity code (date-correct). DeletedEntities is our shipped,
                // authoritative list; LoTW also sends APP_LoTW_DXCC_ENTITY_STATUS, but keying on our
                // list keeps the answer offline and consistent between the LoTW and manual-QSL paths.
                string dxccStr = ExtractAdifField(rec, "dxcc");
                int dxccCode = 0;
                if (int.TryParse((dxccStr ?? string.Empty).Trim(), out dxccCode) && dxccCode > 0)
                {
                    if (DXCCManager.DeletedEntities.IsDeleted(dxccCode))
                        result.ConfirmedDeletedCodes.Add(dxccCode);
                    else
                        result.ConfirmedActiveCodes.Add(dxccCode);
                }

                if (string.Compare(rxDate, result.MaxRxDate, StringComparison.Ordinal) > 0)
                    result.MaxRxDate = rxDate;

                bool isNew = !incremental
                          || string.Compare(rxDate, boundaryDate, StringComparison.Ordinal) > 0
                          || (string.Equals(rxDate, boundaryDate, StringComparison.Ordinal) && !seenKeys.Contains(key));
                if (isNew)
                {
                    result.NewCount++;
                    result.NewList?.Add(new LotwNewQso
                    {
                        Call = call.Trim().ToUpperInvariant(),
                        Country = name ?? string.Empty,
                        DateStr = FormatAdifDateDMY(qsoDate),
                        TimeStr = FormatAdifTime(ExtractAdifField(rec, "time_on")),
                        Band = band.ToUpperInvariant(),
                        Mode = mode.ToUpperInvariant(),
                        IsNewCountry = !string.IsNullOrEmpty(name) && !confirmedSnapshot.Contains(name),
                        Fields = ParseAdifFields(rec)
                    });
                }

                confirmations.Add(new DataAccess.LotwConfirmation
                {
                    Call = call.Trim().ToUpperInvariant(),
                    Band = band,
                    Mode = mode,
                    QsoDate = qsoDate,
                    StationCallsign = (ExtractAdifField(rec, "station_callsign") ?? string.Empty).Trim(),
                    QslRDate = (ExtractAdifField(rec, "qslrdate") ?? string.Empty).Trim(),
                    DxccCode = dxccCode
                });

                // Report every so often, not every record: marshaling to the UI thread on all ~6,000
                // would itself become the bottleneck.
                if ((++seen % 200) == 0) progress?.Report(("Reading confirmations", seen, totalRecords));
            }
            progress?.Report(("Reading confirmations", seen, totalRecords));

            // Rebuild the boundary-day key set from this run's records on the latest QSL-received date.
            foreach (var rec in records)
            {
                if (string.IsNullOrWhiteSpace(ExtractAdifField(rec, "call"))) continue;
                if (string.Equals(QslRcvdDate(rec), result.MaxRxDate, StringComparison.Ordinal))
                    result.NewSeenKeys.Add(QsoKey(rec));
            }

            // fullReset on a full (non-incremental) download: it is authoritative, so clear every mark
            // first and rebuild - which also scrubs any bad marks a pre-station-scoping build left behind.
            // The matching is its own phase with its own counter, so the overlay keeps moving through the
            // slow database work instead of freezing on the last "Reading…" number.
            // Only what belongs to THIS log's station callsign - LoTW sends the whole account.
            int otherStations;
            confirmations = ForThisLog(confirmations, out otherStations);
            result.OtherStationConfirmations = otherStations;

            int totalConf = confirmations.Count;
            Action<int> matchProgress = n => progress?.Report(("Matching to your log", n, totalConf));
            List<DataAccess.LotwConfirmation> unmatched = null;
            try { result.MarkedConfirmed = DataAccess.GetInstance()?.MarkLotwConfirmed(confirmations, !incremental, matchProgress, ct, out unmatched) ?? 0; }
            catch (OperationCanceledException) { throw; }   // let Stop propagate (transaction rolled back)
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // Rebuild the confirmed-country / deleted-code sets from confirmations that actually MATCHED a
            // QSO in the log (the per-record loop above accumulated them from the whole download). This
            // keeps the "Confirmed (LoTW)" tile honest - it never counts an entity the log has no confirmed
            // QSO for, matching how the QRZ / eQSL / Club Log folders now build their sets.
            var lotwUnmatched = new HashSet<DataAccess.LotwConfirmation>(unmatched ?? new List<DataAccess.LotwConfirmation>());
            result.ResolvedNames.Clear();
            result.ConfirmedDeletedCodes.Clear();
            result.ConfirmedActiveCodes.Clear();
            foreach (var c in confirmations)
            {
                if (lotwUnmatched.Contains(c)) continue;
                string nm = Resolve(c.Call, c.QsoDate)?.Name;
                if (!string.IsNullOrEmpty(nm) && !string.Equals(nm, "Unknown", StringComparison.OrdinalIgnoreCase))
                    result.ResolvedNames.Add(nm);
                if (c.DxccCode > 0)
                {
                    if (DXCCManager.DeletedEntities.IsDeleted(c.DxccCode)) result.ConfirmedDeletedCodes.Add(c.DxccCode);
                    else result.ConfirmedActiveCodes.Add(c.DxccCode);
                }
            }

            try { WriteUnmatchedReport(confirmations.Count, result.MarkedConfirmed, unmatched, result.OtherStationConfirmations); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return result;
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

        // The date (yyyy-MM-dd) to send as qso_qslsince, from the stored marker (which may be a legacy
        // full timestamp "yyyy-MM-dd HH:mm:ss" or a bare date). qso_qslsince is date-only, so we send
        // the date and de-dupe same-day repeats in the caller. Falls back to a full pull if unrecognized.
        private static string MarkerDate(string marker)
        {
            marker = (marker ?? string.Empty).Trim();
            var m = System.Text.RegularExpressions.Regex.Match(marker, @"^(\d{4}-\d{2}-\d{2})");
            return m.Success ? m.Groups[1].Value : "1970-01-01";
        }

        // The QSL-received date (yyyy-MM-dd) of one record — QSLRDATE ("yyyymmdd"), falling back to the
        // date part of APP_LoTW_RXQSL. This is what qso_qslsince filters on, so it's the de-dupe boundary.
        private static string QslRcvdDate(string record)
        {
            string q = ExtractAdifField(record, "qslrdate");
            if (!string.IsNullOrWhiteSpace(q) && System.Text.RegularExpressions.Regex.IsMatch(q, @"^\d{8}$"))
                return q.Substring(0, 4) + "-" + q.Substring(4, 2) + "-" + q.Substring(6, 2);
            string rx = ExtractAdifField(record, "app_lotw_rxqsl");
            var m = System.Text.RegularExpressions.Regex.Match(rx ?? string.Empty, @"^(\d{4}-\d{2}-\d{2})");
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        // A stable identity for one QSO, so a same-day QSL isn't counted twice across checks.
        private static string QsoKey(string record)
        {
            string U(string f) => (ExtractAdifField(record, f) ?? string.Empty).Trim().ToUpperInvariant();
            return string.Join("|", U("call"), U("qso_date"), U("time_on"), U("band"), U("mode"));
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

        // Every ADIF field in one record, in the order they appear (uppercased names), for the
        // "all details" drill-down. Uses the same length-prefixed <field:len[:type]> parsing as
        // ExtractAdifField, so it's exactly what LoTW sent — nothing dropped.
        private static List<AdifFieldRow> ParseAdifFields(string record)
        {
            var rows = new List<AdifFieldRow>();
            if (string.IsNullOrEmpty(record)) return rows;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         record, @"<([A-Za-z0-9_]+):(\d+)(?::[^>]*)?>",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                int len = int.Parse(m.Groups[2].Value);
                int start = m.Index + m.Length;
                if (len <= 0 || start + len > record.Length) continue;
                rows.Add(new AdifFieldRow
                {
                    Field = m.Groups[1].Value.ToUpperInvariant(),
                    Value = record.Substring(start, len).Trim()
                });
            }
            return rows;
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
            else if (_workedSort == WorkedSort.ConfirmedDesc || _workedSort == WorkedSort.ConfirmedAsc)
            {
                // Group by confirmed state. The UNCONFIRMED group (the countries you still need) is
                // sub-sorted alphabetically by country name so it's easy to scan; the confirmed group
                // keeps its count order.
                var confirmed   = _workedList.Where(c => c.IsConfirmed).OrderByDescending(c => c.Count).ThenBy(c => c.Name);
                var unconfirmed = _workedList.Where(c => !c.IsConfirmed).OrderBy(c => c.Name);
                sorted = _workedSort == WorkedSort.ConfirmedDesc
                    ? confirmed.Concat(unconfirmed).ToList()    // confirmed first, unconfirmed (A–Z) below
                    : unconfirmed.Concat(confirmed).ToList();   // unconfirmed (A–Z) first
            }
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
            // Blank on the Worked folder (no confirmation source), so there is no "Conf." header over an
            // empty column.
            TB_SortWorkedConfirmed.Text = _source == ConfSource.Worked ? ""
                                        : _workedSort == WorkedSort.ConfirmedDesc ? "Conf. ▼"
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

        // Confirmation state, shown in the Confirmed column: green check when confirmed, bold red cross
        // when not. ✓ = check mark, ✗ = ballot X (clearer than a thin minus). ShowConfirmation is false
        // on the Worked folder (no confirmation source), where the column is blank instead of all-crosses.
        public bool ShowConfirmation { get; set; }
        public bool IsConfirmed { get; set; }
        public string ConfirmedMark => !ShowConfirmation ? "" : (IsConfirmed ? "✓" : "✗");
        public Brush ConfirmedBrush => !ShowConfirmation
            ? System.Windows.Media.Brushes.Transparent
            : (IsConfirmed
                ? System.Windows.Media.Brushes.ForestGreen
                : (Brush)new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)));   // vivid red, far more visible
        public string ConfirmedTip => !ShowConfirmation ? null : (IsConfirmed ? "Confirmed" : "Not confirmed");
    }

    // One newly-confirmed QSO, captured from the last incremental LoTW check for the "see the new
    // QSOs" viewer. The top-level fields drive the overview grid; Fields holds EVERY ADIF field from
    // the record for the drill-down. Persisted as JSON in Settings.LotwLastNewJson.
    public class LotwNewQso
    {
        public string Call { get; set; }
        public string Country { get; set; }
        public string DateStr { get; set; }
        public string TimeStr { get; set; }
        public string Band { get; set; }
        public string Mode { get; set; }
        // True when this QSO's DXCC entity wasn't confirmed before this check — i.e. it gave a new country.
        public bool IsNewCountry { get; set; }
        public List<AdifFieldRow> Fields { get; set; }
    }

    // One ADIF field (name + value) for the "all details" grid.
    public class AdifFieldRow
    {
        public string Field { get; set; }
        public string Value { get; set; }
    }
}
