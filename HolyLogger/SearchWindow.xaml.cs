using HolyParser;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HolyLogger
{
    public partial class SearchWindow : Window
    {
        private readonly ObservableCollection<QSO> _allQsos;
        private System.Collections.Generic.List<SearchCountryItem> _allCountries;
        private ListCollectionView _countriesView;
        private string _countryFilter = "";
        private TextBox _countryEditBox;   // the ComboBox's internal editable text box

        // Clear button: blue when there is something to clear, gray (but still enabled) otherwise.
        private static readonly Brush ClearActiveBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
        private static readonly Brush ClearIdleBrush   = new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75));

        // A click on a callsign in the results opens that station's QRZ.com page in the default
        // browser — the callsign acts like a web link (hand cursor + "QRZ" tooltip in the XAML).
        // Gated on ClickCount==1 so a double-click opens the page once, not twice.
        private void Callsign_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 1) return;
            var qso = (sender as FrameworkElement)?.DataContext as QSO;
            string call = (qso?.DXCall ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(call)) return;
            try { System.Diagnostics.Process.Start("https://www.qrz.com/db/" + call); }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        public SearchWindow(ObservableCollection<QSO> qsos)
        {
            InitializeComponent();
            _allQsos = qsos;

            // Build country list from distinct countries in the log, sorted A-Z
            _allCountries = _allQsos
                .Select(q => q.Country)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c)
                .Select(name => new SearchCountryItem(name))
                .ToList();

            _countriesView = new ListCollectionView(_allCountries);
            _countriesView.Filter = o =>
            {
                if (string.IsNullOrEmpty(_countryFilter)) return true;
                // Prefix match: show only countries that START WITH the typed text.
                return ((SearchCountryItem)o).Name.StartsWith(_countryFilter, StringComparison.OrdinalIgnoreCase);
            };
            CB_Country.ItemsSource = _countriesView;

            // Attach TextChanged to the internal editable text box (for filter updates)
            CB_Country.Loaded += (sender, ev) =>
            {
                _countryEditBox = CB_Country.Template.FindName("PART_EditableTextBox", CB_Country) as TextBox;
                if (_countryEditBox != null)
                    _countryEditBox.TextChanged += OnCountryTextChanged;
            };

            // When the country dropdown is open, the ComboBox has mouse capture, so a normal
            // Click on the Clear button is swallowed by the popup-close (first click only
            // closes the list). This purpose-built event fires for a press outside the
            // captured element, letting us run Clear on that very first click.
            Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(this, OnMouseDownOutsideCapture);

            PopulateFilterLists();
            UpdateClearButton();

            // Placement via the shared helper, like every other window. The bespoke code this replaces
            // saved on every LocationChanged/SizeChanged but restored without checking the position was
            // still on a monitor, and it never saved a window still open when the program closed.
            WindowBounds.Attach(this, "Search");
        }

        private void OnMouseDownOutsideCapture(object sender, MouseButtonEventArgs e)
        {
            if (!CB_Country.IsDropDownOpen) return;

            // Did the press land on the Clear button? If so, clear on this first click.
            Point p = e.GetPosition(Btn_Clear);
            if (p.X >= 0 && p.Y >= 0 && p.X <= Btn_Clear.ActualWidth && p.Y <= Btn_Clear.ActualHeight)
                ClearAll();
        }

        // Pre-fills the Callsign box (used when opened from a log-row right-click).
        public void SetCallsign(string call, bool runSearch = false)
        {
            TB_Callsign.Text = (call ?? string.Empty).Trim().ToUpperInvariant();
            TB_Callsign.CaretIndex = TB_Callsign.Text.Length;
            UpdateClearButton();
            TB_Callsign.Focus();
            if (runSearch)
                RunSearch();
        }

        // Pre-fills the Country box and (optionally) runs the search (used when opened from a country
        // row in the Statistics window). Clears the callsign so it is a pure country search.
        public void SetCountry(string country, bool runSearch = false)
        {
            TB_Callsign.Text = "";
            CB_Country.IsDropDownOpen = false;
            CB_Country.SelectedItem = null;   // before Text= so WPF doesn't fight the assignment
            CB_Country.Text = (country ?? string.Empty).Trim();
            _countryFilter = CB_Country.Text;
            UpdateClearButton();
            if (runSearch)
                RunSearch();
        }

        // Keep filter in sync whenever text changes (from typing or selection)
        private void OnCountryTextChanged(object sender, TextChangedEventArgs e)
        {
            _countryFilter = CB_Country.Text;
            _countriesView.Refresh();
            UpdateClearButton();
        }

        // Enter before the ComboBox processes it → search (only when dropdown is closed)
        private void CB_Country_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !CB_Country.IsDropDownOpen)
            {
                RunSearch();
                e.Handled = true;
            }
        }

        // KeyUp bubbles up from the internal text box AFTER the text is already updated.
        // Open the filtered dropdown for any printable/editing key; skip navigation/modifier keys.
        // Mouse-click selections never fire KeyUp, so the dropdown won't reopen after a selection.
        private void CB_Country_KeyUp(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                case Key.Escape:
                case Key.Tab:
                case Key.Up:
                case Key.Down:
                case Key.Left:
                case Key.Right:
                case Key.LeftCtrl:   case Key.RightCtrl:
                case Key.LeftShift:  case Key.RightShift:
                case Key.LeftAlt:    case Key.RightAlt:
                case Key.LWin:       case Key.RWin:
                    return;
            }

            CB_Country.IsDropDownOpen = !string.IsNullOrEmpty(CB_Country.Text);
        }

        // Opening the dropdown makes WPF auto-select the whole edit-box text, so the next
        // character would REPLACE it (the first letter vanished when typing fast). This
        // fires synchronously right before each character is committed: if the text is
        // fully selected, collapse the selection to the caret so the character appends
        // instead. Doing it here (not via an async Dispatcher call) removes the race, so
        // it works at any typing speed.
        private void CB_Country_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var tb = _countryEditBox;
            if (tb != null && tb.SelectionLength > 0 && tb.SelectionLength == tb.Text.Length)
            {
                tb.SelectionStart  = tb.Text.Length;
                tb.SelectionLength = 0;
            }
        }

        // Callsign box: clear results immediately when text is fully deleted
        private void SearchField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(TB_Callsign.Text))
                ClearResults();
            UpdateClearButton();
        }

        private void SearchField_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                RunSearch();
        }

        // Put the caret in the Callsign box as soon as the window opens so the user can type
        // a callsign immediately.
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            TB_Callsign.Focus();
            Keyboard.Focus(TB_Callsign);
        }

        // Esc anywhere in the window clears both fields and the results (same as the Clear
        // button). PreviewKeyDown tunnels in before the ComboBox can swallow Esc to merely
        // close its dropdown, so Esc always performs the full clear.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ClearAll();
                e.Handled = true;
            }
        }

        private void Btn_Search_Click(object sender, RoutedEventArgs e) => RunSearch();

        private void Btn_Clear_Click(object sender, RoutedEventArgs e) => ClearAll();

        private void ClearAll()
        {
            TB_Callsign.Text        = "";
            CB_Country.IsDropDownOpen = false;
            CB_Country.SelectedItem = null;   // must come before Text= so WPF doesn't fight the clear
            CB_Country.Text         = "";
            // Setting ComboBox.Text="" doesn't reliably wipe the visible edit box when the
            // user typed free text, so clear the internal text box directly as well.
            if (_countryEditBox != null)
                _countryEditBox.Text = "";
            _countryFilter          = "";
            _countriesView.Refresh();

            // Clear means clear EVERYTHING, including the second row of filters - otherwise a band or
            // date left set from the previous search silently narrows the next one.
            if (CB_Band.Items.Count > 0)   CB_Band.SelectedIndex = 0;
            if (CB_Mode.Items.Count > 0)   CB_Mode.SelectedIndex = 0;
            if (CB_MyCall.Items.Count > 0) CB_MyCall.SelectedIndex = 0;
            TB_Locator.Text = "";
            TB_Square.Text  = "";
            DP_From.SelectedDate = null;
            DP_To.SelectedDate   = null;

            ClearResults();
            UpdateClearButton();
            TB_Callsign.Focus();
        }

        // Blue while any filter is set (so it's clearly clickable), gray when everything is empty.
        private void UpdateClearButton()
        {
            bool hasContent = !string.IsNullOrEmpty(TB_Callsign.Text) ||
                              !string.IsNullOrEmpty(CB_Country.Text) ||
                              !string.IsNullOrEmpty(TB_Locator.Text) ||
                              !string.IsNullOrEmpty(TB_Square.Text) ||
                              SelectedFilter(CB_Band) != null ||
                              SelectedFilter(CB_Mode) != null ||
                              SelectedFilter(CB_MyCall) != null ||
                              DP_From.SelectedDate != null ||
                              DP_To.SelectedDate != null;
            Btn_Clear.Background = hasContent ? ClearActiveBrush : ClearIdleBrush;
        }

        // The value both "any" entries carry, so an unset dropdown reads as no filter at all.
        private const string AnyItem = "(any)";

        private static string SelectedFilter(ComboBox box)
        {
            string v = box?.SelectedItem as string;
            return string.IsNullOrEmpty(v) || v == AnyItem ? null : v;
        }

        private void RunSearch()
        {
            string callsign = TB_Callsign.Text.Trim().ToUpperInvariant();
            string country  = CB_Country.Text.Trim();
            string band     = SelectedFilter(CB_Band);
            string mode     = SelectedFilter(CB_Mode);
            string myCall   = SelectedFilter(CB_MyCall);
            string locator  = TB_Locator.Text.Trim();
            string square   = TB_Square.Text.Trim();
            DateTime? from  = DP_From.SelectedDate;
            DateTime? to    = DP_To.SelectedDate;

            if (string.IsNullOrEmpty(callsign) && string.IsNullOrEmpty(country) &&
                band == null && mode == null && myCall == null &&
                string.IsNullOrEmpty(locator) && string.IsNullOrEmpty(square) &&
                from == null && to == null)
            {
                ClearResults();
                return;
            }

            var results = _allQsos.AsEnumerable();

            if (!string.IsNullOrEmpty(callsign))
                results = results.Where(q => q.DXCall != null &&
                    q.DXCall.ToUpperInvariant().Contains(callsign));

            if (!string.IsNullOrEmpty(country))
                results = results.Where(q => q.Country != null &&
                    q.Country.IndexOf(country, StringComparison.OrdinalIgnoreCase) >= 0);

            // Band / mode / my callsign come from dropdowns built out of the log itself, so they are
            // exact matches - picking "20M" must not also bring in "20M" QSOs of some other band whose
            // name merely contains it.
            if (band != null)
                results = results.Where(q => string.Equals(q.Band, band, StringComparison.OrdinalIgnoreCase));

            if (mode != null)
                results = results.Where(q => string.Equals(q.Mode, mode, StringComparison.OrdinalIgnoreCase));

            if (myCall != null)
                results = results.Where(q => string.Equals(q.MyCall, myCall, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(locator))
                results = results.Where(q => q.DXLocator != null &&
                    q.DXLocator.IndexOf(locator, StringComparison.OrdinalIgnoreCase) >= 0);

            if (!string.IsNullOrEmpty(square))
                results = results.Where(q => q.SRX != null &&
                    q.SRX.IndexOf(square, StringComparison.OrdinalIgnoreCase) >= 0);

            // Dates are held as text; compare on the yyyyMMdd form so the ordering is the string
            // ordering and no per-QSO DateTime parsing is needed. "To" is inclusive of that whole day.
            if (from != null)
            {
                string fromKey = from.Value.ToString("yyyyMMdd");
                results = results.Where(q => string.Compare(DateKey(q.Date), fromKey, StringComparison.Ordinal) >= 0);
            }
            if (to != null)
            {
                string toKey = to.Value.ToString("yyyyMMdd");
                results = results.Where(q => string.Compare(DateKey(q.Date), toKey, StringComparison.Ordinal) <= 0);
            }

            // Newest first, sorted explicitly rather than relying on the order the log happened to hand
            // us - the Date header carries a sort arrow from the moment results appear, and an arrow
            // that is not backed by a real sort is worse than no arrow at all.
            var found = new ObservableCollection<QSO>(
                results.OrderByDescending(q => DateKey(q.Date), StringComparer.Ordinal)
                       .ThenByDescending(q => TimeKey(q.Time), StringComparer.Ordinal));
            ResultsGrid.DataContext = found;
            ShowDateSortIndicator();
            TB_Count.Text  = found.Count == 1 ? "1 QSO" : $"{found.Count} QSOs";
            TB_Status.Text = found.Count == 0
                ? "No QSOs found."
                : $"{found.Count} QSO{(found.Count == 1 ? "" : "s")} found.";
        }

        // A QSO date reduced to yyyyMMdd for comparison, whatever separators it was stored with
        // ("2019-03-14", "20190314" and "2019/03/14" all become "20190314").
        private static string DateKey(string date)
        {
            if (string.IsNullOrWhiteSpace(date)) return string.Empty;
            var sb = new System.Text.StringBuilder(8);
            foreach (char c in date)
            {
                if (char.IsDigit(c)) sb.Append(c);
                if (sb.Length == 8) break;
            }
            return sb.ToString();
        }

        // A QSO time reduced to digits ("18:43" -> "1843") so it compares as text in clock order.
        private static string TimeKey(string time)
        {
            if (string.IsNullOrWhiteSpace(time)) return string.Empty;
            var sb = new System.Text.StringBuilder(6);
            foreach (char c in time)
            {
                if (char.IsDigit(c)) sb.Append(c);
                if (sb.Length == 6) break;
            }
            return sb.ToString();
        }

        // Puts the sort arrow on Date (descending), matching how the results were just ordered, and
        // takes it off every other column so only one arrow is ever showing.
        private void ShowDateSortIndicator()
        {
            foreach (var column in ResultsGrid.Columns)
                column.SortDirection = null;
            if (Col_Date != null)
                Col_Date.SortDirection = ListSortDirection.Descending;
        }

        // Fills Band / Mode / My callsign from what the log actually contains, so the lists can only
        // offer choices that can return something.
        private void PopulateFilterLists()
        {
            void Fill(ComboBox box, System.Func<QSO, string> pick)
            {
                var values = _allQsos
                    .Select(pick)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                values.Insert(0, AnyItem);
                box.ItemsSource = values;
                box.SelectedIndex = 0;
            }

            Fill(CB_Band, q => q.Band);
            Fill(CB_Mode, q => q.Mode);
            Fill(CB_MyCall, q => q.MyCall);
        }

        // Dropdown / date-picker changes only refresh the Clear button. The search still runs on
        // Search, Enter or Esc, so picking a band does not fire a search through a half-typed callsign.
        private void Filter_Changed(object sender, RoutedEventArgs e) => UpdateClearButton();

        // Double-click a result: hand the QSO to the main window for editing and bring it to the
        // front. The Search window stays open, so a list of QSOs found can be worked through one
        // after another without searching again.
        private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var qso = ResultsGrid.SelectedItem as QSO;
            if (qso == null) return;

            var main = Owner as MainWindow ?? Application.Current.MainWindow as MainWindow;
            if (main == null) return;

            main.EditQsoFromSearch(qso);
        }

        // Frequency and Band sort by NUMBER, not by text.
        //
        // Left to the default string sort, "14.200000" comes before "7.100000" because '1' precedes
        // '7', and the bands read 10M, 160M, 20M, 40M - which looks like the sort is simply broken.
        // Every other column is genuinely text and keeps the built-in behaviour.
        private void ResultsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            string path = e.Column.SortMemberPath;
            bool byBand = path == "Band";
            if (!byBand && path != "Freq") return;   // text column: let the DataGrid handle it

            var view = CollectionViewSource.GetDefaultView(ResultsGrid.ItemsSource) as ListCollectionView;
            if (view == null) return;

            ListSortDirection direction = e.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            e.Column.SortDirection = direction;
            view.CustomSort = new QsoFrequencyComparer(byBand, direction == ListSortDirection.Ascending);
            e.Handled = true;
        }

        // Orders QSOs by frequency in MHz. For the Band column the band's own representative frequency
        // is used, so the bands come out in wavelength order (160M, 80M, 40M ...) rather than A-Z.
        private class QsoFrequencyComparer : System.Collections.IComparer
        {
            private readonly bool _byBand;
            private readonly int _sign;

            public QsoFrequencyComparer(bool byBand, bool ascending)
            {
                _byBand = byBand;
                _sign = ascending ? 1 : -1;
            }

            public int Compare(object x, object y) =>
                _sign * Value(x as QSO).CompareTo(Value(y as QSO));

            private double Value(QSO q)
            {
                if (q == null) return double.MaxValue;   // blanks sort to the end
                string text = _byBand
                    ? HolyLogParser.convertBandToFreq(q.Band ?? string.Empty)
                    : HolyLogParser.NormalizeFreqToMhz(q.Freq);
                double mhz;
                return double.TryParse(text, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out mhz)
                    ? mhz
                    : double.MaxValue;
            }
        }

        private void ClearResults()
        {
            ResultsGrid.DataContext = null;
            // No rows, so no column is sorted - leaving an arrow behind would claim otherwise.
            foreach (var column in ResultsGrid.Columns)
                column.SortDirection = null;
            TB_Count.Text  = "";
            TB_Status.Text = "Enter a Callsign or Country (or both) and press Search.";
        }

        // Placement is handled by WindowBounds (attached in the constructor); these remain only
        // because the XAML wires them up.
        private void Window_LocationChanged(object sender, EventArgs e) { }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { }
    }

    // Represents one country entry in the dropdown: name + flag image (same PNG assets as StatisticsWindow)
    public class SearchCountryItem
    {
        private static readonly System.Collections.Generic.Dictionary<string, BitmapImage> _flagCache =
            new System.Collections.Generic.Dictionary<string, BitmapImage>();

        public string      Name      { get; }
        public BitmapImage FlagImage { get; }

        public SearchCountryItem(string name)
        {
            Name      = name;
            FlagImage = GetFlagImage(name);
        }

        public override string ToString() => Name;  // shown in editable text box after selection

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
    }
}
