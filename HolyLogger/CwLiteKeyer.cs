using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HolyLogger
{
    /// <summary>
    /// THE LITE KEYER: one line to type CW into, on the main window's own menu row.
    ///
    /// The CW Keyer window does everything - twelve macro buttons, a record of what has gone out, ESM,
    /// a speed wheel - and there are moments when none of that is wanted. The operator is working the
    /// four Msg buttons on the main window and needs to add three words by hand; opening a second
    /// window with twelve buttons on it for three words is more than he asked for. This is the three
    /// words and nothing else: a black frame, white inside, the straight key beside it, and the
    /// radio's speed to the right of it.
    ///
    /// ONE OR THE OTHER, NEVER BOTH. It is on the bar only while the CW Keyer window is closed. Two
    /// places to type CW into, both feeding the same radio, is two half-sent messages the first time
    /// somebody uses the wrong one.
    ///
    /// TYPED STRAIGHT ONTO THE AIR. The keyer window can hold a line back for Enter; this one cannot,
    /// because a line held back is a line to think about and this strip is for the words that go now.
    ///
    /// THE SPEED IS READ FROM THE RADIO AND TURNED BY THE WHEEL, exactly as in the keyer window: the
    /// knob on the radio moves the number, and a notch of the wheel over the number is one word a
    /// minute the other way.
    ///
    /// The keying itself is the CW Keyer's arrangement, kept the same on purpose - see the comments on
    /// each piece below, which say what was learned there.
    /// </summary>
    internal class CwLiteKeyer : StackPanel
    {
        // The blue of a character that has left. Fixed rather than themed, the same blue the keyer
        // window uses, and for the same reason: it has to mean the same thing in every colour scheme.
        private static readonly Brush SentBrush = Frozen(Color.FromRgb(0x1E, 0x90, 0xFF));

        // The yellow the number wears while the pointer is on it - the same mark the frequency
        // displays and the keyer's own readout carry where the wheel does something.
        private static readonly Brush WheelZoneBrush = Frozen(Color.FromRgb(0xFF, 0xFF, 0x00));

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // -- WHAT THE MAIN WINDOW LENDS IT -------------------------------------------------------
        //
        // Every one of these is asked FRESH each time rather than taken once at startup: the operator
        // can change radios, turn CAT off and on, or open and close the keyer window while this strip
        // sits on the bar, and a strip built around one radio's answers would go on believing them.
        private readonly Func<string, bool> _sendChunk;
        private readonly Action _stopSending;
        private readonly Func<bool> _isTransmitting;
        private readonly Func<int> _chunkSize;
        private readonly Func<bool> _waitForTxIdle;
        private readonly Func<bool> _canKey;
        private readonly Action _askSpeed;

        // Puts a speed ON the radio, and the range that radio will accept. Set after the strip is
        // built rather than passed in with the rest, and read at the moment the wheel is turned: the
        // operator can change radios without this line leaving the bar.
        internal Action<int> SetSpeed { set { _setSpeed = value; } }
        private Action<int> _setSpeed;

        internal CwKeyboardWindow.SpeedRange SpeedRange { set { _speedRange = value; } }
        private CwKeyboardWindow.SpeedRange _speedRange;

        private readonly TextBox _box;
        private readonly TextBlock _overlay;
        private readonly TextBlock _wpmText;
        private readonly Border _wpmBox;
        private readonly Border _wpmNumberBox;
        private readonly Border _frame;
        private readonly DispatcherTimer _pump;

        // How far ahead of the radio the text is handed over, so the keying never runs dry between
        // one chunk and the next.
        private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(400);

        // The transmit state is read no oftener than this - it is a CAT question like any other.
        private static readonly TimeSpan TxAskEvery = TimeSpan.FromMilliseconds(100);

        private const double SlowestPlausibleWpm = 5.0;
        private const double FastestPlausibleWpm = 60.0;

        // Each entry is a chunk the radio has been given and the space that was written in front of it
        // where the keying was heard to stop. It comes off the screen when the radio has finished it.
        private readonly Queue<KeyValuePair<string, bool>> _inFlight = new Queue<KeyValuePair<string, bool>>();

        private int _handedUpTo;
        private double _unitsKeyed;
        private DateTime _keyClockUtc = DateTime.MinValue;
        private DateTime _radioBusyUntil = DateTime.MinValue;
        private DateTime _worstCaseDoneUtc = DateTime.MinValue;
        private DateTime _earliestDoneUtc = DateTime.MinValue;

        private bool _onAir;
        private DateTime _onAirAskedUtc = DateTime.MinValue;
        private DateTime _txAskedUtc = DateTime.MinValue;
        private DateTime _txStoppedUtc = DateTime.MaxValue;
        private bool _txSeenThisSend;

        private string _paintedText = null;
        private int _paintedKeyed = -1;

        private bool _keyerWindowOpen;

        internal CwLiteKeyer(Func<string, bool> sendChunk, Action stopSending, Func<bool> isTransmitting,
                             Func<int> chunkSize, Func<bool> waitForTxIdle, Func<bool> canKey,
                             Action askSpeed)
        {
            _sendChunk = sendChunk;
            _stopSending = stopSending;
            _isTransmitting = isTransmitting;
            _chunkSize = chunkSize;
            _waitForTxIdle = waitForTxIdle;
            _canKey = canKey;
            _askSpeed = askSpeed;

            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;

            var typeface = new FontFamily("Consolas");

            // A TextBox paints all its text in one colour, so it paints none of it: what is seen comes
            // from this TextBlock underneath, in two runs - blue for what the radio has keyed, black
            // for the rest. The box keeps the caret, the typing and the selection; it just does not
            // draw. Black rather than the theme's text colour: the inside of this frame is white in
            // every scheme, and white-on-white is nothing at all.
            _overlay = new TextBlock
            {
                FontSize = 16,
                FontFamily = typeface,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            _box = new TextBox
            {
                FontSize = 16,
                FontFamily = typeface,
                CharacterCasing = CharacterCasing.Upper,
                AcceptsReturn = false,
                TextWrapping = TextWrapping.NoWrap,
                Background = Brushes.Transparent,
                Foreground = Brushes.Transparent,
                CaretBrush = Brushes.Black,
                SelectionOpacity = 0.35,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Width = 190,
                ToolTip = "Type here and the radio sends it as you type." + Environment.NewLine
                        + "Escape stops it." + Environment.NewLine + Environment.NewLine
                        + "This line is here while the CW Keyer window is closed."
            };

            _box.PreviewTextInput += Box_PreviewTextInput;
            _box.PreviewKeyDown += Box_PreviewKeyDown;

            // AND THE SAME RULE FOR PASTE. PreviewTextInput fires for typing and for nothing else, so
            // what the clipboard held went in unchecked and was dropped later, silently, on the way to
            // the radio. Cleaned rather than refused: what can be keyed goes in, the rest is left out.
            DataObject.AddPastingHandler(_box, (s, e) =>
            {
                try
                {
                    string pasted = e.DataObject.GetDataPresent(DataFormats.UnicodeText)
                                  ? e.DataObject.GetData(DataFormats.UnicodeText) as string
                                  : null;
                    if (pasted == null) { e.CancelCommand(); return; }

                    string clean = new string(pasted.Where(IsSendable).ToArray());
                    if (clean.Length == pasted.Length) return;

                    var wrapped = new DataObject();
                    wrapped.SetData(DataFormats.UnicodeText, clean);
                    e.DataObject = wrapped;
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); e.CancelCommand(); }
            });

            // The line slides left as it fills, and what is drawn has to slide with it or the colours
            // would sit under the wrong characters.
            var overlayShift = new TranslateTransform();
            _overlay.RenderTransform = overlayShift;
            _box.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((s, e) => overlayShift.X = -_box.HorizontalOffset));

            var layers = new Grid { ClipToBounds = true };
            layers.Children.Add(_overlay);
            layers.Children.Add(_box);

            // A BLACK FRAME ROUND A WHITE FIELD. The bar behind it is whatever colour the scheme
            // makes it; the typing field is not, because this is the CW line and it is meant to be
            // found at a glance - and a black edge is what marks it off from the bar in every scheme.
            _frame = new Border
            {
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.White,
                Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = layers
            };

            // THE SPEED, TO THE RIGHT OF THE FRAME. It is on the bar rather than inside the frame
            // because the frame holds one thing - the text going out - and a number living in it would
            // be read as something that had been typed.
            // THE NUMBER AND THE WORD ARE TWO PIECES, because only one of them lights. Anywhere on the
            // patch answers the wheel, but what it CHANGES is the number - so the yellow goes round
            // the digits and nothing else.
            _wpmText = new TextBlock
            {
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            _wpmText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            RefreshSpeedText();

            _wpmNumberBox = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = _wpmText
            };

            var wpmLabel = new TextBlock
            {
                Text = "WPM",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            wpmLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            // THE ARROWS SAY A WHEEL WORKS HERE. A wheel over a piece of text is not something anybody
            // expects, so it is said rather than left to be discovered - the same pair the keyer window
            // carries beside its own number. Grey, because it is a hint and not a thing to read.
            var hint = new TextBlock
            {
                Text = "⇅",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.DimGray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };

            var wpmLine = new StackPanel { Orientation = Orientation.Horizontal };
            wpmLine.Children.Add(_wpmNumberBox);
            wpmLine.Children.Add(wpmLabel);
            wpmLine.Children.Add(hint);

            // AND THE WHEEL OVER IT, the same as in the keyer window: a notch is one word a minute,
            // the pointer says so, and the yellow says exactly what it works on. The tooltip names the
            // range at the moment the pointer arrives rather than now - the radio can be changed under
            // this strip and the numbers with it.
            // THE POINTER MARKS THE WHOLE PATCH, the word as well as the number: anywhere here answers
            // the wheel, and nobody should have to aim at the digits.
            _wpmBox = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.ScrollNS,
                Child = wpmLine
            };

            _wpmBox.MouseWheel += (s, e) =>
            {
                Nudge(e.Delta > 0 ? 1 : -1);
                e.Handled = true;
            };

            _wpmBox.MouseEnter += (s, e) =>
            {
                _wpmNumberBox.Background = WheelZoneBrush;

                // Black while it is yellow. The number wears the theme's own colour the rest of the
                // time, and in a dark scheme that colour is white - which on yellow is nothing.
                _wpmText.Foreground = Brushes.Black;
                _wpmBox.ToolTip = SpeedTip();
            };

            _wpmBox.MouseLeave += (s, e) =>
            {
                _wpmNumberBox.Background = Brushes.Transparent;
                _wpmText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            };

            var icon = CwKeyboardWindow.BuildStraightKeyIcon(true, new Thickness(10, 0, 0, 0));
            ToolTipService.SetToolTip(icon, "The lite CW keyer.");

            Children.Add(icon);
            Children.Add(_frame);
            Children.Add(_wpmBox);

            RepaintLine();

            // The same fiftieth of a second the keyer window runs on: fast enough that the colour
            // arrives with the sound, cheap enough to leave running while nothing is being sent.
            _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _pump.Tick += Pump_Tick;

            Refresh();
        }

        // -- WHEN IT IS THERE AT ALL -------------------------------------------------------------
        //
        // Two rules, and both are about not offering something that cannot work: a radio this program
        // cannot key by CAT has nothing for this strip to do, and the keyer window doing the same job
        // in a bigger space takes it off the bar while it is open.
        internal bool KeyerWindowOpen
        {
            set
            {
                if (_keyerWindowOpen == value) return;
                _keyerWindowOpen = value;
                Refresh();
            }
        }

        internal void Refresh()
        {
            bool wanted;
            try { wanted = !_keyerWindowOpen && _canKey != null && _canKey(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); wanted = false; }

            if (wanted == (Visibility == Visibility.Visible)) return;

            if (wanted)
            {
                Visibility = Visibility.Visible;
                _pump.Start();

                // Asked at once, so the number is the radio's before he has finished reading the bar.
                _askSpeedNow = true;
            }
            else
            {
                // WHATEVER IT WAS HOLDING GOES WITH IT. A strip that vanishes with three characters
                // still waiting to be keyed would send them from nowhere the moment it came back.
                StopEverything();
                _pump.Stop();
                Visibility = Visibility.Collapsed;
            }
        }

        // -- TYPING ------------------------------------------------------------------------------

        private static bool IsSendable(char c)
        {
            if (c == ' ') return true;
            if (c >= 'A' && c <= 'Z') return true;
            if (c >= 'a' && c <= 'z') return true;
            if (c >= '0' && c <= '9') return true;
            return ".,?/@=+-".IndexOf(c) >= 0;
        }

        private void Box_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
            {
                if (IsSendable(c)) continue;

                e.Handled = true;
                MainWindow.BeepRefusedKey();
                return;
            }
        }

        private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                StopEverything();
                e.Handled = true;
                return;
            }

            // ENTER DOES NOTHING HERE. There is no line to release - every character goes as it is
            // typed - and a key that looks like it should do something and does not is worse than one
            // that is plainly dead.
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                return;
            }

            // WHAT HAS GONE IS GONE. The front of this line is in the radio's hands even though it is
            // still on the screen, so Backspace and Delete are not allowed to reach into it.
            if ((e.Key == Key.Back || e.Key == Key.Delete) && _box.SelectionStart < _handedUpTo)
            {
                e.Handled = true;
            }
        }

        private void StopEverything()
        {
            try { if (_stopSending != null) _stopSending(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            _radioBusyUntil = DateTime.MinValue;
            _worstCaseDoneUtc = DateTime.MinValue;
            _earliestDoneUtc = DateTime.MinValue;
            _inFlight.Clear();
            _handedUpTo = 0;
            _unitsKeyed = 0;
            _box.Text = string.Empty;

            _txSeenThisSend = false;
            _txStoppedUtc = DateTime.MaxValue;

            RepaintLine();
        }

        // -- THE LOOP ----------------------------------------------------------------------------

        private void Pump_Tick(object sender, EventArgs e)
        {
            AskRadioItsSpeed();
            AdvanceKeyingClock();
            RepaintLine();
            DropWhatTheRadioHasKeyed();

            string text = _box.Text ?? string.Empty;

            // Everything typed is already in the radio's hands; there is nothing new to hand it.
            if (text.Length <= _handedUpTo) return;

            // The radio still has enough to be going on with.
            if (DateTime.UtcNow + Lead < _radioBusyUntil) return;

            // A radio with no buffer gets nothing until it has stopped sending the last lot.
            bool waitIdle;
            try { waitIdle = _waitForTxIdle != null && _waitForTxIdle(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); waitIdle = false; }

            if (waitIdle && _isTransmitting != null && _isTransmitting()) return;

            int maxChunk;
            try { maxChunk = _chunkSize == null ? 12 : _chunkSize(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); maxChunk = 12; }
            if (maxChunk < 4) maxChunk = 4;

            int waiting = text.Length - _handedUpTo;
            int take = Math.Min(maxChunk, waiting);

            // CUT AT A SPACE, not at the twelfth character. If the radio ever runs dry at a chunk
            // boundary the gap is heard, and a gap in the middle of a word breaks the word in two. At
            // a space it costs nothing, because a gap is expected there anyway.
            if (take == maxChunk && take < waiting)
            {
                int lastSpace = text.LastIndexOf(' ', _handedUpTo + take - 1, take);
                if (lastSpace > _handedUpTo) take = lastSpace - _handedUpTo + 1;
            }

            string chunk = text.Substring(_handedUpTo, take);

            // TYPED TOO SLOWLY TO KEEP THE RADIO FED. The radio finished everything it had before this
            // arrived, so it stopped keying - and a stop in the middle of a word is heard as the end
            // of one word and the start of another. So a space is written into the line wherever the
            // keying actually stopped: nothing extra is sent, the gap is already on air, this only
            // makes it visible. How long is too long is not a guess - four units on top of the letter
            // gap is where one word becomes two in the listener's ear.
            double gapWpm = KeyingWpm();
            double heardAsWordGap =
                (CwSendMonitorWindow.WordGapUnits - CwSendMonitorWindow.LetterGapUnits) * 1.2 / gapWpm;

            string handed = text.Substring(0, _handedUpTo);

            bool ranDry = _handedUpTo > 0
                       && DateTime.UtcNow >= _radioBusyUntil.AddSeconds(heardAsWordGap)
                       && !handed.EndsWith(" ", StringComparison.Ordinal)
                       && !chunk.StartsWith(" ", StringComparison.Ordinal);

            bool ok;
            try { ok = _sendChunk != null && _sendChunk(chunk); }
            catch (Exception swallowed) { Log.Swallow(swallowed); ok = false; }

            // A command that did not go out is not moved down: the same characters are offered again
            // on the next tick rather than being silently lost.
            if (!ok) return;

            if (ranDry)
            {
                string before = _box.Text ?? string.Empty;
                int at = _handedUpTo;
                int caret = _box.CaretIndex;

                _box.Text = before.Substring(0, at) + " " + before.Substring(at);
                _box.CaretIndex = caret >= at ? caret + 1 : caret;
                _handedUpTo = at + 1;
            }

            _handedUpTo += take;

            double wpm = KeyingWpm();
            double seconds = CwSendMonitorWindow.ComputeTotalUnits(chunk) * 1.2 / wpm;
            DateTime from = _radioBusyUntil > DateTime.UtcNow ? _radioBusyUntil : DateTime.UtcNow;
            _radioBusyUntil = from.AddSeconds(seconds);

            _inFlight.Enqueue(new KeyValuePair<string, bool>(chunk, ranDry));

            // The time by which even a five-words-a-minute station would have finished this chunk, and
            // the time before which not even a sixty could have - chained onto whatever is queued
            // ahead of it. Between them the text cannot leave the line too early or hang there for
            // ever on a radio that never says it has stopped.
            double worstSeconds = CwSendMonitorWindow.ComputeTotalUnits(chunk) * 1.2 / SlowestPlausibleWpm;
            DateTime worstFrom = _worstCaseDoneUtc > DateTime.UtcNow ? _worstCaseDoneUtc : DateTime.UtcNow;
            _worstCaseDoneUtc = worstFrom.AddSeconds(worstSeconds);

            double bestSeconds = CwSendMonitorWindow.ComputeTotalUnits(chunk) * 1.2 / FastestPlausibleWpm;
            DateTime bestFrom = _earliestDoneUtc > DateTime.UtcNow ? _earliestDoneUtc : DateTime.UtcNow;
            _earliestDoneUtc = bestFrom.AddSeconds(bestSeconds);

            // THE WATCH STARTS AGAIN HERE, but only if the radio is not already on air: a long line
            // goes over in several chunks and they are one transmission, not several.
            if (!_onAir)
            {
                _txSeenThisSend = false;
                _txStoppedUtc = DateTime.MaxValue;
            }
        }

        // -- HOW FAR THE KEYING HAS GOT ----------------------------------------------------------
        //
        // A running sum, and it only runs while the radio is ON AIR. Measuring from the moment a
        // transmission began and dividing by the speed counts every silence as keying - the gap
        // between one chunk and the next, the gap while he thinks - and the colour runs ahead of the
        // radio. Silence adds nothing here.
        private void AdvanceKeyingClock()
        {
            DateTime now = DateTime.UtcNow;
            DateTime since = _keyClockUtc == DateTime.MinValue ? now : _keyClockUtc;
            _keyClockUtc = now;

            if (_isTransmitting != null && now - _onAirAskedUtc >= TxAskEvery)
            {
                _onAirAskedUtc = now;
                try { _onAir = _isTransmitting(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); _onAir = false; }
            }

            WatchTransmitState();

            if (!_onAir) return;

            double seconds = (now - since).TotalSeconds;
            if (seconds <= 0) return;

            _unitsKeyed += seconds * KeyingWpm() / 1.2;

            // AND NEVER PAST WHAT THE RADIO HAS BEEN GIVEN, so a radio slower than it claims cannot
            // colour in what it has not yet been sent.
            double handed = CwSendMonitorWindow.ComputeTotalUnits(HandedText());
            if (_unitsKeyed > handed) _unitsKeyed = handed;
        }

        // Reads the transmit state, no oftener than TxAskEvery, and remembers the moment it last went
        // from keying to not. MaxValue means "keying now, or never seen keying".
        private void WatchTransmitState()
        {
            if (_isTransmitting == null) return;
            if (DateTime.UtcNow - _txAskedUtc < TxAskEvery) return;
            _txAskedUtc = DateTime.UtcNow;

            bool keying;
            try { keying = _isTransmitting(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); return; }

            if (keying)
            {
                _txSeenThisSend = true;
                _txStoppedUtc = DateTime.MaxValue;
            }
            else if (_txSeenThisSend && _txStoppedUtc == DateTime.MaxValue)
            {
                // Seen on air, and now off it: THIS is the moment the message ended, and the first
                // moment the wire is free to be asked anything.
                _txStoppedUtc = DateTime.UtcNow;
                _askSpeedNow = true;
            }
        }

        private string HandedText()
        {
            string text = _box.Text ?? string.Empty;
            int limit = Math.Min(_handedUpTo, text.Length);

            return limit <= 0 ? string.Empty : text.Substring(0, limit);
        }

        private int KeyedSoFar(string text)
        {
            if (text.Length == 0 || _handedUpTo <= 0) return 0;

            int limit = Math.Min(_handedUpTo, text.Length);
            var cumulative = CwSendMonitorWindow.CumulativeUnits(text.Substring(0, limit));

            // AS EACH CHARACTER STARTS, not when it has finished, so the colour arrives with the sound
            // rather than a whole character behind it.
            for (int i = 0; i < limit; i++)
            {
                double startsAt = i == 0 ? 0 : cumulative[i - 1];
                if (startsAt > _unitsKeyed) return i;
            }

            return limit;
        }

        // NOTHING HAS MOVED, SO NOTHING IS REDRAWN. This runs twenty times a second for as long as the
        // strip is on the bar, and every repaint makes WPF measure and paint. The two things the line
        // is made of are the text and how much of it has been keyed.
        private void RepaintLine()
        {
            string text = _box.Text ?? string.Empty;
            int keyed = KeyedSoFar(text);

            if (text == _paintedText && keyed == _paintedKeyed) return;
            _paintedText = text;
            _paintedKeyed = keyed;

            _overlay.Inlines.Clear();

            if (keyed > 0)
                _overlay.Inlines.Add(new System.Windows.Documents.Run(text.Substring(0, keyed)) { Foreground = SentBrush });

            if (keyed < text.Length)
                _overlay.Inlines.Add(new System.Windows.Documents.Run(text.Substring(keyed)) { Foreground = Brushes.Black });
        }

        // Every chunk the radio has finished with leaves the line, so what stands in the frame is what
        // is still to come. The caret comes back the same number of characters, so text taken from in
        // front of it does not move the cursor out from under his fingers mid-word.
        private void DropWhatTheRadioHasKeyed()
        {
            if (_inFlight.Count == 0) return;

            // NOT WHILE THERE IS MORE TO COME. Text in the line the radio has not been given yet means
            // he is still typing this transmission, and the check below could beat the next hand-over
            // by one tick and empty the line with the new text still standing on it.
            if ((_box.Text ?? string.Empty).Length > _handedUpTo) return;

            // TOO SOON TO BE OVER. Not even a sixty-words-a-minute station could have finished this
            // yet, so whatever the radio says about being back in receive, it has not sent the message.
            if (DateTime.UtcNow < _earliestDoneUtc) return;

            bool backInReceive = _txStoppedUtc != DateTime.MaxValue;

            // A radio that reports going ON air but never reports coming OFF it would hold the text
            // there for ever. So there is a ceiling: the time the message would take at five words a
            // minute. Nothing real is slower.
            if (!backInReceive && DateTime.UtcNow < _worstCaseDoneUtc) return;

            while (_inFlight.Count > 0)
            {
                var sent = _inFlight.Dequeue();
                string chunk = sent.Key;
                string text = _box.Text ?? string.Empty;

                // The space written in for a heard gap sits in front of the chunk and comes off with
                // it, though the radio was never given it.
                int drop = Math.Min(chunk.Length + (sent.Value ? 1 : 0), text.Length);

                int caret = _box.CaretIndex;
                _box.Text = text.Substring(drop);
                _box.CaretIndex = Math.Max(0, caret - drop);
                _handedUpTo = Math.Max(0, _handedUpTo - drop);

                // The units of what just left go with it, or the sum would be measuring a line that is
                // no longer there and everything still on screen would come up already keyed.
                _unitsKeyed = Math.Max(0, _unitsKeyed - CwSendMonitorWindow.ComputeTotalUnits(text.Substring(0, drop)));
            }

            RepaintLine();
        }

        // -- THE SPEED ---------------------------------------------------------------------------
        //
        // Asked once a second while nothing is going out, and at the moment the radio comes off air. A
        // question put on the wire in the middle of a message competes with the text for the same
        // wire, and the text is the thing that must not stutter.
        //
        // AND IT STOPS ASKING A RADIO THAT WILL NOT ANSWER. Not every radio the program keys can be
        // asked its speed. Those are keyed exactly as before and the readout stays at --, which is
        // honest; what must not happen is a question every second for the whole evening.
        private static readonly TimeSpan AskSpeedEvery = TimeSpan.FromSeconds(1);
        private const int AskSpeedGiveUpAfter = 10;
        private DateTime _speedAskedUtc = DateTime.MinValue;
        private int _speedAsksUnanswered;
        private bool _askSpeedNow;
        private int _wpm;

        private void AskRadioItsSpeed()
        {
            if (_askSpeed == null) return;

            if (!_askSpeedNow)
            {
                if ((_box.Text ?? string.Empty).Length > 0 || _inFlight.Count > 0) return;
                if (DateTime.UtcNow < _radioBusyUntil) return;
                if (DateTime.UtcNow - _speedAskedUtc < AskSpeedEvery) return;
            }

            if (_speedAsksUnanswered >= AskSpeedGiveUpAfter) { _askSpeedNow = false; return; }

            _askSpeedNow = false;
            _speedAskedUtc = DateTime.UtcNow;
            _speedAsksUnanswered++;

            try { _askSpeed(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The speed the radio is really at. Nothing here is ever sent back to it.
        internal void ShowSpeed(int wpm)
        {
            // AN ANSWER OF ANY KIND MEANS THE RADIO IS TALKING, even one that says what the readout
            // already shows - so the tries start counting from nothing again.
            _speedAsksUnanswered = 0;

            if (wpm <= 0 || wpm == _wpm) return;

            // See SettleAfterSet: an answer to a question asked before the wheel was turned says where
            // the radio WAS, and would pull the readout back off the notch just made.
            if (DateTime.UtcNow - _setUtc < SettleAfterSet) return;

            // AND IT HAS TO BE A SPEED THIS RADIO COULD BE AT - an Elecraft starts at 8, an Icom stops
            // at 48. A number outside the maker's own range is a number that was misread out of the
            // reply, and it is dropped rather than shown. The keyer window checks its readout the same
            // way; this one was taking whatever came back.
            int low, high;
            if (!Limits(out low, out high)) return;
            if (wpm < low || wpm > high) return;

            _wpm = wpm;
            RefreshSpeedText();
        }

        private void RefreshSpeedText()
        {
            // TWO DASHES UNTIL THE RADIO HAS ANSWERED. A remembered number is exactly the thing this
            // readout exists to stop showing: it would read 22 while the radio keyed at 10 and look no
            // different from the truth.
            _wpmText.Text = _wpm > 0
                ? _wpm.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "--";
        }

        // -- ONE NOTCH, ONE WORD A MINUTE --------------------------------------------------------
        //
        // NOTHING UNTIL THE RADIO HAS SAID WHERE IT IS. A notch has to move a number by one, and there
        // is no moving a number that reads --. Sending a speed of our own choosing at that moment is
        // exactly the guess this readout exists to stop making.
        //
        // AND IT STOPS AT WHAT THE RADIO ACCEPTS - an Elecraft starts at 8, an Icom stops at 48.
        private void Nudge(int by)
        {
            if (_setSpeed == null || _wpm <= 0) return;

            int low, high;
            if (!Limits(out low, out high)) return;

            int wanted = _wpm + by;
            if (wanted < low) wanted = low;
            if (wanted > high) wanted = high;
            if (wanted == _wpm) return;

            _wpm = wanted;
            RefreshSpeedText();

            // THE NUMBER MOVES FIRST, THEN THE RADIO IS TOLD. A notch that waited for the radio to
            // answer before it showed anything would feel like a wheel with a stone in it.
            _setUtc = DateTime.UtcNow;
            try { _setSpeed(_wpm); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private bool Limits(out int low, out int high)
        {
            low = 0;
            high = 0;
            if (_speedRange == null) return false;

            try { _speedRange(out low, out high); }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }

            return high > 0;
        }

        private string SpeedTip()
        {
            string tip = "The radio's keyer speed, as the radio itself reports it.";

            int low, high;
            if (_setSpeed != null && Limits(out low, out high))
            {
                tip += Environment.NewLine + Environment.NewLine
                     + "Roll the wheel over it to change it: " + low + " to " + high
                     + " words a minute.";
            }

            return tip + Environment.NewLine + Environment.NewLine
                 + "The speed knob on the radio does the same job. Whichever you turn last wins, and "
                 + "this number follows both.";
        }

        // A MOMENT'S QUIET AFTER SENDING. A question can already be on the wire when the wheel is
        // turned, and its answer is the OLD speed - so without this the number would jump back for a
        // second and then forward again.
        private static readonly TimeSpan SettleAfterSet = TimeSpan.FromSeconds(2);
        private DateTime _setUtc = DateTime.MinValue;

        // The radio's own number where it gives one, and only otherwise a speed to be going on with.
        // Nothing on this strip measures the keying, so there is no learned figure to fall back on -
        // twenty is the figure the program has always started from.
        private double KeyingWpm()
        {
            return _wpm > 0 ? _wpm : 20.0;
        }
    }
}
