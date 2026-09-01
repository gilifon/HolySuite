using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HolyParser;

namespace HolyLogger
{
    // THE REPORT THE AI WRITES ABOUT ONE QSO, and the box where the operator's own API key is
    // entered the first time. Nothing here changes the log - see AiQsoCheck for why.
    //
    // Deliberately one small window with one job: the callsign at the top, the remarks underneath,
    // and three buttons. It can be left open while the operator works; asking again re-reads the QSO
    // as it stands now, so it is also the way to see whether a correction settled the matter.
    public class AiQsoCheckWindow : Window
    {
        private readonly QSO _qso;

        private readonly TextBlock _status;
        private readonly TextBox _report;
        private readonly Button _askButton;
        private readonly Button _copyButton;

        private readonly AiServicePanel _keyPanel;
        private readonly System.Windows.Controls.ProgressBar _spinner;

        // Counts the wait beside whatever the check last said. Held on the window so that starting a
        // second check cannot leave the first one's timer running behind it.
        private System.Windows.Threading.DispatcherTimer _ticker;

        private CancellationTokenSource _running;

        public AiQsoCheckWindow(QSO qso, Window owner)
        {
            _qso = qso;

            Title = "AI check - " + (qso != null ? qso.DXCall : string.Empty);
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            // Room for a dozen lines of report without scrolling, and resizable for a long one.
            Width = 620;
            // ROOM FOR THE ROWS THAT WERE ADDED, not for the ones it opened with. The paid service
            // grew a sixth signup step and a line saying what credit is left, and the report is the
            // only row that stretches - so without this the new lines would have come straight out
            // of the space the answer is read in.
            Height = 690;
            MinWidth = 460;
            MinHeight = 530;
            Background = ThemeManager.Brush("WindowBg");
            FontSize = 16;

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // who
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // key box
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons

            // ── who this is about ───────────────────────────────────────────────────────────────
            var who = new TextBlock
            {
                Text = qso != null ? qso.DXCall : string.Empty,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = ThemeManager.Brush("TextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var when = new TextBlock
            {
                Text = Subtitle(qso),
                FontSize = 16,
                Foreground = ThemeManager.Brush("TextBrush"),
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 10),
            };
            var head = new StackPanel();
            head.Children.Add(who);
            head.Children.Add(when);
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            // ── WHICH AI, AND HOW TO GET A KEY FOR IT ───────────────────────────────
            //
            // The chooser, the signup steps and the key box all live in AiServicePanel now, because
            // the Log Fixer has to be able to put the same thing in front of the operator when his
            // allowance runs out there. It stays on screen after a key is saved: the day the free
            // allowance is used up is the day he wants to switch without hunting for a setting.
            // COMPACT, LIKE THE LOG FIXER'S DIALOG. This window asks a question about one QSO; it
            // is not where an account is set up. With a key it shows the model and nothing else,
            // and without one it says so and offers the page that fixes it.
            _keyPanel = new AiServicePanel(showModel: true, compact: true)
            {
                Say = text => { if (_status != null) _status.Text = text; },
                KeySaved = Ask,
                ServiceChanged = () =>
                {
                    if (_status == null) return;
                    _status.Text = AiQsoCheck.HasKey
                        ? "Press Ask to check this QSO."
                        : "No API key yet - set one in Options > AI Service.";
                }
            };
            Grid.SetRow(_keyPanel, 1);
            root.Children.Add(_keyPanel);

            // ── what is happening ───────────────────────────────────────────────────────────────
            //
            // THE SAME SPINNER THE PROGRAM SHOWS WHILE IT STARTS, and for the same reason: a line of
            // words says the program is alive only to somebody who is reading it, and the man waiting
            // on an AI has usually looked away. The bar turns, the seconds climb, and the window says
            // from across the room that it is still working.
            var doing = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };

            _spinner = new System.Windows.Controls.ProgressBar
            {
                IsIndeterminate = true,
                Width = 110,
                Height = 5,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeManager.Brush("AccentBrush"),
                Background = System.Windows.Media.Brushes.Transparent,
                Visibility = Visibility.Collapsed,
            };
            DockPanel.SetDock(_spinner, Dock.Left);
            doing.Children.Add(_spinner);

            _status = new TextBlock
            {
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeManager.Brush("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            doing.Children.Add(_status);

            Grid.SetRow(doing, 2);
            root.Children.Add(doing);

            // ── the report ──────────────────────────────────────────────────────────────────────
            // A read-only TextBox rather than a TextBlock, so a line can be selected and copied out
            // of it the way any other text can.
            _report = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 16,
                Padding = new Thickness(8),
                BorderBrush = ThemeManager.Brush("GridLine"),
                BorderThickness = new Thickness(1),
                Background = ThemeManager.Brush("WindowBg"),
                Foreground = ThemeManager.Brush("TextBrush"),
            };
            Grid.SetRow(_report, 3);
            root.Children.Add(_report);

            // ── buttons ─────────────────────────────────────────────────────────────────────────
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };

            _copyButton = new Button
            {
                Content = "Copy",
                FontSize = 16,
                Padding = new Thickness(16, 5, 16, 5),
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = false,
            };
            _copyButton.Click += (s, e) =>
            {
                try { Clipboard.SetText(_report.Text ?? string.Empty); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            };
            buttons.Children.Add(_copyButton);

            _askButton = new Button
            {
                Content = "Ask again",
                FontSize = 16,
                Padding = new Thickness(16, 5, 16, 5),
                Margin = new Thickness(0, 0, 8, 0),
            };
            _askButton.Click += (s, e) => Ask();
            buttons.Children.Add(_askButton);

            var close = new Button
            {
                Content = "Close",
                FontSize = 16,
                Padding = new Thickness(16, 5, 16, 5),
                IsCancel = true,
            };
            close.Click += (s, e) => Close();
            buttons.Children.Add(close);

            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);

            Content = root;

            Loaded += (s, e) =>
            {
                if (AiQsoCheck.HasKey) Ask();
                else
                {
                    _status.Text = "No API key yet - set one in Options > AI Service.";
                    _keyPanel.FocusKey();
                }
            };

            // A check still in flight when the window closes has nobody to report to.
            Closed += (s, e) =>
            {
                try { if (_running != null) _running.Cancel(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            };

            // Where the operator last put it, and how big he made it. This window is opened over and
            // over - once per contact being checked - and it used to come back centred on its parent
            // every single time, so a place found for it beside the log lasted one contact.
            // AFTER the size is set above: Attach clamps a saved size to this window's own minimum.
            WindowBounds.Attach(this, "AiQsoCheck");
        }

        private static string Subtitle(QSO q)
        {
            if (q == null) return string.Empty;
            string parts = string.Empty;
            if (!string.IsNullOrWhiteSpace(q.Date)) parts += q.Date;
            if (!string.IsNullOrWhiteSpace(q.Time)) parts += "  " + q.Time;
            if (!string.IsNullOrWhiteSpace(q.Band)) parts += "   " + q.Band;
            if (!string.IsNullOrWhiteSpace(q.Mode)) parts += "  " + q.Mode;
            return parts.Trim();
        }

        // Stopped in one place, because a timer left running writes its seconds over whatever the
        // window has put on the status line since - an answer, or the reason there is not one.
        private void StopTicking()
        {
            if (_ticker == null) return;
            _ticker.Stop();
            _ticker = null;
        }

        private async void Ask()
        {
            if (!AiQsoCheck.HasKey)
            {
                _keyPanel.Refresh();

                // ONE PLACE TO SET A KEY, AND THIS IS NOT IT. The line about picking another
                // service that already has a key belonged to the days of two services; now there is
                // one, and the key is set on one page.
                _status.Text = "No API key yet - set one in Options > AI Service.";
                return;
            }

            if (_running != null) return;   // one at a time

            _running = new CancellationTokenSource();
            _askButton.IsEnabled = false;
            _copyButton.IsEnabled = false;
            _report.Text = string.Empty;
            // "...and waiting if it has to": a per-minute limit is waited out inside CheckAsync, and
            // an operator watching a still window for ten seconds deserves to know it is not stuck.
            _status.Text = "Asking the AI about this QSO (it waits if the free allowance needs a moment)...";
            _status.Foreground = ThemeManager.Brush("TextBrush");

            // The seconds are counted from here, beside the line above, and the bar turns while they
            // do. Both stop the moment there is an answer or a refusal to show instead.
            string said = _status.Text;
            DateTime began = DateTime.UtcNow;

            _spinner.Visibility = Visibility.Visible;

            StopTicking();
            _ticker = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _ticker.Tick += (t, a) =>
            {
                int seconds = (int)(DateTime.UtcNow - began).TotalSeconds;
                _status.Text = said.TrimEnd('.', ' ')
                             + " - " + seconds + (seconds == 1 ? " second" : " seconds") + " so far";
            };
            _ticker.Start();

            try
            {
                string report = await AiQsoCheck.CheckAsync(_qso, _running.Token);
                if (!IsLoaded) return;

                _report.Text = report;
                _status.Text = "What the AI makes of it. The log has not been changed.";
                _copyButton.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
                // The window closed under it; nothing to say.
            }
            catch (Exception ex)
            {
                _report.Text = string.Empty;
                _status.Text = ex.Message;
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

                // A key that was refused is a key worth re-entering, so the box comes back.
                if (ex.Message.IndexOf("API key", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _keyPanel.ForgetKey();
                    _keyPanel.FocusKey();
                }
            }
            finally
            {
                if (_running != null) { _running.Dispose(); _running = null; }

                StopTicking();
                _spinner.Visibility = Visibility.Collapsed;

                _askButton.IsEnabled = true;

                // THE CHECK HAS JUST SPENT SOME OF IT. Whatever the credit line said was true when
                // the window opened and is not any more, so the account is asked again - and this,
                // right after a check, is the moment an operator paying by use actually looks at it.
                if (IsLoaded) _keyPanel.Refresh();
            }
        }
    }
}
