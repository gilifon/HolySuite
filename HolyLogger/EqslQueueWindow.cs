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
    // Window that lists the QSOs waiting to be uploaded to a service and lets the user send them
    // on demand. Reused for eQSL, LoTW, and QRZ via constructor callbacks.
    //
    // Optional dismissed section: when getDismissed + requeueDismissed are provided, a second
    // panel below the pending list shows QSOs that were cleared from the queue without being
    // uploaded (status=2), with a "Re-queue All" button to move them back to pending.
    public class EqslQueueWindow : Window
    {
        private readonly string _serviceName;
        private readonly Func<List<QSO>> _getPending;
        private readonly Func<Task<int>> _sendAll;
        private readonly Func<List<QSO>> _getDismissed;   // null = no dismissed section
        private readonly Action _requeueDismissed;
        private readonly Action<QSO> _dismissOne;         // null = no per-QSO "Remove from queue" button
        private readonly Action<QSO> _deleteFromLog;      // null = no per-QSO "Delete QSO" button

        private readonly TextBlock _pendingCountLabel;
        private readonly TextBlock _callsignLabel;
        private readonly DataGrid _pendingGrid;
        private readonly Button _uploadButton;
        private readonly Button _removeButton;            // null unless _dismissOne was supplied
        private readonly Button _deleteButton;            // null unless _deleteFromLog was supplied
        private readonly TextBlock _statusText;

        private readonly Border _dismissedSection;
        private readonly TextBlock _dismissedCountLabel;
        private readonly Button _requeueButton;
        private readonly DataGrid _dismissedGrid;

        public EqslQueueWindow(
            Func<List<QSO>> getPending,
            Func<Task<int>> sendAll,
            string serviceName = "eQSL",
            Func<List<QSO>> getDismissed = null,
            Action requeueDismissed = null,
            Action<QSO> dismissOne = null,
            Action<QSO> deleteFromLog = null)
        {
            _serviceName = serviceName;
            _getPending = getPending;
            _sendAll = sendAll;
            _getDismissed = getDismissed;
            _requeueDismissed = requeueDismissed;
            _dismissOne = dismissOne;
            _deleteFromLog = deleteFromLog;

            bool hasDismissed = getDismissed != null;

            Title = $"QSOs waiting for {serviceName}";
            Width = 680;
            Height = hasDismissed ? 580 : 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // 0: pending header
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) }); // 1: pending grid
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // 2: status text
            if (hasDismissed)
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 3: dismissed section

            // ── Pending header ──────────────────────────────────────────────
            var headerPanel = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _pendingCountLabel = new TextBlock
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_pendingCountLabel, 0);
            headerPanel.Children.Add(_pendingCountLabel);

            _callsignLabel = new TextBlock
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_callsignLabel, 1);
            headerPanel.Children.Add(_callsignLabel);

            // Right side: an optional "Remove from queue" button (per-QSO dismiss) then the Upload button.
            var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(rightButtons, 2);
            headerPanel.Children.Add(rightButtons);

            if (_dismissOne != null)
            {
                _removeButton = new Button
                {
                    Content = "Remove from queue",
                    Padding = new Thickness(12, 4, 12, 4),
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 8, 0),
                    IsEnabled = false,
                    ToolTip = "Take the selected QSO out of this upload queue WITHOUT uploading it. " +
                              "It moves to the Dismissed list below and can be re-queued. The QSO itself is not deleted."
                };
                _removeButton.Click += RemoveButton_Click;
                rightButtons.Children.Add(_removeButton);
            }

            if (_deleteFromLog != null)
            {
                _deleteButton = new Button
                {
                    Content = "Delete QSO",
                    Padding = new Thickness(12, 4, 12, 4),
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 8, 0),
                    IsEnabled = false,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x00, 0x00)),
                    ToolTip = "Permanently DELETE the selected QSO from its log (not just from the queue). " +
                              "This cannot be undone."
                };
                _deleteButton.Click += DeleteButton_Click;
                rightButtons.Children.Add(_deleteButton);
            }

            _uploadButton = new Button
            {
                Content = "Upload",
                MinWidth = 100,
                Padding = new Thickness(12, 4, 12, 4),
                FontSize = 14,
                FontWeight = FontWeights.Bold
            };
            _uploadButton.Click += UploadButton_Click;
            rightButtons.Children.Add(_uploadButton);

            Grid.SetRow(headerPanel, 0);
            root.Children.Add(headerPanel);

            // ── Pending DataGrid ─────────────────────────────────────────────
            _pendingGrid = MakeGrid();
            // The per-QSO actions ("Remove from queue" / "Delete QSO") act on the selected pending QSO,
            // so they're only enabled when one is picked.
            _pendingGrid.SelectionChanged += (s, e) =>
            {
                bool hasSel = _pendingGrid.SelectedItem is QSO;
                if (_removeButton != null) _removeButton.IsEnabled = hasSel;
                if (_deleteButton != null) _deleteButton.IsEnabled = hasSel;
            };
            Grid.SetRow(_pendingGrid, 1);
            root.Children.Add(_pendingGrid);

            // ── Status text ──────────────────────────────────────────────────
            _statusText = new TextBlock
            {
                Margin = new Thickness(0, 6, 0, hasDismissed ? 6 : 0),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(_statusText, 2);
            root.Children.Add(_statusText);

            // ── Dismissed section (optional) ─────────────────────────────────
            if (hasDismissed)
            {
                var dismissedContent = new Grid();
                dismissedContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                dismissedContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var dismissedHeader = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                dismissedHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                dismissedHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                _dismissedCountLabel = new TextBlock
                {
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x40, 0x40)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(_dismissedCountLabel, 0);
                dismissedHeader.Children.Add(_dismissedCountLabel);

                _requeueButton = new Button
                {
                    Content = "Re-queue All",
                    Padding = new Thickness(10, 3, 10, 3),
                    FontSize = 13,
                    Visibility = Visibility.Collapsed
                };
                _requeueButton.Click += RequeueButton_Click;
                Grid.SetColumn(_requeueButton, 1);
                dismissedHeader.Children.Add(_requeueButton);

                Grid.SetRow(dismissedHeader, 0);
                dismissedContent.Children.Add(dismissedHeader);

                _dismissedGrid = MakeGrid();
                Grid.SetRow(_dismissedGrid, 1);
                dismissedContent.Children.Add(_dismissedGrid);

                _dismissedSection = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Padding = new Thickness(0, 8, 0, 0),
                    Child = dismissedContent
                };

                Grid.SetRow(_dismissedSection, 3);
                root.Children.Add(_dismissedSection);
            }

            Content = root;
            RefreshList();
        }

        private static DataGrid MakeGrid()
        {
            var grid = new DataGrid
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
            // Same column-header look as the main log table: background comes from the LogHeaderBg theme
            // token, so it follows the "Customize Colors" scheme editor.
            grid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();
            // Every column auto-sizes to its own content/header, so nothing (especially long log names)
            // is trimmed; a horizontal scrollbar appears if the total exceeds the window width.
            ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
            // Date/Time are shown in a friendly form ("11 Jul 2026", "19:30:43") via display-only
            // converters; the QSO's stored Date/Time (yyyyMMdd / HHmmss) are untouched, so uploads are
            // unaffected.
            grid.Columns.Add(MakeColumn("Date", "Date", new QsoDateDisplayConverter()));
            grid.Columns.Add(MakeColumn("Time", "Time", new QsoTimeDisplayConverter()));
            grid.Columns.Add(MakeColumn("Callsign", "DXCall"));
            grid.Columns.Add(MakeColumn("Band", "Band"));
            grid.Columns.Add(MakeColumn("Mode", "Mode"));
            grid.Columns.Add(MakeFreqColumn());
            // Which log each QSO belongs to (the queue pools QSOs from every log). It's the last column and
            // stretches to fill the remaining width (so there's no unused blank column on the right); the
            // fixed columns above stay Auto so they're never trimmed, and the full name is on hover.
            var logCol = MakeColumn("Log", "LogName");
            logCol.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            var logStyle = new Style(typeof(TextBlock));
            logStyle.Setters.Add(new Setter(TextBlock.ToolTipProperty, new Binding("LogName")));
            logCol.ElementStyle = logStyle;
            grid.Columns.Add(logCol);
            return grid;
        }

        private static DataGridTextColumn MakeColumn(string header, string path, IValueConverter converter = null)
        {
            var binding = new Binding(path);
            if (converter != null) binding.Converter = converter;
            return new DataGridTextColumn
            {
                Header = header,
                Binding = binding,
                Width = DataGridLength.Auto
            };
        }

        private static DataGridTextColumn MakeFreqColumn()
        {
            var header = new TextBlock();
            header.Inlines.Add(new System.Windows.Documents.Run("Freq "));
            header.Inlines.Add(new System.Windows.Documents.Run("MHz") { FontSize = 10, FontWeight = FontWeights.Bold });
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding("Freq"),
                Width = DataGridLength.Auto
            };
        }

        public void RefreshList()
        {
            // Pending
            List<QSO> pending;
            try { pending = _getPending() ?? new List<QSO>(); }
            catch { pending = new List<QSO>(); }

            _pendingGrid.ItemsSource = pending;
            int n = pending.Count;
            _pendingCountLabel.Text = n + (n == 1 ? " QSO pending" : " QSOs pending");
            _uploadButton.IsEnabled = n > 0;

            var calls = pending
                .Select(q => (q.MyCall ?? string.Empty).Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _callsignLabel.Text = string.Join(", ", calls);

            // Dismissed
            if (_getDismissed != null)
            {
                List<QSO> dismissed;
                try { dismissed = _getDismissed() ?? new List<QSO>(); }
                catch { dismissed = new List<QSO>(); }

                _dismissedGrid.ItemsSource = dismissed;
                int d = dismissed.Count;
                _dismissedCountLabel.Text = d == 0
                    ? $"No dismissed QSOs — all cleared QSOs have been re-queued or uploaded."
                    : $"{d} QSO{(d == 1 ? "" : "s")} dismissed — cleared without being uploaded to {_serviceName}";
                _requeueButton.Visibility = d > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            _uploadButton.IsEnabled = false;
            _statusText.Text = $"Uploading to {_serviceName}…";

            int sent;
            try { sent = await _sendAll(); }
            catch (Exception ex)
            {
                if (!IsLoaded) return;
                _statusText.Text = "Upload error: " + ex.Message;
                _uploadButton.IsEnabled = true;
                return;
            }

            if (!IsLoaded) return;
            RefreshList();

            int remaining = _pendingGrid.Items.Count;
            _statusText.Text = remaining == 0
                ? $"All QSOs uploaded to {_serviceName}. ✓"
                : $"{sent} uploaded, {remaining} still waiting.";
        }

        // Removes the selected pending QSO from THIS service's queue without uploading it (a per-QSO
        // dismiss). It reappears in the Dismissed list and can be re-queued. Only this service is affected.
        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_dismissOne == null) return;
            if (!(_pendingGrid.SelectedItem is QSO q)) return;
            try { _dismissOne(q); }
            catch (System.Exception ex)
            {
                _statusText.Text = "Remove failed: " + ex.Message;
                return;
            }
            RefreshList();
            _statusText.Text = $"QSO removed from the {_serviceName} queue (moved to Dismissed — not uploaded).";
        }

        // Permanently deletes the selected QSO from its LOG (not just this queue), after a clear warning.
        // Deleting the QSO row removes it from every service's queue at once.
        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_deleteFromLog == null) return;
            if (!(_pendingGrid.SelectedItem is QSO q)) return;

            string when = ((q.Date ?? string.Empty) + " " + (q.Time ?? string.Empty)).Trim();
            string logPart = string.IsNullOrWhiteSpace(q.LogName) ? "its log" : "the log \"" + q.LogName + "\"";
            bool ok = HolyMessageBox.ShowConfirm(
                "Delete this QSO — " + q.DXCall + " on " + q.Band + " " + q.Mode +
                (when.Length > 0 ? " (" + when + ")" : string.Empty) + "?\n\n" +
                "This permanently DELETES the QSO from " + logPart + " — not just from the upload queue — " +
                "and cannot be undone.",
                "Delete QSO from log", HolyMsgType.Warning, this);
            if (!ok) return;

            try { _deleteFromLog(q); }
            catch (System.Exception ex)
            {
                _statusText.Text = "Delete failed: " + ex.Message;
                return;
            }
            RefreshList();
            _statusText.Text = "QSO deleted from the log (and removed from every upload queue).";
        }

        private void RequeueButton_Click(object sender, RoutedEventArgs e)
        {
            try { _requeueDismissed?.Invoke(); }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            _statusText.Text = "Dismissed QSOs moved back to the upload queue.";
            RefreshList();
        }
    }
}
