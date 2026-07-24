using HolyParser;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HolyLogger
{
    // Date / Time entry rules for the Search window's editable cells.
    //
    // These REFUSE a badly formed value instead of quietly discarding it. The converters alone were
    // not enough: their ConvertBack returns Binding.DoNothing for junk, which leaves the stored value
    // safe but tells the operator nothing - the cell simply appears to accept "06:2" and moves on. A
    // failing rule keeps the cell in edit with a red border, so the value has to be corrected (or Esc
    // pressed to abandon the change) before the cell can be left.
    public class QsoDateRule : ValidationRule
    {
        internal static readonly string[] Accepted =
            { "dd-MM-yyyy", "dd/MM/yyyy", "dd.MM.yyyy", "yyyyMMdd", "yyyy-MM-dd" };

        public override ValidationResult Validate(object value, System.Globalization.CultureInfo culture)
        {
            string typed = (value as string)?.Trim();
            if (string.IsNullOrWhiteSpace(typed))
                return new ValidationResult(false, "A QSO must have a date — e.g. 19-04-2025.");

            return DateTime.TryParseExact(typed, Accepted,
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out DateTime _)
                ? ValidationResult.ValidResult
                : new ValidationResult(false, "Date must look like 19-04-2025.");
        }
    }

    public class QsoTimeRule : ValidationRule
    {
        internal static readonly string[] Accepted = { "HH:mm:ss", "HH:mm", "HHmmss", "HHmm" };

        public override ValidationResult Validate(object value, System.Globalization.CultureInfo culture)
        {
            string typed = (value as string)?.Trim();
            if (string.IsNullOrWhiteSpace(typed))
                return new ValidationResult(false, "A QSO must have a time — e.g. 06:22:00.");

            return DateTime.TryParseExact(typed, Accepted,
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out DateTime _)
                ? ValidationResult.ValidResult
                : new ValidationResult(false, "Time must look like 06:22 or 06:22:00.");
        }
    }

    public partial class SearchWindow : Window
    {
        private ObservableCollection<QSO> _allQsos;
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

        // logName names the log being searched, and is shown in the title bar. Any log can be searched
        // now - from the Log Manager, without activating it - so a window that just said "Log Search"
        // would leave you guessing whose QSOs you were about to edit.
        public SearchWindow(ObservableCollection<QSO> qsos, string logName = null)
        {
            InitializeComponent();
            _allQsos = qsos;

            string titleLog = string.IsNullOrWhiteSpace(logName) ? "(unnamed log)" : logName.Trim();
            TB_TitleLog.Text = titleLog;          // the bold half of the custom caption
            Title = "Log Search — " + titleLog;   // taskbar / Alt-Tab still use the plain Title

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

            // A correction to the last QSO touched would otherwise never be offered, because the row
            // is left by closing the window rather than by moving off it.
            Closing += (s, e) => OfferReupload();
        }

        private void OnMouseDownOutsideCapture(object sender, MouseButtonEventArgs e)
        {
            if (!CB_Country.IsDropDownOpen) return;

            // Did the press land on the Clear button? If so, clear on this first click.
            Point p = e.GetPosition(Btn_Clear);
            if (p.X >= 0 && p.Y >= 0 && p.X <= Btn_Clear.ActualWidth && p.Y <= Btn_Clear.ActualHeight)
                ClearAll();
        }

        // Pre-fills the callsign boxes (used when opened from a log-row right-click). The callsign is
        // split into the same two halves the boxes hold, so arriving here from the log shows how that
        // callsign breaks down rather than dropping it whole into one box.
        public void SetCallsign(string call, bool runSearch = false)
        {
            CallsignIdentity.Split((call ?? string.Empty).Trim().ToUpperInvariant(),
                                   out string prefix, out string suffix);
            TB_Prefix.Text = prefix;
            TB_Suffix.Text = suffix;
            TB_Suffix.CaretIndex = TB_Suffix.Text.Length;
            UpdateClearButton();
            TB_Prefix.Focus();
            if (runSearch)
                RunSearch();
        }

        // Pre-fills the Country box and (optionally) runs the search (used when opened from a country
        // row in the Statistics window). Clears the callsign so it is a pure country search.
        public void SetCountry(string country, bool runSearch = false)
        {
            TB_Prefix.Text = "";
            TB_Suffix.Text = "";
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
        // Typing only lights up the Clear button. The results are left alone until Search / Enter, so
        // deleting the last character of a callsign does not throw the whole log back at you mid-edit.
        private void SearchField_TextChanged(object sender, TextChangedEventArgs e)
        {
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
            // Prefix holds 7 characters, suffix 10 - measured rather than guessed in pixels, so the
            // boxes stay right if the theme font or size ever changes.
            SizeToCharacters(TB_Prefix, 7);
            SizeToCharacters(TB_Suffix, 10);

            // These three were far wider than anything they can hold. Sized to a real worst-case value
            // instead of a round number: a six-character grid square, a Holyland square, and - for the
            // callsign list - the longest entry the log actually produced, so the box fits 4X2XMAS
            // without leaving room for a callsign nobody has.
            SizeToSample(TB_Locator, "KM72OR");
            SizeToSample(TB_Square, "K07YZ");
            SizeToSample(CB_MyCall, LongestItem(CB_MyCall), dropDownArrow);

            // After the above: it measures the row, so the fields must already be their final size.
            AlignCommentBox();

            // Open showing the whole log, so the window starts as a view OF the log rather than a
            // blank form. Filters then narrow it down.
            RunSearch();

            TB_Prefix.Focus();
            Keyboard.Focus(TB_Prefix);
        }

        // Esc anywhere in the window clears both fields and the results (same as the Clear
        // button). PreviewKeyDown tunnels in before the ComboBox can swallow Esc to merely
        // close its dropdown, so Esc always performs the full clear.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+Z undoes a SAVED edit - but only where it would otherwise do nothing. Inside a text
            // box it still means "undo my typing", which is what the operator expects there, and inside
            // an open cell editor the same applies.
            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (_cellInEdit || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
                    return;
                UndoLastEdit();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Escape) return;

            // While a cell is being edited, Esc belongs to the DataGrid: it abandons the change and
            // puts the logged value back. This handler tunnels in FIRST, so without this guard Esc
            // would wipe every filter instead - and rebuild the grid underneath a row that was still
            // in edit. Cancelling an edit is what Esc means at that moment.
            if (_cellInEdit) return;

            ClearAll();
            e.Handled = true;
        }

        private void Btn_Search_Click(object sender, RoutedEventArgs e) => RunSearch();

        private void Btn_Clear_Click(object sender, RoutedEventArgs e) => ClearAll();

        // Paper QSL checkbox toggled in the search results. The two-way binding has already updated the
        // QSO; persist it and tell the Statistics window (if open) so its Paper QSL folder recomputes.
        private void PaperQsl_Changed(object sender, RoutedEventArgs e)
        {
            if (!((sender as CheckBox)?.DataContext is QSO qso)) return;
            try
            {
                DataAccess.GetInstance()?.SetPaperQslConfirmed(qso.id, qso.PaperQslConfirmed);
                var stats = Application.Current?.Windows.OfType<StatisticsWindow>().FirstOrDefault();
                if (stats != null && stats.IsLoaded) stats.NotifyPaperQslChanged(qso.id, qso.PaperQslConfirmed);
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        private void ClearAll()
        {
            TB_Prefix.Text          = "";
            TB_Suffix.Text          = "";
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
            if (CB_Lotw.Items.Count > 0)   CB_Lotw.SelectedIndex = 0;
            TB_Locator.Text = "";
            TB_Square.Text  = "";
            TB_Comment.Text = "";
            DP_From.SelectedDate = null;
            DP_To.SelectedDate   = null;

            // Back to the whole log, not to an empty grid - clearing a filter should reveal everything
            // again, exactly as removing a spreadsheet filter does.
            RunSearch();
            UpdateClearButton();
            TB_Prefix.Focus();
        }

        // Blue while any filter is set (so it's clearly clickable), gray when everything is empty.
        private void UpdateClearButton()
        {
            bool hasContent = !string.IsNullOrEmpty(TB_Prefix.Text) ||
                              !string.IsNullOrEmpty(TB_Suffix.Text) ||
                              !string.IsNullOrEmpty(CB_Country.Text) ||
                              !string.IsNullOrEmpty(TB_Locator.Text) ||
                              !string.IsNullOrEmpty(TB_Square.Text) ||
                              !string.IsNullOrEmpty(TB_Comment.Text) ||
                              SelectedFilter(CB_Band) != null ||
                              SelectedFilter(CB_Mode) != null ||
                              SelectedFilter(CB_MyCall) != null ||
                              SelectedFilter(CB_Lotw) != null ||
                              DP_From.SelectedDate != null ||
                              DP_To.SelectedDate != null;
            Btn_Clear.Background = hasContent ? ClearActiveBrush : ClearIdleBrush;
        }

        // Choices offered by the Band and Mode cell dropdowns. Deliberately the SAME lists the
        // Bad-QSO editor uses, so the two places that repair a QSO cannot offer different vocabularies.
        public static readonly string[] KnownBands =
        {
            "160M", "80M", "60M", "40M", "30M", "20M", "17M",
            "15M", "12M", "10M", "6M", "2M", "70CM", "13CM"
        };

        public static readonly string[] KnownModes =
        {
            "SSB", "USB", "LSB", "CW", "FM", "RTTY", "FT8", "FT4", "PSK31", "DIGI"
        };

        // The value both "any" entries carry, so an unset dropdown reads as no filter at all.
        private const string AnyItem = "(any)";

        private static string SelectedFilter(ComboBox box)
        {
            string v = box?.SelectedItem as string;
            return string.IsNullOrEmpty(v) || v == AnyItem ? null : v;
        }

        // Lines the Comment BOX up with the dropdowns above it: left edge under Band's dropdown, right
        // edge under Mode's.
        //
        // Measured rather than written as fixed numbers, because everything to the left of those
        // dropdowns is sized by label TEXT - "Country:", "Holyland Square:" - whose width depends on
        // the theme font. Hard-coded values would line up on one machine and be visibly out on the next.
        //
        // Runs after layout, since nothing has a position before then.
        private void AlignCommentBox()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (CB_Band == null || CB_Mode == null || TB_Comment == null ||
                        CommentGroup == null || FiltersPanel == null) return;
                    if (!CB_Band.IsArrangeValid || !CB_Mode.IsArrangeValid || !TB_Comment.IsArrangeValid) return;

                    double LeftOf(FrameworkElement e) =>
                        e.TransformToAncestor(FiltersPanel).Transform(new Point(0, 0)).X;

                    double bandLeft   = LeftOf(CB_Band);
                    double modeRight  = LeftOf(CB_Mode) + CB_Mode.ActualWidth;
                    double boxLeft    = LeftOf(TB_Comment);

                    // Shift the whole group right so the BOX (not its label) starts under Band. Only
                    // ever rightwards: if the fields ahead of Comment already reach past Band, pulling
                    // left would drag the box over its neighbour, so the left edge is left as it falls
                    // and only the right edge is made to line up.
                    double indent = bandLeft - boxLeft;
                    if (indent > 0)
                    {
                        CommentGroup.Margin = new Thickness(indent, 0, 0, 0);
                        boxLeft = bandLeft;
                    }

                    // Stretch to Mode's right edge. The floor keeps the box usable if the row is ever
                    // so crowded that there is almost nothing left.
                    TB_Comment.Width = Math.Max(60, modeRight - boxLeft);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Room a ComboBox needs beyond its text for the drop-down arrow and the gap before it.
        private const double dropDownArrow = 26;

        // The widest entry currently in a dropdown, so it can be sized to its real contents rather
        // than to a guess about what callsigns might exist.
        private static string LongestItem(ComboBox box)
        {
            string longest = string.Empty;
            if (box?.ItemsSource == null) return longest;
            foreach (var item in box.ItemsSource)
            {
                string text = item as string;
                if (text != null && text.Length > longest.Length) longest = text;
            }
            return longest;
        }

        // Sizes a control to fit one specific sample string in its own font.
        //
        // The sibling below measures repeated 'W', which guarantees nothing clips but is far wider than
        // real content - fine for a free-text box, wasteful for a field that only ever holds a grid
        // square. Here the actual worst-case value is measured instead.
        private static void SizeToSample(Control control, string sample, double extra = 0)
        {
            if (control == null || string.IsNullOrEmpty(sample)) return;
            try
            {
                var typeface = new Typeface(control.FontFamily, control.FontStyle, control.FontWeight, control.FontStretch);
                var text = new FormattedText(
                    sample,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    control.FontSize,
                    Brushes.Black,
                    VisualTreeHelper.GetDpi(control).PixelsPerDip);

                control.Width = Math.Ceiling(text.Width)
                              + control.Padding.Left + control.Padding.Right
                              + control.BorderThickness.Left + control.BorderThickness.Right
                              + 10          // caret / breathing room
                              + extra;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }   // keep the XAML fallback width
        }

        // Makes a text box exactly wide enough for `characters` characters of its own font, plus its
        // padding and border.
        //
        // Measured with 'W', the widest character a callsign can contain, so a full-width entry never
        // has to scroll sideways. A fixed pixel width would drift the moment the theme's font or size
        // changed; this asks the font itself.
        private static void SizeToCharacters(TextBox box, int characters)
        {
            try
            {
                var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
                var text = new FormattedText(
                    new string('W', characters),
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    box.FontSize,
                    Brushes.Black,
                    VisualTreeHelper.GetDpi(box).PixelsPerDip);

                box.Width = Math.Ceiling(text.Width)
                          + box.Padding.Left + box.Padding.Right
                          + box.BorderThickness.Left + box.BorderThickness.Right
                          + 6;   // caret room, so the last character is not flush against the border
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }   // keep the XAML fallback width
        }

        // True when the typed text matches this half of a callsign.
        //
        // "Starts with" rather than "contains", so typing 4Z finds 4Z5SL (prefix 4Z5) without also
        // dragging in every callsign that merely has those letters somewhere. For a prefix carrying a
        // leading stroke the part AFTER the stroke is tried too: 4X/OK1 is found by 4X and by OK1,
        // which are both reasonable things to be looking for.
        private static bool HalfMatches(string half, string typed, bool isPrefix)
        {
            if (string.IsNullOrEmpty(typed)) return true;
            if (string.IsNullOrEmpty(half)) return false;

            if (half.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;

            if (isPrefix)
            {
                int slash = half.LastIndexOf('/');
                if (slash >= 0 && slash < half.Length - 1 &&
                    half.Substring(slash + 1).StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void RunSearch()
        {
            string prefix   = TB_Prefix.Text.Trim();
            string suffix   = TB_Suffix.Text.Trim();
            string country  = CB_Country.Text.Trim();
            string band     = SelectedFilter(CB_Band);
            string mode     = SelectedFilter(CB_Mode);
            string myCall   = SelectedFilter(CB_MyCall);
            string locator  = TB_Locator.Text.Trim();
            string square   = TB_Square.Text.Trim();
            string comment  = TB_Comment.Text.Trim();
            string lotw     = SelectedFilter(CB_Lotw);
            DateTime? from  = DP_From.SelectedDate;
            DateTime? to    = DP_To.SelectedDate;

            // No filter set means show the WHOLE log, the way a spreadsheet shows every row until you
            // filter it. An empty grid told the operator nothing about what was in the log and made the
            // window feel broken before the first search.
            bool unfiltered = string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(suffix) &&
                              string.IsNullOrEmpty(country) && band == null && mode == null &&
                              myCall == null && string.IsNullOrEmpty(locator) &&
                              string.IsNullOrEmpty(square) && string.IsNullOrEmpty(comment) &&
                              lotw == null && from == null && to == null;

            var results = _allQsos.AsEnumerable();

            // Each QSO's callsign is split the same way the boxes are labelled, so what you type into
            // "Prefix" is compared against a real prefix and never against half of a suffix.
            if (!string.IsNullOrEmpty(prefix) || !string.IsNullOrEmpty(suffix))
                results = results.Where(q =>
                {
                    if (q.DXCall == null) return false;
                    CallsignIdentity.Split(q.DXCall, out string qPrefix, out string qSuffix);
                    return HalfMatches(qPrefix, prefix, isPrefix: true)
                        && HalfMatches(qSuffix, suffix, isPrefix: false);
                });

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

            if (lotw != null)
            {
                bool wantConfirmed = lotw == LotwConfirmed;
                results = results.Where(q => (q.LotwQslRcvd == 1) == wantConfirmed);
            }

            // Comments are free text, so this is a "contains" match - the useful thing is finding the
            // QSO where you noted something, not matching how the note began.
            if (!string.IsNullOrEmpty(comment))
                results = results.Where(q => q.Comment != null &&
                    q.Comment.IndexOf(comment, StringComparison.OrdinalIgnoreCase) >= 0);

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
            TB_Count.Text = found.Count == 1 ? "1 QSO" : $"{found.Count:N0} QSOs";
            TB_Status.Text = unfiltered
                ? $"Showing the whole log — {found.Count:N0} QSO{(found.Count == 1 ? "" : "s")}. Set any filter above to narrow it down."
                : found.Count == 0
                    ? "No QSOs found."
                    : $"{found.Count:N0} QSO{(found.Count == 1 ? "" : "s")} found.";
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

            // Fixed choices, not values found in the log: "not confirmed" has to be offerable even when
            // every QSO happens to be confirmed, and the other way round.
            CB_Lotw.ItemsSource = new List<string> { AnyItem, LotwConfirmed, LotwNotConfirmed };
            CB_Lotw.SelectedIndex = 0;
        }

        private const string LotwConfirmed = "Confirmed";
        private const string LotwNotConfirmed = "Not confirmed";

        // Dropdown / date-picker changes only refresh the Clear button. The search still runs on
        // Search, Enter or Esc, so picking a band does not fire a search through a half-typed callsign.
        private void Filter_Changed(object sender, RoutedEventArgs e) => UpdateClearButton();

        // ---- Undo -----------------------------------------------------------------------------------
        //
        // Every committed cell edit becomes one step, kept while the window is open. A step holds ALL
        // the fields that actually changed, not just the cell that was typed in: editing the frequency
        // also rewrites the band, and undoing one without the other would leave a QSO with the old
        // frequency and the new band - a contradiction neither value would explain.
        //
        // The changed set is found by comparing a snapshot taken before the save with the values after
        // it, so a derived rewrite is captured automatically rather than by remembering to list it here.
        private class EditStep
        {
            public QSO Qso;
            public Dictionary<string, string> Before;   // only the fields that changed
            public string Label;
        }

        private readonly Stack<EditStep> _undo = new Stack<EditStep>();

        private static readonly string[] EditableFields =
            { "Date", "Time", "Name", "Freq", "Band", "RST_RCVD", "RST_SENT", "Mode", "SRX", "Comment" };

        private static readonly Dictionary<string, System.Reflection.PropertyInfo> FieldProps = BuildFieldProps();

        private static Dictionary<string, System.Reflection.PropertyInfo> BuildFieldProps()
        {
            var map = new Dictionary<string, System.Reflection.PropertyInfo>(StringComparer.Ordinal);
            foreach (string field in EditableFields)
            {
                var property = typeof(QSO).GetProperty(field);
                if (property != null && property.CanRead && property.CanWrite && property.PropertyType == typeof(string))
                    map[field] = property;
            }
            return map;
        }

        private static Dictionary<string, string> Snapshot(QSO qso)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in FieldProps)
                snapshot[pair.Key] = pair.Value.GetValue(qso) as string;
            return snapshot;
        }

        // Column headers, so the status line says "RST-R" rather than "RST_RCVD".
        private static string Pretty(string field)
        {
            switch (field)
            {
                case "RST_RCVD": return "RST-R";
                case "RST_SENT": return "RST-S";
                case "Freq":     return "Frequency";
                case "SRX":      return "Exchange";
                default:         return field;
            }
        }

        private void RecordUndoStep(QSO qso, Dictionary<string, string> before)
        {
            var changed = new Dictionary<string, string>(StringComparer.Ordinal);
            var after = Snapshot(qso);
            foreach (var pair in before)
            {
                string was = pair.Value ?? string.Empty;
                after.TryGetValue(pair.Key, out string now);
                if (!string.Equals(was, now ?? string.Empty, StringComparison.Ordinal))
                    changed[pair.Key] = pair.Value;
            }
            if (changed.Count == 0) return;   // committed the same value: nothing to undo

            var names = new List<string>();
            foreach (string key in changed.Keys) names.Add(Pretty(key));

            _undo.Push(new EditStep
            {
                Qso = qso,
                Before = changed,
                Label = $"{qso.DXCall} — {string.Join(", ", names)}"
            });
            UpdateUndoButton();
        }

        private void UpdateUndoButton()
        {
            if (Btn_Undo == null) return;
            bool any = _undo.Count > 0;
            Btn_Undo.IsEnabled = any;
            Btn_Undo.Content = any ? $"Undo ({_undo.Count})" : "Undo";
            Btn_Undo.ToolTip = any
                ? "Undo: " + _undo.Peek().Label
                : "Nothing to undo. Changes made in this window can be undone until it is closed.";
        }

        private void Btn_Undo_Click(object sender, RoutedEventArgs e) => UndoLastEdit();

        // Re-reads the rows so a changed display option (the LoTW callsign tint) shows immediately.
        internal void RefreshRows()
        {
            if (_cellInEdit) return;   // a refresh inside an edit transaction is not allowed
            try { ResultsGrid.Items.Refresh(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Points the window at a freshly-loaded QSO collection and re-runs the current search, so an
        // OPEN Search window reflects data that changed underneath it - the LoTW confirmation marking
        // reassigns the main window's collection, and without this the search would keep showing the
        // old objects, which never received the ticks.
        public void ReplaceSource(ObservableCollection<QSO> qsos)
        {
            if (qsos == null || _cellInEdit) return;
            _allQsos = qsos;
            try { RunSearch(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void UndoLastEdit()
        {
            if (_undo.Count == 0) return;

            EditStep step = _undo.Pop();
            try
            {
                foreach (var pair in step.Before)
                    FieldProps[pair.Key].SetValue(step.Qso, pair.Value);

                DataAccess.GetInstance()?.Update(step.Qso);
                try { ResultsGrid.Items.Refresh(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                // Said plainly: putting the values back does NOT chase the QSL services, which may
                // already have been handed the corrected version.
                TB_Status.Text = $"Undone: {step.Label}."
                               + (_undo.Count > 0 ? $"  {_undo.Count} more can be undone." : "  Nothing left to undo.")
                               + "  (Any upload already queued is not withdrawn.)";
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                TB_Status.Text = "Could not undo that change: " + ex.Message;
            }
            UpdateUndoButton();
        }

        // ---- Re-uploading a QSO after it has been corrected -----------------------------------------
        //
        // Held per QSO rather than per cell: correcting Name, then RST, then Comment on one contact is
        // one correction, and asking three times would train the operator to dismiss the question.
        private QSO _editedQso;
        private readonly SortedSet<string> _editedFields = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        // Set when a save rewrote a cell the operator did not type in; acted on once the row's edit
        // transaction has closed (see SaveEditedQso).
        private bool _refreshWhenRowDone;

        // True between opening a cell editor and closing it, so the window-level Esc handler can stand
        // aside and let the DataGrid cancel the edit.
        private bool _cellInEdit;

        private void RememberEdit(QSO qso, string fieldLabel)
        {
            // Moved to a different QSO without a row commit in between - settle up for the old one.
            if (_editedQso != null && !ReferenceEquals(_editedQso, qso))
                OfferReupload();

            _editedQso = qso;
            if (!string.IsNullOrWhiteSpace(fieldLabel)) _editedFields.Add(fieldLabel);
        }

        // The upload services this operator actually uses, each flagged with whether it already holds
        // this QSO.
        private static List<KeyValuePair<string, bool>> ServicesFor(QSO qso)
        {
            var list = new List<KeyValuePair<string, bool>>();
            var s = Properties.Settings.Default;
            if (s.UseLotwService)    list.Add(new KeyValuePair<string, bool>("LoTW", qso.LotwStatus == 1));
            if (s.UseEqslService)    list.Add(new KeyValuePair<string, bool>("eQSL", qso.EqslStatus == 1));
            if (s.UseQrzLogbook)     list.Add(new KeyValuePair<string, bool>("QRZ Logbook", qso.QrzStatus == 1));
            if (s.UseClublogService) list.Add(new KeyValuePair<string, bool>("Club Log", qso.ClublogStatus == 1));
            return list;
        }

        // Offers to send the corrected QSO to the logging services, by putting it back in their normal
        // pending queues - the same queues a brand-new QSO goes through, so no second upload path
        // exists to drift out of step with the first.
        private void OfferReupload()
        {
            QSO qso = _editedQso;
            var fields = new List<string>(_editedFields);
            _editedQso = null;
            _editedFields.Clear();

            if (qso == null || fields.Count == 0) return;

            var services = ServicesFor(qso);
            if (services.Count == 0) return;   // no upload services switched on - nothing to offer

            try
            {
                bool anyAlreadySent = services.Exists(x => x.Value);

                var text = new StringBuilder();
                text.AppendLine($"{qso.DXCall}   {qso.Date} {qso.Time}");
                text.AppendLine("Changed: " + string.Join(", ", fields));
                text.AppendLine();
                text.AppendLine("Send the corrected QSO to:");
                foreach (var service in services)
                    text.AppendLine("    • " + service.Key + (service.Value ? "     (already uploaded)" : ""));

                if (anyAlreadySent)
                {
                    text.AppendLine();
                    text.AppendLine("A service that already holds this QSO usually IGNORES a repeat upload, so the");
                    text.AppendLine("correction may never reach it. And if the date, time, mode or frequency changed,");
                    text.AppendLine("it may store the contact a SECOND time instead of replacing the old one.");
                }

                text.AppendLine();
                text.Append("Put it back in the upload queue?");

                if (!HolyMessageBox.ShowConfirm(text.ToString(), "Upload the corrected QSO?",
                                                HolyMsgType.Warning, this))
                    return;

                var dal = DataAccess.GetInstance();
                if (dal == null) return;

                var s = Properties.Settings.Default;
                if (s.UseLotwService)    { dal.SetLotwStatus(qso.id, 0);    qso.LotwStatus = 0; }
                if (s.UseEqslService)    { dal.SetEqslStatus(qso.id, 0);    qso.EqslStatus = 0; }
                if (s.UseQrzLogbook)     { dal.SetQrzStatus(qso.id, 0);     qso.QrzStatus = 0; }
                if (s.UseClublogService) { dal.SetClublogStatus(qso.id, 0); qso.ClublogStatus = 0; }

                TB_Status.Text = $"{qso.DXCall} queued for upload to " +
                                 string.Join(", ", services.ConvertAll(x => x.Key)) + ".";
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                TB_Status.Text = "Could not queue the QSO for upload: " + ex.Message;
            }
        }

        // Leaving the row means the correction to THIS QSO is finished.
        private void ResultsGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            // Deferred for the same reason as the cell commit: the final write lands after this event,
            // and the row's edit transaction is only closed once it has.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_refreshWhenRowDone)
                {
                    _refreshWhenRowDone = false;
                    try { ResultsGrid.Items.Refresh(); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
                OfferReupload();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // Band is not editable while the frequency already decides it.
        //
        // The band is derived from the frequency, so editing it on its own can only ever produce a
        // contradiction - or cost precision, since making the pair agree again would round a logged
        // 14.206000 down to the band's plain 14.2. Change the frequency and the band follows.
        //
        // The exception matters: when the frequency is missing, or is a value no band table
        // recognises, it decides nothing and the band has to stay editable, otherwise exactly the rows
        // that need repairing would be the ones that could not be repaired.
        private void ResultsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (e.Column?.SortMemberPath != "Band") return;

            var qso = e.Row?.Item as QSO;
            if (qso == null) return;

            string mhz = HolyLogParser.NormalizeFreqToMhz(qso.Freq);
            string derived = mhz == null ? null : HolyLogParser.convertFreqToBand(mhz);
            if (string.IsNullOrWhiteSpace(derived)) return;   // frequency settles nothing - allow it

            e.Cancel = true;
            TB_Status.Text = $"Band is set by the frequency ({qso.Freq}). "
                           + "Edit the frequency and the band follows.";
        }

        // Runs after the handler above, so a cell it refused is not counted as being in edit.
        private void ResultsGrid_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
            => _cellInEdit = true;

        // Edits are made in the results table itself, spreadsheet style, and saved when the cell is
        // left. Date, Time and Callsign are deliberately NOT editable: the callsign cell is the QRZ
        // link, and a click that sometimes opens a web page and sometimes starts typing over a
        // callsign is the kind of surprise that loses log data.
        private void ResultsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            _cellInEdit = false;
            if (e.EditAction != DataGridEditAction.Commit) return;   // Esc: nothing was changed

            var qso = e.Row?.Item as QSO;
            if (qso == null) return;

            string edited = e.Column?.SortMemberPath;
            string label = e.Column?.Header?.ToString();

            // Captured BEFORE the editor writes back. Two jobs: it is what Undo restores, and it guards
            // against a dropdown destroying a value it does not recognise - a QSO on a band outside
            // KnownBands (say 4M) leaves the ComboBox with nothing selected, and committing that would
            // write a null straight over the band.
            Dictionary<string, string> before = Snapshot(qso);

            // The editor writes its value back as part of committing, which finishes AFTER this event.
            // Saving here would store the OLD value, so the write is deferred one turn.
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    SaveEditedQso(qso, edited, before);
                    RecordUndoStep(qso, before);
                    RememberEdit(qso, label);
                }),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void SaveEditedQso(QSO qso, string edited, Dictionary<string, string> before)
        {
            try
            {
                // Never let an unrecognised value be blanked out by the dropdown (see above).
                if (string.IsNullOrWhiteSpace(qso.Band)) qso.Band = before["Band"];
                if (string.IsNullOrWhiteSpace(qso.Mode)) qso.Mode = before["Mode"];

                // Frequency and band are uploaded together and TQSL rejects a contact where they
                // contradict each other, so an edit to either keeps the pair honest.
                string note = string.Empty;
                if (edited == "Freq")
                {
                    // The frequency is the precise value, so it decides the band.
                    string mhz = HolyLogParser.NormalizeFreqToMhz(qso.Freq);
                    string band = mhz == null ? null : HolyLogParser.convertFreqToBand(mhz);
                    if (!string.IsNullOrWhiteSpace(band) &&
                        !string.Equals(band, qso.Band, StringComparison.OrdinalIgnoreCase))
                    {
                        qso.Band = band;
                        note = $"  (band set to {band})";
                    }
                }
                else if (edited == "Band")
                {
                    // Reachable only for a QSO whose frequency decides nothing (see
                    // ResultsGrid_BeginningEdit). A MISSING frequency is filled in from the band, which
                    // at least puts the QSO in the right part of the spectrum for an upload. A
                    // frequency that is present but unrecognised is left strictly alone: it is real
                    // logged data, and replacing it with a round number would destroy the only record
                    // of where the contact actually was.
                    if (string.IsNullOrWhiteSpace(qso.Freq))
                    {
                        string standard = HolyLogParser.convertBandToFreq(qso.Band ?? string.Empty);
                        if (!string.IsNullOrWhiteSpace(standard))
                        {
                            qso.Freq = standard;
                            note = $"  (frequency set to {standard})";
                        }
                    }
                }

                var dal = DataAccess.GetInstance();
                if (dal == null) return;
                dal.Update(qso);

                // QSO has no change notification, so a cell this method rewrote BEHIND the operator's
                // back (the Band, after a frequency edit) only redraws when the rows are re-read.
                //
                // Deliberately NOT refreshed here: the row is still inside its edit transaction at this
                // point and WPF forbids Items.Refresh() during one. Doing it anyway threw, and the
                // swallowed exception left the cell drawn blank - which looked exactly like the edit
                // had wiped a value. It is done once the row is finished instead.
                _refreshWhenRowDone = true;

                TB_Status.Text = $"Saved: {qso.DXCall}  {qso.Date} {qso.Time}{note}";
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                TB_Status.Text = "Could not save the change: " + ex.Message;
            }
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

        // Placement is handled by WindowBounds (attached in the constructor); these remain only
        // because the XAML wires them up.
        private void Window_LocationChanged(object sender, EventArgs e) { }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { }

        // Custom caption buttons - WindowStyle=None removed the OS ones.
        private void TitleBar_Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void TitleBar_MaxRestore_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        private void TitleBar_Close_Click(object sender, RoutedEventArgs e) => Close();

        // Keep the maximize/restore glyph in sync with the window state.
        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (TitleBar_MaxRestoreBtn == null) return;
            bool maximized = WindowState == WindowState.Maximized;
            TitleBar_MaxRestoreBtn.Content = maximized ? "" : "";
            TitleBar_MaxRestoreBtn.ToolTip = maximized ? "Restore Down" : "Maximize";
        }
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
