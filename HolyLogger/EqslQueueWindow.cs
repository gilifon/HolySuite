using HolyParser;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace HolyLogger
{
    // Window that lists the QSOs still waiting to be uploaded to eQSL and lets the user send them on
    // demand. Nothing is uploaded automatically: the QSOs sit here until the user presses "Send".
    // It owns no data of its own — it asks the caller for the current pending list (getPending) and
    // delegates the upload to the caller (sendAll, which runs MainWindow.PumpEqslQueue and returns the
    // number actually uploaded), then re-reads the list to reflect what went out.
    public class EqslQueueWindow : Window
    {
        private readonly Func<List<QSO>> _getPending;
        private readonly Func<Task<int>> _sendAll;

        private readonly TextBlock _header;
        private readonly TextBlock _callsignHeader;
        private readonly DataGrid _grid;
        private readonly Button _sendButton;
        private readonly TextBlock _status;

        public EqslQueueWindow(Func<List<QSO>> getPending, Func<Task<int>> sendAll)
        {
            _getPending = getPending;
            _sendAll = sendAll;

            Title = "QSOs waiting for eQSL";
            Width = 420;
            Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header row: "N QSOs" on the left, the station callsign in bold in the middle, and the
            // Send button on the right.
            var headerPanel = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _header = new TextBlock
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_header, 0);
            headerPanel.Children.Add(_header);

            _callsignHeader = new TextBlock
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_callsignHeader, 1);
            headerPanel.Children.Add(_callsignHeader);

            _sendButton = new Button
            {
                Content = "Send",
                MinWidth = 100,
                Padding = new Thickness(12, 4, 12, 4),
                FontSize = 14,
                FontWeight = FontWeights.Bold
            };
            _sendButton.Click += SendButton_Click;
            Grid.SetColumn(_sendButton, 2);
            headerPanel.Children.Add(_sendButton);

            Grid.SetRow(headerPanel, 0);
            root.Children.Add(headerPanel);

            // The waiting QSOs, with a vertical scrollbar that appears automatically when the list is
            // longer than the window.
            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                SelectionMode = DataGridSelectionMode.Single,
                FontSize = 14
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_grid, ScrollBarVisibility.Auto);
            _grid.Columns.Add(MakeColumn("Date", "Date", 1.2));
            _grid.Columns.Add(MakeColumn("Time", "Time", 1.0));
            _grid.Columns.Add(MakeColumn("Call sign", "DXCall", 1.4));
            _grid.Columns.Add(MakeFreqColumn(1.3));
            Grid.SetRow(_grid, 1);
            root.Children.Add(_grid);

            _status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), FontSize = 13, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(_status, 2);
            root.Children.Add(_status);

            Content = root;

            RefreshList();
        }

        private static DataGridTextColumn MakeColumn(string header, string path, double starWidth)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = new DataGridLength(starWidth, DataGridLengthUnitType.Star)
            };
        }

        // Freq column: plain frequency value in the cells, with the unit shown only in the header as
        // "Freq" followed by a smaller, bold "MHz".
        private static DataGridTextColumn MakeFreqColumn(double starWidth)
        {
            var header = new TextBlock();
            header.Inlines.Add(new System.Windows.Documents.Run("Freq "));
            header.Inlines.Add(new System.Windows.Documents.Run("MHz") { FontSize = 10, FontWeight = FontWeights.Bold });

            return new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding("Freq"),
                Width = new DataGridLength(starWidth, DataGridLengthUnitType.Star)
            };
        }

        // Re-reads the pending list and updates the header count and Send button state.
        public void RefreshList()
        {
            List<QSO> pending;
            try { pending = _getPending() ?? new List<QSO>(); }
            catch { pending = new List<QSO>(); }

            _grid.ItemsSource = pending;

            int n = pending.Count;
            _header.Text = n + (n == 1 ? " QSO" : " QSOs");
            _sendButton.IsEnabled = n > 0;

            // Show the station callsign(s) of the waiting QSOs in bold at the top (usually just one;
            // if QSOs from several callsigns are queued, list them).
            var calls = pending
                .Select(q => (q.MyCall ?? string.Empty).Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _callsignHeader.Text = string.Join(", ", calls);
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            _sendButton.IsEnabled = false;
            _status.Text = "Sending to eQSL…";

            int sent;
            try
            {
                sent = await _sendAll();
            }
            catch (Exception ex)
            {
                _status.Text = "Upload error: " + ex.Message;
                _sendButton.IsEnabled = true;
                return;
            }

            RefreshList();

            int remaining = _grid.Items.Count;
            if (remaining == 0)
                _status.Text = "All QSOs have been uploaded to eQSL. ✓";
            else
                _status.Text = sent + " uploaded, " + remaining + " still waiting. A callsign with no eQSL account " +
                    "won't send until you add its user name/password in Options → eQSL Service; otherwise eQSL may be offline.";
        }
    }
}
