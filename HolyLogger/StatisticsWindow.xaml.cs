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
        // NOT A COUNT OF ANYTHING ANY MORE. Nothing on the page is counted from this: the confirmed
        // countries, like the worked ones, are a set of entity NUMBERS (_confirmedCodes), resolved from
        // the QSOs themselves. This survives for one job only - the LoTW check writes it to log state as
        // a '|'-joined list, and how much it GREW by is what "N new countries" reports after a download.
        // Kept as names because that is what is already stored in every operator's log state; nothing
        // reads it back as a count, so the two cannot disagree about a total.
        private HashSet<string> _confirmedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // THE SAME SET, BY NUMBER - what every count on the page is actually made of now. The name set
        // above survives only for the few places that still speak in names.
        private HashSet<int> _confirmedCodes = new HashSet<int>();

        // The DELETED entities among them, by ARRL entity number. Built in the same pass as the set
        // above, from the log's own QSOs, so it needs nothing saved from a past download.
        private HashSet<int> _confirmedDeletedCodes = new HashSet<int>();

        // The confirmation source whose analysis the window is currently showing - one folder each in the
        // vertical tab strip. "Worked" is the plain log with no confirmation overlay; the rest color the
        // worked list by that service's confirmations. Only LoTW and QRZ are wired in this first step.
        // Award is not a service: it is every source the ARRL actually accepts, added together - LoTW,
        // a paper card, or a credit the ARRL has already granted. It exists because no single tab can
        // answer "how many countries do I have for DXCC": the sets genuinely differ. In the log this was
        // built against, LoTW confirms 325 including Bouvet but not Minami Torishima (paper card only),
        // and the granted credits are a different 325 - Minami yes, Bouvet never submitted. Together: 326.
        // QRZ, eQSL and Club Log are deliberately NOT in it. The ARRL does not accept them, so counting
        // them would produce a bigger number that no award will honour.
        private enum ConfSource { Worked, Lotw, Qrz, Eqsl, Clublog, Paper, Award }
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

        private enum WorkedSort { CountDesc, CountAsc, NameAsc, NameDesc, ConfirmedDesc, ConfirmedAsc, CodeAsc, CodeDesc }
        private enum MissingSort { NameAsc, NameDesc, CodeAsc, CodeDesc }
        private WorkedSort  _workedSort  = WorkedSort.CountDesc;
        private MissingSort _missingSort = MissingSort.NameAsc;

        // Raised when the user clicks a worked country; the main window opens the Search window
        // filtered by that country.
        public event Action<string> CountrySearchRequested;

        // "Show me these QSOs, properly" - a set of contacts this window has identified, handed to the
        // Log Workshop rather than shown in a little read-only table of its own. The string names the
        // slice, so the Workshop's title can say what is in it and nobody mistakes it for the whole log.
        // Raised here, acted on by the main window, which is what owns the Workshop.
        public event Action<ObservableCollection<QSO>, string> QsoSubsetRequested;

        public StatisticsWindow(ObservableCollection<QSO> qsos)
        {
            InitializeComponent();
            _allQsos = qsos;

            // A QSO edited in the log table or the Log Workshop changes the very objects these numbers
            // were counted from, and tells this window nothing. Coming back to the window is the moment
            // the operator expects to see the result, so that is where the page is recounted from
            // scratch. Switching folders while reading it still costs nothing.
            Activated += (s2, e2) => RebuildAfterPossibleEdit();
            HookQsoCollection(null, qsos);

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
                // Open on the screen HOLYLOGGER is on, not on whichever monitor Windows calls primary.
                // SystemParameters.WorkArea always answers for the primary one, so on a two-screen desk
                // this put the statistics on the opposite screen from the program that opened them -
                // which reads as the window having gone missing.
                Rect wa = ProgramWorkArea();
                Left = wa.Left + 60;
                Top  = wa.Top  + 60;

                // Keep the whole window on that screen where it can be: an opening position is ours to
                // choose, unlike a position the operator has since dragged it to.
                if (Left + Width > wa.Right)  Left = Math.Max(wa.Left, wa.Right  - Width);
                if (Top + Height > wa.Bottom) Top  = Math.Max(wa.Top,  wa.Bottom - Height);
            }

            LoadConfirmedCache();
            ComputeStats();
            _statsBuilt = true;      // from here on, coming back to the window recounts the page
            BuildSourceFolders();
            BuildLeftViewFolders();
            ApplyLeftView();

            // Match country-table scroll heights to the pivot table height whenever the pivot resizes.
            PivotOuterBorder.SizeChanged += (sender, e) =>
            {
                if (e.NewSize.Height > 0)
                {
                    _tableHeight = e.NewSize.Height;
                    // The height goes on the lists themselves now: each one carries its own ScrollViewer
                    // inside its template (that is what makes the rows virtualize), so there is no outer
                    // ScrollViewer left to size.
                    IC_WorkedCountries.Height  = _tableHeight;
                    IC_MissingCountries.Height = _tableHeight;
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
        // True once the constructor's own first build has run, so the activation that comes WITH opening
        // the window does not build the page a second time.
        private bool _statsBuilt;

        // ── A QSO LOGGED WHILE THIS WINDOW IS OPEN ────────────────────────
        //
        // Every new contact changes these numbers - one more QSO, possibly one more country, possibly a
        // zone that is no longer missing - so the page follows the log live rather than showing what was
        // true when it opened.
        //
        // Coalesced, not per QSO. The statistics share ONE UI thread with the log window the operator is
        // typing into, and in a contest a contact lands every few seconds; recounting the whole page on
        // each one would stutter the very window they are working in. A short quiet period after the last
        // change is enough to make it feel immediate without paying for every keystroke of a run.
        private System.Windows.Threading.DispatcherTimer _liveRefreshTimer;

        private void HookQsoCollection(ObservableCollection<QSO> old, ObservableCollection<QSO> fresh)
        {
            if (old != null) old.CollectionChanged -= QsoCollectionChanged;
            if (fresh != null) fresh.CollectionChanged += QsoCollectionChanged;
        }

        // Closing the window is not the end of it. The collection above belongs to the MAIN window and
        // outlives every Statistics window ever opened, so a handler left attached to it holds this whole
        // page - tables, caches, the images - in memory with nothing left to show them on. Worse than the
        // memory: the handler still RUNS. Every QSO logged afterwards would start the timer below and
        // recount a page nobody can see, on the same UI thread the operator is typing into, once for each
        // time the window had been opened and closed.
        protected override void OnClosed(EventArgs e)
        {
            HookQsoCollection(_allQsos, null);
            _liveRefreshTimer?.Stop();   // a quiet period still running has this window in the dispatcher queue
            _spinnerClock?.Stop();
            base.OnClosed(e);
        }

        private void QsoCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            InvalidateSourceStats();     // at once, so nothing can read a stale count in the meantime

            if (_liveRefreshTimer == null)
            {
                _liveRefreshTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(800)
                };
                _liveRefreshTimer.Tick += (s, e2) =>
                {
                    _liveRefreshTimer.Stop();
                    RebuildAfterPossibleEdit();
                };
            }
            _liveRefreshTimer.Stop();    // restart the quiet period: a run of QSOs repaints once, at its end
            _liveRefreshTimer.Start();
        }

        // Counts the whole page again, from the QSOs as they are now. The operator edits a callsign in
        // the log, a country changes, and the statistics have to say so - nothing about a QSO edit
        // reaches this window on its own, so returning to it is the trigger.
        private void RebuildAfterPossibleEdit()
        {
            if (!_statsBuilt) return;
            try
            {
                InvalidateSourceStats();
                ComputeStats();        // the left page: tiles, worked and missing country tables
                RefreshForSource();    // the open folder, off the freshly counted numbers
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ── EVERYTHING A FOLDER NEEDS, FROM ONE PASS ──────────────────────
        //
        // Opening a folder used to walk the whole log SEVEN times: filter it, count distinct callsigns,
        // collect and SORT every date for the first/last, the QSO pivot, the country pivot, missing CQ
        // zones, missing ITU zones - the last three each resolving the DXCC entity of every QSO again.
        // All of it on the UI thread, so the window sat frozen for the duration, and going back to a
        // folder just visited paid the whole cost a second time. On a 28,000-QSO log that is what the
        // operator felt as "it takes time to switch folder".
        //
        // One pass now fills this, and the answer is kept per folder until the log or its confirmations
        // change (see InvalidateSourceStats). Nothing here decides anything - it only counts - so the
        // painters below read it instead of the log and produce exactly what they always did.
        private sealed class SourceStats
        {
            public int QsoCount;
            public int UniqueCalls;
            public string FirstDate;                 // ADIF yyyyMMdd, null when the folder holds none
            public string LastDate;

            // QSO counts: band -> mode -> count. "Other" collects bands outside the pivot's own list.
            public readonly Dictionary<string, Dictionary<string, int>> QsoByBandMode =
                new Dictionary<string, Dictionary<string, int>>();

            // COUNTRIES are sets, not counters: the same country worked ten times on 20m SSB is one
            // country. Keyed "band|mode", plus the per-band, per-mode and overall sets.
            public readonly Dictionary<string, HashSet<string>> CountryCell =
                new Dictionary<string, HashSet<string>>();
            public readonly Dictionary<string, HashSet<string>> CountryByBand =
                new Dictionary<string, HashSet<string>>();
            public readonly Dictionary<string, HashSet<string>> CountryByMode =
                new Dictionary<string, HashSet<string>>();
            public readonly HashSet<string> Countries =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public readonly HashSet<int> CqZones = new HashSet<int>();
            public readonly HashSet<int> ItuZones = new HashSet<int>();
        }

        private readonly Dictionary<ConfSource, SourceStats> _statsCache =
            new Dictionary<ConfSource, SourceStats>();

        // Throw the per-folder answers away, and the resolved entities with them. Called whenever the QSOs
        // behind them can have changed - a reload after a confirmation check, a Paper QSL tick, and every
        // time this window is activated, because a QSO edited in the log or the Log Workshop changes the
        // objects in the very list these numbers were counted from and raises nothing this window hears.
        //
        // A cached count of stale data is worse than a slow count of fresh data: the whole point of the
        // page is to be believed. Recomputing on activation costs ONE pass, which is what this rewrite
        // made it - the folder switching the operator does while reading the page stays instant.
        private void InvalidateSourceStats()
        {
            _statsCache.Clear();
            _qsoDxcc = null;     // a QSO whose callsign or date was edited resolves to a different entity
        }

        // The resolved DXCC entity of one QSO, remembered against the QSO OBJECT. Resolve(call, date)
        // keeps its own cache too, but keyed by a "call|date" string it has to build on every lookup -
        // one throwaway string per QSO per pass, which on a large log ran into six figures per folder
        // click. Here the key is the object already in hand.
        private Dictionary<QSO, DXCC> _qsoDxcc;

        private DXCC ResolveQso(QSO q)
        {
            if (q == null) return null;
            if (_qsoDxcc == null) _qsoDxcc = new Dictionary<QSO, DXCC>();
            DXCC d;
            if (!_qsoDxcc.TryGetValue(q, out d))
            {
                d = Resolve(q.DXCall, q.Date);
                _qsoDxcc[q] = d;
            }
            return d;
        }

        private SourceStats StatsForCurrentSource()
        {
            SourceStats cached;
            if (_statsCache.TryGetValue(_source, out cached)) return cached;

            var st = new SourceStats();
            foreach (string b in PivotBands)
                st.QsoByBandMode[b] = new Dictionary<string, int> { { "SSB", 0 }, { "CW", 0 }, { "DIGI", 0 }, { "FM", 0 } };
            st.QsoByBandMode["Other"] = new Dictionary<string, int> { { "SSB", 0 }, { "CW", 0 }, { "DIGI", 0 }, { "FM", 0 } };

            var calls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            HashSet<string> Bucket(Dictionary<string, HashSet<string>> d, string key)
            {
                HashSet<string> set;
                if (!d.TryGetValue(key, out set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); d[key] = set; }
                return set;
            }

            foreach (QSO q in _allQsos ?? (System.Collections.Generic.IEnumerable<QSO>)new QSO[0])
            {
                if (q == null || !IsAchievedForSource(q)) continue;

                st.QsoCount++;

                if (!string.IsNullOrEmpty(q.DXCall)) calls.Add(q.DXCall);

                // First and last date without sorting anything: the extremes of a yyyyMMdd string are the
                // extremes of the date, and two comparisons per QSO beat sorting 28,000 strings.
                if (!string.IsNullOrEmpty(q.Date))
                {
                    if (st.FirstDate == null || string.CompareOrdinal(q.Date, st.FirstDate) < 0) st.FirstDate = q.Date;
                    if (st.LastDate == null || string.CompareOrdinal(q.Date, st.LastDate) > 0) st.LastDate = q.Date;
                }

                string band = NormalizeBand(q.Band);
                string mode = NormalizeMode(q.Mode);           // always SSB/CW/DIGI/FM - never null
                string pivotBand = (band != null && Array.IndexOf(PivotBands, band) >= 0) ? band : "Other";
                st.QsoByBandMode[pivotBand][mode]++;

                // Resolved live from the callsign and the QSO's own date, exactly as the worked/missing
                // lists do, so these tables can never disagree with the tiles beside them.
                DXCC d = ResolveQso(q);
                if (d == null) continue;

                // A country, not a "no DXCC entity" answer - see DXCC.IsDxccEntity.
                if (d.IsDxccEntity)
                {
                    Bucket(st.CountryCell, pivotBand + "|" + mode).Add(d.Name);
                    Bucket(st.CountryByBand, pivotBand).Add(d.Name);
                    Bucket(st.CountryByMode, mode).Add(d.Name);
                    st.Countries.Add(d.Name);
                }

                if (d.CqZone >= 1 && d.CqZone <= 40) st.CqZones.Add(d.CqZone);
                if (d.ItuZone >= 1 && d.ItuZone <= 90) st.ItuZones.Add(d.ItuZone);
            }

            st.UniqueCalls = calls.Count;
            _statsCache[_source] = st;
            return st;
        }

        // What the LEFT panel calls the open folder. The folder's own name is the whole truth: each one
        // now counts exactly what it is named after.
        private string LeftSourceTitle()
        {
            return SourceTitle(_source);
        }

        // Repaints everything that depends on which source folder is open. Cheap enough to run on every
        // folder change: one pass over the log to filter, then the pivot's own pass.
        private void ApplySourceCounts()
        {
            if (TB_TotalQSOs == null) return;
            SourceStats st = StatsForCurrentSource();

            TB_TotalQSOs.Text = st.QsoCount.ToString("N0");

            // "Total" would be a lie on a confirmation folder - the count under it is that source's
            // confirmed QSOs, not the log's total - so the tile names the source, like the pivot header
            // right below it already does.
            if (TB_TotalQsoLabel != null)
                TB_TotalQsoLabel.Text = _source == ConfSource.Worked
                    ? "Total QSOs"
                    : LeftSourceTitle() + " QSOs";

            _uniqueCallsText = st.UniqueCalls.ToString("N0");
            ApplyUniqueTile();

            // The dates follow too, so the range is the span of the QSOs actually being counted - the
            // first and last CONFIRMED contact on a confirmation folder, not the first and last logged.
            TB_DateStart.Text = st.FirstDate != null ? FormatAdifDate(st.FirstDate) : "—";
            TB_DateEnd.Text = st.LastDate != null ? FormatAdifDate(st.LastDate) : "—";

            BuildPivot(st);

            TB_PivotHeader.Text = "QSOs by Bands & Mode"
                + (_source == ConfSource.Worked ? "" : " — " + LeftSourceTitle())
                + "\n(" + st.QsoCount.ToString("N0") + ")";

            // The status line lives here, not in ComputeStats, so it FOLLOWS the folder: it used to be
            // written once at window open and then kept showing the whole-log figure while everything
            // above it had been recomputed for the open source - "computed for 28,366" over a page of
            // numbers computed for 19,263. On a confirmation folder it now names both, because both are
            // worth knowing: what this source has confirmed, and how big the log is underneath it.
            if (TB_Status != null)
            {
                int logTotal = _allQsos != null ? _allQsos.Count : 0;
                TB_Status.Text =
                    logTotal == 0            ? "No QSOs to analyze." :
                    _source == ConfSource.Worked
                        ? $"Statistics computed for {logTotal:N0} QSO{(logTotal == 1 ? "" : "s")}."
                        : $"Statistics computed for {st.QsoCount:N0} {SourceTitle(_source)}-confirmed "
                          + $"QSO{(st.QsoCount == 1 ? "" : "s")} — this log holds {logTotal:N0}.";
            }
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
                int totalDxcc = ActiveEntityCount();
                TB_TotalQSOs.Text       = "0";
                _uniqueCallsText        = "0";
                _countryCountText       = "0";
                ApplyUniqueTile();
                TB_UniqueCountries.Text = $"0 / {totalDxcc}";
                SetMissingTile(totalDxcc, totalDxcc);   // an empty log is missing every one of them
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

            // The band/mode warning and its editor are gone: the Log Fixer reports a missing band with
            // everything else that is wrong with a QSO, and can fill it in from the frequency, which the
            // old editor could not. One place to look, one place to put things right.
            TB_DataQuality.Text = "";

            PopulateMissingZones();
            // The status line is set by ApplySourceCounts (called above), which knows which folder is
            // open - setting it here too would overwrite the folder-aware text with the whole-log one.
        }

        // Fills the "Missing CQ Zones" (1..40) and "Missing ITU Zones" (1..90) scrollable lists with
        // the zones not yet present in any QSO, and shows the count in each header.
        private void PopulateMissingZones()
        {
            SourceStats st = StatsForCurrentSource();
            List<int> missingCq  = MissingZones(40, st.CqZones);
            List<int> missingItu = MissingZones(90, st.ItuZones);

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
        // ── THE ENTITY LIST, BY NUMBER ────────────────────────────────────
        //
        // Every count on this page is made of ADIF entity NUMBERS now, not of country names. A name is
        // not an identity: two databases spell the same country differently, either may re-spell it, and
        // a name in nobody's list reads as a deleted country - which is exactly how "Maritime Mobile",
        // the answer that means no country at all, came to be reported as a deleted entity worked and
        // confirmed. A number is fixed, unique, and never reused, and Club Log states outright which
        // numbers are deleted instead of leaving it to be inferred.
        private Dictionary<int, string> _entityNames;      // code -> the name to print
        private Dictionary<string, int> _codeByName;       // name -> code, for the fallbacks below
        private HashSet<int> _activeCodes;                 // the entities that exist today
        private HashSet<int> _deletedCodes;

        private void EnsureEntityTable()
        {
            if (_entityNames != null) return;
            _entityNames = new Dictionary<int, string>();
            _codeByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _activeCodes = new HashSet<int>();
            _deletedCodes = new HashSet<int>();
            try
            {
                foreach (var e in DXCCManager.CountryLookup.Shared.AllEntities())
                {
                    if (e.Code <= 0) continue;
                    _entityNames[e.Code] = e.Name;
                    if (!string.IsNullOrEmpty(e.Name)) _codeByName[e.Name] = e.Code;
                    if (e.Deleted) _deletedCodes.Add(e.Code); else _activeCodes.Add(e.Code);
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (_entityNames.Count > 0) return;

            // NO CLUB LOG FILE - never downloaded, or the download failed. Club Log is where the entity
            // NUMBERS come from, so without it there are none, and counting by number would count
            // nothing: an operator with 28,000 QSOs would open this window and be told they had worked
            // no countries at all. cty.dat still knows every entity by name, so each gets an id of its
            // own here and the page goes on counting identities rather than spellings - they are simply
            // OUR ids for this session instead of the ARRL's. Negative, so they can never be mistaken
            // for a real ADIF code, and cty.dat lists only entities that exist, so none is deleted.
            try
            {
                int next = -1;
                foreach (string name in _masterResolver.GetAllEntityNames())
                {
                    if (string.IsNullOrWhiteSpace(name) || _codeByName.ContainsKey(name)) continue;
                    _entityNames[next] = name;
                    _codeByName[name] = next;
                    _activeCodes.Add(next);
                    next--;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // THE DENOMINATOR every "/ 340" on this page is printed over, taken from the SAME set the Missing
        // list is built from. Two different counts of "how many countries exist" is how a page ends up
        // saying 326 worked and 15 missing out of 340, which is 341. Club Log's active list when we have
        // it; cty.dat's names only when we do not.
        private int ActiveEntityCount()
        {
            EnsureEntityTable();
            if (_activeCodes.Count > 0) return _activeCodes.Count;
            try { return _masterResolver.GetAllEntityNames().Count; }
            catch (Exception swallowed) { Log.Swallow(swallowed); return 0; }
        }

        private string EntityNameOf(int code)
        {
            EnsureEntityTable();
            string name;
            return _entityNames.TryGetValue(code, out name) && !string.IsNullOrEmpty(name)
                ? name : ("DXCC " + code);
        }

        private bool IsDeletedEntityCode(int code)
        {
            EnsureEntityTable();
            return _deletedCodes.Contains(code);
        }

        // The entity NUMBER a QSO counts towards, or 0 when it counts towards none.
        //
        // The resolver supplies the number with the answer nearly always - measured at 28,434 of 28,454
        // QSOs on a real log. The handful without one are old contacts whose callsign Club Log has no
        // dated record for; their COUNTRY is known perfectly well, so the number is looked up from that
        // name rather than throwing the QSO away. Falling back to the name here, and only here, is what
        // lets everything above be a number.
        private int EntityCodeOf(QSO q)
        {
            if (q == null) return 0;
            DXCC d = ResolveQso(q);
            if (d == null || !d.IsDxccEntity) return 0;
            if (d.DxccCode > 0) return d.DxccCode;

            try
            {
                int byName = DXCCManager.CountryLookup.Shared.EntityCodeForCountry(d.Name);
                if (byName > 0) return byName;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // Last: the entity table's own id for that name - a real ADIF code for an entity Club Log
            // knows but could not put a number to on this callsign, or our own session id when there is
            // no Club Log file at all.
            EnsureEntityTable();
            int fallback;
            return !string.IsNullOrEmpty(d.Name) && _codeByName.TryGetValue(d.Name, out fallback) ? fallback : 0;
        }

        // The entity name to COUNT a callsign under, or null when the answer is not a country at all -
        // "Unknown", or one of Club Log's no-DXCC-entity answers (Maritime Mobile and the rest). Every
        // place that builds a set of worked or confirmed entities goes through this, so none of them can
        // be the one that forgets and lets a non-country into the totals. See DXCC.IsDxccEntity.
        private string EntityNameFor(string call, string adifDate = null)
        {
            DXCC d = Resolve(call, adifDate);
            return d != null && d.IsDxccEntity ? d.Name : null;
        }

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

        // The zones this folder has NOT reached. The achieved set was gathered in the folder's single
        // pass; this used to be two more walks of the whole log, one per zone system, each resolving
        // every QSO's entity over again.
        private List<int> MissingZones(int maxZone, HashSet<int> achieved)
        {
            return Enumerable.Range(1, maxZone).Where(z => !achieved.Contains(z)).ToList();
        }

        // ── pivot table builder ───────────────────────────────────────────

        // qsos is the set for the OPEN FOLDER (see SourceQsos): the whole log on Worked, only that
        // service's confirmed contacts on any other.
        private void BuildPivot(SourceStats st)
        {
            // The counting was done in the folder's single pass (see SourceStats); this only draws it.
            var counts = st.QsoByBandMode;
            var other = counts["Other"];

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
            // All four sets come from the folder's single pass (see SourceStats); this only draws them.
            SourceStats st = StatsForCurrentSource();
            var cell = st.CountryCell;
            var bandTotal = st.CountryByBand;
            var modeTotal = st.CountryByMode;
            var grand = st.Countries;

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
            _countryBreakdown = BuildCountryBreakdown(grand);
            ApplyUniqueTile();

            if (TB_CountryPivotHeader != null)
                TB_CountryPivotHeader.Text = "Countries by Bands & Mode — " + LeftSourceTitle()
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
        private List<KeyValuePair<string, string>> _countryBreakdown = new List<KeyValuePair<string, string>>();

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
        // The parts the country total is made of, written out under it: which source earned each country,
        // and how many are DELETED entities.
        //
        // The total counts deleted entities and the tiles opposite do not, which is why "331" here and
        // "326" there look like a contradiction and why 326 + 2 never reached 331. Naming the parts is
        // what makes the two readable side by side - and on the LoTW folder it also says plainly which
        // countries came from a card rather than from LoTW, since that folder counts both.
        private List<KeyValuePair<string, string>> BuildCountryBreakdown(HashSet<string> countries)
        {
            var empty = new List<KeyValuePair<string, string>>();
            if (countries == null || countries.Count == 0) return empty;

            var current = new HashSet<string>(_masterResolver.GetAllEntityNames(), StringComparer.OrdinalIgnoreCase);
            int deleted = countries.Count(n => !current.Contains(n));
            int active = countries.Count - deleted;

            // ONE PART PER LINE. Side by side they made the tile far wider than the two beside it, which
            // dragged the whole left page - and the source folders beyond it - out with them. Stacked, the
            // tile keeps its original width and the parts can be read at a proper size.
            // WORD FIRST, NUMBER AFTER, one part per row - the words right-justified against the numbers
            // so the three read as a small table. No "+" signs: the rows are parts OF the total above
            // them, and a plus sign invites the reader to add them to it instead.
            var parts = new List<KeyValuePair<string, string>>();
            parts.Add(new KeyValuePair<string, string>("active", active.ToString()));
            if (_source == ConfSource.Lotw)
            {
                // Split the active ones by what actually earned them. Cards first-class, not an asterisk,
                // and named exactly as their own folder names them.
                int card = PaperOnlyEntities().Count(n => countries.Contains(n));
                parts.Add(new KeyValuePair<string, string>("LoTW", (active - card).ToString()));
                parts.Add(new KeyValuePair<string, string>("Paper QSL", card.ToString()));
            }
            // Nothing more on the other folders: "active" is already the line above. An else-branch here
            // added it a SECOND time, so every folder except LoTW printed "active 326" twice under the
            // total and the reader was left looking for the difference between two identical rows.
            if (deleted > 0)
                parts.Add(new KeyValuePair<string, string>("deleted", deleted.ToString()));
            return parts;
        }

        private void ApplyUniqueTile()
        {
            if (TB_UniqueCalls == null || TB_UniqueCallsLabel == null) return;
            bool qso = _leftView == LeftView.Qso;
            // "Countries" alone was read as the same thing the tiles opposite count, and it is not: this
            // one counts every country in the QSOs below it, DELETED entities included, while the tiles
            // opposite are measured against the 340 that exist today and leave the deleted out. Two honest
            // numbers, and the sum between them never worked (326 confirmed + 2 unconfirmed against 331
            // here). Saying "incl. deleted" is what makes 331 = 326 + 5 readable instead of a puzzle.
            TB_UniqueCallsLabel.Text = qso ? "Unique Calls" : "Countries";
            TB_UniqueCalls.Text = qso ? _uniqueCallsText : _countryCountText;

            // The tile is a FIXED width on both folders, so the left page - and the source folders beyond
            // it - cannot shift sideways when the operator switches between QSO and DXCC. "Unique Calls"
            // with a five-figure count does not fit that width on one line, so on the QSO folder the
            // number sits UNDER its label; on the DXCC folder they share a line, leaving room for the
            // three parts below them.
            if (UniqueTileHeader != null)
            {
                UniqueTileHeader.Orientation = qso ? Orientation.Vertical : Orientation.Horizontal;
                TB_UniqueCalls.Margin = qso ? new Thickness(0) : new Thickness(8, 0, 0, 0);
                TB_UniqueCalls.HorizontalAlignment = qso ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                TB_UniqueCallsLabel.HorizontalAlignment = qso ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            }

            // The breakdown carries the "incl. deleted" meaning far better than a label could, so the
            // label goes back to the plain word and the parts are spelled out underneath.
            if (CountryBreakdownGrid != null)
            {
                bool show = !qso && _countryBreakdown != null && _countryBreakdown.Count > 0;
                CountryBreakdownGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                var labels = new[] { TB_BdL1, TB_BdL2, TB_BdL3 };
                var values = new[] { TB_BdV1, TB_BdV2, TB_BdV3 };
                for (int i = 0; i < labels.Length; i++)
                {
                    bool has = show && i < _countryBreakdown.Count;
                    labels[i].Text = has ? _countryBreakdown[i].Key : string.Empty;
                    values[i].Text = has ? _countryBreakdown[i].Value : string.Empty;
                }
            }
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
                case ConfSource.Award: return "ARRL DXCC Award";
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
            //
            // IsDxccEntity, not just a name test: it also throws out Club Log's "no DXCC entity" answers -
            // MARITIME MOBILE, AERONAUTICAL MOBILE, SATELLITE/INTERNET OR REPEATER and INVALID. Those were
            // being counted as countries, and since no current-entity list contains them they were then
            // filed as DELETED entities: an operator with three /MM contacts was told they had worked and
            // confirmed a deleted country called Maritime Mobile.
            // COUNTED BY ENTITY NUMBER. See EntityCodeOf: the number is the identity, the name is only
            // what gets printed, and "deleted" is Club Log's own flag rather than "this name is not in
            // the list of current ones".
            var workedCounts = new Dictionary<int, int>();
            foreach (QSO q in _allQsos)
            {
                int code = EntityCodeOf(q);
                // ZERO means no entity. A NEGATIVE code is one of our own session ids, used when there is no
                // Club Log file to take real ADIF numbers from - it identifies the entity just as well.
                if (code == 0) continue;
                int n;
                workedCounts.TryGetValue(code, out n);
                workedCounts[code] = n + 1;
            }

            _workedList = workedCounts
                .Select(p => new CountryItem
                {
                    Code            = p.Key,
                    Name            = EntityNameOf(p.Key),
                    Count           = p.Value,
                    FlagImage       = GetFlagImage(EntityNameOf(p.Key)),
                    IsDeletedEntity = IsDeletedEntityCode(p.Key),
                }).ToList();

            // Single line now — the LoTW button sits beside it on the same row.
            // The CURRENT entities only, so this header, the table under it and the tile above it are one
            // number - the same 326 that "326 / 340" prints. It used to count deleted entities too, and a
            // header reading 333 sat directly beneath a tile reading 326.
            TB_WorkedHeader.Text = $"Worked Active Countries ({_workedList.Count(c => !c.IsDeletedEntity)})";

            // The Missing list is source-aware, so it is built in its own method that the folder switch
            // also calls. The tiles are set by ApplyConfirmedHighlight from the same _missingList.
            RebuildMissingCountries();

            TB_SortWorkedName.MouseLeftButtonUp  -= SortWorkedByName;
            TB_SortWorkedName.MouseLeftButtonUp  += SortWorkedByName;
            TB_SortWorkedCount.MouseLeftButtonUp -= SortWorkedByCount;
            TB_SortWorkedCount.MouseLeftButtonUp += SortWorkedByCount;
            TB_SortWorkedConfirmed.MouseLeftButtonUp -= SortWorkedByConfirmed;
            TB_SortWorkedConfirmed.MouseLeftButtonUp += SortWorkedByConfirmed;
            TB_SortWorkedCode.MouseLeftButtonUp  -= SortWorkedByCode;
            TB_SortWorkedCode.MouseLeftButtonUp  += SortWorkedByCode;
            TB_SortMissingName.MouseLeftButtonUp -= SortMissingByName;
            TB_SortMissingName.MouseLeftButtonUp += SortMissingByName;
            TB_SortMissingCode.MouseLeftButtonUp -= SortMissingByCode;
            TB_SortMissingCode.MouseLeftButtonUp += SortMissingByCode;

            ApplyConfirmedHighlight();   // sets the tiles + colors the worked rows for the current source
            ApplyWorkedSort();
        }

        // The list holds only CURRENT ("active") entities - it is built by subtracting from the 340 that
        // exist today, so a deleted entity can never appear in it however it was worked or confirmed. The
        // labels say "Missing Active Countries" for that reason, and to pair with "Confirmed Active
        // Countries": both are measured against the same 340.
        //
        // Rebuilds the Missing list for the CURRENT folder, so it always matches the Missing
        // tile: on the Worked folder it is the entities never contacted; on a confirmation folder it is
        // the entities not confirmed by that source (all DXCC minus that source's confirmed set).
        private void RebuildMissingCountries()
        {
            // THE ACTIVE ENTITIES BY NUMBER, minus the numbers this folder has achieved. Missing is now
            // arithmetic on two sets of integers rather than a comparison of two lists of spellings.
            EnsureEntityTable();
            HashSet<int> achieved = _source == ConfSource.Worked
                ? new HashSet<int>(_workedList.Where(c => c.Code != 0).Select(c => c.Code))
                : AchievedCodes();

            _missingList = _activeCodes
                .Where(code => !achieved.Contains(code))
                .Select(code => new CountryItem
                {
                    Code = code,
                    Name = EntityNameOf(code),
                    FlagImage = GetFlagImage(EntityNameOf(code)),
                })
                .ToList();

            TB_MissingHeader.Text = $"Missing Countries\n({_missingList.Count})";
            ApplyMissingSort();
        }

        // Worked entities that still exist as DXCC countries - the basis every "/ 340" on this page uses.
        private int WorkedActiveCount()
        {
            if (_workedList == null) return 0;
            // Each item already knows whether its ENTITY is deleted - Club Log said so, by number - so
            // this no longer asks whether a name appears in a list of names.
            return _workedList.Count(c => !c.IsDeletedEntity);
        }

        // A tile number is a TextBlock, not a Hyperlink, so its click arrives as a mouse event; the work
        // itself is shared with anything else that wants to open the same list.
        private void WorkedNotConfirmedTile_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            WorkedNotConfirmed_Click(sender, e);
        }

        // The countries in that tile, named - and, for each, what DOES confirm it. An operator told their
        // log holds "2 not confirmed" while they know every country they worked came back has no way to
        // check who is right; this shows them the two rows and the answer next to each.
        private void WorkedNotConfirmed_Click(object sender, RoutedEventArgs e)
        {
            // BY ENTITY NUMBER, like every other count on this page - so the countries listed here are
            // exactly the ones the tile counted, and the two can never be built from different questions.
            EnsureEntityTable();
            HashSet<int> achieved = AchievedCodes();

            // entity -> QSO count, and which services confirmed any of its QSOs
            var counts = new Dictionary<int, int>();
            var sources = new Dictionary<int, SortedSet<string>>();
            if (_allQsos != null)
            {
                foreach (QSO q in _allQsos)
                {
                    if (q == null) continue;
                    int code = EntityCodeOf(q);
                    if (code == 0 || !_activeCodes.Contains(code) || achieved.Contains(code)) continue;

                    int n;
                    counts[code] = counts.TryGetValue(code, out n) ? n + 1 : 1;
                    SortedSet<string> set;
                    if (!sources.TryGetValue(code, out set)) sources[code] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (q.LotwQslRcvd == 1) set.Add("LoTW");
                    if (q.QrzQslRcvd == 1) set.Add("QRZ");
                    if (q.EqslQslRcvd == 1) set.Add("eQSL");
                    if (q.ClublogQslRcvd == 1) set.Add("Club Log");
                    if (q.PaperQslRcvd == 1) set.Add("Paper QSL");
                }
            }

            var rows = counts.Select(p => new
                             {
                                 Name = EntityNameOf(p.Key),
                                 Count = p.Value,
                                 ConfirmedBy = sources[p.Key].Count > 0 ? string.Join(", ", sources[p.Key]) : "— nothing —",
                             })
                             .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                             .ToList();

            if (rows.Count == 0)
            {
                HolyMessageBox.Show(
                    "Every current DXCC country in this log is confirmed here.\n\nNothing left to chase.",
                    SourceName, HolyMsgType.Info, this);
                return;
            }

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                SelectionMode = DataGridSelectionMode.Single,
                FontSize = 16,
                ItemsSource = rows,
            };
            grid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();
            ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
            grid.Columns.Add(new DataGridTextColumn { Header = "Country", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "QSOs", Binding = new System.Windows.Data.Binding("Count"), Width = 70 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Confirmed by", Binding = new System.Windows.Data.Binding("ConfirmedBy"), Width = 190 });

            var win = new Window
            {
                Title = $"Worked, not confirmed at {SourceName} ({rows.Count})",
                Owner = this,
                Width = 560,
                Height = 300,
                MinWidth = 380,
                MinHeight = 200,
                ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)ThemeManager.Brush("WindowBg"),
                Content = new Border { Padding = new Thickness(10), Child = grid },
            };
            WindowBounds.Attach(win, "WorkedNotConfirmed");
            win.ShowDialog();
        }

        // The Missing tile's two lines: what is missing out of every entity that exists, and what is
        // missing for the DXCC Honor Roll.
        //
        // Honor Roll is the numerical TOP TEN of the current DXCC List, so it asks for nine fewer than
        // the total - 331 against today's 340 (ARRL Honor Roll listing, 4 August 2026). Derived from
        // totalDxcc rather than written as 331, so the day the ARRL adds or removes an entity this line
        // follows the same list the rest of the window already counts against.
        //
        // The arithmetic reduces to "nine may be missing": missing-for-Honor-Roll = missing - 9. Shown
        // only while it is still out of reach; at 9 or fewer missing the operator IS on the Honor Roll,
        // and a line reading "0 for Honor Roll" would be a strange way to say so.
        private void SetMissingTile(int missingCount, int totalDxcc)
        {
            const int honorRollShortfall = 9;          // top ten of the list = total - 9
            int honorTarget = Math.Max(0, totalDxcc - honorRollShortfall);
            int missingForHonor = Math.Max(0, missingCount - honorRollShortfall);

            TB_MissingDxcc.Text = $"{missingCount} for {totalDxcc}";

            // The target itself, spelled out beside the words - smaller and in plain text, so it reads as
            // the definition of "Honor Roll" rather than as a second count competing with the one in
            // front of it. Written as Inlines because the two halves are different sizes and colours.
            TB_MissingHonor.Inlines.Clear();
            if (missingForHonor > 0)
            {
                TB_MissingHonor.Inlines.Add(new System.Windows.Documents.Run($"{missingForHonor} for Honor Roll "));
                TB_MissingHonor.Inlines.Add(new System.Windows.Documents.Run($"({honorTarget})")
                {
                    FontSize = 16,
                    FontWeight = FontWeights.Normal,
                    Foreground = System.Windows.Media.Brushes.Black,
                });
                TB_MissingHonor.ToolTip = $"The DXCC Honor Roll is the top ten of the current list — {honorTarget} "
                                        + $"of today's {totalDxcc} entities, so up to {honorRollShortfall} may be missing.";
            }
            else
            {
                TB_MissingHonor.Inlines.Add(new System.Windows.Documents.Run("Honor Roll reached "));
                TB_MissingHonor.Inlines.Add(new System.Windows.Documents.Run($"({honorTarget})")
                {
                    FontSize = 16,
                    FontWeight = FontWeights.Normal,
                    Foreground = System.Windows.Media.Brushes.Black,
                });
                TB_MissingHonor.ToolTip = $"{honorTarget} or more of today's {totalDxcc} entities — the top ten of the "
                                        + "current DXCC List.";
            }
        }

        // The QSOs behind the "N deleted" count, and how many distinct entities they cover.
        private ObservableCollection<QSO> DeletedEntityQsos(out int entityCount)
        {
            var subset = new ObservableCollection<QSO>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            entityCount = 0;
            if (_allQsos == null) return subset;

            var current = new HashSet<string>(_masterResolver.GetAllEntityNames(), StringComparer.OrdinalIgnoreCase);
            foreach (QSO q in _allQsos)
            {
                if (q == null || !IsAchievedForSource(q)) continue;
                DXCC d = ResolveQso(q);
                // A DELETED COUNTRY, not merely something absent from the current list. Maritime Mobile
                // and the rest of Club Log's "no DXCC entity" answers are in no current-entity list
                // either, and were being listed here as deleted countries chased and confirmed.
                if (d == null || !d.IsDxccEntity) continue;
                if (current.Contains(d.Name)) continue;   // still exists - not a deleted entity
                names.Add(d.Name);
                subset.Add(q);
            }
            entityCount = names.Count;
            return subset;
        }

        // Click the "N deleted" count -> THE QSOs THEMSELVES, in the Log Workshop.
        //
        // It used to open a little read-only table of country names and a QSO count each, which answered
        // "which ones" and nothing else. A deleted entity is often the rarest thing in an operator's log
        // and the contact they most want to look at: when it was, on what band, whether it is confirmed,
        // what the card says. The Workshop already shows all of that, sorts it, edits it, exports it and
        // uploads it, so the honest thing is to hand it the contacts rather than build a lesser table.
        private void DeletedCountries_Click(object sender, RoutedEventArgs e)
        {
            int entities;
            ObservableCollection<QSO> qsos = DeletedEntityQsos(out entities);

            // On the Worked folder every logged QSO counts, so these are the deleted entities the operator
            // has WORKED - the wording follows, rather than claiming a confirmation.
            bool workedFolder = _source == ConfSource.Worked;
            if (qsos.Count == 0)
            {
                HolyMessageBox.Show(workedFolder
                        ? "No deleted DXCC entities have been worked in this log."
                        : "No deleted DXCC entities are confirmed here.",
                    SourceName, HolyMsgType.Info, this);
                return;
            }

            string what = workedFolder
                ? $"deleted entities worked ({entities} {(entities == 1 ? "country" : "countries")}, {qsos.Count:N0} QSOs)"
                : $"deleted entities confirmed by {SourceName} ({entities} {(entities == 1 ? "country" : "countries")}, {qsos.Count:N0} QSOs)";

            QsoSubsetRequested?.Invoke(qsos, what);
        }

        // How many CURRENT entities the operator holds a paper card for that the folder's own source has
        // not confirmed. Deleted entities are left out: the figure sits beside a count measured against
        // the 340 entities that exist today, and mixing the two is what made "330 / 340" unreadable.
        // THE ONE SET the whole folder counts against: the entities this folder treats as achieved.
        //
        // Every number on the page reads from here - the Confirmed tile, the Missing list, the "worked,
        // not confirmed" tile and the ticks in the table - so they cannot contradict each other. They did:
        // when the LoTW folder started counting paper cards, only the Confirmed tile and the Missing list
        // learned about it, and the middle tile kept measuring against LoTW alone. Minami Torishima was
        // then confirmed AND not-confirmed at once, and the tiles read 326 + 3 against 333 worked.
        //
        // On the LoTW folder that means LoTW's own confirmations plus the paper cards, because the ARRL
        // accepts both. Every other folder answers only for its own service.
        // THE ONE SET the whole folder counts against, by number. Everything that used to compare country
        // names against each other compares these instead.
        private HashSet<int> AchievedCodes()
        {
            return _confirmedCodes;
        }

        private HashSet<string> PaperOnlyEntities()
        {
            var paper = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lotw = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_allQsos == null) return paper;

            // Both sets are read from the QSOs themselves rather than from the folder's confirmed set,
            // because that set now HOLDS the paper cards - comparing against it would always answer zero
            // and the line explaining the tile would quietly disappear.
            var current = new HashSet<string>(_masterResolver.GetAllEntityNames(), StringComparer.OrdinalIgnoreCase);
            foreach (QSO q in _allQsos)
            {
                if (q == null) continue;
                if (q.LotwQslRcvd != 1 && q.PaperQslRcvd != 1) continue;
                DXCC d = Resolve(q.DXCall, q.Date);
                if (d == null || string.IsNullOrEmpty(d.Name)) continue;
                if (!current.Contains(d.Name)) continue;   // deleted entity - not part of "/ 340"
                if (q.LotwQslRcvd == 1) lotw.Add(d.Name);
                if (q.PaperQslRcvd == 1) paper.Add(d.Name);
            }
            paper.ExceptWith(lotw);   // what the card brings that LoTW does not
            return paper;
        }

        // Whether an ADIF CREDIT_GRANTED list awards any DXCC credit. The field is a comma list of awards
        // ("DXCC,DXCC-M,DXCC-CHAL,DXCC-5B,DXCC-20…"), each optionally qualified by how it was earned; every
        // DXCC flavour means the entity itself has been credited. WAZ and other non-DXCC awards do not.
        private static bool HasDxccCredit(string creditGranted)
        {
            if (string.IsNullOrWhiteSpace(creditGranted)) return false;
            foreach (string token in creditGranted.Split(','))
                if (token.Trim().StartsWith("DXCC", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // Whether a QSO counts as "achieved" for the current folder - i.e. removes its entity/zone from
        // the Missing lists. On the Worked folder any logged QSO counts; on a confirmation folder only a
        // QSO confirmed by that source counts. Reads the per-QSO confirmation flags.
        private bool IsAchievedForSource(QSO q)
        {
            switch (_source)
            {
                // LoTW answers for LoTW ALONE. A paper card the ARRL would accept is reported beside this
                // folder's number ("+1 by paper card") and counted in the ARRL DXCC Award folder, which is
                // the one whose name promises that sum. Counting it here made the two folders identical.
                case ConfSource.Lotw:    return q.LotwQslRcvd == 1;
                case ConfSource.Qrz:     return q.QrzQslRcvd == 1;
                case ConfSource.Eqsl:    return q.EqslQslRcvd == 1;
                case ConfSource.Clublog: return q.ClublogQslRcvd == 1;
                case ConfSource.Paper:   return q.PaperQslRcvd == 1;
                // Everything the ARRL accepts, in one answer: confirmed at LoTW, a paper card in hand, or
                // a credit they have already granted for this contact.
                case ConfSource.Award:   return q.LotwQslRcvd == 1 || q.PaperQslRcvd == 1
                                             || HasDxccCredit(q.CreditGranted);
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
            if (s.UseLotwService || !string.IsNullOrWhiteSpace(LotwConfirmedEntities))
                AddSourceFolder(ConfSource.Lotw, "LoTW");
            if (s.UseQrzLogbook || !string.IsNullOrWhiteSpace(QrzConfirmedEntities))
                AddSourceFolder(ConfSource.Qrz, "QRZ");
            // eQSL is configured by the per-callsign accounts table, so show the folder when an account
            // exists (or the service is on, or a past download left a cache).
            bool hasEqsl = false;
            try { hasEqsl = (DataAccess.GetInstance()?.GetEqslAccounts().Count ?? 0) > 0; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            if (s.UseEqslService || hasEqsl || !string.IsNullOrWhiteSpace(EqslConfirmedEntities))
                AddSourceFolder(ConfSource.Eqsl, "eQSL");
            // NO CLUB LOG FOLDER. Club Log is still used by the program - QSOs are uploaded to it, and
            // its country database is one of the two the DXCC resolver reads - but its CONFIRMATIONS are
            // not shown as a folder here any more. Only this window's folder strip is affected; the
            // upload service, the Options page and the country data are untouched, and the code behind
            // the folder (ConfSource.Clublog, the check, the marking) is left in place so it can be put
            // back by restoring this one call.
            //   if (s.UseClublogService || !string.IsNullOrWhiteSpace(ClublogConfirmedEntities))
            //       AddSourceFolder(ConfSource.Clublog, "Club Log");
            // Paper QSL is manual (no service to configure), so it is ALWAYS available.
            AddSourceFolder(ConfSource.Paper, "Paper QSL");
            // ...and last, the one that answers the question every DXer actually asks. Always shown: it
            // needs no service and no setup, and it is the only tab whose number can be compared with an
            // award standing. Placed at the end so no existing folder moves.
            // Two rows INSIDE the height the strip already has: the longest name here needs a second
            // line, and a second line is only free if the line spacing and padding are pulled in to match
            // what one 14pt line plus its padding already occupies. Done that way the tab is neither
            // taller nor wider than its neighbours.
            AddSourceFolder(ConfSource.Award, "ARRL Granted\nDXCC Award", 11);
            LB_Source.SelectedIndex = 0;   // Worked; fires LB_Source_SelectionChanged -> RefreshForSource
        }

        // fontSize overrides the strip's own size for a tab whose name is longer than the rest; 0 keeps
        // the shared size from the ListBox style. A label containing a newline becomes a two-line tab that
        // still fits the strip's existing height: the line box is tightened to the font (LineHeight) and
        // the vertical padding cut to match, so two small lines occupy what one normal line did.
        private void AddSourceFolder(ConfSource src, string label, double fontSize = 0)
        {
            var item = new ListBoxItem { Tag = src, Background = SourceBackground(src) };
            if (fontSize > 0) item.FontSize = fontSize;

            if (label.IndexOf('\n') >= 0)
            {
                item.Content = new TextBlock
                {
                    Text = label,
                    TextAlignment = TextAlignment.Center,
                    LineHeight = (fontSize > 0 ? fontSize : 14) + 2,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                };
                item.Padding = new Thickness(20, 3, 20, 3);
            }
            else
            {
                item.Content = label;
            }
            LB_Source.Items.Add(item);
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
                case ConfSource.Award:   return HexBrush("#FAD7A0");                  // award gold
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
        // Re-reads the log after a check and repaints. MEASURED as the real cause of the "spinner stops"
        // freeze: this read pulled all 28,366 QSOs of a large log out of SQLite ON THE UI THREAD, which
        // held the window for 10.8 seconds - long after the download itself had finished in under four.
        // The watchdog put the stall squarely here ("while: storing the result").
        //
        // Two things fix it. The read now happens on a background thread, and it does not happen at all
        // when the check marked nothing: re-reading a log to find it unchanged is pure cost, and the
        // commonest case of all - a check that brings back nothing - paid it in full.
        private async System.Threading.Tasks.Task ReloadQsosAfterCheck(bool reread)
        {
            // The check has just changed confirmation flags, which is exactly what the per-folder counts
            // are counting - so they are recounted, reread or not.
            InvalidateSourceStats();

            if (reread)
            {
                try
                {
                    var dal = DataAccess.GetInstance();
                    if (dal != null)
                    {
                        long logId = dal.ActiveLogId;
                        _uiPhase = "re-reading the log";
                        var fresh = await Task.Run(() => dal.GetQSOsForLog(logId));
                        // The live-QSO subscription has to follow the list, or it stays attached to the
                        // collection this window has just stopped showing and every QSO logged from here
                        // on goes unnoticed.
                        HookQsoCollection(_allQsos, fresh);
                        _allQsos = fresh;
                        _resolveCache = null;   // rebuilt lazily against the fresh list
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

            _uiPhase = "repainting the tables";
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
                InvalidateSourceStats();   // the Paper folder's confirmed set just changed
                if (_source == ConfSource.Paper) RefreshForSource();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Opens the window wide enough to SHOW the page instead of leaving part of it behind a scrollbar
        // - but never wider than the screen it is on.
        //
        // The XAML width was measured against the folder strip across the top, and only that. Anything
        // added to the page BELOW the folders - the Missing CQ / ITU Zones columns were - makes the
        // content wider than the window without changing a number anybody thought to update, and the
        // last column ends up behind a horizontal scrollbar. Asking the ScrollViewer how much it is
        // hiding (ExtentWidth - ViewportWidth) needs no measured constant, so it cannot go stale the way
        // the last one did: whatever the page holds, the window opens to it.
        //
        // IT NEVER MOVES THE WINDOW. The first version of this did, and it was a disaster: it capped the
        // width with SystemParameters.WorkArea, which is the PRIMARY screen only, so a window opened on
        // a second monitor was dragged back onto the primary one - carrying a Top that belonged to the
        // other screen, which put the title bar above the visible area. Nothing left to grab, no way to
        // drag it back, and the only way out was killing the program. Widening is a convenience;
        // stranding a window where it cannot be reached is not a trade worth making, so Left and Top are
        // not touched here under any circumstance, and growing stops at the edge of whatever room the
        // window already has.
        private void FitWidthToContent()
        {
            try
            {
                if (SV_SourceContent == null) return;

                // Measured after layout, so these are real numbers rather than the pre-layout zeros.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (WindowState != WindowState.Normal) return;   // maximised: not ours to resize

                        double hidden = 0;
                        if (SV_SourceContent != null)
                            hidden += Math.Max(0, SV_SourceContent.ExtentWidth - SV_SourceContent.ViewportWidth);
                        if (SV_LeftContent != null)
                            hidden += Math.Max(0, SV_LeftContent.ExtentWidth - SV_LeftContent.ViewportWidth);

                        if (hidden < 1) return;   // nothing is being cut off

                        // The monitor THIS window is on, not the primary one.
                        Rect work = MonitorWorkArea();

                        // Only the room that already exists to the right of the window. Growing past it
                        // would push the right-hand edge off the screen, and moving the window to make
                        // that fit is exactly what is forbidden above.
                        double room = work.Right - Left;
                        double target = Math.Min(Width + hidden, room);
                        if (target <= Width + 1) return;   // no room, or nothing to gain

                        Width = target;
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The working area of the monitor the MAIN HolyLogger window is on. Used only to choose where
        // this window first opens - this one has no handle of its own yet at that point.
        private static Rect ProgramWorkArea()
        {
            try
            {
                Window main = Application.Current != null ? Application.Current.MainWindow : null;
                if (main != null)
                {
                    IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(main).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
                        double scale;
                        using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                            scale = g.DpiX / 96.0;
                        if (scale <= 0) scale = 1.0;
                        return new Rect(wa.Left / scale, wa.Top / scale, wa.Width / scale, wa.Height / scale);
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return SystemParameters.WorkArea;
        }

        // The working area of the monitor this window is actually on, in the units Left/Width use.
        // SystemParameters.WorkArea answers for the PRIMARY screen whatever monitor you are on, which is
        // the trap that stranded the window; Screen.FromHandle answers for this one. Its rectangle is in
        // device pixels, so it is converted, or a scaled display would give a wrong edge.
        private Rect MonitorWorkArea()
        {
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
                    var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                    if (src != null && src.CompositionTarget != null)
                    {
                        var m = src.CompositionTarget.TransformFromDevice;
                        Point tl = m.Transform(new Point(wa.Left, wa.Top));
                        Point br = m.Transform(new Point(wa.Right, wa.Bottom));
                        return new Rect(tl, br);
                    }
                    return new Rect(wa.Left, wa.Top, wa.Width, wa.Height);
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return SystemParameters.WorkArea;   // single-screen answer, and never used to MOVE anything
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

            // Folders differ in width, so what fits on one can be cut off on the next.
            FitWidthToContent();

            // ...and tint the LEFT page and its two tabs to the same colour, so the whole window reads as
            // one open folder rather than two unrelated halves wearing different colours.
            ApplyLeftViewColour();

            // The header button is the INCREMENTAL check - "just what is new". Only LoTW
            // (qso_qslsince) and eQSL (RcvdSince) can actually answer that.
            //
            // NOT QRZ: it documents a MODSINCE option but rejects every form of it (see
            // QrzLogbookService), so a button there would have been slower than the full download and
            // no different in result. NOT Club Log: its date parameters filter OQRS card requests, not
            // the log export. NOT Paper QSL: nothing to download. On those three the button is hidden
            // and the full download in the frame is the only way, which is the honest offer.
            if (BTN_CheckLotw != null)
            {
                bool canCheckUpdates = _source == ConfSource.Lotw
                                    || _source == ConfSource.Qrz
                                    || _source == ConfSource.Eqsl;
                BTN_CheckLotw.Visibility = canCheckUpdates ? Visibility.Visible : Visibility.Collapsed;
                BTN_CheckLotw.Content = "Check " + SourceName + " Updates";
                BTN_CheckLotw.ToolTip = "Fetches only what is NEW since your last check — the quick, everyday update.\n\n"
                                      + "To rebuild from scratch instead, use “Get All " + SourceName + " Confirmations” below.";
            }
        }

        // Restore the last downloaded confirmed-entity set so the colors/count show immediately on open
        // without re-downloading. Stored as a '|'-joined list of DXCC entity names.
        private void LoadConfirmedCache()
        {
            _confirmedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _confirmedCodes = new HashSet<int>();
            _confirmedDeletedCodes = new HashSet<int>();

            // Computed LIVE from the log, for EVERY source - the entities of the QSOs carrying that
            // service's tick. It used to be read back from a list of country names saved at download
            // time, which went wrong in three separate ways: the list was written only by a download, so
            // a log whose marks arrived some other way showed nothing (QRZ: 431 confirmed QSOs, 91
            // countries, and a tile reading 0); it was one list shared by every log; and it froze the
            // country names as they were understood on the day of the download, so the two entities
            // whose identification depends on the QSO's date came back wrong for ever.
            //
            // The marks themselves are on the QSOs, which is the only place they belong. Reading them
            // means the tile, the table and the highlighted rows are all the same count by construction.
            if (_source == ConfSource.Worked || _allQsos == null) return;

            foreach (var q in _allQsos)
            {
                if (q == null || !IsAchievedForSource(q)) continue;
                DXCCManager.DXCC d = Resolve(q.DXCall, q.Date);
                if (d == null || !d.IsDxccEntity) continue;   // not a country - see DXCC.IsDxccEntity
                _confirmedEntities.Add(d.Name);
                int cCode = d.DxccCode > 0 ? d.DxccCode : EntityCodeOf(q);
                if (cCode != 0) _confirmedCodes.Add(cCode);

                // The deleted-entity split, from the same pass. The entity NUMBER now comes with the
                // answer and Club Log says which numbers are deleted, so this no longer needs a list of
                // codes saved by whichever download last ran.
                try
                {
                    if (d.DxccCode > 0 && DXCCManager.CountryLookup.Shared.IsDeletedEntityCode(d.DxccCode))
                        _confirmedDeletedCodes.Add(d.DxccCode);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            return;

            // Self-heal a bogus total left over from earlier broken-download testing: the confirmed-QSO
            // count can never be below the number of confirmed countries (each country has >=1 confirmed
            // QSO). If it is, drop the incremental marker so the next Check LoTW does a one-time full
            // re-download that recomputes the true total. The cached colors stay until then. LoTW-only:
            // the incremental marker belongs to the LoTW download.
            if (_source != ConfSource.Lotw) return;
            var s = Properties.Settings.Default;
            if (LotwConfirmedQsoCount < _confirmedCodes.Count && !string.IsNullOrWhiteSpace(LotwLastQsl))
            {
                LotwLastQsl = string.Empty;
                s.Save();
            }
        }

        // Green-highlight the worked rows whose entity is in the confirmed set, and update the count line.
        // Does not re-sort; the caller refreshes the list (BuildCountryTables / the button both do).
        // Count of distinct DELETED entities confirmed by the current source, from the codes that
        // source's last download stored.
        // Built alongside the confirmed set, from the log itself - see LoadConfirmedCache.
        private int CountConfirmedDeleted()
        {
            return _confirmedDeletedCodes.Count;
        }

        // The current source's display name, for the confirmed tile and status line.
        private string SourceName =>
            _source == ConfSource.Lotw    ? "LoTW" :
            _source == ConfSource.Qrz     ? "QRZ" :
            _source == ConfSource.Eqsl    ? "eQSL" :
            _source == ConfSource.Clublog ? "Club Log" :
            _source == ConfSource.Paper   ? "Paper QSL" :
            _source == ConfSource.Award   ? "ARRL DXCC Award" : "Worked";

        private void ApplyConfirmedHighlight()
        {
            if (_workedList == null) return;
            // The Worked folder has no confirmation source, so its Conf. column is blank (not all-crosses).
            bool showConf = _source != ConfSource.Worked;
            // The same achieved set the Missing list is built from - so "worked, not confirmed" is exactly
            // the worked countries that are NOT in the Missing list's achieved set, and the three tiles
            // add up to the worked total instead of contradicting it.
            HashSet<int> achieved = AchievedCodes();
            int confirmed = 0;
            foreach (var item in _workedList)
            {
                item.ShowConfirmation = showConf;
                item.IsConfirmed = item.Code != 0 && achieved.Contains(item.Code);
                if (item.IsConfirmed) confirmed++;
            }

            // How many DELETED entities are confirmed (from the DXCC codes LoTW returned, stored by the
            // download). ALWAYS shown - including "0 deleted" - because an operator needs to see the
            // figure to trust it, and a deleted-entity total is a thing many operators actively track.
            // The deleted count is not part of "of N" (that is the current worked list), so it reads as
            // a separate clause.
            int deletedConfirmed = CountConfirmedDeleted();

            // The three source tiles. Confirmed and Missing PARTITION all DXCC entities (out of the full
            // 340): Confirmed = CURRENT entities confirmed by this source, Missing = 340 - Confirmed.
            // "Worked, not confirmed" is the chaseable SUBSET of Missing - entities already contacted but
            // not yet confirmed here (worked - confirmed) - so it is not a third partition, just a
            // highlight.
            int workedDxcc = _workedList.Count;                                  // 265 - entities contacted
            int totalDxcc  = ActiveEntityCount();                                // 340 - all DXCC entities
            // The Missing tile ALWAYS reads the Missing Countries list, so tile and list can never
            // disagree (that mismatch is the bug this fixes). The list itself is source-aware.
            int missingCount = _missingList != null ? _missingList.Count : 0;

            // Confirmed CURRENT ("active") entities - the number DXCC is actually awarded on. The
            // `confirmed` counted above is every confirmed name in the worked list, and the resolver is
            // date-aware, so a 1991 QSO resolves to the DELETED entity that existed then and lands in
            // that figure too. The 340 total is cty.dat's list of entities that exist TODAY and has no
            // deleted ones in it, so printing the mixed number against it read as "330 / 340" while
            // Missing said 15 - three numbers that could not all be true at once.
            // Deriving it as 340 minus Missing makes the two tiles a real partition of the current list
            // by construction: Missing IS the current entities this source has not confirmed.
            int confirmedActive = Math.Max(0, totalDxcc - missingCount);

            // Kept to one line to fit the worked column (wrapping would misalign the four column
            // headers, which share a fixed height). "of N" is dropped because the worked-countries
            // header right above already shows that total. The two figures are disjoint - the deleted
            // ones are NOT inside the active count - so the reader can add them up.
            // On the LoTW folder ONLY: the current entities a paper card confirms and LoTW does not. The
            // ARRL accepts both, so those countries count towards DXCC just as much - but they are NOT
            // added into the LoTW figure, because that tile answers "what has LoTW confirmed" and must
            // keep answering exactly that. Shown beside it instead, so the operator can see both halves
            // of their real position without either number being quietly redefined. (The DXCC Award
            // folder is where the two are actually added together.)
            string paperNote = string.Empty;
            if (_source == ConfSource.Lotw)
            {
                // Its OWN line. The line above it already fills the worked column at this font size, so
                // anything appended is cut off mid-sentence - it read "…5 deleted,  +1" and stopped,
                // which tells the operator nothing at all.
                // The tile above counts LoTW ALONE, so the card is an ADDITION to it - not a slice of it.
                // This line used to subtract the card from the tile (325 - 1 = "324 at LoTW"), which was
                // right only while the tile counted cards too, and became a number belonging to nothing
                // the moment LoTW went back to answering for itself.
                int paperOnly = PaperOnlyEntities().Count;
                if (paperOnly > 0)
                    paperNote = $"\n+{paperOnly} by Paper QSL  →  {confirmedActive + paperOnly} for the award";
            }

            // Built from inlines rather than one string, so the DELETED count can be a link: those
            // entities are the hardest thing on this page to check, and the operator had no way to see
            // WHICH ones they are without exporting the log and reading it elsewhere.
            TB_LotwStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
            TB_LotwStatus.Inlines.Clear();
            if (_source != ConfSource.Worked && _confirmedCodes.Count > 0)
            {
                TB_LotwStatus.Inlines.Add(new System.Windows.Documents.Run(
                    $"Confirmed: {confirmedActive} active,  "));

                if (deletedConfirmed > 0)
                {
                    var link = new System.Windows.Documents.Hyperlink(
                        new System.Windows.Documents.Run($"{deletedConfirmed} deleted"))
                    {
                        ToolTip = "Click to open these QSOs in the Log Workshop",
                        Foreground = System.Windows.Media.Brushes.ForestGreen,
                    };
                    link.Click += DeletedCountries_Click;
                    TB_LotwStatus.Inlines.Add(link);
                }
                else
                {
                    TB_LotwStatus.Inlines.Add(new System.Windows.Documents.Run("0 deleted"));
                }

                if (!string.IsNullOrEmpty(paperNote))
                    TB_LotwStatus.Inlines.Add(new System.Windows.Documents.Run(paperNote));
            }
            // The tile above now counts the paper cards on the LoTW folder, so the deleted/active clause
            // alone would no longer explain it - the second line does, by splitting the tile's own number
            // into the two things it is made of.

            // The Confirmed tile only makes sense for a confirmation source; the Worked folder is the
            // plain log, so it shows just Worked / DXCC and Missing DXCC.
            if (TileConfirmed != null)
                TileConfirmed.Visibility = _source == ConfSource.Worked ? Visibility.Collapsed : Visibility.Visible;

            if (_source == ConfSource.Worked)
            {
                // Plain log folder: Worked = worked / total; Missing = never contacted. The tiles spell out
                // "Countries" (the number is a country count); the source is named by the folder tab.
                //
                // ACTIVE entities only, because the 340 it is printed over is the list of entities that
                // exist TODAY. It used to print the whole worked list, deleted entities included, so the
                // tile read "333 / 340" beside a Missing tile of 14 - and 333 + 14 = 347, which is not
                // 340. The same log's worked-active count is 326, and 326 + 14 = 340 exactly. The deleted
                // ones are not lost: they are said on their own line below, where they can be added up
                // rather than silently folded into a total they do not belong to.
                int workedActive = WorkedActiveCount();
                // "Active", like the two tiles beside it and the one on every confirmation folder. The
                // number counts only entities that still exist, and the label now says so rather than
                // leaving the reader to work out why it is not the log's whole country total.
                TB_WorkedTileLabel.Text = "Worked Active Countries";
                // Set here too, or the confirmation folders' tooltip ("...but this source has not
                // confirmed") stays attached to a tile that no longer means that after a folder switch.
                TB_WorkedTileLabel.ToolTip = "Countries that still exist as DXCC entities and appear in this log";
                TB_UniqueCountries.Text = $"{workedActive} / {totalDxcc}";
                TB_MissingTileLabel.Text = "Missing Active Countries";
                SetMissingTile(missingCount, totalDxcc);

                // The deleted entities this log has worked, stated plainly - the Worked folder had no line
                // of its own, so the difference between the two numbers had nowhere to be explained. The
                // count is a link to the list, exactly as on the confirmation folders: an operator who
                // worked a country that no longer exists is entitled to see which, and it is the one
                // figure on this page they cannot arrive at any other way.
                int workedDeleted = Math.Max(0, workedDxcc - workedActive);
                TB_LotwStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
                TB_LotwStatus.Inlines.Clear();
                TB_LotwStatus.Inlines.Add(new System.Windows.Documents.Run($"Worked: {workedActive} active"));
                if (workedDeleted > 0)
                {
                    TB_LotwStatus.Inlines.Add(new System.Windows.Documents.Run(",  "));
                    var workedDeletedLink = new System.Windows.Documents.Hyperlink(
                        new System.Windows.Documents.Run($"{workedDeleted} deleted"))
                    {
                        ToolTip = "Click to open these QSOs in the Log Workshop",
                        Foreground = System.Windows.Media.Brushes.ForestGreen,
                    };
                    workedDeletedLink.Click += DeletedCountries_Click;
                    TB_LotwStatus.Inlines.Add(workedDeletedLink);
                    TB_LotwStatus.Inlines.Add(new System.Windows.Documents.Run("  =  "));

                    // The total wears the SAME YELLOW as the grand total of the table below it -
                    // EditFieldBg, the one brush both read from. This line is the only place the page
                    // explains where 332 comes from, and the colour ties it to the 332 in the table so
                    // the eye finds the pair without being told.
                    TB_LotwStatus.Inlines.Add(new System.Windows.Documents.Run($"{workedDxcc} Total")
                    {
                        Background = ThemeManager.Brush("EditFieldBg")
                    });
                }
            }
            else
            {
                // Confirmation folder: Confirmed + Missing partition all 340. Worked-not-confirmed is
                // the chaseable subset (contacted but not confirmed here). Labels spell out "Countries";
                // the source name is dropped here because the folder tab (and status line) already show it.
                TB_ConfirmedTileLabel.Text = "Confirmed Active Countries";
                // Two lines, because the label area is a fixed 44px shared by all three tiles: the full
                // sentence needed three and the first one was cut off. The tooltip carries the long form.
                TB_WorkedTileLabel.Text = "Worked Active, not confirmed";
                TB_WorkedTileLabel.ToolTip = "Countries that still exist as DXCC entities, which you have worked but this source has not confirmed";
                TB_MissingTileLabel.Text = "Missing Active Countries";
                TB_ConfirmedDxcc.Text = $"{confirmedActive} / {totalDxcc}";
                // ACTIVE only, like the two tiles beside it. It used to be worked-minus-confirmed over
                // every entity, deleted ones included, so a deleted entity confirmed somewhere else (this
                // operator's Blenheim Reef, on eQSL) showed up here as "worked, not confirmed" while the
                // tiles either side of it were counting only the 340 that exist. Now all three answer for
                // the same 340: this one is the part of Missing that has already been contacted.
                int workedNotConfirmed = Math.Max(0, WorkedActiveCount() - confirmedActive);
                TB_UniqueCountries.Text = workedNotConfirmed.ToString();

                // Clickable, like the deleted count below: the number is an accusation ("you have worked
                // countries that are not confirmed") and the operator is entitled to see which, and to
                // see what DOES confirm each of them.
                TB_UniqueCountries.MouseLeftButtonUp -= WorkedNotConfirmedTile_MouseUp;
                bool clickable = workedNotConfirmed > 0;
                if (clickable) TB_UniqueCountries.MouseLeftButtonUp += WorkedNotConfirmedTile_MouseUp;
                TB_UniqueCountries.Cursor = clickable ? System.Windows.Input.Cursors.Hand : null;
                TB_UniqueCountries.TextDecorations = clickable ? System.Windows.TextDecorations.Underline : null;
                TB_UniqueCountries.ToolTip = clickable ? "Click to see which countries, and what confirms them" : null;
                SetMissingTile(missingCount, totalDxcc);
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

            // THE BUTTON WEARS ITS FOLDER'S COLOUR. Four buttons sit on top of each other in this one
            // slot and only the open folder's is shown, so a button that looks the same on every folder
            // gives no clue which service it is about to go and ask. The tint is the folder's own -
            // SourceBackground, the same brush the tab and the page behind it use - so the button reads
            // as part of the folder rather than as a grey control that happens to be there.
            Brush folderTint = SourceBackground(_source);
            foreach (var b in new[] { BTN_GetAllConfirmations, BTN_CheckQrz, BTN_CheckEqsl, BTN_CheckClublog })
                if (b != null && b.Visibility == Visibility.Visible) b.Background = folderTint;

            // Worked and Paper QSL have no downloaded summary (Paper is manual). Keep the frame's SPACE
            // (Hidden) so the zone lists still line up with the download folders.
            // Worked, Paper QSL and DXCC Award have no download of their own (Paper is manual, and Award is
            // computed from the other sources), so there is no summary to show. Keep the frame's SPACE
            // (Hidden) so the zone lists still line up with the download folders.
            if (_source == ConfSource.Worked || _source == ConfSource.Paper || _source == ConfSource.Award)
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
                // ...and never show a number this log has not earned. A log nobody has checked yet has no
                // stored count at all, and must say so - a figure borrowed from another log is worse than
                // no figure, because it looks like an answer.
                bool totalKnown = HasBeenChecked(ConfSource.Lotw)
                                  && LotwConfirmedQsoCount > 0
                                  && LotwConfirmedQsoCount >= _confirmedCodes.Count;
                TB_SumTotalQsls.Text = totalKnown ? LotwConfirmedQsoCount.ToString("N0", inv)
                                     : HasBeenChecked(ConfSource.Lotw) ? "—" : "not checked yet";

                int matched = 0;
                try { if (dal != null) matched = dal.GetLotwConfirmedCount(dal.ActiveLogId); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                TB_SumMatchedInLog.Text = matched.ToString("N0", inv);

                bool fullDownload = string.IsNullOrWhiteSpace(LotwLastCheckSince);
                TB_SumNewQsls.Text = fullDownload ? "—" : LotwLastNewQsls.ToString(inv);
                TB_SumSince.Text = fullDownload ? "   (full download)" : $"   (since {LotwLastCheckSince})";
                bool hasNew = !fullDownload && LotwLastNewQsls > 0 && !string.IsNullOrWhiteSpace(LotwLastNewJson);
                StyleSummaryLink(LNK_NewQsls, hasNew, muted);

                TB_SumNewCountries.Text = fullDownload ? "—" : LotwLastNewCountries.ToString(inv);
                bool hasNewCountry = !fullDownload && LotwLastNewCountries > 0 && !string.IsNullOrWhiteSpace(LotwLastNewJson);
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
                    if (_source == ConfSource.Qrz)          total = QrzConfirmedQsoCount;
                    else if (_source == ConfSource.Clublog) total = ClublogConfirmedQsoCount;
                    else                                    total = EqslConfirmedQsoCount;

                    if (dal != null)
                    {
                        if (_source == ConfSource.Qrz)          matched = dal.GetQrzConfirmedCount(dal.ActiveLogId);
                        else if (_source == ConfSource.Clublog) matched = dal.GetClublogConfirmedCount(dal.ActiveLogId);
                        else                                    matched = dal.GetEqslConfirmedCount(dal.ActiveLogId);
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                TB_SumConfirmedLabel.Text = $"Confirmed on {SourceName}";
                TB_SumTotalQsls.Text = HasBeenChecked(_source) ? total.ToString("N0", inv) : "not checked yet";
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

        // ── per-log confirmation memory ────────────────────────────────────
        //
        // These stand in for what used to be application settings shared by every log. Same names, same
        // call sites, but each value now belongs to the log it was measured on. That is what stops a log
        // created five minutes ago from announcing 5,936 confirmations at LoTW.
        //
        // An unset value reads as empty / 0, and HasBeenChecked below tells the difference between "this
        // log has nothing confirmed" and "nobody has ever asked" - two very different statements.
        private long StateLogId
        {
            get { try { return DataAccess.GetInstance()?.ActiveLogId ?? 0; } catch { return 0; } }
        }

        private string LogState(string key)
        {
            try { return DataAccess.GetInstance()?.GetLogState(StateLogId, key) ?? string.Empty; }
            catch (Exception swallowed) { Log.Swallow(swallowed); return string.Empty; }
        }

        private void LogState(string key, string value)
        {
            try { DataAccess.GetInstance()?.SetLogState(StateLogId, key, value ?? string.Empty); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private int LogStateInt(string key)
        {
            int n;
            return int.TryParse(LogState(key), out n) ? n : 0;
        }

        // Has this log ever been checked against the given source? The confirmed-QSO count is written on
        // every completed download - even a download that found nothing - so its presence is the marker.
        private bool HasBeenChecked(ConfSource src)
        {
            switch (src)
            {
                case ConfSource.Lotw: return LogState("LotwConfirmedQsoCount").Length > 0;
                case ConfSource.Qrz: return LogState("QrzConfirmedQsoCount").Length > 0;
                case ConfSource.Eqsl: return LogState("EqslConfirmedQsoCount").Length > 0;
                case ConfSource.Clublog: return LogState("ClublogConfirmedQsoCount").Length > 0;
                default: return true;                 // Worked and Paper QSL are read from the log itself
            }
        }

        private string LotwConfirmedEntities { get { return LogState("LotwConfirmedEntities"); } set { LogState("LotwConfirmedEntities", value); } }
        private string LotwConfirmedDeletedCodes { get { return LogState("LotwConfirmedDeletedCodes"); } set { LogState("LotwConfirmedDeletedCodes", value); } }
        private int LotwConfirmedQsoCount { get { return LogStateInt("LotwConfirmedQsoCount"); } set { LogState("LotwConfirmedQsoCount", value.ToString()); } }
        private string LotwLastQsl { get { return LogState("LotwLastQsl"); } set { LogState("LotwLastQsl", value); } }
        private string LotwLastCheckSince { get { return LogState("LotwLastCheckSince"); } set { LogState("LotwLastCheckSince", value); } }
        private string LotwLastNewJson { get { return LogState("LotwLastNewJson"); } set { LogState("LotwLastNewJson", value); } }
        private int LotwLastNewQsls { get { return LogStateInt("LotwLastNewQsls"); } set { LogState("LotwLastNewQsls", value.ToString()); } }
        private int LotwLastNewCountries { get { return LogStateInt("LotwLastNewCountries"); } set { LogState("LotwLastNewCountries", value.ToString()); } }
        private string LotwSeenKeysJson { get { return LogState("LotwSeenKeysJson"); } set { LogState("LotwSeenKeysJson", value); } }

        private string QrzConfirmedEntities { get { return LogState("QrzConfirmedEntities"); } set { LogState("QrzConfirmedEntities", value); } }
        private string QrzConfirmedDeletedCodes { get { return LogState("QrzConfirmedDeletedCodes"); } set { LogState("QrzConfirmedDeletedCodes", value); } }
        private int QrzConfirmedQsoCount { get { return LogStateInt("QrzConfirmedQsoCount"); } set { LogState("QrzConfirmedQsoCount", value.ToString()); } }

        private string EqslConfirmedEntities { get { return LogState("EqslConfirmedEntities"); } set { LogState("EqslConfirmedEntities", value); } }
        private string EqslConfirmedDeletedCodes { get { return LogState("EqslConfirmedDeletedCodes"); } set { LogState("EqslConfirmedDeletedCodes", value); } }
        private int EqslConfirmedQsoCount { get { return LogStateInt("EqslConfirmedQsoCount"); } set { LogState("EqslConfirmedQsoCount", value.ToString()); } }

        private string ClublogConfirmedEntities { get { return LogState("ClublogConfirmedEntities"); } set { LogState("ClublogConfirmedEntities", value); } }
        private string ClublogConfirmedDeletedCodes { get { return LogState("ClublogConfirmedDeletedCodes"); } set { LogState("ClublogConfirmedDeletedCodes", value); } }
        private int ClublogConfirmedQsoCount { get { return LogStateInt("ClublogConfirmedQsoCount"); } set { LogState("ClublogConfirmedQsoCount", value.ToString()); } }

        // How many QSOs in the open log currently carry that service's tick. Read from the database, so
        // it is the truth at this instant - taken either side of a marking pass, the difference is
        // exactly how many QSOs the pass newly confirmed.
        private int ConfirmedInLog(ConfSource src)
        {
            try
            {
                var dal = DataAccess.GetInstance();
                if (dal == null) return 0;
                switch (src)
                {
                    case ConfSource.Lotw: return dal.GetLotwConfirmedCount(dal.ActiveLogId);
                    case ConfSource.Qrz: return dal.GetQrzConfirmedCount(dal.ActiveLogId);
                    case ConfSource.Eqsl: return dal.GetEqslConfirmedCount(dal.ActiveLogId);
                    case ConfSource.Clublog: return dal.GetClublogConfirmedCount(dal.ActiveLogId);
                    default: return 0;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return 0; }
        }

        // The callsign the open log belongs to, or "" when it has no identity set.
        private string ActiveLogCallsign()
        {
            try
            {
                var dal = DataAccess.GetInstance();
                if (dal == null) return string.Empty;
                string call;
                dal.GetLogIdentity(dal.ActiveLogId, out call, out _);
                return (call ?? string.Empty).Trim();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return string.Empty; }
        }

        // ---- "is this log even mine?" ----------------------------------------------------------------
        //
        // No confirmation service will hand over another station's QSLs: each one answers only for the
        // account that uploaded the QSOs. Downloading into a log that belongs to someone else - an ADIF a
        // friend sent - therefore cannot work, and it used to be found out the slow way or not at all:
        // LoTW spent a long minute downloading nothing and said so only afterwards, while QRZ never
        // noticed, announcing a successful download of confirmations that could not match a single QSO in
        // front of the operator. This says it before one request is made.
        //
        // The callsigns this installation can show the operator set up for themselves: TQSL certificates
        // (LoTW), eQSL accounts, and the LoTW website login when it is a callsign at all. Deliberately NOT
        // my_callsign or the station-callsign box - opening someone else's log sets those to THEIR
        // callsign, which is the very case this exists to catch.
        private List<string> MyOwnCallsigns()
        {
            var calls = new List<string>();
            try
            {
                foreach (var loc in TqslStationData.Read())
                    AddCallsign(calls, loc?.Call);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            try
            {
                var accounts = Dal != null ? Dal.GetEqslAccounts() : null;
                if (accounts != null)
                    foreach (var a in accounts) AddCallsign(calls, a?.Callsign);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // Service logins are usually the operator's own callsign, but none of them has to be, so each
            // counts only when it is shaped like one - otherwise it would be listed back to the operator
            // as a callsign they own, which it is not. The more of these are set, the less often an
            // operator whose everyday call has no TQSL certificate is asked about their own log.
            var s = Properties.Settings.Default;
            foreach (string login in new[] { s.LotwWebUser, s.EqslUsername, s.qrz_username })
            {
                string v = (login ?? string.Empty).Trim();
                if (CallsignIdentity.LooksLikeCallsign(v)) AddCallsign(calls, v);
            }

            return calls;
        }

        private static void AddCallsign(List<string> into, string call)
        {
            string c = (call ?? string.Empty).Trim();
            if (c.Length == 0) return;
            if (!into.Any(x => CallsignIdentity.Same(x, c))) into.Add(c);
        }

        // "Try anyway" is remembered for as long as this window is open, so a deliberate override is not
        // re-asked on the next button. It is not persisted: the answer belongs to this sitting.
        private bool _otherStationAccepted;

        // True when the caller must STOP. Returns instantly - it asks nothing of the network.
        //
        // It only speaks when it is sure enough to be useful: a log with no identity, or an installation
        // with no callsign set up anywhere, teaches us nothing and is waved through. And the answer is a
        // question, never a lock: one account can hold certificates for a club or contest call that is
        // configured nowhere on this machine, and that operator must still be able to press the button.
        private bool BlockedAsAnotherStationsLog(string serviceName)
        {
            if (_otherStationAccepted) return false;

            string logCall = ActiveLogCallsign();
            if (string.IsNullOrWhiteSpace(logCall)) return false;   // no identity -> nothing to compare

            var mine = MyOwnCallsigns();
            if (mine.Count == 0) return false;                      // nothing set up -> we know nothing

            // The LoTW login is compared even when it is not callsign-shaped: it costs nothing and covers
            // the operator whose account name simply is not a callsign we recognise.
            string web = (Properties.Settings.Default.LotwWebUser ?? string.Empty).Trim();
            if (mine.Any(c => CallsignIdentity.Same(c, logCall)) || CallsignIdentity.Same(web, logCall))
                return false;

            bool goAhead = HolyMessageBox.ShowConfirm(
                $"This log belongs to {logCall}, which is not one of your callsigns.\n\n" +
                $"Set up on this computer: {string.Join(", ", mine)}.\n\n" +
                $"{serviceName} sends confirmations only to the account that uploaded the QSOs, so asking " +
                $"for {logCall} would bring back nothing. Only {logCall} can download them.\n\n" +
                "Ask anyway?",
                serviceName + " — this log is another station's", HolyMsgType.Warning, this);

            if (goAhead) _otherStationAccepted = true;
            return !goAhead;
        }

        // Every station callsign this log actually contains - the log's own identity plus each stroke
        // variant present in its QSOs. One LoTW request is made per entry, because qso_owncall matches
        // the callsign as it was UPLOADED: asking for 4Z5SL alone would leave whatever was signed
        // 4Z5SL/6 undownloaded, and those are the same station.
        //
        // Returns a single empty string when the log has no QSOs and no identity, which asks LoTW for
        // everything exactly as before - the old behaviour, kept for the case where we know nothing.
        private List<string> OwnCallsForActiveLog()
        {
            var calls = new List<string>();
            try
            {
                var dal = DataAccess.GetInstance();
                if (dal != null)
                {
                    string logCall;
                    dal.GetLogIdentity(dal.ActiveLogId, out logCall, out _);
                    if (!string.IsNullOrWhiteSpace(logCall)) calls.Add(logCall.Trim());

                    if (_allQsos != null)
                        foreach (var q in _allQsos)
                        {
                            string c = (q?.MyCall ?? string.Empty).Trim();
                            if (c.Length == 0) continue;
                            if (!calls.Any(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase)))
                                calls.Add(c);
                        }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (calls.Count == 0) calls.Add(string.Empty);   // nothing known: ask for the whole account
            return calls;
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
                var json = LotwLastNewJson;
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
                var json = LotwLastNewJson;
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
                FontSize = 16
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

        // LoTW HAS THEM, THIS LOG DOES NOT. A confirmation whose callsign appears nowhere in the log is
        // a contact the log has lost, and LoTW kept enough of it to put it back. Asked as a question,
        // because adding QSOs to somebody's log is not a thing to do quietly, and skipped entirely when
        // there is nothing to add.
        private async System.Threading.Tasks.Task OfferLotwRestore(List<DataAccess.LotwConfirmation> missing)
        {
            try
            {
                if (missing == null || missing.Count == 0) return;

                string n = "**" + missing.Count.ToString("N0") + "**";
                bool review = HolyMessageBox.ShowConfirm(
                    "LoTW has " + n + (missing.Count == 1 ? " confirmed contact" : " confirmed contacts")
                    + " whose callsign is not in this log at all.\n\n"
                    + "These are contacts the log does not hold — LoTW can put them back, with the date, "
                    + "time, band, mode, entity and square it keeps.\n\n"
                    + "Look at them now?",
                    "Contacts missing from this log", HolyMsgType.Info, this);
                if (!review) return;

                var win = new LotwRestoreWindow(missing) { Owner = this };
                win.ShowDialog();

                if (win.Added > 0)
                {
                    // THE LOG IS NOW BIGGER THAN EVERY COUNT ON SCREEN. Adding QSOs changes more than the
                    // grid: the figures along the foot of the main window, this window's own list of the
                    // log, its per-folder counts, its countries and its zones are all made FROM the QSOs.
                    // Restoring 147 contacts and leaving those reading the old numbers is the program
                    // disagreeing with itself, so everything that counts is made to count again.
                    try { (Application.Current.Windows.OfType<MainWindow>().FirstOrDefault())?.ReloadActiveLogQsos(); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }

                    await ReloadQsosAfterCheck(true);
                    try { RefreshForSource(); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // THE OTHER PILE: the station IS in the log, but LoTW's band, mode or date does not agree with
        // what the log holds. Nothing here is missing, so nothing is added; it is a question about
        // contacts that already exist, and it is asked in the Log Fixer - the table that exists for
        // exactly this shape of answer, the log in red on top and what is proposed in green underneath.
        private void OfferLotwDifferences(List<DataAccess.LotwConfirmation> nearMisses)
        {
            try
            {
                if (nearMisses == null || nearMisses.Count == 0) return;

                // **…** is bold in HolyMessageBox: the count is the fact the operator is deciding on,
                // and it should not have to be picked out of a paragraph.
                string n = "**" + nearMisses.Count.ToString("N0") + "**";
                bool review = HolyMessageBox.ShowConfirm(
                    "LoTW has " + n + (nearMisses.Count == 1 ? " confirmation" : " confirmations")
                    + " for stations that ARE in this log, but the band, the mode or the date does not "
                    + "match what LoTW was sent.\n\n"
                    + "Nothing is missing here — these are contacts you already have, with one detail "
                    + "that the two of you recorded differently.\n\n"
                    + "Look at them in the Log Fixer?",
                    "Where LoTW disagrees with this log", HolyMsgType.Info, this);
                if (!review) return;

                int rows = LogVerifierWindow.ShowLotwDifferences(this, _allQsos, nearMisses);
                if (rows == 0)
                    HolyMessageBox.Show(
                        "Nothing to show: every one of them agrees with the log on band, mode and date. "
                        + "They differ only in which of your callsigns was used, which is not something "
                        + "to change from here.",
                        "Where LoTW disagrees with this log", HolyMsgType.Info, this);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Writes the confirmations that matched no QSO, each followed by what the log holds for that
        // same callsign. Desktop file, same place as the other diagnostics.
        private static void WriteUnmatchedReport(int total, int matched, List<DataAccess.LotwConfirmation> unmatched,
                                                 int otherStations = 0)
        {
            string path = System.IO.Path.Combine(
                DataAccess.ReportsFolder,
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
                Reports.Note(path);
        }

        // Downloads the complete confirmation history and marks every confirmed QSO.
        //
        // The ordinary check is incremental - it asks for confirmations since the last one - so it can
        // never fill in the years already confirmed before this feature existed. This runs the very same
        // code with the "since" date reset to the beginning, so there is one download path, not two.
        private void BTN_GetAllConfirmations_Click(object sender, RoutedEventArgs e)
        {
            // Asked here as well as in the check itself, so the ownership question comes FIRST - before
            // the operator is asked to approve a long download that could never return anything.
            if (BlockedAsAnotherStationsLog("LoTW")) return;

            // How many QSOs of THIS log are already marked. It used to count every marked QSO in the
            // database, across all logs - a figure that belongs to no log the operator is looking at, and
            // that reads as a promise about the log in front of them.
            int already = 0;
            try
            {
                var d = DataAccess.GetInstance();
                if (d != null) already = d.GetLotwConfirmedCount(d.ActiveLogId);
            }
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

        // The header button serves the two folders that can answer "just what is new", so it routes to
        // whichever is open. eQSL runs the same download as its full button, only asked for recent
        // cards and marking WITHOUT a reset - a partial answer must only ever add.
        private async void BTN_CheckLotw_Click(object sender, RoutedEventArgs e)
        {
            if (_source == ConfSource.Qrz) { await RunQrzCheck(incremental: true); return; }
            if (_source == ConfSource.Eqsl) { await RunEqslCheck(incremental: true); return; }

            // Before anything else: LoTW cannot answer for a log that is not this operator's, and finding
            // that out took a full download that came back empty.
            if (BlockedAsAnotherStationsLog("LoTW")) return;

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
                               && _confirmedCodes.Count > 0
                               && !string.IsNullOrWhiteSpace(LotwLastQsl)
                               && !string.IsNullOrWhiteSpace(LotwSeenKeysJson);
            _forceFullDownload = false;   // one-shot: only the click that set it gets the full run
            string sinceQuery = incremental ? MarkerDate(LotwLastQsl) : "1970-01-01";
            string sinceDisplay = incremental ? PrettySince(LotwLastQsl) : string.Empty;

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
                // qso_owncall scopes the report to ONE of your station callsigns, which is what stops a
                // 1,771-QSO log downloading the whole account's 5,936 confirmations. It is asked for the
                // log's own callsign - and separately for each stroke variant the log actually contains,
                // because LoTW matches this parameter as the callsign was UPLOADED: ask for 4Z5SL and
                // whatever was signed 4Z5SL/6 does not come back. Enumerating the variants FROM THE LOG
                // means we ask for exactly what this log could hold and nothing else.
                List<string> ownCalls = OwnCallsForActiveLog();
                string adifAll = string.Empty;
                int eorSoFar = 0;
                int callIndex = 0;

                // ONE client for every callsign, built once. A fresh HttpClientHandler per request pays
                // the connection and proxy-detection cost again each time, for nothing.
                using (var handler = new System.Net.Http.HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
                })
                using (var http = new System.Net.Http.HttpClient(handler))
                {
                http.Timeout = TimeSpan.FromSeconds(300);   // large accounts can take a while server-side

                foreach (string own in ownCalls)
                {
                callIndex++;

                // Said BEFORE the request goes out, not only when bytes come back. The old text was
                // written solely inside the read loop, so through the whole wait for LoTW to answer -
                // the longest part, and the entire operation when the answer turns out to be empty - the
                // overlay kept showing the PREVIOUS callsign's "Downloaded 0 confirmations", which reads
                // exactly like a program that has stopped.
                _uiPhase = $"asking LoTW about {own} ({callIndex} of {ownCalls.Count})";
                TB_LotwLoadingText.Text = ownCalls.Count > 1 && !string.IsNullOrEmpty(own)
                    ? $"Asking LoTW about {own}…   ({callIndex} of {ownCalls.Count})"
                    : "Asking LoTW…";
                TB_LotwLoadingSub.Text = "Waiting for LoTW to answer. It builds the report before sending it, "
                                       + "so nothing arrives for a while.";

                string url = "https://lotw.arrl.org/lotwuser/lotwreport.adi"
                           + "?login=" + Uri.EscapeDataString(user)
                           + "&password=" + Uri.EscapeDataString(pass)
                           + (string.IsNullOrEmpty(own) ? "" : "&qso_owncall=" + Uri.EscapeDataString(own))
                           + "&qso_query=1&qso_qsl=yes&qso_mydetail=yes&qso_qsldetail=yes&qso_qslsince=" + Uri.EscapeDataString(sinceQuery);

                string adifOne;
                {
                    // NOTHING of the request may touch the UI thread. This is .NET Framework 4.8, where
                    // HttpClient sits on HttpWebRequest and resolves the proxy (App.config turns the
                    // system proxy on) and the DNS name SYNCHRONOUSLY on whichever thread calls it,
                    // before any of it goes async. Called from the UI thread that froze the window solid
                    // for the whole wait - the spinner stopped turning and the elapsed clock stopped
                    // counting, which is precisely how a working program comes to look hung.
                    //
                    // So the request AND the reading run on a background thread, and the only thing that
                    // crosses back is text, through Progress<T> - which marshals each report onto the UI
                    // thread by itself. Same treatment the matching phase below already had, for the same
                    // reason.
                    //
                    // Stream the reply rather than take it whole, and count <eor> records as they arrive,
                    // so the overlay shows a real "Downloaded N confirmations…" climbing instead of a
                    // spinner. ResponseHeadersRead hands us the body stream before it has all arrived; the
                    // AutomaticDecompression above means the stream we read is already un-gzipped.
                    int soFar = eorSoFar;
                    string thisCall = own;
                    var report = new Progress<(string main, string sub)>(t =>
                    {
                        TB_LotwLoadingText.Text = t.main;
                        TB_LotwLoadingSub.Text = t.sub;
                    });
                    var reporter = (IProgress<(string main, string sub)>)report;

                    var got = await Task.Run(async () =>
                    {
                        var sb = new System.Text.StringBuilder();
                        int eor = 0;
                        // Progress<T> posts to the UI thread at Normal priority, which OUTRANKS Render.
                        // Reporting on every 16 KB chunk of a large report therefore floods the dispatcher
                        // with work that keeps jumping the queue ahead of drawing - the text updates while
                        // the spinner stops, which is the other way a working download looks stuck. Five
                        // updates a second is plenty for a human to read.
                        var lastReport = System.Diagnostics.Stopwatch.StartNew();
                        using (var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                        using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
                        {
                            char[] buf = new char[16384];
                            string carry = string.Empty;   // last 4 chars, to catch an <eor> split across reads
                            int n;
                            while ((n = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
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

                                if (lastReport.ElapsedMilliseconds >= 200)
                                {
                                    lastReport.Restart();
                                    _uiPhase = $"reading LoTW's reply for {thisCall} ({soFar + eor} records so far)";
                                    reporter.Report((
                                        ownCalls.Count > 1
                                            ? $"Downloaded {soFar + eor:N0} confirmations from LoTW… ({thisCall})"
                                            : $"Downloaded {eor:N0} confirmations from LoTW…",
                                        "Reading the confirmations LoTW is sending back."));
                                }
                            }
                        }
                        // The throttle can swallow the last chunk's update, so the final figure is always
                        // reported - the number left on screen must be the one that actually arrived.
                        reporter.Report((
                            ownCalls.Count > 1
                                ? $"Downloaded {soFar + eor:N0} confirmations from LoTW… ({thisCall})"
                                : $"Downloaded {eor:N0} confirmations from LoTW…",
                            "Reading the confirmations LoTW is sending back."));
                        return (body: sb.ToString(), eor: eor);
                    }, ct);

                    adifOne = got.body;
                    eorSoFar += got.eor;
                }

                if (adifOne.IndexOf("Invalid password", StringComparison.OrdinalIgnoreCase) >= 0
                    || adifOne.IndexOf("login incorrect", StringComparison.OrdinalIgnoreCase) >= 0
                    || adifOne.IndexOf("<Error>", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    TB_LotwStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                    TB_LotwStatus.Text = "LoTW rejected the login — check your username and password.";
                    return;
                }

                // Sanity-check the payload really is the ADIF report (not an error/login web page, and
                // not unreadable compressed bytes). Catches auth failures whose wording we didn't match.
                bool looksAdif = adifOne.IndexOf("<eoh>", StringComparison.OrdinalIgnoreCase) >= 0
                              || adifOne.IndexOf("<eor>", StringComparison.OrdinalIgnoreCase) >= 0
                              || adifOne.IndexOf("<call:", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!looksAdif)
                {
                    bool looksHtml = adifOne.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0
                                  || adifOne.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase) >= 0;
                    TB_LotwStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                    TB_LotwStatus.Text = looksHtml
                        ? "LoTW returned a web page, not data — the login was likely rejected. Check your username and password."
                        : $"LoTW returned no usable data ({adifOne.Length} chars). Check your LoTW login and try again.";
                    return;
                }

                adifAll += adifOne;
                }   // next station callsign of this log
                }   // the one HttpClient serving every callsign

                string adif = adifAll;

                // Everything from here - splitting the reply into records, resolving each callsign, and
                // marking the log - is CPU/DB work that used to run on the UI thread, freezing the window
                // for over a minute on a full download (the debugger raised ContextSwitchDeadlock). It now
                // runs on a background thread, reporting a running count so the operator sees a number
                // climb instead of a stalled spinner. The DXCC resolver is read-only after load, so
                // concurrent lookups are safe.
                int eohIdx = adif.IndexOf("<eoh>", StringComparison.OrdinalIgnoreCase);
                string recordsBody = eohIdx >= 0 ? adif.Substring(eohIdx + 5) : adif;

                string boundaryDate = incremental ? MarkerDate(LotwLastQsl) : string.Empty;
                var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (incremental && !string.IsNullOrWhiteSpace(LotwSeenKeysJson))
                {
                    try
                    {
                        foreach (var k in JsonConvert.DeserializeObject<List<string>>(LotwSeenKeysJson) ?? new List<string>())
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

                _uiPhase = "matching the reply to the log";
                LotwRunResult result = await Task.Run(() =>
                    ProcessLotwConfirmations(recordsBody, incremental, boundaryDate, seenKeys, confirmedSnapshot, progress, ct));
                _uiPhase = "storing the result";

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

                // A full download that came back with NOTHING AT ALL is the absence of an answer, not an
                // answer of zero, and what this log already knows must survive it. Overwriting here turned
                // "Confirmed on LoTW: 3,672" into 0 the moment a download was aimed at the wrong account -
                // a real figure replaced by a worse one on the strength of a reply that said nothing.
                // Confirmations only ever accumulate at LoTW; they are never taken away. So an empty full
                // reply means we asked the wrong question far more often than it means the QSLs are gone.
                //
                // Only when something IS stored: a first-ever download that finds nothing writes its zero
                // as before, so a genuinely empty account still reads "checked, none" rather than
                // "not checked yet".
                bool nothingCameBack = !incremental && qslCount == 0;
                bool keepWhatIsStored = nothingCameBack
                                        && (LotwConfirmedQsoCount > 0
                                            || !string.IsNullOrWhiteSpace(LotwConfirmedEntities));

                if (!keepWhatIsStored)
                {
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
                    LotwConfirmedEntities = string.Join("|", _confirmedEntities);
                    LotwConfirmedQsoCount = incremental ? LotwConfirmedQsoCount + newCount : qslCount;
                    LotwLastNewQsls = incremental ? newCount : qslCount;
                    LotwLastNewCountries = newCountries;
                    LotwLastCheckSince = sinceDisplay;   // empty = a full download
                    // The list of new confirmations, for the viewer. Cleared on a full download (no delta).
                    LotwLastNewJson = newList != null ? JsonConvert.SerializeObject(newList) : string.Empty;
                    if (!string.IsNullOrWhiteSpace(maxRxDate)) LotwLastQsl = maxRxDate;   // stored as a date
                    LotwSeenKeysJson = JsonConvert.SerializeObject(newSeenKeys);

                    // Confirmed DELETED entities, by DXCC code. Full run replaces the set; an incremental
                    // run adds to it (a deleted entity newly confirmed since last check). Stored as codes so
                    // re-confirming the same entity never inflates the count.
                    var deletedCodes = new HashSet<int>(result.ConfirmedDeletedCodes);
                    if (incremental && !string.IsNullOrWhiteSpace(LotwConfirmedDeletedCodes))
                        foreach (var part in LotwConfirmedDeletedCodes.Split(','))
                            if (int.TryParse(part.Trim(), out int old)) deletedCodes.Add(old);
                    LotwConfirmedDeletedCodes = string.Join(",", deletedCodes);
                    s.Save();
                }

                // Re-read the log so QSO confirmation flags are live (the zone lists read them), then
                // repaint the folder - RefreshForSource rebuilds the Missing lists, sets the tiles/status,
                // colors the rows, and fills the 3-row summary. No manual reopen needed. Only worth
                // re-reading when a flag actually changed.
                await ReloadQsosAfterCheck(markedConfirmed > 0);

                // THE PROGRESS PANEL COMES DOWN BEFORE ANYTHING IS SAID. The work is over by this line;
                // leaving it to the finally at the foot of the method meant the summary opened ON TOP of
                // a frozen last frame - "Matching to your log 2,800 of 2,929", a clock still reading 4:08
                // and a Stop button - so a finished check looked like one still running behind a dialog
                // that claimed it had finished. The finally still calls this; it does no harm twice.
                ShowLotwSpinner(false);

                // EVERY check ends with a summary, incremental included. It used to be left off the
                // quick check to avoid nagging, which meant pressing the button and getting no answer
                // at all - and it read as a fault beside eQSL and QRZ, which do report. Consistency
                // wins: press a button, get told what happened.
                // On a quick check report the NEW count, not the raw download. LoTW's qso_qslsince
                // honours the DATE only, so every check re-sends whatever arrived on the boundary date
                // - typically the last confirmation, over and over. It is recognised as already seen
                // and changes nothing, but saying "downloaded 1" made it look like something had
                // arrived each time. What matters is how many were new, which is 0.
                ShowCheckSummary(ConfSource.Lotw, incremental ? newCount : qslCount, incremental,
                                 anythingChanged: !incremental || newCount > 0);

                // AND THEN: LoTW may be holding contacts this log does not have at all. Offered after the
                // summary, never instead of it, and only when there is something to offer - the operator
                // is told what the check did before being asked anything.
                await OfferLotwRestore(result.MissingFromLog);
                OfferLotwDifferences(result.NearMisses);
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
            await RunQrzCheck(incremental: false);
        }

        // incremental=true adds MODSINCE, asking only for what changed since the last check.
        private async System.Threading.Tasks.Task RunQrzCheck(bool incremental)
        {
            // QRZ used to miss this case completely: the key's own logbook downloaded happily, every
            // confirmation was then discarded for belonging to another callsign, and the operator was told
            // the check had succeeded.
            if (BlockedAsAnotherStationsLog("QRZ.com")) return;

            string since = incremental ? LogState("QrzLastCheck") : string.Empty;
            if (string.IsNullOrWhiteSpace(since)) incremental = false;

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
            TB_LotwLoadingText.Text = incremental ? "Checking QRZ for what is new…" : "Downloading confirmations from QRZ…";
            TB_LotwLoadingSub.Text = "Reading your confirmed QSOs from QRZ.com.";
            ShowLotwSpinner(true);
            string stampedAt = DateTime.UtcNow.ToString("yyyy-MM-dd");
            int confirmedBefore = ConfirmedInLog(ConfSource.Qrz);
            try
            {
                // Off the UI thread - see the note in the LoTW download. On .NET Framework the request
                // resolves proxy and DNS synchronously on the calling thread, which freezes the window
                // (spinner included) until the server answers.
                QrzLogbookService.QrzFetchResult fetch = await Task.Run(
                    () => QrzLogbookService.FetchConfirmationsAsync(key, incremental ? since : null, ct), ct);

                // A quick check that QRZ genuinely refuses falls back to the full download, so the
                // operator gets their confirmations either way. An EMPTY answer is not a refusal and
                // does not come through here - see the note in QrzLogbookService about RESULT=FAIL
                // with COUNT=0, which simply means nothing has changed.
                if (incremental && !fetch.Ok && !fetch.NetworkError)
                {
                    incremental = false;
                    TB_LotwLoadingText.Text = "QRZ would not take the quick check — downloading everything…";
                    fetch = await Task.Run(() => QrzLogbookService.FetchConfirmationsAsync(key, null, ct), ct);
                }

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

                // QRZ answered in full, and not one of its confirmations was logged under this log's
                // callsign. That is proof, not a guess: the logbook this API key opens belongs to another
                // station. Said plainly, and nothing is written - clearing the existing marks here would
                // wipe a set this key was never entitled to rebuild.
                if (confirmations.Count == 0 && otherStations > 0)
                {
                    string qrzLogCall = ActiveLogCallsign();
                    if (string.IsNullOrWhiteSpace(qrzLogCall)) qrzLogCall = "this log's callsign";
                    ShowLotwSpinner(false);
                    HolyMessageBox.Show(
                        $"Your QRZ logbook holds {otherStations:N0} confirmation(s), and every one of them is " +
                        $"for a different callsign — none for {qrzLogCall}.\n\n" +
                        "A QRZ API key opens one logbook, and that logbook belongs to whoever it was issued " +
                        $"to. Only {qrzLogCall} can download these.\n\nNothing in your log was changed.",
                        "QRZ confirmations", HolyMsgType.Error, this);
                    return;
                }

                // fullReset only on the authoritative rebuild. An incremental answer holds just what
                // changed, so clearing first would throw away every mark it does not happen to mention.
                int marked = await Task.Run(() =>
                    Dal.MarkQrzConfirmed(confirmations, !incremental, ((IProgress<int>)markProgress).Report, ct, out unmatched));

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
                    string name = EntityNameFor(c.Call, c.QsoDate);
                    if (!string.IsNullOrEmpty(name) && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                        qrzNames.Add(name);
                    if (DXCCManager.DeletedEntities.IsDeleted(c.DxccCode)) qrzDeleted.Add(c.DxccCode);
                }
                var qs = Properties.Settings.Default;

                // As on the LoTW and Club Log paths: a full download that returned nothing at all leaves
                // a figure this log was already given alone. QRZ does not un-confirm QSOs, so an empty
                // reply says the question was wrong, not that the confirmations went away. A log with
                // nothing stored yet still records the zero, so "checked, none" stays distinguishable
                // from "not checked yet".
                bool qrzHasStored = QrzConfirmedQsoCount > 0 || !string.IsNullOrWhiteSpace(QrzConfirmedEntities);
                if (!(!incremental && confirmations.Count == 0 && qrzHasStored))
                {
                    QrzConfirmedEntities = string.Join("|", qrzNames);
                    QrzConfirmedDeletedCodes = string.Join(",", qrzDeleted);
                    QrzConfirmedQsoCount = incremental
                        ? QrzConfirmedQsoCount + Math.Max(0, ConfirmedInLog(ConfSource.Qrz) - confirmedBefore)
                        : confirmations.Count;
                }
                LogState("QrzLastCheck", stampedAt);
                qs.Save();

                // Re-read the log so the QSO confirmation flags (which the zone lists use) are live, then
                // repaint the current folder - no manual reopen needed.
                await ReloadQsosAfterCheck(marked > 0);

                // "now marked" only when this run actually marked something. A full rebuild always
                // counts as a change (it rewrote every mark); a quick check that found nothing has
                // changed nothing, and saying "now" would claim work that did not happen.
                ShowCheckSummary(ConfSource.Qrz, fetch.Count, incremental,
                                 !incremental || ConfirmedInLog(ConfSource.Qrz) > confirmedBefore);
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

        // THE ONE PLACE a finished confirmation check reports itself. Every service calls this.
        //
        // There used to be four of these, one per service, and they drifted apart every time one was
        // touched: different wording, a count that meant something different in each, and LoTW saying
        // nothing at all after a quick check. Four copies of one idea is four chances to disagree.
        //
        // newOrDownloaded is what the operator should be told arrived - on a quick check the number
        // that were actually NEW, not what the service re-sent; on a full download everything fetched.
        // Only the caller can tell those apart, so only the caller decides it.
        private void ShowCheckSummary(ConfSource src, int newOrDownloaded, bool quickCheck,
                                      bool anythingChanged, List<string> failed = null)
        {
            string name = SourceTitle(src);

            // A FULL download that brought back nothing, for a log signed with a callsign that is not the
            // operator's own. "Downloaded 0 confirmation(s)" reads as "you have none", when what actually
            // happened is that LoTW was asked about someone else's callsign and answered - correctly -
            // with nothing. It only ever reports what the logged-in account uploaded itself.
            //
            // Said on EVERY empty full download, and it names the ACCOUNT that did the asking. It first
            // compared the log's callsign with the station-callsign setting and stayed quiet when they
            // matched - useless, because opening another operator's log sets that field to THEIR callsign,
            // which is exactly the case the message exists for. The account is the thing that decides what
            // LoTW will send, and it is not the same as the callsign a log is signed with.
            //
            // It states only what is CERTAIN - this account holds no confirmations for that callsign - and
            // does not say the log belongs to someone else, because that cannot be known from here: a LoTW
            // username need not be a callsign at all, and one account can hold certificates for several
            // (a club station, a contest call, a previous call). LoTW answers the same empty report when
            // the account genuinely owns the callsign but has never uploaded under it.
            if (src == ConfSource.Lotw && newOrDownloaded == 0 && !quickCheck)
            {
                string logCall = ActiveLogCallsign();
                if (string.IsNullOrWhiteSpace(logCall)) logCall = "this log's callsign";
                string account = (Properties.Settings.Default.LotwWebUser ?? string.Empty).Trim();

                HolyMessageBox.Show(
                    $"Nothing came back for {logCall}.\n\n" +
                    "Your LoTW account" + (account.Length > 0 ? $" ({account})" : "") +
                    " has no confirmations for that callsign. Only the account that uploaded those QSOs " +
                    "can download them." + FreezeNote(),
                    // Red, not the blue "i": a check that came back with nothing is a result the operator
                    // has to act on (ask the log's owner to run it), not a note they can wave past.
                    name + " confirmations", HolyMsgType.Error, this);
                return;
            }

            var text = new System.Text.StringBuilder();

            // A quick check that brought nothing back says so in those words, rather than announcing
            // "downloaded 0" as though a download were the point.
            text.AppendLine(quickCheck && newOrDownloaded == 0
                ? $"Nothing new at {name} since your last check."
                : $"Downloaded {newOrDownloaded:N0} confirmation(s) from {name}.");
            text.AppendLine();

            // THIS log only. It used to print a count for every log holding marks, with a paragraph
            // explaining that only this one had really been checked - worth saying while one download
            // wrote into all of them, but now each asks about a single callsign and those were just
            // other logs' numbers on a screen about this one.
            int marked = ConfirmedInLog(src);
            text.AppendLine(marked == 0
                ? "None of them matched a QSO in this log."
                : $"{marked:N0} QSO(s) in your log are {(anythingChanged ? "now" : "already")} marked confirmed on {name}.");

            if (failed != null && failed.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Some could not be downloaded:");
                foreach (var f in failed) text.AppendLine("    • " + f);
            }

            HolyMessageBox.Show(text.ToString().TrimEnd() + FreezeNote(), name + " confirmations updated", HolyMsgType.Info, this);
        }

        // The eQSL side of the confirmation feature. eQSL is per-callsign, so this loops over every eQSL
        // account (Options ▸ eQSL) and downloads that account's In Box (received cards). eQSL's download
        // carries no <DXCC>, so the deleted-entity split is resolved from the callsign via cty.dat and is
        // only approximate. Always a full rebuild.
        private async void BTN_CheckEqsl_Click(object sender, RoutedEventArgs e)
        {
            await RunEqslCheck(incremental: false);
        }

        // incremental=true asks eQSL only for cards that ARRIVED since the last check (RcvdSince) and
        // marks without clearing. Note the filter is on arrival time, not on the QSO's date, so the
        // marker stored is the moment of the check.
        private async System.Threading.Tasks.Task RunEqslCheck(bool incremental)
        {
            if (BlockedAsAnotherStationsLog("eQSL")) return;

            string since = incremental ? LogState("EqslLastCheck") : string.Empty;
            if (string.IsNullOrWhiteSpace(since)) incremental = false;   // nothing to be incremental from
            string stampedAt = DateTime.UtcNow.ToString("yyyyMMddHHmm");
            int confirmedBefore = ConfirmedInLog(ConfSource.Eqsl);   // to count what this run actually adds

            List<EqslAccount> accounts;
            try { accounts = Dal?.GetEqslAccounts() ?? new List<EqslAccount>(); }
            catch (Exception ex) { HolyMessageBox.Show("Couldn't read eQSL accounts: " + ex.Message, "eQSL confirmations", HolyMsgType.Warning, this); return; }

            accounts = accounts.Where(a => !string.IsNullOrWhiteSpace(a.Username) && !string.IsNullOrWhiteSpace(a.Password)).ToList();

            // ONLY the account for the callsign this log belongs to. eQSL keeps one In Box per callsign,
            // and downloading every configured account on every log fetched a special-event station's
            // cards while standing in the everyday log, where not one of them could ever match.
            // Compared by identity, so an account registered as 4Z5SL serves a log of 4Z5SL/6 QSOs.
            string eqslLogCall = ActiveLogCallsign();
            if (!string.IsNullOrWhiteSpace(eqslLogCall))
            {
                var mine = accounts.Where(a => CallsignIdentity.Same(a.Callsign, eqslLogCall)).ToList();
                if (mine.Count == 0)
                {
                    HolyMessageBox.Show(
                        $"No eQSL account is set up for {eqslLogCall}, the callsign this log belongs to.\n\n" +
                        "Add one in Options → eQSL, or open the log whose callsign you do have an account for.",
                        "eQSL confirmations", HolyMsgType.Warning, this);
                    return;
                }
                accounts = mine;
            }

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
                    TB_LotwLoadingText.Text = incremental
                        ? $"Checking eQSL for new cards… ({idx} of {accounts.Count})"
                        : $"Downloading eQSL In Box… ({idx} of {accounts.Count})";
                    TB_LotwLoadingSub.Text = $"Account {acct.Callsign}";
                    // Off the UI thread - see the note in the LoTW download.
                    var acctCopy = acct;
                    var r = await Task.Run(() => EqslConfirmationService.FetchInboxAsync(
                        acctCopy.Username, acctCopy.Password, acctCopy.Callsign, incremental ? since : null, ct), ct);
                    if (r.Ok) all.AddRange(r.Confirmations);
                    else if (r.NetworkError) { failed.Add($"{acct.Callsign}: no connection"); }
                    else failed.Add($"{acct.Callsign}: {r.Reason}");
                }

                if (all.Count == 0)
                {
                    // An empty result is still an answer and has to be written down, or the folder
                    // claims it was never checked. A FAILED download writes nothing.
                    //
                    // But NOTHING NEW is not the same as NOTHING AT ALL: on a quick check an empty
                    // answer means only that the window since the last check was quiet, so the running
                    // total must be left exactly as it is. Zeroing it here would have thrown away the
                    // whole count every time nothing had arrived - which is most of the time.
                    //
                    // And a FULL download that brings back nothing does not disprove what this log was
                    // already told: cards are not taken back once received. So a stored figure stands,
                    // and only a log with nothing stored yet records the zero. (Same reasoning as the
                    // LoTW path above - see the note there.)
                    bool eqslHasStored = EqslConfirmedQsoCount > 0
                                         || !string.IsNullOrWhiteSpace(EqslConfirmedEntities);
                    if (failed.Count == 0 && !incremental && !eqslHasStored)
                    {
                        EqslConfirmedQsoCount = 0;
                        EqslConfirmedEntities = string.Empty;
                        EqslConfirmedDeletedCodes = string.Empty;
                    }
                    if (failed.Count == 0) LogState("EqslLastCheck", stampedAt);

                    // A real failure is its own message; an empty result goes through the SAME summary
                    // every other service uses, so the four folders answer alike.
                    if (failed.Count > 0)
                    {
                        HolyMessageBox.Show(
                            "The eQSL In Box could not be read.\n\n" + string.Join("\n", failed) +
                            "\n\nNothing in your log was changed.",
                            "eQSL confirmations", HolyMsgType.Warning, this);
                    }
                    else
                    {
                        ShowCheckSummary(ConfSource.Eqsl, 0, incremental, false, failed);
                    }
                    RefreshForSource();
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
                // fullReset only on the authoritative rebuild - see the QRZ path for why.
                int marked = await Task.Run(() =>
                    Dal.MarkEqslConfirmed(all, !incremental, ((IProgress<int>)markProgress).Report, ct, out unmatched));

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
                    string name = EntityNameFor(c.Call, c.QsoDate);
                    if (!string.IsNullOrEmpty(name) && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
                }
                var s = Properties.Settings.Default;
                EqslConfirmedEntities = string.Join("|", names);
                EqslConfirmedDeletedCodes = string.Empty;
                // Added: how many QSOs actually changed to confirmed, not how many cards arrived - see
                // the QRZ path for why counting records would creep upward.
                EqslConfirmedQsoCount = incremental
                    ? EqslConfirmedQsoCount + Math.Max(0, ConfirmedInLog(ConfSource.Eqsl) - confirmedBefore)
                    : all.Count;                     // what eQSL reported (frame "Confirmed on eQSL")
                LogState("EqslLastCheck", stampedAt);
                s.Save();

                await ReloadQsosAfterCheck(marked > 0);
                ShowCheckSummary(ConfSource.Eqsl, all.Count, incremental,
                                 !incremental || ConfirmedInLog(ConfSource.Eqsl) > confirmedBefore, failed);
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


        // The Club Log side of the confirmation feature. Club Log is a single account (e-mail + password),
        // but getadif.php is per-callsign, so this loops over every station callsign the operator used and
        // downloads that call's whole-log export, keeping the QSOs Club Log reports confirmed
        // (QSL_RCVD = Y/V). Unlike eQSL, Club Log DOES send <DXCC>, so the deleted-entity split is exact.
        // Always a full rebuild.
        private async void BTN_CheckClublog_Click(object sender, RoutedEventArgs e)
        {
            if (BlockedAsAnotherStationsLog("Club Log")) return;

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

            // The callsign THIS LOG belongs to - not the personal callsign in Settings, which is a
            // different thing the moment you keep a log for a special-event or club station. Asking Club
            // Log about 4Z5SL while standing in the 4X2XMAS log downloaded a set that could not match a
            // single QSO in front of you.
            //
            // Still exactly one callsign, never a loop over every my_callsign in the database: a shared
            // machine holds other operators' logs too, and asking Club Log about a friend's call under
            // your login is both wrong and pointless (Club Log rejects it).
            string myCall = ActiveLogCallsign();
            if (string.IsNullOrWhiteSpace(myCall)) myCall = s0.my_callsign?.Trim();
            if (string.IsNullOrWhiteSpace(myCall))
            {
                HolyMessageBox.Show("This log has no station callsign set, so there is nothing to download from Club Log.\n\n" +
                    "Set it with \"Set Identity\" in the Log Manager.",
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
                    // Off the UI thread - see the note in the LoTW download.
                    var callCopy = call;
                    var r = await Task.Run(() => ClublogService.FetchLogAsync(email, password, callCopy, ct), ct);
                    if (r.Ok) all.AddRange(r.Confirmations);
                    else if (r.NetworkError) failed.Add($"{call}: no connection");
                    else failed.Add($"{call}: {r.Reason}");
                }

                if (all.Count == 0)
                {
                    // Record that the check RAN even though it found nothing. Without this, a download
                    // that legitimately returns nothing is indistinguishable from never having asked,
                    // and the folder says "not checked yet" for ever however many times you press the
                    // button. Only when the download actually FAILED is nothing written - because then
                    // we genuinely do not know.
                    //
                    // Nor is a figure this log was already given thrown away on an empty reply: Club Log
                    // does not un-confirm QSOs, so "nothing came back" is far likelier to mean the wrong
                    // callsign was asked for than that the confirmations vanished. A log with nothing
                    // stored yet still records the zero. (Same reasoning as the LoTW path - see there.)
                    bool clublogHasStored = ClublogConfirmedQsoCount > 0
                                            || !string.IsNullOrWhiteSpace(ClublogConfirmedEntities);
                    if (failed.Count == 0 && !clublogHasStored)
                    {
                        ClublogConfirmedQsoCount = 0;
                        ClublogConfirmedEntities = string.Empty;
                        ClublogConfirmedDeletedCodes = string.Empty;
                    }
                    string why = failed.Count > 0 ? "\n\n" + string.Join("\n", failed) : "";
                    HolyMessageBox.Show(
                        (failed.Count > 0
                            ? "Club Log could not be read." + why
                            : $"Club Log has no confirmations for {myCall}.")
                        + "\n\nNothing in your log was changed.",
                        "Club Log confirmations", HolyMsgType.Warning, this);
                    RefreshForSource();
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
                    string name = EntityNameFor(c.Call, c.QsoDate);
                    if (!string.IsNullOrEmpty(name) && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
                    if (DXCCManager.DeletedEntities.IsDeleted(c.DxccCode)) deleted.Add(c.DxccCode);
                }
                ClublogConfirmedEntities = string.Join("|", names);
                ClublogConfirmedDeletedCodes = string.Join(",", deleted);
                ClublogConfirmedQsoCount = all.Count;   // what Club Log reported (frame "Confirmed on Club Log")
                s0.Save();

                await ReloadQsosAfterCheck(marked > 0);
                ShowCheckSummary(ConfSource.Clublog, all.Count, false, true, failed);   // Club Log is always a full rebuild
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


        // Reports the outcome of a full confirmation download, and shows plainly that the marks reached
        // EVERY log - the per-log breakdown makes the cross-log effect visible instead of leaving the
        // operator to wonder whether their other logs were touched.

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

            // THE CONFIRMATIONS THAT MATCHED NOTHING, sorted into the two piles that want different
            // things done to them.
            //
            // MissingFromLog: this log has never worked that callsign at all. LoTW is holding a contact
            // the log has lost - which is how a log destroyed with its computer can be got back, if it
            // was uploaded before the machine died - and every field LoTW keeps of it is here.
            //
            // NearMisses: the station IS in the log, but band, mode or date do not agree with what LoTW
            // was sent. Adding these would not restore anything; it would double-log contacts that are
            // already there. They are a question - which of the two is right - not an import.
            public List<DataAccess.LotwConfirmation> MissingFromLog = new List<DataAccess.LotwConfirmation>();
            public List<DataAccess.LotwConfirmation> NearMisses = new List<DataAccess.LotwConfirmation>();
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

                string name = EntityNameFor(call, qsoDate);
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
                    DxccCode = dxccCode,

                    // Everything else the record holds about the contact itself, so a confirmation for a
                    // QSO the log has lost can be turned back into one. Costs nothing here - the record
                    // is already parsed - and is ignored by the matching, which reads none of it.
                    TimeOn = (ExtractAdifField(rec, "time_on") ?? string.Empty).Trim(),
                    Grid = (ExtractAdifField(rec, "gridsquare") ?? string.Empty).Trim(),
                    Country = (ExtractAdifField(rec, "country") ?? string.Empty).Trim(),
                    Continent = (ExtractAdifField(rec, "cont") ?? string.Empty).Trim(),
                    CqZone = (ExtractAdifField(rec, "cqz") ?? string.Empty).Trim(),
                    ItuZone = (ExtractAdifField(rec, "ituz") ?? string.Empty).Trim()
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
                string nm = EntityNameFor(c.Call, c.QsoDate);
                if (!string.IsNullOrEmpty(nm) && !string.Equals(nm, "Unknown", StringComparison.OrdinalIgnoreCase))
                    result.ResolvedNames.Add(nm);
                if (c.DxccCode > 0)
                {
                    if (DXCCManager.DeletedEntities.IsDeleted(c.DxccCode)) result.ConfirmedDeletedCodes.Add(c.DxccCode);
                    else result.ConfirmedActiveCodes.Add(c.DxccCode);
                }
            }

            // Sort what matched nothing into "the log has never worked this station" and "it has, but
            // the details disagree". One query for the whole log's callsigns, then a set lookup each -
            // asking the database per confirmation would be thousands of queries.
            try
            {
                var dal = DataAccess.GetInstance();
                if (dal != null && unmatched != null && unmatched.Count > 0)
                {
                    HashSet<string> worked = dal.WorkedCallsignsInLog(dal.ActiveLogId);
                    foreach (var c in unmatched)
                    {
                        if (worked.Contains(CallsignIdentity.Base(c.Call ?? string.Empty)))
                            result.NearMisses.Add(c);
                        else
                            result.MissingFromLog.Add(c);
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            try { WriteUnmatchedReport(confirmations.Count, result.MarkedConfirmed, unmatched, result.OtherStationConfirmations); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return result;
        }

        // Show/hide the download overlay and run (or stop) the spinner's continuous rotation.
        // Ticks the overlay's clock. Deliberately a DispatcherTimer on the UI thread: that is the point.
        // A rotating ring proves nothing to an operator who has just watched the same sentence sit still
        // for a minute, and it cannot tell "waiting for the server" from "hung". A number that changes
        // every second can.
        private System.Windows.Threading.DispatcherTimer _spinnerClock;
        private DateTime _spinnerStartedUtc;

        // The timer is set to fire every second. When it fires LATE, the UI thread was busy and could not
        // service it - and for exactly as long, nothing was redrawn either, which is what makes a spinner
        // stop turning. The worst gap seen is kept and shown, so "the spinner froze" stops being a
        // judgement call and becomes a number: no "paused" means the UI thread was free the whole way and
        // any stall is in the animation itself, not in the work.
        private DateTime _spinnerLastTickUtc;
        private double _spinnerWorstGapSeconds;

        // ---- UI-thread freeze watchdog ---------------------------------------------------------------
        //
        // The clock above can only report a stall AFTER it ends, because a frozen window is not drawn.
        // This measures from OUTSIDE instead: a background thread posts a do-nothing message to the UI
        // thread every 100ms and times how long the answer takes. That timing is unaffected by the UI
        // being stuck - which is the whole point - so it records exactly when the freeze began, how long
        // it lasted, and what the window was doing at the time. The result is written to a file, so it
        // survives and can be read afterwards.
        private System.Threading.Thread _uiWatchdog;
        private volatile bool _uiWatchdogRun;
        private volatile string _uiPhase = string.Empty;
        private readonly List<string> _uiStalls = new List<string>();
        private readonly object _uiStallsLock = new object();
        private DateTime _watchdogStartedUtc;

        private void UiWatchdogLoop()
        {
            var dispatcher = Dispatcher;
            while (_uiWatchdogRun)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string phase = _uiPhase;
                try
                {
                    using (var answered = new System.Threading.ManualResetEventSlim(false))
                    {
                        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send,
                                               new Action(() => answered.Set()));
                        answered.Wait(120000);
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); return; }

                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms >= 500)
                {
                    double atSecond = (DateTime.UtcNow - _watchdogStartedUtc).TotalSeconds - (ms / 1000.0);
                    lock (_uiStallsLock)
                        _uiStalls.Add($"  at {atSecond,6:0.0}s   frozen {ms / 1000.0,6:0.0}s   while: {phase}");
                }

                System.Threading.Thread.Sleep(100);
            }
        }

        private void WriteFreezeReport()
        {
            try
            {
                List<string> stalls;
                lock (_uiStallsLock) stalls = new List<string>(_uiStalls);

                // Only a stall long enough to be FELT is worth a file. A checkup that leaves a report on
                // the operator's Desktop every single time is litter; one that stays silent until the
                // window genuinely locks up is evidence, gathered without anyone having to reproduce the
                // fault to order. (Everything under 3s still shows in the report when one is written.)
                if (_spinnerWorstGapSeconds < 3) return;

                string path = System.IO.Path.Combine(
                    DataAccess.ReportsFolder,
                    "holylogger_ui_freeze.txt");

                var t = new System.Text.StringBuilder();
                t.AppendLine($"UI-thread responsiveness during the last confirmation check — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                t.AppendLine(new string('=', 78));
                t.AppendLine($"Check lasted           : {(DateTime.UtcNow - _watchdogStartedUtc).TotalSeconds:0.0}s");
                t.AppendLine($"Times the window froze : {stalls.Count}  (anything over 0.5s is listed)");
                t.AppendLine();
                if (stalls.Count == 0)
                    t.AppendLine("  none — the UI thread answered within half a second, every time.");
                else
                    foreach (string s in stalls) t.AppendLine(s);
                System.IO.File.WriteAllText(path, t.ToString(), System.Text.Encoding.UTF8);
                Reports.Note(path);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

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

                _spinnerStartedUtc = DateTime.UtcNow;
                _spinnerLastTickUtc = _spinnerStartedUtc;
                _spinnerWorstGapSeconds = 0;

                _watchdogStartedUtc = _spinnerStartedUtc;
                lock (_uiStallsLock) _uiStalls.Clear();
                _uiPhase = "starting the check";
                _uiWatchdogRun = true;
                _uiWatchdog = new System.Threading.Thread(UiWatchdogLoop)
                {
                    IsBackground = true,
                    Name = "UiFreezeWatchdog"
                };
                _uiWatchdog.Start();
                if (TB_LotwElapsed != null) TB_LotwElapsed.Text = "running  0:00";
                if (_spinnerClock == null)
                {
                    // Send priority, above Render: the clock must be serviced whenever the thread is free
                    // at all, so a stalled clock means a stalled thread rather than a busy dispatcher
                    // queue outranking it.
                    _spinnerClock = new System.Windows.Threading.DispatcherTimer(
                        System.Windows.Threading.DispatcherPriority.Send)
                    {
                        Interval = TimeSpan.FromSeconds(1)
                    };
                    _spinnerClock.Tick += (s, e) =>
                    {
                        if (TB_LotwElapsed == null) return;
                        DateTime now = DateTime.UtcNow;

                        double gap = (now - _spinnerLastTickUtc).TotalSeconds;
                        _spinnerLastTickUtc = now;
                        if (gap > _spinnerWorstGapSeconds) _spinnerWorstGapSeconds = gap;

                        TimeSpan t = now - _spinnerStartedUtc;
                        string text = $"running  {(int)t.TotalMinutes}:{t.Seconds:00}";
                        // Only a real stall is worth saying. A second or two of scheduling jitter is not.
                        if (_spinnerWorstGapSeconds >= 3)
                            text += $"    (window frozen {_spinnerWorstGapSeconds:0.0}s at worst)";
                        TB_LotwElapsed.Text = text;
                    };
                }
                _spinnerClock.Start();
            }
            else
            {
                // Taking it down twice is expected - once the moment the work ends, once from the finally
                // that guarantees it happens at all - and the second time must do nothing, or the freeze
                // report would be written again over the figures of the run that has just been reported.
                if (LotwLoadingOverlay.Visibility != Visibility.Visible) return;

                MeasureFreeze();          // a stall that ran right up to the end still counts
                _uiWatchdogRun = false;   // the thread exits on its own; never joined from the UI thread
                WriteFreezeReport();
                _spinnerClock?.Stop();
                if (TB_LotwElapsed != null) TB_LotwElapsed.Text = string.Empty;
                SpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                LotwLoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        // Folds the time since the last tick into the worst-gap figure. Called whenever the UI thread is
        // known to be running, so a freeze that ended only when the work did is not missed.
        private void MeasureFreeze()
        {
            if (_spinnerClock == null || !_spinnerClock.IsEnabled) return;
            double gap = (DateTime.UtcNow - _spinnerLastTickUtc).TotalSeconds;
            if (gap > _spinnerWorstGapSeconds) _spinnerWorstGapSeconds = gap;
        }

        // The overlay cannot show a freeze WHILE it is frozen - it is not being drawn - so the figure has
        // to outlive the overlay. It is put in the message that ends the check, where it can be read at
        // leisure and screenshotted. Silent unless there really was a stall worth naming.
        private string FreezeNote()
        {
            MeasureFreeze();
            return _spinnerWorstGapSeconds >= 3
                ? $"\n\n(The window stopped responding for up to {_spinnerWorstGapSeconds:0.0}s during this check.)"
                : string.Empty;
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

        // By ENTITY NUMBER. Countries with no number sort last whichever way the arrow points: they are
        // not "0", they are unknown, and burying them under the top of an ascending list would put the
        // rows nothing recognises where the eye lands first.
        private void SortWorkedByCode(object sender, MouseButtonEventArgs e)
        {
            _workedSort = _workedSort == WorkedSort.CodeAsc ? WorkedSort.CodeDesc : WorkedSort.CodeAsc;
            ApplyWorkedSort();
        }

        private void SortMissingByName(object sender, MouseButtonEventArgs e)
        {
            _missingSort = _missingSort == MissingSort.NameAsc ? MissingSort.NameDesc : MissingSort.NameAsc;
            ApplyMissingSort();
        }

        private void SortMissingByCode(object sender, MouseButtonEventArgs e)
        {
            _missingSort = _missingSort == MissingSort.CodeAsc ? MissingSort.CodeDesc : MissingSort.CodeAsc;
            ApplyMissingSort();
        }

        private void ApplyWorkedSort()
        {
            // CURRENT entities only. Every figure on this page is measured against the 340 that exist
            // today, and a table holding seven more rows than the tile above it counts is a table nobody
            // can reconcile. The deleted ones are not dropped from the program - they are counted on the
            // status line and listed in full behind its "N deleted" link.
            var shown = _workedList.Where(c => !c.IsDeletedEntity).ToList();

            List<CountryItem> sorted;
            if      (_workedSort == WorkedSort.NameAsc)  sorted = shown.OrderBy(c => c.Name).ToList();
            else if (_workedSort == WorkedSort.NameDesc) sorted = shown.OrderByDescending(c => c.Name).ToList();
            else if (_workedSort == WorkedSort.CountAsc) sorted = shown.OrderBy(c => c.Count).ThenBy(c => c.Name).ToList();
            else if (_workedSort == WorkedSort.ConfirmedDesc || _workedSort == WorkedSort.ConfirmedAsc)
            {
                // Group by confirmed state. The UNCONFIRMED group (the countries you still need) is
                // sub-sorted alphabetically by country name so it's easy to scan; the confirmed group
                // keeps its count order.
                var confirmed   = shown.Where(c => c.IsConfirmed).OrderByDescending(c => c.Count).ThenBy(c => c.Name);
                var unconfirmed = shown.Where(c => !c.IsConfirmed).OrderBy(c => c.Name);
                sorted = _workedSort == WorkedSort.ConfirmedDesc
                    ? confirmed.Concat(unconfirmed).ToList()    // confirmed first, unconfirmed (A–Z) below
                    : unconfirmed.Concat(confirmed).ToList();   // unconfirmed (A–Z) first
            }
            // By entity NUMBER. A country whose wording no database knows has no number (0) and is put
            // LAST either way - it is unknown, not "before number 1", and an ascending sort would
            // otherwise open with the rows that say least.
            else if (_workedSort == WorkedSort.CodeAsc)
                sorted = shown.OrderBy(c => c.Code == 0 ? 1 : 0).ThenBy(c => c.Code).ThenBy(c => c.Name).ToList();
            else if (_workedSort == WorkedSort.CodeDesc)
                sorted = shown.OrderBy(c => c.Code == 0 ? 1 : 0).ThenByDescending(c => c.Code).ThenBy(c => c.Name).ToList();
            else                                         sorted = shown.OrderByDescending(c => c.Count).ThenBy(c => c.Name).ToList();

            for (int i = 0; i < sorted.Count; i++)
                sorted[i].RowBg = i % 2 == 0 ? ThemeManager.Brush("GridRowBg") : ThemeManager.Brush("GridAltRowBg");

            IC_WorkedCountries.ItemsSource = sorted;
            UpdateWorkedSortHeaders();
        }

        private void ApplyMissingSort()
        {
            List<CountryItem> sorted;
            if      (_missingSort == MissingSort.NameAsc)  sorted = _missingList.OrderBy(c => c.Name).ToList();
            else if (_missingSort == MissingSort.NameDesc) sorted = _missingList.OrderByDescending(c => c.Name).ToList();
            else if (_missingSort == MissingSort.CodeAsc)
                sorted = _missingList.OrderBy(c => c.Code == 0 ? 1 : 0).ThenBy(c => c.Code).ThenBy(c => c.Name).ToList();
            else
                sorted = _missingList.OrderBy(c => c.Code == 0 ? 1 : 0).ThenByDescending(c => c.Code).ThenBy(c => c.Name).ToList();

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
            // Two lines: "Country" over "Code", the sort arrow beside the second. \n in a TextBlock's Text
            // is a line break, so the header can still be assigned as one string from here.
            TB_SortWorkedCode.Text  = _workedSort == WorkedSort.CodeAsc  ? "Country\nCode ▲"
                                    : _workedSort == WorkedSort.CodeDesc ? "Country\nCode ▼"
                                    :                                      "Country\nCode";
            // Blank on the Worked folder (no confirmation source), so there is no "Conf." header over an
            // empty column.
            TB_SortWorkedConfirmed.Text = _source == ConfSource.Worked ? ""
                                        : _workedSort == WorkedSort.ConfirmedDesc ? "Conf. ▼"
                                        : _workedSort == WorkedSort.ConfirmedAsc  ? "Conf. ▲"
                                        :                                            "Conf.";
        }

        private void UpdateMissingSortHeaders()
        {
            TB_SortMissingName.Text = _missingSort == MissingSort.NameAsc  ? "Country ▲"
                                    : _missingSort == MissingSort.NameDesc ? "Country ▼"
                                    :                                        "Country";
            TB_SortMissingCode.Text = _missingSort == MissingSort.CodeAsc  ? "Country\nCode ▲"
                                    : _missingSort == MissingSort.CodeDesc ? "Country\nCode ▼"
                                    :                                        "Country\nCode";
        }

        // A COUNTRY THAT HAS NO FLAG FILE IS REMEMBERED TOO. Only successes used to be cached, so every
        // entity whose PNG is missing re-attempted the load - and a missing pack: resource THROWS, at
        // roughly a third of a millisecond a time. Rebuilding the Missing list on a folder where nearly
        // every country is missing meant a few hundred of those, which measured 134 ms of the ~250 ms a
        // folder took to open. Storing the null makes each one throw at most once per session.
        //
        // Frozen so the image is shareable and cheap to hand to a row: these are handed out to a list
        // that rebinds on every folder change.
        private static BitmapImage GetFlagImage(string countryName)
        {
            if (!MainWindow.DxccNameToIso.TryGetValue(countryName, out string iso)) return null;
            if (_flagCache.TryGetValue(iso, out BitmapImage cached)) return cached;   // may be a known miss

            BitmapImage bm = null;
            try
            {
                bm = new BitmapImage(new Uri($"pack://application:,,,/Images/flags/{iso}.png"));
                if (bm.CanFreeze) bm.Freeze();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); bm = null; }

            _flagCache[iso] = bm;
            return bm;
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
                    FontSize = 16,
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

            // THE TITLE BAR HAS TO BE GRABBABLE. Not "the window is somewhere on the desktop" - that is
            // what this used to ask, against the virtual screen, which is the bounding box around ALL
            // monitors and therefore includes empty corners no monitor covers. A saved position from
            // above the visible area passed that test, the window opened with its title bar off the top
            // of the screen, and with nothing to grab the only way out was killing the program.
            //
            // So test the one point the mouse actually needs - a spot on the title bar, past the icon -
            // and require it to be inside ONE REAL MONITOR's working area.
            double grabX = left + 60;
            double grabY = top + 12;

            try
            {
                // Screen rectangles are device pixels; Left/Top are WPF units.
                double scale;
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                    scale = g.DpiX / 96.0;
                if (scale <= 0) scale = 1.0;

                foreach (var sc in System.Windows.Forms.Screen.AllScreens)
                {
                    var wa = sc.WorkingArea;
                    if (grabX >= wa.Left / scale && grabX <= wa.Right  / scale - 40 &&
                        grabY >= wa.Top  / scale && grabY <= wa.Bottom / scale - 40)
                        return true;
                }
                return false;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // Could not ask the monitors: fall back to the bounding box, with the top tested exactly
            // rather than with the old slack that let an off-top position through.
            double vsLeft   = SystemParameters.VirtualScreenLeft;
            double vsTop    = SystemParameters.VirtualScreenTop;
            double vsRight  = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop  + SystemParameters.VirtualScreenHeight;

            return left >= vsLeft - 10 && top >= vsTop &&
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

        // The ARRL/ADIF entity number - the thing the award world actually speaks in, and the only way
        // to say WHICH country when two databases word the same entity differently. Worked out from the
        // name on first use rather than at build time, so both tables that use this class get it without
        // either of them having to remember to fill it in; the lists are virtualized, so only the rows
        // on screen ever ask.
        //
        // 0 when no database recognises the wording the log used - shown as an empty cell, never as "0".
        // A country nothing can put a number to is still in the log and still belongs in the list.
        // SET when the item is built, because the item is now built FROM the number - the statistics
        // identify an entity by its ADIF code and carry the name only to print it. The lazy lookup from
        // the name remains as a fallback for the few places that still construct an item from a name
        // alone (the missing-countries list before an entity table exists, say).
        private int? _code;
        public int Code
        {
            get
            {
                if (!_code.HasValue)
                {
                    try { _code = CountryLookup.Shared.EntityCodeForCountry(Name); }
                    catch { _code = 0; }
                }
                return _code.Value;
            }
            set { _code = value; }
        }
        public string CodeText => Code > 0 ? Code.ToString() : "";

        // True for an entity that no longer exists as a DXCC country. The worked table shows the CURRENT
        // ones, so that its count is the same 326 the tile above it prints against 340; the deleted ones
        // are reached from the "N deleted" link beside it rather than mixed into a list measured against
        // a total they are not part of.
        public bool IsDeletedEntity { get; set; }

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
    // the record for the drill-down. Persisted as JSON in LotwLastNewJson.
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






