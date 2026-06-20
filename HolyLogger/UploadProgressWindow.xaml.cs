using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HolyLogger
{
    public partial class UploadProgressWindow : Window
    {
        private readonly TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>();
        private int _success, _failed;

        public UploadProgressWindow()
        {
            InitializeComponent();
        }

        // Adds a service section header before the QSO rows for that service.
        public void StartService(string name, int count)
        {
            if (LogPanel.Children.Count > 0)
            {
                LogPanel.Children.Add(new Separator
                {
                    Margin = new Thickness(0, 6, 0, 6),
                    Background = new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xDA))
                });
            }
            LogPanel.Children.Add(new TextBlock
            {
                Text = $"{name}  ({count} QSO{(count != 1 ? "s" : "")})",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8)),
                Margin = new Thickness(0, 0, 0, 3)
            });
            ScrollToEnd();
        }

        // Adds a service that was skipped (offline, not configured).
        public void SkipService(string name, string reason)
        {
            if (LogPanel.Children.Count > 0)
            {
                LogPanel.Children.Add(new Separator
                {
                    Margin = new Thickness(0, 6, 0, 6),
                    Background = new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xDA))
                });
            }
            LogPanel.Children.Add(new TextBlock
            {
                Text = $"{name} — {reason}",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x5C, 0x00)),
                Margin = new Thickness(0, 0, 0, 3),
                TextWrapping = TextWrapping.Wrap
            });
            ScrollToEnd();
        }

        // Adds one QSO result row (✓ / ✗ callsign  band  mode).
        public void ReportQso(string callsign, string band, string mode, bool ok)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 1, 0, 1) };
            var okColor = ok ? Color.FromRgb(0x1E, 0x7E, 0x34) : Color.FromRgb(0xCC, 0x00, 0x00);

            row.Children.Add(new TextBlock
            {
                Text = ok ? "✓" : "✗",
                Width = 20,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = new SolidColorBrush(okColor),
                VerticalAlignment = VerticalAlignment.Center
            });

            string label = callsign ?? "";
            if (!string.IsNullOrWhiteSpace(band)) label += "  " + band;
            if (!string.IsNullOrWhiteSpace(mode)) label += "  " + mode;

            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = new SolidColorBrush(ok ? Colors.Black : okColor),
                VerticalAlignment = VerticalAlignment.Center
            });

            LogPanel.Children.Add(row);
            if (ok) _success++; else _failed++;
            ScrollToEnd();
        }

        // Adds a single result line for a batch service like LoTW (TQSL signs the whole file at once).
        public void ReportBatchResult(string text, bool ok)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 0, 2) };
            var okColor = ok ? Color.FromRgb(0x1E, 0x7E, 0x34) : Color.FromRgb(0xCC, 0x00, 0x00);

            row.Children.Add(new TextBlock
            {
                Text = ok ? "✓" : "✗",
                Width = 20,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = new SolidColorBrush(okColor),
                VerticalAlignment = VerticalAlignment.Top
            });
            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = new SolidColorBrush(ok ? Colors.Black : okColor),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top
            });

            LogPanel.Children.Add(row);
            if (ok) _success++; else _failed++;
            ScrollToEnd();
        }

        // Hides the progress spinner and shows the summary + OK button. Call after all uploads finish.
        public void ShowComplete()
        {
            Spinner.Visibility = Visibility.Collapsed;

            if (_success > 0 && _failed == 0)
            {
                SummaryText.Text = $"✓  All {_success} QSO{(_success != 1 ? "s" : "")} uploaded successfully.";
                SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x7E, 0x34));
            }
            else if (_success == 0 && _failed > 0)
            {
                SummaryText.Text = $"✗  {_failed} upload{(_failed != 1 ? "s" : "")} failed — those QSOs remain in the queue.";
                SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0x00, 0x00));
            }
            else if (_success > 0)
            {
                SummaryText.Text = $"✓ {_success} uploaded    ✗ {_failed} failed (remain in queue)";
                SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x5C, 0x00));
            }
            else
            {
                SummaryText.Text = "Done.";
                SummaryText.Foreground = new SolidColorBrush(Colors.DimGray);
            }

            SummaryText.Visibility = Visibility.Visible;
            OkBtn.Visibility = Visibility.Visible;
            OkBtn.Focus();
        }

        // Awaited by UploadAllAndCloseAsync — resolves when the user clicks OK or closes the window.
        public Task WaitForOkAsync() => _done.Task;

        private void ScrollToEnd()
        {
            LogScroll.UpdateLayout();
            LogScroll.ScrollToEnd();
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            _done.TrySetResult(true);
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            _done.TrySetResult(true);  // resolve even if the window is closed via Alt+F4
        }
    }
}
