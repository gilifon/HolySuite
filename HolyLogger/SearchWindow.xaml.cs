using HolyParser;
using System;
using System.Collections.ObjectModel;
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

        public SearchWindow(ObservableCollection<QSO> qsos)
        {
            InitializeComponent();
            _allQsos = qsos;

            var s = Properties.Settings.Default;
            if (s.SearchWindowLeft > 0 || s.SearchWindowTop > 0)
            {
                Left = s.SearchWindowLeft;
                Top  = s.SearchWindowTop;
            }
            if (s.SearchWindowWidth > 100)
                Width = s.SearchWindowWidth;
            if (s.SearchWindowHeight > 100)
                Height = s.SearchWindowHeight;

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

            UpdateClearButton();
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
            ClearResults();
            UpdateClearButton();
            TB_Callsign.Focus();
        }

        // Blue while either field has text (so it's clearly clickable), gray when empty.
        private void UpdateClearButton()
        {
            bool hasContent = !string.IsNullOrEmpty(TB_Callsign.Text) ||
                              !string.IsNullOrEmpty(CB_Country.Text);
            Btn_Clear.Background = hasContent ? ClearActiveBrush : ClearIdleBrush;
        }

        private void RunSearch()
        {
            string callsign = TB_Callsign.Text.Trim().ToUpperInvariant();
            string country  = CB_Country.Text.Trim();

            if (string.IsNullOrEmpty(callsign) && string.IsNullOrEmpty(country))
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

            var found = new ObservableCollection<QSO>(results);
            ResultsGrid.DataContext = found;
            TB_Count.Text  = found.Count == 1 ? "1 QSO" : $"{found.Count} QSOs";
            TB_Status.Text = found.Count == 0
                ? "No QSOs found."
                : $"{found.Count} QSO{(found.Count == 1 ? "" : "s")} found.";
        }

        private void ClearResults()
        {
            ResultsGrid.DataContext = null;
            TB_Count.Text  = "";
            TB_Status.Text = "Enter a Callsign or Country (or both) and press Search.";
        }

        private void Window_LocationChanged(object sender, EventArgs e)
        {
            if (Left >= 0) Properties.Settings.Default.SearchWindowLeft = Left;
            if (Top  >= 0) Properties.Settings.Default.SearchWindowTop  = Top;
            Properties.Settings.Default.Save();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Width  > 0) Properties.Settings.Default.SearchWindowWidth  = Width;
            if (Height > 0) Properties.Settings.Default.SearchWindowHeight = Height;
            Properties.Settings.Default.Save();
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
