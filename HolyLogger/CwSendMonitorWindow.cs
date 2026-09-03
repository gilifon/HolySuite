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

            _cumulativeUnits = CumulativeUnits(_text);
            _totalUnits = _cumulativeUnits.Length == 0 ? 0 : _cumulativeUnits[_cumulativeUnits.Length - 1];

            Title = title;
            // Frameless, compact: no title bar/chrome, and the window shrinks to fit its content.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            // Never steal focus: the main window must keep keyboard focus so the F-keys keep
            // working (pressing the same key again stops the transmission and closes this window).
            ShowActivated = false;
            Focusable = false;
            IsHitTestVisible = false;
            Background = Brushes.Transparent;

            var rootBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0xFF)),
                Margin = new Thickness(10),
                MinWidth = 260,
                Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xFB, 0xFF)),
                Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.3, Color = Color.FromRgb(0x6A, 0x82, 0x96) }
            };

            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title row: caption + live WPM
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var caption = new TextBlock
            {
                Text = title,
                FontSize = 16,
                Foreground = Brushes.DimGray,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(caption, 0);
            titleRow.Children.Add(caption);

            _wpmLabel = new TextBlock
            {
                Text = "~" + Math.Round(_wpm) + " WPM",
                FontSize = 16,
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
                MaxWidth = 560,
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
        // MORSE SPACING, AND ONLY WHERE IT BELONGS. Three units between the letters of a word, seven
        // between words - and the seven REPLACES the three, it is not added to it.
        public const double LetterGapUnits = 3.0;
        public const double WordGapUnits = 7.0;

        // THE COUNT USED TO RUN ABOUT A SEVENTH TOO HIGH, and everything resting on it went wrong in
        // the same direction. Every character was charged a trailing three-unit gap, including the
        // last one where no gap follows, and a space was charged seven ON TOP of the three already
        // added by the character in front of it - ten units for a word gap that is seven.
        //
        // It mattered twice over. The keying speed is worked out as units divided by the seconds the
        // radio was on air, so an inflated count read the radio FASTER than it keys: a radio set to
        // twenty was reported at twenty-three. And the cursor that walks the message as it goes out
        // was pacing itself against a message longer than the one being sent, so it arrived at the
        // last letter after the operator had already heard it.
        public static double[] CumulativeUnits(string text)
        {
            text = text ?? string.Empty;

            var cumulative = new double[text.Length];
            double running = 0;
            bool afterLetter = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = char.ToUpperInvariant(text[i]);

                if (c == ' ')
                {
                    // A run of spaces is one word gap, not one each.
                    if (afterLetter) running += WordGapUnits;
                    afterLetter = false;
                }
                else
                {
                    if (afterLetter) running += LetterGapUnits;
                    running += ElementUnits(c);
                    afterLetter = true;
                }

                cumulative[i] = running;
            }

            return cumulative;
        }

        public static double ComputeTotalUnits(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var cumulative = CumulativeUnits(text);
            return cumulative.Length == 0 ? 0 : cumulative[cumulative.Length - 1];
        }

        // The dits and dahs of one character, with the one-unit gaps between them. NO gap after it -
        // what follows the character is the business of whatever comes next.
        private static double ElementUnits(char c)
        {
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
            return units;
        }

        /// <summary>Called by the owner when the radio actually keys up (TX on). Starts the cursor.</summary>
        public void StartCursor()
        {
            if (_finished || _running) return;
            _running = true;
            _startUtc = DateTime.UtcNow;
            _lastTickUtc = _startUtc;
            _unitsDone = 0;
            _blinkTimer.Start();
            _advanceTimer.Start();
        }

        /// <summary>Lets the owner update the speed base mid-life (e.g. with a freshly learned WPM).</summary>
        public void UpdateWpm(double wpm)
        {
            _wpm = wpm < 5 ? 5 : (wpm > 80 ? 80 : wpm);
            _wpmLabel.Text = "~" + Math.Round(_wpm) + " WPM";
        }

        /// <summary>
        /// True once the cursor has reached the end of the text - every unit of it counted at the
        /// speed the radio reports. The owner uses it to know that a reported return to receive IS
        /// the end of the message rather than a break-in gap: those happen while units remain.
        /// </summary>
        internal bool TextDone
        {
            get { return _totalUnits > 0 && _unitsDone >= _totalUnits; }
        }

        /// <summary>Transmission ended normally: close the window immediately, no delay.</summary>
        public void Complete()
        {
            if (_finished) return;
            _finished = true;
            _advanceTimer.Stop();
            _blinkTimer.Stop();
            try { Close(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        /// <summary>Transmission aborted early: close the window immediately, no delay.</summary>
        public void Freeze()
        {
            if (_finished) return;
            _finished = true;
            _advanceTimer.Stop();
            _blinkTimer.Stop();
            try { Close(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // UNITS ARE ADDED UP AS THEY GO, not worked out afresh from the start time each tick. The
        // operator can turn the radio's speed knob in the middle of a message - the keyer reads it and
        // this window is told - and dividing the WHOLE elapsed time by the NEW speed would re-price
        // everything already sent and jump the cursor. What has been keyed has been keyed; only what
        // comes after it moves at the new speed.
        private double _unitsDone;
        private DateTime _lastTickUtc = DateTime.MinValue;

        private void AdvanceTimer_Tick(object sender, EventArgs e)
        {
            if (_finished || _totalUnits <= 0) return;

            DateTime now = DateTime.UtcNow;
            DateTime since = _lastTickUtc == DateTime.MinValue ? now : _lastTickUtc;
            _lastTickUtc = now;

            double seconds = (now - since).TotalSeconds;
            if (seconds > 0) _unitsDone += seconds * _wpm / 1.2;

            double elapsedUnits = _unitsDone;

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
