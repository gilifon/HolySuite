using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace HolyLogger
{
    /// <summary>
    /// THE CW KEYER: what is typed here goes out of the radio as it is typed.
    ///
    /// Its own window, and not the main one, for the same reason every contest logger does it that
    /// way: the entry form already owns every keystroke, and a keyboard that stole them would break
    /// logging.
    ///
    /// TWO ROWS, AND THE TEXT FALLS FROM ONE TO THE OTHER. The top row is what has NOT gone out yet;
    /// the row below is what HAS. A character is not copied down, it MOVES down - and it moves when
    /// the radio has KEYED it, not when the radio was handed it. Those are seconds apart: a CAT
    /// command carrying a dozen characters is accepted at once and then takes several seconds to go
    /// on air. Moving on the handover emptied the top row before a single dit had been sent, so the
    /// operator never saw what he had just pressed.
    ///
    /// So a chunk given to the radio waits in the top row until its keying time is up, and only then
    /// drops to the row below. The top row therefore reads as what is still to come - some of it in
    /// the radio's hands, the rest still only typed - which is why Backspace has to be held back from
    /// the part already handed over: see _handedUpTo.
    ///
    /// HOW THE TEXT GETS OUT. The radio is keyed by CAT, and a CAT command carries a handful of
    /// characters at a time (Icom takes thirty, the KY radios about two dozen). So the typing is not
    /// sent letter by letter - that would flood the radio with commands - but in short chunks, PACED:
    /// after each chunk the window works out how long the radio will be busy keying it (the same
    /// PARIS timing the sending monitor uses, at the same self-learned speed) and holds the next one
    /// back until the radio is nearly done. The radio therefore always has a little in hand and never
    /// a backlog, which is what makes Escape stop the sending NOW instead of a paragraph from now.
    ///
    /// ONE TRANSMISSION, ONE LINE. What has gone does not run on for ever in a single blue line: a
    /// few seconds after the radio falls silent that line is finished with, and the next thing sent
    /// starts a new line at the top - which pushes the finished one, and everything under it, down.
    /// So the rows below the typing row read like the last few things said, newest first. How long
    /// the silence has to be before a line is finished is set at the gear; nought never finishes one,
    /// and everything stays on the single line it used to.
    ///
    /// Escape stops the radio and throws away the backlog - what has already gone stays in the row
    /// below, because it has gone. Enter does NOT close the window: the operator carries on typing
    /// the next thing. The window closes on the X, on Ctrl+K, or when the radio leaves CW.
    ///
    /// EIGHT BUTTONS ALONG THE BOTTOM, for the things said over and over. Mouse only - no F-keys,
    /// because those already belong to the four Msg buttons on the main window and a key that means
    /// two things depending on which window has the focus is a key nobody trusts. A left-click drops
    /// the text into the typing row, so it goes out through the same paced sending as everything else
    /// and lands in the same record; a right-click opens the same editor the Msg buttons use.
    ///
    /// THE FIRST FOUR ARE THE MSG BUTTONS. Not copies of them - the same four texts, shown in a second
    /// place. Edit either and both change, because both read CwMsgText1..4. That is what makes it safe
    /// to take the four Msg buttons off the main window one day: their texts do not live there.
    ///
    /// N1MM closes its window on Enter and opens a fresh empty one next time, so it needs no history
    /// and nothing to tidy. This one stays open, so the sent text has somewhere to go.
    /// </summary>
    public class CwKeyboardWindow : Window
    {
        // Hands one chunk of text to the radio. True when the CAT command went out.
        private readonly Func<string, bool> _sendChunk;

        // Aborts whatever the radio is keying.
        private readonly Action _stopSending;

        // The keying speed as the program currently understands it - learned from real transmissions,
        // so it improves the more the radio is used.
        private readonly Func<double> _currentWpm;

        // Whether that speed has ever been measured. Until it has, the number above is only the figure
        // the program started with, and a guess shown as a reading is worse than no reading.
        private readonly Func<bool> _wpmMeasured;

        // How much a single CAT command may carry. The radio's own buffer, not a guess.
        private readonly int _maxChunk;

        // Turns * and ! into callsigns. The stored text keeps the macro; only what goes on air is
        // expanded, so a button reads the same next year when the callsign in the form is different.
        private readonly Func<string, string> _expandMacros;

        // Hands back a speed measured from a real transmission, so the program's idea of how fast the
        // radio keys improves every time this window is used - not only when a Msg button is pressed.
        private readonly Action<double> _learnWpm;

        // Names the macro in a text that has nothing to fill it, or null when the text can be sent.
        private readonly Func<string, string> _macroProblem;

        // Opens the shared CW text editor (title, current text) and gives back the new text, or null
        // when the operator cancelled.
        private readonly Func<string, string, string> _editText;

        // Reads and writes the four texts the Msg buttons own. Kept as callbacks rather than reading
        // the settings here, so the main window can redraw its own four faces when one is edited from
        // this side.
        private readonly Func<int, string> _getSharedText;
        private readonly Action<int, string> _setSharedText;

        private const int ButtonCount = 8;

        // Buttons 1..SharedButtons are the Msg buttons' texts; the rest are this window's own.
        private const int SharedButtons = 4;
        private readonly Button[] _buttons = new Button[ButtonCount];
        private readonly string[] _buttonTexts;

        // Is the radio keying THIS INSTANT? Null when nothing can be asked. The line below is
        // finished a few seconds after this goes false, and not before.
        private readonly Func<bool> _isTransmitting;

        // Has the radio been seen KEYING since the last chunk was handed to it, and when did it stop.
        //
        // THE FLAG IS PER SEND, not per session, and that is the whole point of it. A radio takes a
        // moment to key up after the CAT command lands, and during that moment it answers "not
        // transmitting" - which is indistinguishable from "finished" unless you insist on having seen
        // it transmitting first. Without that insistence every message was declared sent the instant
        // it was handed over, and the text left the typing row before a dit had been keyed.
        private bool _txSeenThisSend;
        private DateTime _txStoppedUtc = DateTime.MaxValue;

        // Asking the radio costs a CAT call, so it is asked no oftener than this - and only while
        // something is actually on air or a line is waiting to be finished, never idly.
        private DateTime _txAskedUtc = DateTime.MinValue;
        private static readonly TimeSpan TxAskEvery = TimeSpan.FromMilliseconds(100);

        // NOBODY KEYS SLOWER THAN THIS. Working the message out at five words a minute gives a time
        // that CANNOT be beaten by any real station, so once it has passed the message is finished -
        // whatever the radio does or does not say about its transmit state.
        //
        // It is deliberately not the learned speed. That is a guess, and a guess that runs SHORT
        // takes the text off the screen early; this one only ever runs long, and it is only ever
        // reached when the radio has told us nothing.
        private const double SlowestPlausibleWpm = 5.0;

        // AND NOBODY KEYS FASTER THAN THIS. Worked out at sixty words a minute it gives the EARLIEST
        // the message could possibly have finished. Before that moment the radio saying "receive" is
        // not the end of the message - it is the radio not having started yet - so it is ignored.
        private const double FastestPlausibleWpm = 60.0;



        // The earliest everything handed over could possibly be finished.
        private DateTime _earliestDoneUtc = DateTime.MinValue;

        // What is needed to MEASURE the speed rather than guess at it: how much Morse was handed over
        // for this transmission, and when the radio actually started keying it. Time divided by units
        // is the speed, and that is the same sum the sending monitor does for the canned messages.
        // The title bar says what speed the program believes the radio is keying at, so the operator
        // can see it settle on the truth instead of taking it on faith. Redrawn only when the whole
        // number changes, not on every tick.
        // Draws the send line in its two colours; see where it is built for why the box itself does
        // not draw at all.
        private TextBlock _sendOverlay;

        private TextBlock _titleText;
        private int _shownWpm = -1;

        // Working out whether every button can be sent means reading the entry form eight times over.
        // Four times a second is far oftener than a callsign is typed and far seldomer than the pump.
        private DateTime _availabilityAskedUtc = DateTime.MinValue;
        private static readonly TimeSpan AvailabilityEvery = TimeSpan.FromMilliseconds(250);

        private double _unitsThisSend;
        private DateTime _txStartedUtc = DateTime.MinValue;

        // When the radio must have finished everything handed to it, worked out at that speed.
        private DateTime _worstCaseDoneUtc = DateTime.MinValue;

        // The backlog being typed, and below it the record of what has gone.
        private readonly TextBox _box;
        private readonly TextBox _history;

        private readonly DispatcherTimer _pump;

        // When the radio is expected to finish what it has been given. The next chunk goes out
        // shortly before this, so the keying never stops in the middle of a word.
        private DateTime _radioBusyUntil = DateTime.MinValue;

        // How much work to keep in the radio's hands. Long enough to cover the gap between two
        // pumps, short enough that Escape still means "now".
        private static readonly TimeSpan Lead = TimeSpan.FromSeconds(0.6);

        // The line being added to now - what the radio is sending, or has just sent - and the finished
        // ones under it, newest first. They are held as text rather than read back off the screen so
        // that the row count can change without the record changing with it.
        // How many characters at the FRONT of the typing row have already been given to the radio.
        // They are still on screen - the radio has not keyed them yet - but they cannot be taken back.
        private int _handedUpTo;

        // Each chunk handed over, with the moment the radio should have finished keying it. That is
        // when it leaves the typing row and joins the record below.
        // Each chunk handed to the radio, and whether the radio had ALREADY RUN DRY before it went.
        // That second half is what the listener heard as a gap, and it is written into the record.
        private readonly Queue<KeyValuePair<string, bool>> _inFlight = new Queue<KeyValuePair<string, bool>>();

        private string _openLine = string.Empty;
        private readonly List<string> _finishedLines = new List<string>();

        // Further back than any row count can show, so raising the rows at the gear reveals lines that
        // were already there instead of empty space. A session that runs all weekend still stops here.
        private const int MaxFinishedLines = 60;

        // The blue of a character that has left. Fixed rather than themed: it has to mean the same
        // thing in every colour scheme, and it is legible on all of them.
        private static readonly Brush SentBrush = MakeSentBrush();

        // The face colour of a CW message button. Fixed, like the buttons themselves - it is what
        // marks CW out from everything else on the screen, in any colour scheme.
        private static readonly Brush CwKeyBrush = MakeCwKeyBrush();

        private static Brush MakeCwKeyBrush()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x7F, 0xFE, 0xFF));
            brush.Freeze();
            return brush;
        }

        private static Brush MakeSentBrush()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF));
            brush.Freeze();
            return brush;
        }

        public CwKeyboardWindow(Func<string, bool> sendChunk, Action stopSending, Func<double> currentWpm,
                                Func<bool> wpmMeasured, int maxChunk, Func<bool> isTransmitting,
                                Func<string, string> expandMacros, Func<string, string> macroProblem,
                                Action<double> learnWpm, Func<string, string, string> editText,
                                Func<int, string> getSharedText, Action<int, string> setSharedText)
        {
            _sendChunk = sendChunk;
            _stopSending = stopSending;
            _currentWpm = currentWpm;
            _wpmMeasured = wpmMeasured;
            _maxChunk = maxChunk < 4 ? 4 : maxChunk;
            _isTransmitting = isTransmitting;
            _expandMacros = expandMacros;
            _macroProblem = macroProblem;
            _learnWpm = learnWpm;
            _editText = editText;
            _getSharedText = getSharedText;
            _setSharedText = setSharedText;
            _buttonTexts = LoadButtonTexts();

            Title = "CW Keyer";

            // The window is as tall as its two rows, whatever the operator has set the history to -
            // change the number of history rows at the gear and the window grows or shrinks with it.
            SizeToContent = SizeToContent.Height;

            // THE WIDTH IS THE OPERATOR'S, the height is not. Dragging the sides makes the line longer
            // or shorter and WindowBounds remembers it; the height is worked out from the rows.
            Width = 560;

            // Four buttons across, each wide enough for eight characters of the face font, is what
            // sets the floor now - narrower than this and the faces start losing characters.
            MinWidth = 420;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // THE OS CAPTION IS REPLACED BY OUR OWN, only so the gear can sit in it - Windows will not
            // let anything of ours into its title bar. Same WindowChrome setup as the Cluster window:
            // CaptionHeight matches the bar built below, so dragging the bar still moves the window.
            WindowStyle = WindowStyle.None;
            SetResourceReference(BackgroundProperty, "WindowBg");
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 32,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = false
            });

            var typeface = new FontFamily("Consolas");

            _box = new TextBox
            {
                FontSize = 20,
                FontFamily = typeface,
                CharacterCasing = CharacterCasing.Upper,
                Margin = new Thickness(10, 10, 10, 0),
                Padding = new Thickness(4, 2, 4, 2),
                AcceptsReturn = false,
                TextWrapping = TextWrapping.NoWrap
            };
            _box.PreviewKeyDown += Box_PreviewKeyDown;
            _box.PreviewTextInput += Box_PreviewTextInput;

            // AND THE SAME RULE FOR PASTE. PreviewTextInput fires for typing and for nothing else, so
            // Ctrl+V put straight into the box whatever the clipboard held - and what a keyer cannot
            // send is dropped later, silently, on the way to the radio. Cleaned rather than refused:
            // what can be keyed goes in, the rest is left out.
            DataObject.AddPastingHandler(_box, (s2, e2) =>
            {
                try
                {
                    string pasted = e2.DataObject.GetDataPresent(DataFormats.UnicodeText)
                                  ? e2.DataObject.GetData(DataFormats.UnicodeText) as string
                                  : null;
                    if (pasted == null) { e2.CancelCommand(); return; }

                    string clean = new string(pasted.Where(IsSendable).ToArray());
                    if (clean.Length == pasted.Length) return;

                    var wrapped = new DataObject();
                    wrapped.SetData(DataFormats.UnicodeText, clean);
                    e2.DataObject = wrapped;
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); e2.CancelCommand(); }
            });

            // A TextBox paints all its text in one colour, so it paints none of it: what the operator
            // sees comes from this TextBlock sitting exactly under the box, in two runs - blue for the
            // characters the radio has already keyed, ordinary for the rest. The box keeps the caret,
            // the typing and the selection; it just does not draw.
            _sendOverlay = new TextBlock
            {
                FontSize = 20,
                FontFamily = typeface,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            var overlayShift = new TranslateTransform();
            _sendOverlay.RenderTransform = overlayShift;
            _box.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((s2, e2) => overlayShift.X = -_box.HorizontalOffset));

            _box.Background = Brushes.Transparent;
            _box.Foreground = Brushes.Transparent;
            _box.BorderThickness = new Thickness(0);
            _box.Padding = new Thickness(0);
            _box.Margin = new Thickness(0);
            _box.VerticalContentAlignment = VerticalAlignment.Center;
            _box.SelectionOpacity = 0.35;
            _box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "TextBrush");

            var sendLayers = new Grid { ClipToBounds = true };
            sendLayers.Children.Add(_sendOverlay);
            sendLayers.Children.Add(_box);

            var sendFrame = new Border
            {
                BorderThickness = new Thickness(1),
                Margin = new Thickness(10, 10, 10, 0),
                Padding = new Thickness(4, 2, 4, 2),
                Child = sendLayers
            };
            sendFrame.SetResourceReference(Border.BorderBrushProperty, "MutedTextBrush");

            // WHAT HAS GONE, in blue and a size smaller. Read-only rather than merely disabled, so the
            // operator can still select a line of it and copy it out; never reached by Tab, because the
            // only thing in this window worth having the focus is the row being typed into.
            _history = new TextBox
            {
                FontSize = 16,
                FontFamily = typeface,
                Foreground = SentBrush,
                IsReadOnly = true,
                IsTabStop = false,

                // Real lines, so one transmission sits on one row and the rest are under it. Without
                // AcceptsReturn a read-only box shows the whole record run together on a single line.
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Margin = new Thickness(10, 6, 10, 10),
                Padding = new Thickness(4, 2, 4, 2)
            };
            ApplyHistoryRows();

            // THE SEND LINE IS A BOX LIKE THE ONE BELOW IT. Its own TextBox paints nothing - that is
            // how the two colours of the text are drawn - so without this the window's grey showed
            // through and the top row looked disabled beside the white record under it. Bound rather
            // than set, so the two stay the same colour through a change of scheme.
            sendFrame.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { Source = _history });

            var body = new DockPanel { LastChildFill = false };

            var titleBar = BuildTitleBar();
            DockPanel.SetDock(titleBar, Dock.Top);
            DockPanel.SetDock(sendFrame, Dock.Top);
            DockPanel.SetDock(_history, Dock.Top);

            var buttonGrid = BuildButtons(typeface);
            DockPanel.SetDock(buttonGrid, Dock.Top);

            body.Children.Add(titleBar);
            body.Children.Add(sendFrame);
            body.Children.Add(_history);
            body.Children.Add(buttonGrid);

            // WindowStyle.None takes the OS frame with it, so this border IS the window's visible edge.
            var frame = new Border { BorderThickness = new Thickness(1), Child = body };
            frame.SetResourceReference(Border.BorderBrushProperty, "TextBrush");

            Content = frame;
            Loaded += (s, e) => { _box.Focus(); LockMinimumHeight(); };

            _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _pump.Tick += Pump_Tick;
            _pump.Start();

            // Where the operator put it is where it comes back. WindowBounds is the one that knows how
            // to bring a corner saved on a second screen back onto a screen that is still there.
            WindowBounds.Attach(this, "CwKeyboard");
        }

        // Only the characters a keyer can actually send. Anything else would be dropped by the radio
        // anyway, and dropping it here keeps the screen honest about what is going out.
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
                if (!IsSendable(c)) { e.Handled = true; return; }
            }
        }

        private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+K again puts the window away - the key that opened it. The X in the corner does the
            // same thing; this is only so the operator's hands need not leave the keyboard.
            if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                Close();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                StopEverything();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                // Swallowed on purpose. There is nothing for it to do - what is typed is already on
                // its way out - and it must NOT close the window: the operator keeps typing.
                e.Handled = true;
                return;
            }

            // WHAT HAS GONE IS GONE. The front of this row is in the radio's hands even though it is
            // still on the screen, so Backspace and Delete are not allowed to reach into it.
            if ((e.Key == Key.Back || e.Key == Key.Delete) && _box.SelectionStart < _handedUpTo)
            {
                e.Handled = true;
            }
        }

        private void Pump_Tick(object sender, EventArgs e)
        {
            // Whatever the radio has finished keying leaves the typing row first, so the row always
            // shows what is still to come rather than what has just gone.
            RefreshTitle();
            RefreshButtonAvailability();
            RepaintSendLine();
            DropWhatTheRadioHasKeyed();

            string text = _box.Text ?? string.Empty;

            // Everything in the row is already in the radio's hands; there is nothing new to hand it.
            if (text.Length <= _handedUpTo)
            {
                if (text.Length == 0) FinishLineIfSilent();
                return;
            }

            // The radio still has enough to be going on with.
            if (DateTime.UtcNow + Lead < _radioBusyUntil) return;

            int waiting = text.Length - _handedUpTo;
            int take = Math.Min(_maxChunk, waiting);

            // CUT AT A SPACE, not at the twelfth character. If the radio ever runs dry at a chunk
            // boundary the gap is heard, and a gap in the middle of a word breaks the word in two.
            // At a space the gap is expected anyway, so it costs nothing. Only when the chunk is
            // full and more text follows - a short tail being typed is sent as it stands.
            if (take == _maxChunk && take < waiting)
            {
                int lastSpace = text.LastIndexOf(' ', _handedUpTo + take - 1, take);
                if (lastSpace > _handedUpTo) take = lastSpace - _handedUpTo + 1;
            }

            string chunk = text.Substring(_handedUpTo, take);

            // TYPED TOO SLOWLY TO KEEP THE RADIO FED. The radio had finished everything it was given
            // before this arrived, so it stopped keying - and a stop in the middle of a word is heard
            // as the end of one word and the start of another. "GOODBYE" typed haltingly goes out as
            // "GOO D BY E", and the operator has no way of knowing unless he is told.
            //
            // So the record shows what was HEARD, not what was typed: a space is written in wherever
            // the keying actually stopped. Nothing extra is sent to the radio - the gap is already on
            // air, this only makes it visible.
            // HOW LONG IS TOO LONG IS NOT A GUESS. Morse puts three units between the letters of a
            // word and seven between words, so four units of silence ON TOP of the normal letter gap
            // is the moment one word becomes two in the listener's ear. At the speed the radio has
            // been measured keying, that is a real number of seconds - and only a pause longer than
            // it is written into the record. A few milliseconds of overrun is not a word break.
            double gapWpm = 20.0;
            try { if (_currentWpm != null) gapWpm = _currentWpm(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            if (gapWpm < 5) gapWpm = 5;

            double heardAsWordGap =
                (CwSendMonitorWindow.WordGapUnits - CwSendMonitorWindow.LetterGapUnits) * 1.2 / gapWpm;

            // Measured against WHAT HAS BEEN HANDED TO THE RADIO, not against the record below. The
            // record fills only when the send line is cleared, which is seconds later - so while the
            // first word was still sitting on the screen the record was empty, the test failed, and
            // no gap was ever marked. What the radio has been given is the thing that went on air.
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

            // THE GAP IS WRITTEN INTO THE LINE THE OPERATOR IS LOOKING AT, straight away, so he sees
            // his own hesitation as he makes it rather than afterwards. It goes in AFTER the handed
            // mark, where the radio can never reach it - the silence is already on air, and sending a
            // space as well would put a second word gap there.
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

            double wpm = 20.0;
            try { if (_currentWpm != null) wpm = _currentWpm(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            if (wpm < 5) wpm = 5;

            double seconds = CwSendMonitorWindow.ComputeTotalUnits(chunk) * 1.2 / wpm;
            DateTime from = _radioBusyUntil > DateTime.UtcNow ? _radioBusyUntil : DateTime.UtcNow;
            _radioBusyUntil = from.AddSeconds(seconds);

            // It comes off the screen when the radio is done with it, not now.
            _inFlight.Enqueue(new KeyValuePair<string, bool>(chunk, ranDry));

            // The time by which even a five-words-a-minute station would have finished this chunk,
            // chained onto whatever is already queued ahead of it.
            double worstSeconds = CwSendMonitorWindow.ComputeTotalUnits(chunk) * 1.2 / SlowestPlausibleWpm;
            DateTime worstFrom = _worstCaseDoneUtc > DateTime.UtcNow ? _worstCaseDoneUtc : DateTime.UtcNow;
            _worstCaseDoneUtc = worstFrom.AddSeconds(worstSeconds);

            _unitsThisSend += CwSendMonitorWindow.ComputeTotalUnits(chunk);

            double bestSeconds = CwSendMonitorWindow.ComputeTotalUnits(chunk) * 1.2 / FastestPlausibleWpm;
            DateTime bestFrom = _earliestDoneUtc > DateTime.UtcNow ? _earliestDoneUtc : DateTime.UtcNow;
            _earliestDoneUtc = bestFrom.AddSeconds(bestSeconds);

            // THE WATCH STARTS AGAIN HERE. Without this, the moment the radio last went back to
            // receive - from the PREVIOUS transmission - still counted as "back in receive", and the
            // text dropped off the screen the instant it was handed over, before a dit was keyed.
            // A radio takes a moment to key up after a CAT command; until it does, and until it has
            // come back down again, nothing has been sent as far as this window is concerned.
            // ONLY IF THE RADIO IS NOT ALREADY ON AIR. A long message goes over in several chunks, and
            // they are one transmission, not several: resetting here restarted the clock the colouring
            // measures from while the character count went on running from the first letter, so the
            // tail of a message was never reached and never turned blue.
            if (!_txSeenThisSend || _txStoppedUtc != DateTime.MaxValue)
            {
                _txSeenThisSend = false;
                _txStoppedUtc = DateTime.MaxValue;
            }
        }

        // Every chunk whose keying time is up leaves the typing row and joins the record below it.
        // The caret comes back the same number of characters, so text taken from in front of it does
        // not move the cursor out from under the operator's fingers mid-word.
        private void DropWhatTheRadioHasKeyed()
        {
            if (_inFlight.Count == 0) return;

            WatchTransmitState();

            // THE RADIO SAYS WHEN IT HAS FINISHED - it is asked whether it is back in receive, and
            // until it says so nothing moves, however long this program's own arithmetic thought the
            // keying would take. The arithmetic is a guess at a speed; this is the radio itself.
            // TOO SOON TO BE OVER. Not even a sixty-words-a-minute station could have finished this
            // yet, so whatever the radio says about being in receive, it has not sent the message.
            if (DateTime.UtcNow < _earliestDoneUtc) return;

            bool backInReceive = _txStoppedUtc != DateTime.MaxValue;

            // A radio that reports going ON air but never reports coming OFF it would hold the text
            // on screen for ever. So there is a ceiling: the time the message would take at five
            // words a minute. Nothing real is slower, so once that has passed it is over.
            if (!backInReceive && DateTime.UtcNow < _worstCaseDoneUtc) return;

            // AND THEN THE OPERATOR'S OWN SECONDS. Sending has ended; the text stays where it was
            // typed for as long as he asked for, so he can read back what just went out, and only
            // then drops to the record. That is what "Clear send line after sending ended" means.
            DateTime endedAt = backInReceive ? _txStoppedUtc : _worstCaseDoneUtc;
            if (DateTime.UtcNow < endedAt.AddSeconds(BreakSeconds())) return;

            bool moved = false;

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

                if (sent.Value) _openLine += " ";
                _openLine += chunk;
                moved = true;
            }

            if (moved) RenderHistory();
        }

        // The radio has stopped and stayed stopped for as long as the operator asked for. The line
        // being added to is finished with: it drops into the list of finished ones at the top, which
        // pushes everything already there one row further down, and the next thing sent starts fresh.
        private void FinishLineIfSilent()
        {
            if (_openLine.Length == 0) return;

            int seconds = BreakSeconds();
            if (seconds <= 0) return;                       // nought: the line runs on for ever

            WatchTransmitState();

            // THE COUNT STARTS WHEN THE RADIO ACTUALLY STOPS KEYING, which is a thing the radio can
            // be asked. It used to start from _radioBusyUntil - the program's ESTIMATE of when the
            // keying would end - and that estimate runs long: the speed behind it defaults to 20 wpm
            // and is only ever corrected by the canned F-key messages, never by this window, so a
            // radio keying at 25 or 30 left the estimate seconds adrift and three seconds became six.
            // The estimate is still the fallback, for a radio that will not say whether it is keying.
            DateTime silentSince;

            if (_txStoppedUtc != DateTime.MaxValue && _txSeenThisSend)
            {
                // The radio said so itself: this is the moment it stopped keying.
                silentSince = _txStoppedUtc;
            }
            else if (_txSeenThisSend && DateTime.UtcNow < _worstCaseDoneUtc)
            {
                // It says it is still keying and even the slowest possible station would not have
                // finished yet, so it is taken at its word.
                return;
            }
            else
            {
                // NO USABLE READING, so the ORDINARY estimate decides - the one worked out at the
                // speed the radio is believed to be keying at.
                //
                // NOT the five-words-a-minute ceiling. That ceiling exists so text is never taken off
                // the screen early, and being generous costs nothing there. Here it would be added to
                // the operator's own three seconds and make the wait thirteen - which is why every
                // message ran on into the same blue line instead of pushing it down.
                silentSince = _radioBusyUntil;
            }

            if (DateTime.UtcNow < silentSince.AddSeconds(seconds)) return;

            FinishLineNow();
        }

        // Reads the radio's transmit state, no oftener than TxAskEvery, and remembers the moment it
        // last went from keying to not. MaxValue means "keying now, or never seen keying" - either
        // way the count above has not started.
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
                if (!_txSeenThisSend) _txStartedUtc = DateTime.UtcNow;
                _txSeenThisSend = true;
                _txStoppedUtc = DateTime.MaxValue;
            }
            else if (_txSeenThisSend && _txStoppedUtc == DateTime.MaxValue)
            {
                // Seen on air, and now off it: THIS is the moment the message ended.
                _txStoppedUtc = DateTime.UtcNow;
                LearnSpeedFromThisSend();
            }
        }

        // THE SEND LINE HOLDS THE NEWEST MESSAGE AND NOTHING ELSE. When a new one is pressed while
        // the last is still sitting there waiting out its few seconds, the old one does not wait any
        // longer: it goes down to the record at once, so the line the operator is looking at is
        // always the thing he has just sent.
        private void FlushHandedText()
        {
            bool moved = false;

            while (_inFlight.Count > 0)
            {
                var sent = _inFlight.Dequeue();
                string chunk = sent.Key;

                string text = _box.Text ?? string.Empty;
                int drop = Math.Min(chunk.Length + (sent.Value ? 1 : 0), text.Length);

                _box.Text = text.Substring(drop);
                _handedUpTo = Math.Max(0, _handedUpTo - drop);

                if (sent.Value) _openLine += " ";
                _openLine += chunk;
                moved = true;
            }

            if (moved) RenderHistory();
        }

        // HOW FAST THE RADIO REALLY KEYS, worked out from what just happened rather than assumed.
        // The message is a known number of Morse units and the radio was on air for a known number of
        // seconds; one divided by the other is the speed. The keyer used to take no part in this - only
        // the canned Msg buttons did - so a station that never pressed one was timed at the default
        // twenty for ever, and every judgement resting on that ran wrong in the same direction.
        private void LearnSpeedFromThisSend()
        {
            double units = _unitsThisSend;
            _unitsThisSend = 0;

            if (_learnWpm == null || units <= 0) return;
            if (_txStartedUtc == DateTime.MinValue || _txStoppedUtc == DateTime.MaxValue) return;

            double seconds = (_txStoppedUtc - _txStartedUtc).TotalSeconds;
            // A SHORT TRANSMISSION MEASURES NOTHING USEFUL. The transmit state is polled a few times
            // a second, so a two-tenths error sits on every reading; on a message lasting half a
            // second that is a fifth of the answer, and the speed on screen jumped about even though
            // the radio had not been touched. Below a second, the reading is thrown away.
            if (seconds < 1.0) return;

            try { _learnWpm(units * 1.2 / seconds); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // THE MESSAGE GOES BLUE AS IT IS KEYED, a character at a time, the same way the sending
        // monitor walks the canned messages. The radio cannot be asked which character it is on, so it
        // is worked out from the clock: how long it has been transmitting, at the speed it has been
        // measured keying, in Morse units - and units are what the timing is built on throughout.
        //
        // Only once that speed has actually been MEASURED. Running this off the default twenty would
        // paint the line at a speed the radio is not sending at, and a cursor that is confidently in
        // the wrong place is worse than no cursor.
        private void RepaintSendLine()
        {
            if (_sendOverlay == null) return;

            string text = _box.Text ?? string.Empty;
            int keyed = KeyedSoFar(text);

            _sendOverlay.Inlines.Clear();

            if (keyed > 0)
            {
                _sendOverlay.Inlines.Add(new Run(text.Substring(0, keyed)) { Foreground = SentBrush });
            }

            if (keyed < text.Length)
            {
                var pending = new Run(text.Substring(keyed));
                pending.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
                _sendOverlay.Inlines.Add(pending);
            }
        }

        // How many characters of the send line the radio has had time to key. Never past what has
        // actually been handed to it, however long it has been transmitting.
        private int KeyedSoFar(string text)
        {
            if (text.Length == 0 || _handedUpTo <= 0) return 0;
            if (!_txSeenThisSend || _txStartedUtc == DateTime.MinValue) return 0;

            bool measured = false;
            try { measured = _wpmMeasured != null && _wpmMeasured(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            if (!measured) return 0;

            double wpm = 20.0;
            try { if (_currentWpm != null) wpm = _currentWpm(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            if (wpm < 5) wpm = 5;

            // THE RADIO HAS STOPPED, so everything it was given HAS been keyed - there is nothing left
            // to work out. Walking the clock for this case left the last character short: its timing
            // includes the gap that follows it, and that gap is over once the radio drops, so the
            // arithmetic never quite reached the end of the message.
            if (_txStoppedUtc != DateTime.MaxValue) return Math.Min(_handedUpTo, text.Length);

            DateTime until = DateTime.UtcNow;
            double elapsed = (until - _txStartedUtc).TotalSeconds;
            if (elapsed <= 0) return 0;

            double unitsDone = elapsed * wpm / 1.2;
            int limit = Math.Min(_handedUpTo, text.Length);

            // The same running total the sending monitor walks, so the two agree character for
            // character - and it counts the gaps BETWEEN letters rather than charging one to each.
            var cumulative = CwSendMonitorWindow.CumulativeUnits(text.Substring(0, limit));

            for (int i = 0; i < limit; i++)
            {
                if (cumulative[i] > unitsDone) return i;
            }

            return limit;
        }

        // A BUTTON THAT CANNOT BE SENT IS DIMMED. Its text asks for something that is not there - most
        // often ! with no callsign yet in the entry form - and dimming says so before it is pressed
        // rather than after.
        //
        // DIMMED, NOT DISABLED. A disabled button in WPF receives no mouse at all, so it could not be
        // right-clicked to edit either, and a button that cannot be used OR changed is a dead end.
        // The Msg buttons on the main window are dimmed for the same reason.
        // A STRAIGHT KEY, SIDE ON: base, upright, lever arm, contact post, knob. The same drawing as
        // the one on the View menu item, so the window and the way in to it are plainly the same
        // thing. Drawn rather than fetched - five shapes, and no licence to carry.
        private static UIElement BuildStraightKeyIcon()
        {
            var canvas = new Canvas { Width = 24, Height = 24 };

            canvas.Children.Add(Piece(2, 18.5, 20, 3, 1.2));      // base
            canvas.Children.Add(Piece(15.2, 11, 2.6, 7.5, 0));    // upright at the back
            canvas.Children.Add(Piece(4.5, 10.4, 14, 2.4, 1.2));  // lever arm
            canvas.Children.Add(Piece(6.6, 12.8, 1.8, 5.7, 0));   // contact post under the knob

            var knob = new System.Windows.Shapes.Ellipse
            {
                Width = 7.8,
                Height = 5,
                Fill = Brushes.Black
            };
            Canvas.SetLeft(knob, 3.6);
            Canvas.SetTop(knob, 6.2);
            canvas.Children.Add(knob);

            return new Viewbox
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = canvas
            };
        }

        private static System.Windows.Shapes.Rectangle Piece(double left, double top, double width, double height, double radius)
        {
            var piece = new System.Windows.Shapes.Rectangle
            {
                Width = width,
                Height = height,
                RadiusX = radius,
                RadiusY = radius,
                Fill = Brushes.Black
            };
            Canvas.SetLeft(piece, left);
            Canvas.SetTop(piece, top);
            return piece;
        }

        private void RefreshButtonAvailability()
        {
            if (_macroProblem == null) return;
            if (DateTime.UtcNow - _availabilityAskedUtc < AvailabilityEvery) return;
            _availabilityAskedUtc = DateTime.UtcNow;

            for (int i = 0; i < ButtonCount; i++)
            {
                var button = _buttons[i];
                if (button == null) continue;

                string text = _buttonTexts[i] ?? string.Empty;
                bool usable = true;

                if (text.Trim().Length > 0)
                {
                    try { usable = _macroProblem(text) == null; }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }

                double wanted = usable ? 1.0 : 0.5;
                if (button.Opacity != wanted) button.Opacity = wanted;
            }
        }

        private void RefreshTitle()
        {
            if (_titleText == null || _currentWpm == null) return;

            // NOTHING UNTIL THERE IS SOMETHING TO SAY. At startup the speed is the default the program
            // was built with, not the radio's - so the title carries no number at all until a real
            // transmission has been timed.
            bool measured = false;
            try { measured = _wpmMeasured == null || _wpmMeasured(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (!measured)
            {
                if (_shownWpm == -1) return;
                _shownWpm = -1;
                _titleText.Inlines.Clear();
                _titleText.Inlines.Add(new Run("CW Keyer") { Foreground = Brushes.Black });
                return;
            }

            double wpm;
            try { wpm = _currentWpm(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); return; }

            int shown = (int)Math.Round(wpm);
            if (shown == _shownWpm) return;

            _shownWpm = shown;

            // Bigger than the title beside it and in the same blue as the sent text, because it is a
            // reading the operator glances at mid-QSO - not a caption.
            _titleText.Inlines.Clear();
            _titleText.Inlines.Add(new Run("CW Keyer      ") { Foreground = Brushes.Black });
            _titleText.Inlines.Add(new Run(shown + " WPM") { FontSize = 20, Foreground = SentBrush });
        }

        // Puts the line being added to at the top of the finished ones and starts a new one, with no
        // reference to the clock. FinishLineIfSilent is this, once the radio has been quiet long
        // enough; a button press calls it outright.
        private void FinishLineNow()
        {
            if (_openLine.Length == 0) return;

            _finishedLines.Insert(0, _openLine);
            if (_finishedLines.Count > MaxFinishedLines) _finishedLines.RemoveRange(MaxFinishedLines, _finishedLines.Count - MaxFinishedLines);

            _openLine = string.Empty;
            RenderHistory();
        }

        // The open line on top, the finished ones under it, newest first. Redrawn whole rather than
        // appended to, because a line being finished moves every row on the screen.
        private void RenderHistory()
        {
            var text = new StringBuilder();

            // NOTHING IS WRITTEN FOR A LINE THAT HAS NOT STARTED. Between one transmission and the
            // next the open line is empty, and writing it anyway left a blank row at the top of the
            // list, pushing everything down a row before there was anything to push it for. The row
            // appears when there is something to put in it.
            if (_openLine.Length > 0) text.Append(_openLine);

            foreach (string line in _finishedLines)
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(line);
            }

            _history.Text = text.ToString();

            // Home, not End: the newest line is the top one, and that is the one to keep in view.
            _history.ScrollToHome();
        }

        // Escape: the radio stops and the backlog is thrown away. WHAT HAS ALREADY GONE STAYS in the
        // row below - it was sent, and a record that quietly loses the last thing it sent is worse
        // than no record at all.
        private void StopEverything()
        {
            try { if (_stopSending != null) _stopSending(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            _radioBusyUntil = DateTime.MinValue;
            _worstCaseDoneUtc = DateTime.MinValue;
            _earliestDoneUtc = DateTime.MinValue;
            _unitsThisSend = 0;
            _inFlight.Clear();
            _handedUpTo = 0;
            _box.Text = string.Empty;
        }

        // How many rows of sent text are on show. Read from the setting every time rather than kept in
        // a field, so a change at the gear takes hold at once.
        // How long the radio must stay silent before the line being added to is finished with.
        private static int BreakSeconds()
        {
            try
            {
                int seconds = Properties.Settings.Default.CwKeyboardBreakSeconds;
                return seconds < 0 ? 0 : seconds;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return 3; }
        }

        private static int HistoryRows()
        {
            try
            {
                int rows = Properties.Settings.Default.CwKeyboardHistoryRows;
                if (rows < 1) rows = 1;
                if (rows > 5) rows = 5;
                return rows;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return 3; }
        }

        // MinLines and MaxLines together fix the row to exactly that many lines of its own font, which
        // is the one measurement that stays right when the font or the theme changes under it.
        private void ApplyHistoryRows()
        {
            int rows = HistoryRows();
            _history.MinLines = rows;
            _history.MaxLines = rows;
            _history.ScrollToHome();

            LockMinimumHeight();
        }

        // THE WINDOW CANNOT BE SHRUNK PAST ITS OWN CONTENTS. Dragging the bottom edge up hid the eight
        // buttons entirely - the window still had them, there was simply nowhere for them to be drawn.
        // The floor is whatever the rows currently need, so it rises and falls with the history row
        // count set at the gear instead of being a number written down once.
        private void LockMinimumHeight()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    MinHeight = 0;
                    SizeToContent = SizeToContent.Height;
                    UpdateLayout();
                    if (ActualHeight > 0) MinHeight = ActualHeight;
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Two rows of four. One row of eight would need close to eight hundred pixels to show eight
        // characters a button, which is half again as wide as this window wants to be.
        private UniformGrid BuildButtons(FontFamily typeface)
        {
            var grid = new UniformGrid
            {
                Rows = 2,
                Columns = 4,
                Margin = new Thickness(7, 0, 7, 7)
            };

            for (int i = 0; i < ButtonCount; i++)
            {
                int number = i;

                var button = new Button
                {
                    // The same cyan keycap the CW message buttons wear, so the two sets of CW
                    // buttons in front of the operator look like the same kind of thing. Its own
                    // font size is 11, which is far too small here, so that one setter is overridden.
                    Style = Application.Current.Resources["MsgButtonCwStyle"] as Style,
                    FontSize = 16,
                    FontFamily = typeface,
                    Height = 34,
                    Margin = new Thickness(3),
                    Padding = new Thickness(2, 0, 2, 0),

                    // Stretch, not centre: the text block has to be handed the whole width before it
                    // can work out how much of the message fits and where to put the ellipsis.
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };

                button.Click += (s, e) => SendButton(number);

                // Right-click edits, exactly as it does on the four Msg buttons. Handled here so the
                // click does not also go looking for a context menu that does not exist.
                button.PreviewMouseRightButtonUp += (s, e) => { EditButton(number); e.Handled = true; };

                // Built as the tooltip opens, not when the face was drawn: what a macro comes out as
                // changes with every callsign typed into the entry form, and a tooltip written half
                // an hour ago would be telling the operator about the station before last.
                button.ToolTipOpening += (s, e) => button.ToolTip = BuildButtonTooltip(number);

                _buttons[i] = button;
                grid.Children.Add(button);
                RefreshButtonFace(i);
            }

            return grid;
        }

        // The first few characters of what the button holds, or just its number when it holds nothing
        // - an empty button that looked like a full one would be pressed once and never again.
        private void RefreshButtonFace(int index)
        {
            var button = _buttons[index];
            if (button == null) return;

            string text = _buttonTexts[index] ?? string.Empty;

            // AS MUCH OF IT AS THE BUTTON CAN HOLD, and an ellipsis where it runs out - not a fixed
            // eight characters, which cut "CQ CQ DE * * K" down to "CQ CQ DE" while there was still
            // room beside it. Drag the window wider and more of every message comes into view.
            if (text.Length == 0)
            {
                button.Content = (index + 1).ToString();
                button.SetResourceReference(ForegroundProperty, "MutedTextBrush");
            }
            else
            {
                button.Content = new TextBlock
                {
                    Text = text,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                button.SetResourceReference(ForegroundProperty, "TextBrush");
            }

            // Something has to be here for ToolTipOpening to fire at all; the real one is built there.
            button.ToolTip = BuildButtonTooltip(index);
        }

        // WHAT IT HOLDS, AND WHAT THAT WOULD SEND. The two are the same line for a plain message and
        // two different lines for one with a macro in it, which is the whole point of showing both.
        private string BuildButtonTooltip(int index)
        {
            string text = _buttonTexts[index] ?? string.Empty;

            if (text.Length == 0) return "Empty - right-click to give it some text";

            var lines = new StringBuilder();
            lines.Append(text);

            string problem = _macroProblem != null ? _macroProblem(text) : null;

            if (problem != null)
            {
                lines.Append("\nCannot send: ").Append(problem);
            }
            else
            {
                string sends = _expandMacros != null ? _expandMacros(text) : text;
                if (!string.Equals(sends, text, StringComparison.Ordinal))
                {
                    lines.Append("\nSends: ").Append(sends);
                }
            }

            lines.Append("\nRight-click to edit");
            return lines.ToString();
        }

        // A left-click puts the text in the typing row rather than firing it at the radio itself. One
        // sending path, one record, and Escape still stops it half way like anything else typed.
        private void SendButton(int index)
        {
            string stored = _buttonTexts[index] ?? string.Empty;

            if (stored.Trim().Length == 0)
            {
                HolyMessageBox.ShowWarning("Button " + (index + 1) + " is empty. Right-click it to give it some text.",
                                           "CW Keyer", this);
                return;
            }

            // A macro with nothing behind it stops the send and says which one, rather than keying a
            // hole where the number or the callsign should have been.
            if (_macroProblem != null)
            {
                string problem = _macroProblem(stored);
                if (problem != null)
                {
                    HolyMessageBox.ShowWarning("Button " + (index + 1) + " cannot be sent: " + problem + ".",
                                               "CW Keyer", this);
                    return;
                }
            }

            string text = _expandMacros != null ? _expandMacros(stored) : stored;
            text = (text ?? string.Empty).ToUpperInvariant();

            // Whatever a macro brought in is held to the same rule as typing: only what a keyer can
            // actually send gets through.
            text = new string(text.Where(IsSendable).ToArray()).Trim();

            if (text.Length == 0)
            {
                // NOT ABOUT A CALLSIGN. It used to say the callsign was empty, and by this point
                // that has already been ruled out - a missing callsign, serial or exchange is
                // caught above, by name. What is left is a button whose text holds nothing a keyer
                // can send. Typing and pasting are both filtered now, so this is a last resort for
                // a text saved by an older build.
                HolyMessageBox.ShowWarning(
                    "Nothing to send.\n\n"
                    + "Button " + (index + 1) + " holds nothing a CW keyer can send. Only letters, "
                    + "digits and  . , ? / @ = + -  go out over the air.\n\n"
                    + "Right-click the button to change its text.",
                    "CW Keyer", this);
                return;
            }

            // A PRESS IS A LINE. The timer decides where typing breaks, because typing has no edges
            // the program can see; a button press has one, so it does not need guessing at. Pressing
            // three buttons gives three rows, newest at the top, however fast they are pressed.
            //
            // Unless the radio is still working through the last one: then this is the tail of the
            // same transmission and belongs on the same row.
            FlushHandedText();
            FinishLineNow();

            _box.Text = (_box.Text ?? string.Empty) + text;
            _box.CaretIndex = _box.Text.Length;
            _box.Focus();
        }

        private void EditButton(int index)
        {
            if (_editText == null) return;

            // The first four say which F-key they are, because they ARE that message - the operator
            // should not have to wonder whether he is editing the same text twice.
            string title = index < SharedButtons
                ? "Edit CW Text " + (index + 1) + " (F" + (index + 5) + ")"
                : "Edit CW Keyer Text " + (index + 1);

            string updated = _editText(title, _buttonTexts[index] ?? string.Empty);
            if (updated == null) return;

            _buttonTexts[index] = updated;
            SaveButtonText(index, updated);
            RefreshButtonFace(index);
        }

        // All eight in ONE setting, the way WindowBoundsJson holds every window's placement: a ninth
        // button later costs nothing, and a profile that snapshots the settings takes them all along.
        private string[] LoadButtonTexts()
        {
            var texts = new string[ButtonCount];
            for (int i = 0; i < ButtonCount; i++) texts[i] = string.Empty;

            // The last four, in one setting of their own, the way WindowBoundsJson holds every
            // window's placement. The first four slots are written empty and never read: those texts
            // belong to the Msg buttons and are fetched below.
            try
            {
                string json = Properties.Settings.Default.CwKeyerButtonsJson;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var saved = JsonConvert.DeserializeObject<string[]>(json);
                    if (saved != null)
                    {
                        for (int i = SharedButtons; i < ButtonCount && i < saved.Length; i++)
                        {
                            texts[i] = saved[i] ?? string.Empty;
                        }
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (_getSharedText != null)
            {
                for (int i = 0; i < SharedButtons; i++)
                {
                    try { texts[i] = _getSharedText(i + 1) ?? string.Empty; }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
            }

            return texts;
        }

        // The Msg buttons keep their own four; only the rest go in this window's setting.
        private void SaveButtonText(int index, string text)
        {
            if (index < SharedButtons)
            {
                if (_setSharedText == null) return;
                try { _setSharedText(index + 1, text); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                return;
            }

            var own = new string[ButtonCount];
            for (int i = 0; i < ButtonCount; i++)
            {
                own[i] = i < SharedButtons ? string.Empty : (_buttonTexts[i] ?? string.Empty);
            }

            try
            {
                Properties.Settings.Default.CwKeyerButtonsJson = JsonConvert.SerializeObject(own);
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Called by the main window when one of the four Msg texts was edited over there, so the same
        // four faces in here do not go on showing what they used to say.
        internal void RefreshSharedButtons()
        {
            if (_getSharedText == null) return;

            for (int i = 0; i < SharedButtons; i++)
            {
                try { _buttonTexts[i] = _getSharedText(i + 1) ?? string.Empty; }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                RefreshButtonFace(i);
            }
        }

        // Our own caption: the title, then the gear, then the X. The gear is the way in to anything
        // this window ever needs asking about - today the history rows, tomorrow whatever else.
        private Border BuildTitleBar()
        {
            var gearBtn = new Button
            {
                Content = "",
                FontSize = 16,
                Style = Application.Current.Resources["CaptionButtonStyle"] as Style,
                Foreground = Brushes.Black,
                ToolTip = "CW keyer settings"
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(gearBtn, true);
            gearBtn.Click += (s, e) => ShowSettings();

            var closeBtn = new Button
            {
                Content = "",
                Style = Application.Current.Resources["CaptionCloseButtonStyle"] as Style,
                Foreground = Brushes.Black,
                ToolTip = "Close"
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            closeBtn.Click += (s, e) => Close();

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            right.Children.Add(gearBtn);
            right.Children.Add(closeBtn);

            _titleText = new TextBlock
            {
                Text = "CW Keyer",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            _titleText.Foreground = Brushes.Black;
            var keyIcon = BuildStraightKeyIcon();
            DockPanel.SetDock(keyIcon, Dock.Left);
            DockPanel.SetDock(_titleText, Dock.Left);
            RefreshTitle();

            var bar = new DockPanel { LastChildFill = false };
            bar.Children.Add(right);
            bar.Children.Add(keyIcon);
            bar.Children.Add(_titleText);

            // The pale cyan of the CW keycaps, so the window is plainly part of the same set.
            var border = new Border { Height = 32, Child = bar, Background = CwKeyBrush };
            return border;
        }

        // WHAT THE GEAR OPENS. One question today. It is a window rather than a prompt box because
        // the next thing this keyboard needs asking about goes in here beside it, and a settings
        // window that already exists is easier to add a line to than one that has to be invented.
        private void ShowSettings()
        {
            var rowsBox = new TextBox
            {
                Text = HistoryRows().ToString(),
                FontSize = 16,
                Width = 70,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var label = new TextBlock
            {
                Text = "Rows of sent text to show:",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            
            var row = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(label, Dock.Left);
            DockPanel.SetDock(rowsBox, Dock.Left);
            row.Children.Add(label);
            row.Children.Add(rowsBox);

            var secondsBox = new TextBox
            {
                Text = BreakSeconds().ToString(),
                FontSize = 16,
                Width = 70,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var secondsLabel = new TextBlock
            {
                Text = "Clear send line after sending ended",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            secondsLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var secondsHint = new TextBlock
            {
                Text = "0 never clears it - everything sent stays on the one line.",
                FontSize = 16,
                Margin = new Thickness(0, 10, 0, 0)
            };
            secondsHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            var secondsRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 18, 0, 0) };
            DockPanel.SetDock(secondsLabel, Dock.Left);
            DockPanel.SetDock(secondsBox, Dock.Left);
            // The unit sits after the number, where it is read: "... ended [3] Seconds".
            var secondsUnit = new TextBlock
            {
                Text = "Seconds",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            secondsUnit.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            DockPanel.SetDock(secondsUnit, Dock.Left);

            secondsRow.Children.Add(secondsLabel);
            secondsRow.Children.Add(secondsBox);
            secondsRow.Children.Add(secondsUnit);

            var okBtn = new Button { Content = "OK", FontSize = 16, Width = 90, Height = 32, IsDefault = true };
            var cancelBtn = new Button { Content = "Cancel", FontSize = 16, Width = 90, Height = 32, IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);

            var stack = new StackPanel { Margin = new Thickness(16) };
            stack.Children.Add(row);
            stack.Children.Add(secondsRow);
            stack.Children.Add(secondsHint);
            stack.Children.Add(BuildHelp());
            stack.Children.Add(BuildRadioList());
            stack.Children.Add(buttons);

            var dialog = new Window
            {
                Title = "CW Keyer Settings",

                // A fixed width and a height that follows the content: the help below wraps its
                // second column, and a column that may wrap has to be told how wide it is.
                Width = 560,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,

                // GROWS TO ITS CONTENT, BUT NOT PAST THE SCREEN. The list of radios made this dialog
                // half as tall again, and SizeToContent has nothing to stop it at the bottom of the
                // monitor - what goes over the edge is the OK button. Capped, with the scroller taking
                // over from there, exactly as the message boxes do it.
                Content = new ScrollViewer
                {
                    Content = stack,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 120)
                }
            };
            dialog.SetResourceReference(BackgroundProperty, "WindowBg");

            okBtn.Click += (s, e) =>
            {
                // A number in range or nothing at all. Anything else leaves the setting as it was
                // rather than quietly picking a row count the operator did not ask for.
                if (!int.TryParse((rowsBox.Text ?? string.Empty).Trim(), out int rows) || rows < 1 || rows > 5)
                {
                    HolyMessageBox.ShowWarning("Type a whole number of rows, from 1 to 5.",
                                               "CW Keyer Settings", dialog);
                    return;
                }

                if (!int.TryParse((secondsBox.Text ?? string.Empty).Trim(), out int seconds) || seconds < 0)
                {
                    HolyMessageBox.ShowWarning("Type a whole number of seconds, or 0 to never start a new line.",
                                               "CW Keyer Settings", dialog);
                    return;
                }

                Properties.Settings.Default.CwKeyboardHistoryRows = rows;
                Properties.Settings.Default.CwKeyboardBreakSeconds = seconds;
                try { Properties.Settings.Default.Save(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                ApplyHistoryRows();
                dialog.DialogResult = true;
            };

            dialog.ShowDialog();
            _box.Focus();
        }

        // ── WHICH RADIOS CAN SEND CW FROM HERE ──────────────────────────────────────────────────
        //
        // Until now the only way to find out was to press a key and be refused - and on a Yaesu not
        // even that: the command went out, the radio ignored it, and nothing was said at all.
        //
        // His own radio comes FIRST and in bold, because it is the only line he is really asking
        // about. The rest is there for the day he is choosing a radio, or helping somebody else.
        private UIElement BuildRadioList()
        {
            var box = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

            var heading = new TextBlock
            {
                Text = "Which radios can send CW from here",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            box.Children.Add(heading);

            // Asked of the main window, which is the only place that knows what OmniRig is running.
            string mine = null;
            try
            {
                var main = Owner as MainWindow;
                if (main != null) mine = main.CwKeyingForThisRadio();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (!string.IsNullOrEmpty(mine))
            {
                var yours = new TextBlock
                {
                    Text = "Your radio: " + mine,
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                yours.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                box.Children.Add(yours);
            }

            var makers = new[]
            {
                new[] { "Icom",     "Any Icom OmniRig supports. The text goes out as CI-V command 17, and "
                                  + "HolyLogger reads the radio's address from OmniRig's own rig file." },
                new[] { "Kenwood",  "TS-590S, TS-590SG, TS-480, TS-890S, TS-990S, TS-2000 and other models "
                                  + "with the KY command. An older TS is sent it and quietly ignores it." },
                new[] { "Elecraft", "K3, K3S, K4, KX2 and KX3." },
                new[] { "Yaesu",    "None. Yaesu has no CAT command that sends typed CW - its KY plays back "
                                  + "a message already stored in the radio. Use the radio's own memory keyer." }
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < makers.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var maker = new TextBlock
                {
                    Text = makers[i][0],
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 3, 0, 3)
                };
                maker.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                Grid.SetRow(maker, i);
                Grid.SetColumn(maker, 0);
                grid.Children.Add(maker);

                var what = new TextBlock
                {
                    Text = makers[i][1],
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 3)
                };
                what.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                Grid.SetRow(what, i);
                Grid.SetColumn(what, 2);
                grid.Children.Add(what);
            }

            box.Children.Add(grid);
            return box;
        }

        // WHAT THE KEYS DO, in the one place the operator already opens to change how this window
        // behaves. Every line here is something the window does that nothing on its face announces -
        // a key with no button, or a button whose second meaning is on the right mouse button.
        private UIElement BuildHelp()
        {
            var pairs = new[]
            {
                new[] { "Ctrl+K",       "Closes this window. On the main window it opens it." },
                new[] { "Escape",       "Stops the radio now and drops whatever has not gone out. What has already gone stays in the record." },
                new[] { "Backspace",    "Takes back only what has not gone out yet." },
                new[] { "Mouse click",  "Puts that button's text into the typing row, and it goes out from there." },
                new[] { "Right-click",  "Edits that button's text." },
                new[] { "*",            "In a text: your Station Callsign." },
                new[] { "!",            "In a text: the DX Callsign." },
                new[] { "#",            "In a text: the serial number you are sending. Needs contest mode." },
                new[] { "$",            "In a text: the rest of your sent exchange. Needs contest mode." }
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < pairs.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var key = new TextBlock
                {
                    Text = pairs[i][0],
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 3, 0, 3)
                };
                key.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                Grid.SetRow(key, i);
                Grid.SetColumn(key, 0);
                grid.Children.Add(key);

                var meaning = new TextBlock
                {
                    Text = pairs[i][1],
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 3)
                };
                meaning.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                Grid.SetRow(meaning, i);
                Grid.SetColumn(meaning, 2);
                grid.Children.Add(meaning);
            }

            AddHelpExample(grid, pairs.Length);

            var heading = new TextBlock
            {
                Text = "What the keys do",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");


            var inner = new StackPanel();
            inner.Children.Add(heading);
            inner.Children.Add(grid);

            var separated = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 18, 0, 0),
                Padding = new Thickness(0, 14, 0, 0),
                Child = inner
            };
            separated.SetResourceReference(Border.BorderBrushProperty, "MutedTextBrush");
            return separated;
        }

        // ONE LINE OF IT ACTUALLY DONE, with this station's own callsign in it rather than a made-up
        // one - the macros are far easier to believe when the answer on the right is the operator's
        // own call. A station callsign that is not filled in yet leaves the example unexpanded.
        private void AddHelpExample(Grid grid, int row)
        {
            const string before = "CQ CQ DE ";
            const string after = " K";

            string mine = _expandMacros != null ? (_expandMacros("*") ?? string.Empty).Trim() : string.Empty;
            if (mine.Length == 0) return;   // no Station Callsign yet - an example with a hole in it teaches nothing

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "Example",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 3)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var line = new TextBlock
            {
                FontSize = 16,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 3)
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            // The * on the left and the callsign it turned into on the right are both in the same
            // blue, so the eye pairs them without a word of explanation.
            line.Inlines.Add(new Run(before));
            line.Inlines.Add(new Run("*") { Foreground = SentBrush });
            line.Inlines.Add(new Run(after + "  =  " + before));
            line.Inlines.Add(new Run(mine) { Foreground = SentBrush });
            line.Inlines.Add(new Run(after));
            Grid.SetRow(line, row);
            Grid.SetColumn(line, 2);
            grid.Children.Add(line);
        }

        protected override void OnClosed(EventArgs e)
        {
            _pump.Stop();
            base.OnClosed(e);
        }
    }
}
