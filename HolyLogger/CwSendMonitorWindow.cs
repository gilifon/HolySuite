using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace HolyLogger
{
    /// <summary>
    /// A small "CW sending monitor" window. It shows the full CW text being keyed and a blinking
    /// coloured cursor that advances through the characters in sync with the radio's transmission.
    ///
    /// The IC-7300 (and CI-V radios in general) do not report keying progress, so the cursor is
    /// driven by a timing simulation based on the standard PARIS CW timing. The speed (WPM) is
    /// self-calibrated by the owner: after each transmission the real elapsed time is divided by the
    /// computed unit count to learn the radio's actual keyer speed for the next message.
    /// </summary>
    public class CwSendMonitorWindow : Window
    {
        private static readonly Dictionary<char, string> Morse = new Dictionary<char, string>
        {
            {'A',".-"},   {'B',"-..."}, {'C',"-.-."}, {'D',"-.."},  {'E',"."},    {'F',"..-."},
            {'G',"--."},  {'H',"...."}, {'I',".."},   {'J',".---"}, {'K',"-.-"},  {'L',".-.."},
            {'M',"--"},   {'N',"-."},   {'O',"---"},  {'P',".--."}, {'Q',"--.-"}, {'R',".-."},
            {'S',"..."},  {'T',"-"},    {'U',"..-"},  {'V',"...-"}, {'W',".--"},  {'X',"-..-"},
            {'Y',"-.--"}, {'Z',"--.."},
            {'0',"-----"},{'1',".----"},{'2',"..---"},{'3',"...--"},{'4',"....-"},
            {'5',"....."},{'6',"-...."},{'7',"--..."},{'8',"---.."},{'9',"----."},
            {'.',".-.-.-"},{',',"--..--"},{'?',"..--.."},{'/',"-..-."},
            {'@',".--.-."},{'=',"-...-"}, {'+',".-.-."}, {'-',"-....-"},
        };

        private static readonly Brush SentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x99, 0xCC));
        private static readonly Brush UpcomingBrush = new SolidColorBrush(Color.FromRgb(0xB5, 0xB5, 0xB5));
        private static readonly Brush CurrentForeground = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x34));
        private static readonly Brush CursorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0x57));
        private static readonly Brush DoneBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA8, 0x4D));

        static CwSendMonitorWindow()
        {
            SentBrush.Freeze();
            UpcomingBrush.Freeze();
            CurrentForeground.Freeze();
            CursorBrush.Freeze();
            DoneBrush.Freeze();
        }

        private readonly string _text;
        private readonly double[] _cumulativeUnits;
        private readonly double _totalUnits;
        private readonly Border[] _cells;
        private readonly TextBlock[] _glyphs;
        private readonly TextBlock _wpmLabel;
        private readonly ProgressBar _progress;

        private readonly DispatcherTimer _advanceTimer;
        private readonly DispatcherTimer _blinkTimer;

        private double _wpm;
        private DateTime _startUtc;
        private bool _running;
        private bool _finished;
        private bool _cursorOn = true;
        private int _currentIndex;

        /// <summary>Total PARIS units for the supplied text (used by the owner for WPM calibration).</summary>
        public double TotalUnits => _totalUnits;

        public CwSendMonitorWindow(string text, double initialWpm, string title)
        {
            _text = string.IsNullOrEmpty(text) ? string.Empty : text.ToUpperInvariant();
            _wpm = initialWpm < 5 ? 5 : (initialWpm > 80 ? 80 : initialWpm);

            _cumulativeUnits = new double[_text.Length];
            double running = 0;
            for (int i = 0; i < _text.Length; i++)
            {
                running += CharUnits(_text[i]);
                _cumulativeUnits[i] = running;
            }
            _totalUnits = running;

            Title = title;
            Width = 620;
            Height = 220;
            MinWidth = 360;
            MinHeight = 160;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xFB, 0xFF));

            var rootBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0xFF)),
                Margin = new Thickness(10),
                Background = Brushes.White,
                Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.3, Color = Color.FromRgb(0x6A, 0x82, 0x96) }
            };

            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title row: caption + live WPM
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var caption = new TextBlock
            {
                Text = title,
                FontSize = 12,
                Foreground = Brushes.DimGray,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(caption, 0);
            titleRow.Children.Add(caption);

            _wpmLabel = new TextBlock
            {
                Text = "~" + Math.Round(_wpm) + " WPM",
                FontSize = 12,
                Foreground = Brushes.DimGray,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_wpmLabel, 1);
            titleRow.Children.Add(_wpmLabel);

            Grid.SetRow(titleRow, 0);
            grid.Children.Add(titleRow);

            // The message text as per-character cells
            var wrap = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 10)
            };

            _cells = new Border[_text.Length];
            _glyphs = new TextBlock[_text.Length];
            for (int i = 0; i < _text.Length; i++)
            {
                var glyph = new TextBlock
                {
                    Text = _text[i] == ' ' ? "\u00A0" : _text[i].ToString(),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 30,
                    FontWeight = FontWeights.Bold,
                    Foreground = UpcomingBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var cell = new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(2, 0, 2, 0),
                    Margin = new Thickness(1, 0, 1, 0),
                    Background = Brushes.Transparent,
                    Child = glyph
                };

                _glyphs[i] = glyph;
                _cells[i] = cell;
                wrap.Children.Add(cell);
            }

            Grid.SetRow(wrap, 1);
            grid.Children.Add(wrap);

            _progress = new ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x99, 0xCC)),
                Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
                BorderThickness = new Thickness(0)
            };
            Grid.SetRow(_progress, 2);
            grid.Children.Add(_progress);

            rootBorder.Child = grid;
            Content = rootBorder;

            _advanceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _advanceTimer.Tick += AdvanceTimer_Tick;

            _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _blinkTimer.Tick += BlinkTimer_Tick;

            _currentIndex = 0;
            RepaintAll();
        }

        /// <summary>Computes the total PARIS units for a message (static, for the owner's calibration).</summary>
        public static double ComputeTotalUnits(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double total = 0;
            foreach (char c in text.ToUpperInvariant())
            {
                total += CharUnits(c);
            }
            return total;
        }

        private static double CharUnits(char c)
        {
            if (c == ' ') return 7.0;

            if (!Morse.TryGetValue(char.ToUpperInvariant(c), out string pattern) || string.IsNullOrEmpty(pattern))
            {
                return 4.0;
            }

            double units = 0;
            for (int i = 0; i < pattern.Length; i++)
            {
                units += pattern[i] == '-' ? 3 : 1;
                if (i < pattern.Length - 1) units += 1; // intra-character gap
            }
            units += 3; // trailing inter-character gap
            return units;
        }

        /// <summary>Called by the owner when the radio actually keys up (TX on). Starts the cursor.</summary>
        public void StartCursor()
        {
            if (_finished || _running) return;
            _running = true;
            _startUtc = DateTime.UtcNow;
            _blinkTimer.Start();
            _advanceTimer.Start();
        }

        /// <summary>Lets the owner update the speed base mid-life (e.g. with a freshly learned WPM).</summary>
        public void UpdateWpm(double wpm)
        {
            _wpm = wpm < 5 ? 5 : (wpm > 80 ? 80 : wpm);
            _wpmLabel.Text = "~" + Math.Round(_wpm) + " WPM";
        }

        /// <summary>Transmission ended normally: snap the cursor to the end, flash done, auto-close.</summary>
        public void Complete()
        {
            if (_finished) return;
            _finished = true;
            _advanceTimer.Stop();
            _blinkTimer.Stop();
            _currentIndex = _text.Length;
            _progress.Value = 100;
            RepaintAll();
            CloseAfter(900);
        }

        /// <summary>Transmission aborted early: freeze the cursor where it is, dim, then close.</summary>
        public void Freeze()
        {
            if (_finished) return;
            _finished = true;
            _advanceTimer.Stop();
            _blinkTimer.Stop();
            _cursorOn = false;
            RepaintAll();
            CloseAfter(500);
        }

        private void CloseAfter(int milliseconds)
        {
            var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            closeTimer.Tick += (s, e) =>
            {
                closeTimer.Stop();
                try { Close(); } catch { }
            };
            closeTimer.Start();
        }

        private void AdvanceTimer_Tick(object sender, EventArgs e)
        {
            if (_finished || _totalUnits <= 0) return;

            double elapsedSeconds = (DateTime.UtcNow - _startUtc).TotalSeconds;
            double unitSeconds = 1.2 / _wpm;
            double elapsedUnits = elapsedSeconds / unitSeconds;

            int idx = 0;
            while (idx < _text.Length && _cumulativeUnits[idx] <= elapsedUnits)
            {
                idx++;
            }

            if (idx >= _text.Length)
            {
                // Reached the end of our estimate but the radio hasn't reported RX yet.
                // Hold on the last character until the owner calls Complete().
                idx = _text.Length - 1;
            }

            _progress.Value = Math.Min(100, elapsedUnits / _totalUnits * 100.0);

            if (idx != _currentIndex)
            {
                _currentIndex = idx;
                RepaintAll();
            }
        }

        private void BlinkTimer_Tick(object sender, EventArgs e)
        {
            _cursorOn = !_cursorOn;
            if (_currentIndex >= 0 && _currentIndex < _cells.Length)
            {
                PaintCell(_currentIndex);
            }
        }

        private void RepaintAll()
        {
            for (int i = 0; i < _cells.Length; i++)
            {
                PaintCell(i);
            }
        }

        private void PaintCell(int i)
        {
            if (i < 0 || i >= _cells.Length) return;

            if (_finished && _currentIndex >= _text.Length)
            {
                _glyphs[i].Foreground = DoneBrush;
                _cells[i].Background = Brushes.Transparent;
                return;
            }

            if (i < _currentIndex)
            {
                _glyphs[i].Foreground = SentBrush;
                _cells[i].Background = Brushes.Transparent;
            }
            else if (i == _currentIndex)
            {
                _glyphs[i].Foreground = CurrentForeground;
                _cells[i].Background = _cursorOn ? CursorBrush : Brushes.Transparent;
            }
            else
            {
                _glyphs[i].Foreground = UpcomingBrush;
                _cells[i].Background = Brushes.Transparent;
            }
        }
    }
}
