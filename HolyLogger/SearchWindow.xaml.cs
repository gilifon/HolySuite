using HolyParser;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HolyLogger
{
    public partial class SearchWindow : Window
    {
        private readonly ObservableCollection<QSO> _allQsos;

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
        }

        // Pre-fills the Callsign box (used when opened from a log-row right-click). Setting the
        // text fires TextChanged, which enables the Search button.
        public void SetCallsign(string call)
        {
            TB_Callsign.Text = (call ?? string.Empty).Trim().ToUpperInvariant();
            TB_Callsign.CaretIndex = TB_Callsign.Text.Length;
            TB_Callsign.Focus();
        }

        private void SearchField_TextChanged(object sender, TextChangedEventArgs e)
        {
            Btn_Search.IsEnabled =
                !string.IsNullOrWhiteSpace(TB_Callsign.Text) ||
                !string.IsNullOrWhiteSpace(TB_Country.Text);
        }

        private void SearchField_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Btn_Search.IsEnabled)
                RunSearch();
        }

        private void Btn_Search_Click(object sender, RoutedEventArgs e)
        {
            RunSearch();
        }

        private void RunSearch()
        {
            string callsign = TB_Callsign.Text.Trim().ToUpperInvariant();
            string country  = TB_Country.Text.Trim();

            var results = _allQsos.AsEnumerable();

            if (!string.IsNullOrEmpty(callsign))
                results = results.Where(q => q.DXCall != null &&
                    q.DXCall.ToUpperInvariant().Contains(callsign));

            if (!string.IsNullOrEmpty(country))
                results = results.Where(q => q.Country != null &&
                    q.Country.IndexOf(country, StringComparison.OrdinalIgnoreCase) >= 0);

            var found = new ObservableCollection<QSO>(results);
            ResultsGrid.DataContext = found;

            TB_Count.Text = found.Count == 1 ? "1 QSO" : $"{found.Count} QSOs";

            TB_Status.Text = found.Count == 0
                ? "No QSOs found."
                : $"{found.Count} QSO{(found.Count == 1 ? "" : "s")} found.";
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
}
