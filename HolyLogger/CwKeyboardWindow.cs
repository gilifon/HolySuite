using System;
using System.Collections.Generic;
using System.Globalization;
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

        // ONE CHUNK AT A TIME, FOR A RADIO THAT HAS NO BUFFER.
        //
        // An Icom or a Kenwood takes the next characters into a buffer while it is still keying the
        // last ones, so the keyer hands them over EARLY and the keying never runs dry. A Yaesu has no
        // buffer to hand them to: the text is written into a memory and that memory is played, and
        // writing the next lot while the last is still playing writes over what is being sent.
        //
        // So for those radios the keyer holds back until the radio has stopped transmitting. It costs
        // a pause between chunks - which is why a Yaesu is given the whole 50 characters its memory
        // holds, rather than the 12 an Icom's buffer likes, so the pause comes four times less often.
        private readonly bool _waitForTxIdle;

        // What speeds this radio will accept. Null when the program knows nothing about it - then
        // the readout is not built at all.
        private readonly SpeedRange _speedRange;

        // Asks the radio what speed it is keying at; the answer comes back later, through ShowSpeed.
        // SET AFTER THE WINDOW IS BUILT rather than passed in with the rest: the constructor already
        // takes fourteen of these, and one more in the row would be one more thing to miscount.
        internal Action AskSpeed { set { _askSpeed = value; } }
        private Action _askSpeed;

        public delegate void SpeedRange(out int low, out int high);

        // Turns * and ! into callsigns. The stored text keeps the macro; only what goes on air is
        // expanded, so a button reads the same next year when the callsign in the form is different.
        private readonly Func<string, string> _expandMacros;

        // Logs the QSO, for a button whose text carries {LOG}. The keyer does not know what logging
        // is - it hands the job back to the main window, which is where Add lives.
        private readonly Action _logQso;
        private readonly Action _wipeForm;

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

        // TWELVE, THE WAY A CONTESTER KNOWS THEM. They were eight, and the first four were the
        // main window's Msg buttons shown a second time. That link is gone: the four on the main
        // window are for ordinary working now, and everything a contest needs lives in here - twelve
        // texts of this window's own, on F1 to F12, which is the set N1MM has taught everybody.
        private const int ButtonCount = 12;

        // The four ESM steps, and the order it expects them in: 1 the CQ, 2 the exchange, 3 the TU,
        // 4 his own call for Search and Pounce. The other eight are the operator's to fill.
        private const int EsmButtons = 4;
        private readonly Button[] _buttons = new Button[ButtonCount];

        // The window's stack, kept so the "this radio cannot be keyed" line can be put into it after
        // the window is built - see CannotKey.
        private DockPanel _body;

        // TRUE WHEN THE RADIO IN FRONT OF HIM CANNOT BE KEYED AT ALL. The window still opens: its
        // twelve buttons are where his macros live, and writing them is worth doing with no radio on
        // the desk, let alone with the wrong one. What it will not do is pretend to send.
        private bool _cannotKey;
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

        // Working out whether every button can be sent means reading the entry form twelve times over.
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
                                Func<int, string> getSharedText,
                                bool waitForTxIdle = false,
                                SpeedRange speedRange = null,
                                Action logQso = null, Action wipeForm = null)
        {
            _waitForTxIdle = waitForTxIdle;
            _speedRange = speedRange;
            _sendChunk = sendChunk;
            _stopSending = stopSending;
            _currentWpm = currentWpm;
            _wpmMeasured = wpmMeasured;
            _maxChunk = maxChunk < 4 ? 4 : maxChunk;
            _isTransmitting = isTransmitting;
            _expandMacros = expandMacros;
            _macroProblem = macroProblem;
            _logQso = logQso;
            _wipeForm = wipeForm;
            _learnWpm = learnWpm;
            _editText = editText;
            _getSharedText = getSharedText;
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

            // F1 TO F12 PRESS THE TWELVE BUTTONS, wherever the caret happens to be in this window.
            // On the window rather than on the typing row so a button, the gear or the send line can
            // hold the focus without the keys going quiet. Auto-repeat is ignored: a held F1 must not
            // queue the CQ twenty times over.
            PreviewKeyDown += (s2, e2) =>
            {
                if (e2.IsRepeat) return;
                if (e2.Key < Key.F1 || e2.Key > Key.F12) return;

                PressButton(e2.Key - Key.F1);
                e2.Handled = true;
            };
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

                // AND IT WRAPS. A transmission can now be as long as the operator keeps feeding it, and
                // a record that does not wrap simply hides everything past the right-hand edge. Wrapped,
                // a long one uses two or three of the rows set at the gear and every character of it can
                // be read. WPF does the measuring, so it fits the width whatever the window is dragged to.
                TextWrapping = TextWrapping.Wrap,
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

            _body = new DockPanel { LastChildFill = false };

            var titleBar = BuildTitleBar();
            DockPanel.SetDock(titleBar, Dock.Top);
            DockPanel.SetDock(sendFrame, Dock.Top);
            DockPanel.SetDock(_history, Dock.Top);

            var buttonGrid = BuildButtons(typeface);
            DockPanel.SetDock(buttonGrid, Dock.Top);

            _body.Children.Add(titleBar);
            _body.Children.Add(sendFrame);
            _body.Children.Add(_history);
            _body.Children.Add(buttonGrid);

            // WindowStyle.None takes the OS frame with it, so this border IS the window's visible edge.
            var frame = new Border { BorderThickness = new Thickness(1), Child = _body };
            frame.SetResourceReference(Border.BorderBrushProperty, "TextBrush");

            Content = frame;
            Loaded += (s, e) => { _box.Focus(); LockMinimumHeight(); };

            _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _pump.Tick += Pump_Tick;
            _pump.Start();

            // Where the operator put it is where it comes back. WindowBounds is the one that knows how
            // to bring a corner saved on a second screen back onto a screen that is still there.
            WindowBounds.Attach(this, "CwKeyboard");

            // CLOSING IT STOPS THE RADIO, exactly as Escape does. The window going away used to leave
            // the radio keying whatever it had already been handed, with nothing left on the screen
            // saying so and no Escape to press. This catches every way out - the X, Ctrl+K, and the
            // program closing it itself when the radio leaves CW.
            Closing += (s2, e2) => StopEverything();
        }

        // ── A KEYER IN FRONT OF A RADIO IT CANNOT KEY ───────────────────────────────────────────
        //
        // The window used to refuse to open at all on such a radio, which took his twelve macros away
        // with it: the only place they can be written is here, and there is no reason a man should
        // need a keyable radio switched on to write "CQ CQ DE ...". So it opens, and says plainly
        // why nothing will go out - a window that merely looked dead would read as a fault.
        //
        // WHAT IS TAKEN AWAY IS SENDING, AND ONLY SENDING. The typing line goes read-only and the
        // buttons are dimmed and refuse a left-click; right-click still opens the editor, which is
        // the whole point of letting the window open.
        private string _cannotKeyReason = string.Empty;

        internal void CannotKey(string reason)
        {
            _cannotKey = true;
            _cannotKeyReason = reason ?? string.Empty;

            // READ-ONLY, NOT DISABLED. A disabled box is skipped by the caret and by selection, and
            // the operator cannot even mark what he had already typed to copy it somewhere useful.
            _box.IsReadOnly = true;
            _box.IsTabStop = false;

            foreach (var button in _buttons)
                if (button != null) button.Opacity = 0.5;

            var line = new TextBlock
            {
                Text = _cannotKeyReason,
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 8, 10, 0)
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            var banner = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 6),
                Margin = new Thickness(10, 8, 10, 0),
                Child = line
            };
            banner.SetResourceReference(Border.BorderBrushProperty, "MutedTextBrush");

            DockPanel.SetDock(banner, Dock.Top);

            // Under the title bar and above the typing line, where it is read before anything is
            // typed rather than after.
            if (_body != null && _body.Children.Count > 0) _body.Children.Insert(1, banner);

            // NOTHING IS DONE ABOUT THE HEIGHT HERE ON PURPOSE. Adding a row usually means raising
            // Height and MinHeight by hand or the bottom is cut off - but this window sizes itself
            // to its content on Loaded and locks its minimum to what it measured (LockMinimumHeight),
            // and this line goes in before Show. Adding to Height as well would grow it twice.
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
            AskRadioItsSpeed();
            AdvanceKeyingClock();

            // Whatever the radio has finished keying leaves the typing row first, so the row always
            // shows what is still to come rather than what has just gone.
            RefreshTitle();
            RefreshButtonAvailability();
            RepaintSendLine();
            DropWhatTheRadioHasKeyed();
            TrimRowToWidth();

            string text = _box.Text ?? string.Empty;

            // Everything in the row is already in the radio's hands; there is nothing new to hand it.
            if (text.Length <= _handedUpTo)
            {
                if (text.Length == 0) FinishLineIfSilent();
                return;
            }

            // The radio still has enough to be going on with.
            if (DateTime.UtcNow + Lead < _radioBusyUntil) return;

            // A radio with no buffer gets nothing until it has stopped sending the last lot.
            if (_waitForTxIdle && _isTransmitting != null && _isTransmitting()) return;

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
            // The radio's own speed where it gives one, and only otherwise the timed estimate this
            // paragraph was written for - see KeyingWpm.
            double gapWpm = KeyingWpm();

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

            // How long this chunk will take the radio, at the speed the radio itself reports - and
            // only at the timed estimate where it reports none.
            double wpm = KeyingWpm();

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

            // NOT WHILE THERE IS MORE TO COME. Text sitting in the row that the radio has not been
            // given yet means the operator has added to this transmission - a macro pressed or a word
            // typed - and the quiet seconds that empty the row start again from the end of THAT.
            // Without this the check below could beat the next hand-over by one tick and empty the row
            // with the new text still standing on it.
            if ((_box.Text ?? string.Empty).Length > _handedUpTo) return;

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

                // The units of what just left go with it, or the sum would be measuring a row that is
                // no longer there and everything still on screen would come up already keyed.
                _unitsKeyed = Math.Max(0, _unitsKeyed - CwSendMonitorWindow.ComputeTotalUnits(text.Substring(0, drop)));

                if (sent.Value) _openLine += " ";
                _openLine += chunk;
                moved = true;
            }

            if (moved) RenderHistory();
        }

        // -- THE ROW NEVER RUNS OFF THE SIDE ------------------------------------------------------
        //
        // A row can now hold as many presses as the operator makes, and a single line that grows past
        // its box scrolls: what is being keyed slides left out of sight and he is left reading the
        // middle of his own transmission. So when it no longer fits, the row hands its OLDEST piece
        // down to the record and goes on doing that until it fits again. The text moves DOWNWARDS
        // instead of sideways, and what stays in front of him is what is going out now and what is
        // still to come.
        //
        // ONLY PIECES THE RADIO HAS FINISHED WITH. Nothing that has not been keyed ever leaves the row
        // - that is the whole rule this window works by - so a burst of presses that fills the row with
        // text still waiting to go out will scroll until the front of it has been sent. There is no way
        // round that: the alternative is throwing away something the operator asked to send.
        private void TrimRowToWidth()
        {
            if (_box == null || _box.ViewportWidth <= 0) return;

            bool moved = false;

            while (_box.ExtentWidth > _box.ViewportWidth && _inFlight.Count > 0)
            {
                string text = _box.Text ?? string.Empty;
                var sent = _inFlight.Peek();

                int drop = Math.Min(sent.Key.Length + (sent.Value ? 1 : 0), text.Length);
                if (drop <= 0) break;
                if (KeyedSoFar(text) < drop) break;   // still being sent: it stays where he can see it

                _inFlight.Dequeue();

                int caret = _box.CaretIndex;
                _box.Text = text.Substring(drop);
                _box.CaretIndex = Math.Max(0, caret - drop);
                _handedUpTo = Math.Max(0, _handedUpTo - drop);
                _unitsKeyed = Math.Max(0, _unitsKeyed - CwSendMonitorWindow.ComputeTotalUnits(text.Substring(0, drop)));

                if (sent.Value) _openLine += " ";
                _openLine += sent.Key;
                moved = true;

                // The box has to be re-measured before the loop asks again whether it still overflows.
                _box.UpdateLayout();
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

                // The units of what just left go with it, or the sum would be measuring a row that is
                // no longer there and everything still on screen would come up already keyed.
                _unitsKeyed = Math.Max(0, _unitsKeyed - CwSendMonitorWindow.ComputeTotalUnits(text.Substring(0, drop)));

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

        // -- HOW MUCH OF THE ROW HAS BEEN KEYED --------------------------------------------------
        //
        // WRITTEN FRESH on a fact this program did not have until today: the radio's OWN keying speed,
        // asked of it rather than timed and guessed at (see the WPM readout on the bar). With the speed
        // known exactly, how far the keying has got is a running sum - so many units of Morse per
        // second - and the only thing that has to be right is WHEN to count.
        //
        // THE SUM ONLY RUNS WHILE THE RADIO IS ON AIR. That is the whole idea, and it is the one thing
        // every earlier version got wrong. They measured from the moment a transmission began and
        // divided the elapsed time by the speed, which counted every silence as keying - the gap between
        // one chunk and the next, the gap between one button and the next - so the colour ran ahead of
        // the radio and had to be patched wherever it showed. Silence adds nothing here, so there is
        // nothing to patch.
        //
        // AND NEVER PAST WHAT THE RADIO HAS BEEN GIVEN. The sum is capped at the units of the text
        // actually handed over, so a radio slower than it claims cannot colour in what it has not yet
        // been sent.
        private double _unitsKeyed;
        private DateTime _keyClockUtc = DateTime.MinValue;
        private bool _onAir;
        private DateTime _onAirAskedUtc = DateTime.MinValue;

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

            if (!_onAir) return;

            double seconds = (now - since).TotalSeconds;
            if (seconds <= 0) return;

            _unitsKeyed += seconds * KeyingWpm() / 1.2;

            double handed = CwSendMonitorWindow.ComputeTotalUnits(HandedText());
            if (_unitsKeyed > handed) _unitsKeyed = handed;
        }

        // The radio's own number where it will give one - the readout on this bar IS its answer - and
        // the timed estimate only for a radio that will not say.
        private double KeyingWpm()
        {
            if (_wpm > 0) return _wpm;

            double wpm = 20.0;
            try { if (_currentWpm != null) wpm = _currentWpm(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return wpm < 5 ? 5 : wpm;
        }

        // What of the row the radio has actually been handed. Everything after this is still ours.
        private string HandedText()
        {
            string text = _box.Text ?? string.Empty;
            int limit = Math.Min(_handedUpTo, text.Length);

            return limit <= 0 ? string.Empty : text.Substring(0, limit);
        }

        // The units already keyed, read back as a number of characters. The same running total the
        // sending monitor walks, so the two agree character for character - and it counts the gaps
        // BETWEEN letters rather than charging one to each.
        private int KeyedSoFar(string text)
        {
            if (text.Length == 0 || _handedUpTo <= 0) return 0;

            int limit = Math.Min(_handedUpTo, text.Length);
            var cumulative = CwSendMonitorWindow.CumulativeUnits(text.Substring(0, limit));

            // AS EACH CHARACTER STARTS, not when it has finished. Comparing against the END of a
            // character meant the operator heard it go out and only then saw it turn blue - a whole
            // character behind the radio, every character. What is measured now is the point the
            // keying has REACHED, so the colour arrives with the sound instead of after it.
            for (int i = 0; i < limit; i++)
            {
                double startsAt = i == 0 ? 0 : cumulative[i - 1];
                if (startsAt > _unitsKeyed) return i;
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

            // ONE SPEED ON THIS BAR, NOT TWO. The measured figure was shown on the left while the one
            // the operator sets sat on the right, and they disagreed - he set 30 and the left still
            // read 23, which reads as the program contradicting itself. Where the speed can be
            // COMMANDED the commanded number is the truth and the measurement has nothing to add, so
            // the title stays plain and the number lives in the readout he turns.
            if (_wpmText != null)
            {
                if (_shownWpm == -1) return;
                _shownWpm = -1;
                _titleText.Inlines.Clear();
                _titleText.Inlines.Add(new Run("CW Keyer") { Foreground = Brushes.Black });
                return;
            }

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
            _unitsKeyed = 0;
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
                // TWO ROWS OF SIX. The window keeps the height it always had; what it costs is face
                // width - six across the same 560 is about 85 pixels a button instead of 125, so a
                // longer text runs into its ellipsis sooner. The tooltip still shows the whole thing.
                Rows = 2,
                Columns = 6,
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
        // THE ONE ESM WILL SEND NEXT, in the same green the Msg row uses on the main window - these
        // are the same four texts shown twice, so they light up together. 0 clears them all, which is
        // what arrives when ESM is off or has nothing to send.
        //
        // Only the first four: buttons 5-12 are the operator's own, and ESM knows nothing about them.
        internal void ShowEsmNext(int messageNumber)
        {
            for (int i = 0; i < EsmButtons && i < _buttons.Length; i++)
            {
                var button = _buttons[i];
                if (button == null) continue;

                if (i + 1 == messageNumber) button.Background = EsmNextBrush;
                else button.ClearValue(BackgroundProperty);
            }
        }

        private static readonly Brush EsmNextBrush = MakeEsmNextBrush();

        private static Brush MakeEsmNextBrush()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x9E, 0xE4, 0x93));
            brush.Freeze();
            return brush;
        }

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
                    lines.Append("\nSends: ").Append(sends.Trim());
                }

                // {LOG} keys nothing, so the line above cannot show it. Said in words instead,
                // or the tooltip would quietly drop the one part of the button that changes
                // the log.
                if (MainWindow.CwMacroLogsQso(text)) lines.Append("\nAnd logs the QSO");
                else if (MainWindow.CwMacroWipesForm(text)) lines.Append("\nAnd clears the form");
            }

            lines.Append("\nRight-click to edit");
            return lines.ToString();
        }

        // A left-click puts the text in the typing row rather than firing it at the radio itself. One
        // sending path, one record, and Escape still stops it half way like anything else typed.
        private void SendButton(int index)
        {
            // NOT A SILENT REFUSAL. The line across the top already says the radio cannot be keyed,
            // but a button that is still there to be right-clicked will be left-clicked sooner or
            // later, and a press that does nothing at all is the fault this window was opened to
            // stop being.
            if (_cannotKey)
            {
                HolyMessageBox.ShowWarning(_cannotKeyReason, "CW Keyer", this);
                return;
            }

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

            bool logsQso = _logQso != null && MainWindow.CwMacroLogsQso(stored);
            bool wipesForm = _wipeForm != null && MainWindow.CwMacroWipesForm(stored);

            string text = _expandMacros != null ? _expandMacros(stored) : stored;
            text = (text ?? string.Empty).ToUpperInvariant();

            // Whatever a macro brought in is held to the same rule as typing: only what a keyer can
            // actually send gets through.
            text = new string(text.Where(IsSendable).ToArray()).Trim();

            if (text.Length == 0)
            {
                // A button holding nothing but {LOG} sends nothing and logs, which is a button
                // working, not a button empty.
                if (logsQso) { _logQso(); return; }
                if (wipesForm) { _wipeForm(); return; }

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
            // NOTHING LEAVES THIS ROW BEFORE IT HAS BEEN SENT. Flushing on every press was the fault:
            // press a second button while the first was still going and the first went straight down
            // into the record, filed as finished while the operator was still listening to it.
            //
            // Still busy means any of these: the radio is on air, it holds text it has not worked
            // through yet, or the row still has text waiting to be handed over. In any of them this
            // press is the tail of what is going out and joins the same row - which is also what puts
            // the word gap in, above. When the radio has really finished, the press starts a row of
            // its own as before.
            //
            // THIS COST THE COLOURING ONCE, and it is worth saying why it does not now. The old
            // painter measured one burst from the moment the radio went on air, and the hand-off
            // resets that watch whenever the radio drops between bursts - so a row left on screen
            // across two bursts lost its colour entirely. The painter has since been rewritten to
            // count units only while the radio is keying (see AdvanceKeyingClock); it never asks when
            // the burst began, so a row may now span as many presses as the operator likes.
            bool stillSending = _inFlight.Count > 0
                             || (_box.Text ?? string.Empty).Length > 0
                             || DateTime.UtcNow < _radioBusyUntil
                             || _onAir;

            if (!stillSending)
            {
                FlushHandedText();
                FinishLineNow();
            }

            // A WORD GAP BETWEEN ONE PRESS AND THE NEXT. The macro's own text is trimmed, so pressing
            // the button twice while the radio is still keying the first one ran the two together, and
            // "TU 73" followed by "CQ" went out as "TU 73CQ". The space goes IN FRONT of the new text
            // rather than at the end of every macro: with nothing waiting there is nothing to separate,
            // so a press on an idle keyer starts as immediately as it always did, and no transmission
            // ends by keying a gap nobody is waiting for.
            string pending = _box.Text ?? string.Empty;
            if (pending.Length > 0 && !pending.EndsWith(" ", StringComparison.Ordinal)) pending += " ";

            _box.Text = pending + text;
            _box.CaretIndex = _box.Text.Length;
            _box.Focus();

            // AFTER the text is on its way, and without waiting for the radio to finish keying it -
            // the whole point of putting {LOG} on the TU button is that the next callsign can be
            // typed while the TU is still going out.
            // Log wins over wipe - see RunCwMacroActions on the main window for why they are not
            // both run.
            if (logsQso) _logQso();
            else if (wipesForm) _wipeForm();
        }

        private void EditButton(int index)
        {
            if (_editText == null) return;

            // Named by the key that presses it, because that is how the operator thinks of it.
            string title = "Edit CW Keyer Text " + (index + 1) + " (F" + (index + 1) + ")";

            string updated = _editText(title, _buttonTexts[index] ?? string.Empty);
            if (updated == null) return;

            _buttonTexts[index] = updated;
            SaveButtonText(index, updated);
            RefreshButtonFace(index);
        }

        // All twelve in ONE setting, the way WindowBoundsJson holds every window's placement: a
        // thirteenth button later costs nothing, and a profile that snapshots the settings takes them
        // all along. A file written by an older build holds only eight, and the four it does not hold
        // are filled in below.
        private string[] LoadButtonTexts()
        {
            var texts = new string[ButtonCount];
            for (int i = 0; i < ButtonCount; i++) texts[i] = string.Empty;

            try
            {
                string json = Properties.Settings.Default.CwKeyerButtonsJson;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var saved = JsonConvert.DeserializeObject<string[]>(json);
                    if (saved != null)
                    {
                        for (int i = 0; i < ButtonCount && i < saved.Length; i++)
                        {
                            texts[i] = saved[i] ?? string.Empty;
                        }
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // ONCE, AND ONLY INTO EMPTY SLOTS. Until now the first four of these buttons WERE the main
            // window's four Msg texts, and the keyer's own setting kept those four slots blank. Now that
            // the two sets are apart, a keyer that opened blank would look as though it had thrown his
            // CQ and his exchange away - so the first time it finds them empty it takes a copy of what
            // the Msg buttons hold. Copy, not move: the four on the main window are untouched, and from
            // this moment the two sets go their own ways.
            if (_getSharedText != null && ButtonsAreEmpty(texts, EsmButtons))
            {
                bool copied = false;
                for (int i = 0; i < EsmButtons; i++)
                {
                    try
                    {
                        texts[i] = _getSharedText(i + 1) ?? string.Empty;
                        if (texts[i].Length > 0) copied = true;
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }

                if (copied) SaveAllButtonTexts(texts);
            }

            return texts;
        }

        private static bool ButtonsAreEmpty(string[] texts, int howMany)
        {
            for (int i = 0; i < howMany && i < texts.Length; i++)
                if (!string.IsNullOrWhiteSpace(texts[i])) return false;

            return true;
        }

        // All twelve are this window's own now - nothing here is written back to the Msg buttons.
        private void SaveButtonText(int index, string text)
        {
            SaveAllButtonTexts(_buttonTexts);
        }

        private static void SaveAllButtonTexts(string[] texts)
        {
            var own = new string[ButtonCount];
            for (int i = 0; i < ButtonCount; i++)
            {
                own[i] = i < texts.Length ? (texts[i] ?? string.Empty) : string.Empty;
            }

            try
            {
                Properties.Settings.Default.CwKeyerButtonsJson = JsonConvert.SerializeObject(own);
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // PRESSED BY ITS F-KEY. F1 to F12 while this window is the one in front, and forwarded here
        // from the main window while the keyer is open - see HandleGlobalFunctionKey. Nothing about
        // the send is different from a mouse click on the same button.
        internal void PressButton(int index)
        {
            if (index < 0 || index >= ButtonCount) return;
            SendButton(index);
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
            right.Children.Add(BuildSpeedReadout());
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

            
            // Second on the page now, under the clear-line setting, so it carries the gap between them.
            var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 18, 0, 0) };
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

            var secondsRow = new DockPanel { LastChildFill = false };
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

            // ── ESM ────────────────────────────────────────────────────────────────────────
            //
            // It lived in the status bar of the main window and has been moved in here: the keyer is
            // where a man is when he cares about it, and the status bar was carrying it in front of
            // everybody else for ever. Written straight into the settings on OK, like the two above;
            // the main window follows them through Settings.PropertyChanged, so nothing has to be
            // told about this window at all.
            var esmOff = new RadioButton
            {
                Content = "Off",
                FontSize = 16,
                GroupName = "EsmMode",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0),
                IsChecked = !Properties.Settings.Default.EsmEnabled,
                ToolTip = "Enter does what it always did."
            };
            var esmRun = new RadioButton
            {
                Content = "Run",
                FontSize = 16,
                GroupName = "EsmMode",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0),
                IsChecked = Properties.Settings.Default.EsmEnabled && !Properties.Settings.Default.EsmSearchAndPounce,
                ToolTip = "You are calling CQ. Enter sends Txt 1 (CQ); with a callsign in the form, Txt 2 (his call and your exchange); then Txt 3 (TU), which logs the QSO."
            };
            var esmSp = new RadioButton
            {
                Content = "S&P",
                FontSize = 16,
                GroupName = "EsmMode",
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = Properties.Settings.Default.EsmEnabled && Properties.Settings.Default.EsmSearchAndPounce,
                ToolTip = "You are answering others. With his callsign typed in, Enter sends Txt 4 (your callsign), then Txt 2 (your exchange), then Txt 3 (TU), which logs the QSO."
            };
            esmOff.SetResourceReference(ForegroundProperty, "TextBrush");
            esmRun.SetResourceReference(ForegroundProperty, "TextBrush");
            esmSp.SetResourceReference(ForegroundProperty, "TextBrush");

            var esmLabel = new TextBlock
            {
                Text = "Enter Sends Message (Ctrl+M):",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            esmLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var esmRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 18, 0, 0) };
            DockPanel.SetDock(esmLabel, Dock.Left);
            DockPanel.SetDock(esmOff, Dock.Left);
            DockPanel.SetDock(esmRun, Dock.Left);
            DockPanel.SetDock(esmSp, Dock.Left);
            esmRow.Children.Add(esmLabel);
            esmRow.Children.Add(esmOff);
            esmRow.Children.Add(esmRun);
            esmRow.Children.Add(esmSp);

            var esmHint = new TextBlock
            {
                Text = "One key walks the whole QSO: Run is you calling CQ, S&P is you answering others. "
                     + "Which text goes out is decided by how far the contact has got, and the button Enter "
                     + "will press is lit green.",
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            };
            esmHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

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
            stack.Children.Add(secondsRow);
            stack.Children.Add(secondsHint);
            stack.Children.Add(esmRow);
            stack.Children.Add(esmHint);
            stack.Children.Add(row);
            stack.Children.Add(BuildHelp());
            stack.Children.Add(BuildStandardTextsButton());
            stack.Children.Add(BuildRadioLink());
            stack.Children.Add(buttons);

            var dialog = new Window
            {
                Title = "CW Keyer Settings",

                // A fixed width and a height that follows the content: the help below wraps its
                // second column, and a column that may wrap has to be told how wide it is. WIDER
                // SINCE THE HELP WENT INTO TWO COLUMNS - the keys on the left, everything that may go
                // in a text on the right - which is what the width has to carry now.
                Width = 1000,
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

                // The main window is watching these two, and puts its own green hint right the moment
                // they change - so switching ESM here needs nothing said to anybody.
                bool esmSearchAndPounce = esmSp.IsChecked == true;
                Properties.Settings.Default.EsmEnabled = esmRun.IsChecked == true || esmSearchAndPounce;
                Properties.Settings.Default.EsmSearchAndPounce = esmSearchAndPounce;

                Properties.Settings.Default.CwKeyboardHistoryRows = rows;
                Properties.Settings.Default.CwKeyboardBreakSeconds = seconds;
                try { Properties.Settings.Default.Save(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                ApplyHistoryRows();
                dialog.DialogResult = true;
            };

            // WHERE HE LEFT IT, like every other window in the program. It opened centred on the
            // keyer every time, which on a two-monitor desk means dragging it off the keyer again
            // on every visit. CenterOwner still decides the FIRST one, before there is anything
            // remembered.
            WindowBounds.Attach(dialog, "CwKeyerSettings");

            dialog.ShowDialog();
            _box.Focus();
        }

        // ── THE WAY TO THE LIST, NOT THE LIST ───────────────────────────────────
        //
        // Which radios can be keyed is a REFERENCE: nothing on it is chosen or changed, and a page of
        // it in the middle of a settings dialog is a page standing between a man and the two boxes he
        // came to alter. It lives under Help now, and this is the door to it - here, because this is
        // where he is standing when the question occurs to him.
        // THE STANDARD SET, in one press. The eight texts most CW contesters use, written with the
        // macros so they fill themselves in - and in the order ESM expects: 1 is the CQ, 2 the
        // exchange, 3 the TU, 4 your own callsign. A new installation starts with the first four
        // already set to these; this is here for everybody else, whose buttons were filled in long
        // before the macros existed.
        private static readonly string[] StandardTexts =
        {
            "CQ TEST {MYCALL} {MYCALL} TEST",
            "{CALL} {SENTRST} {EXCH}",
            "TU {MYCALL} TEST {LOG}",
            "{MYCALL}",
            "{CALL}",
            "AGN?",
            "NR?",
            "QSO B4"
        };

        private UIElement BuildStandardTextsButton()
        {
            var button = new Button
            {
                Content = "Use the standard contest texts",
                FontSize = 16,
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 18, 0, 0),
                ToolTip = "Writes the eight texts most CW contesters use into all eight buttons."
            };

            button.Click += (s, e) =>
            {
                // IT OVERWRITES ALL EIGHT, so it asks first, and it says what it is about to write.
                // Anything the operator wrote himself is gone after this, and there is no undo for it.
                var said = new StringBuilder();
                said.Append("This writes the standard contest texts into ALL EIGHT buttons.\n\n");
                said.Append("Whatever they hold now is replaced, and it cannot be undone.\n\n");
                for (int i = 0; i < StandardTexts.Length; i++)
                {
                    said.Append(i + 1).Append(".  ").Append(StandardTexts[i]).Append('\n');
                }
                said.Append("\nTxt 1 is the CQ, Txt 2 the exchange, Txt 3 the TU, Txt 4 your own callsign - ");
                said.Append("which is the order ESM sends them in.");

                if (!HolyMessageBox.ShowConfirm(said.ToString(), "CW Keyer", HolyMsgType.Warning, this, 620,
                                                "Write them", "Leave mine")) return;

                for (int i = 0; i < ButtonCount && i < StandardTexts.Length; i++)
                {
                    _buttonTexts[i] = StandardTexts[i];
                    SaveButtonText(i, StandardTexts[i]);
                    RefreshButtonFace(i);
                }
            };

            return button;
        }

        private UIElement BuildRadioLink()
        {
            var line = new TextBlock { Margin = new Thickness(0, 14, 0, 0), FontSize = 16 };

            var hyper = new System.Windows.Documents.Hyperlink(
                            new System.Windows.Documents.Run("Which radios can send CW"));
            hyper.Click += (s, e) => CwRadiosWindow.Show(this);
            line.Inlines.Add(hyper);
            line.Inlines.Add(new System.Windows.Documents.Run("   — also under Help."));
            line.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            return line;
        }

        // -- WPM ##, READ FROM THE RADIO -------------------------------------------------------
        //
        // DISPLAY ONLY, AND ON PURPOSE. The speed was settable from here with a wheel over the
        // number, and it worked - but the radio's own knob overruled it the moment it was touched,
        // and the knob resumes from ITS last number rather than from ours. Two controls for one
        // setting, and no way for the operator to tell which of them had spoken last.
        //
        // So the knob on the radio is the only control now, and this is the readout that says where
        // the knob has left it. Nothing here is ever sent to the radio.
        //
        // IN THE TITLE BAR, because it is a number glanced at in the middle of a QSO.

        private TextBlock _wpmText;
        private int _wpm;

        private UIElement BuildSpeedReadout()
        {
            var holder = new Border
            {
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(6, 0, 6, 0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Nothing to show on a radio this program cannot ask.
            if (_speedRange == null) return holder;

            int low, high;
            Limits(out low, out high);
            if (high == 0) return holder;

            // TWO DASHES UNTIL THE RADIO HAS ANSWERED, which is a moment - the question goes out as
            // the window opens. It used to start at a remembered number, and a remembered number is
            // exactly the thing this readout exists to stop showing: it read 22 while the radio keyed
            // at 10 and looked no different from the truth.
            _wpm = 0;

            var wpmLabel = new TextBlock
            {
                Text = "WPM",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center
            };

            _wpmText = new TextBlock
            {
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            RefreshSpeedText();

            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(wpmLabel);
            line.Children.Add(_wpmText);

            holder.ToolTip = "The radio's keyer speed, as the radio itself reports it."
                           + Environment.NewLine + Environment.NewLine
                           + "Change it with the speed knob on the radio.";

            holder.Child = line;
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(holder, true);

            // ASKED THE MOMENT THE WINDOW IS UP, not on the first tick a second later: the operator
            // opens this window and looks straight at the number.
            Dispatcher.BeginInvoke(new Action(AskRadioItsSpeed), DispatcherPriority.Loaded);

            return holder;
        }

        // What the radio itself will accept - an Elecraft starts at 8, an Icom stops at 48. A number
        // outside it is a number that was misread, and is dropped rather than shown.
        private void Limits(out int low, out int high)
        {
            _speedRange(out low, out high);
        }

        private void RefreshSpeedText()
        {
            if (_wpmText != null)
                _wpmText.Text = _wpm > 0 ? _wpm.ToString(CultureInfo.InvariantCulture) : "--";
        }

        // -- THE NUMBER FOLLOWS THE RADIO ------------------------------------------------------
        //
        // The radio is ASKED, over and over, and what it answers is what stands in the readout. Turn
        // the speed knob on the radio and the number here follows within a second.
        //
        // AND THE PROGRAM NEVER SETS IT, which is why asking is enough. Setting it from here did work
        // - the radio keyed at the speed it was sent - but the number BEHIND THE KNOB stayed where it
        // was, so the first nudge of the knob threw the sent speed away and went back to the radio's
        // own. One control that the other silently undoes is worse than one control; so the knob is
        // the control, and this window reports it.
        //
        // ONCE A SECOND, AND NOT WHILE ANYTHING IS BEING SENT. A question on the wire in the middle
        // of a message is a question competing with the text for the same wire, and the text is the
        // thing that must not stutter.
        //
        // AND IT STOPS ASKING A RADIO THAT WILL NOT ANSWER. Not every radio the program keys can
        // be asked its speed - a maker that publishes no command for it has none, and there is no
        // arguing with that. Those radios are keyed exactly as before and the readout simply stays
        // at --, which is honest. What must not happen is a question every second for the whole
        // evening on a wire the CW itself is using, so after ten unanswered tries it gives up.
        private static readonly TimeSpan AskSpeedEvery = TimeSpan.FromSeconds(1);
        private const int AskSpeedGiveUpAfter = 10;
        private DateTime _speedAskedUtc = DateTime.MinValue;
        private int _speedAsksUnanswered;

        private void AskRadioItsSpeed()
        {
            if (_askSpeed == null || _wpmText == null) return;

            if ((_box.Text ?? string.Empty).Length > 0 || _inFlight.Count > 0) return;
            if (DateTime.UtcNow < _radioBusyUntil) return;
            if (DateTime.UtcNow - _speedAskedUtc < AskSpeedEvery) return;
            if (_speedAsksUnanswered >= AskSpeedGiveUpAfter) return;

            _speedAskedUtc = DateTime.UtcNow;
            _speedAsksUnanswered++;

            try { _askSpeed(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The speed the radio is really at, shown without being sent back to it.
        internal void ShowSpeed(int wpm)
        {
            if (_wpmText == null || wpm <= 0) return;

            // AN ANSWER OF ANY KIND MEANS THE RADIO IS TALKING, even one that says what the readout
            // already shows - so the tries start counting from nothing again and the asking goes on.
            _speedAsksUnanswered = 0;
            if (wpm == _wpm) return;

            int low, high;
            Limits(out low, out high);
            if (wpm < low || wpm > high) return;

            _wpm = wpm;
            RefreshSpeedText();
        }

        // WHAT THE KEYS DO, AND WHAT MAY GO IN A TEXT: the one place the operator already opens to
        // change how this window behaves. Every line is something the window does that nothing on its
        // face announces - a key with no button, a button whose second meaning is on the right mouse
        // button, or a macro that shows itself only once it has been typed into a text.
        //
        // TWO COLUMNS SIDE BY SIDE, because the macro list has outgrown the keys. One under the other
        // they had become a page to scroll through; the gear window is widened to carry them abreast.
        private UIElement BuildHelp()
        {
            var keys = new[]
            {
                new[] { "F1 to F12",    "Press the twelve buttons, 1 to 12, while this window is open - even when you are typing in the main window." },
                new[] { "Ctrl+K",       "Closes this window. On the main window it opens it." },
                new[] { "Escape",       "Stops the radio now and drops whatever has not gone out. What has already gone stays in the record." },
                new[] { "Backspace",    "Takes back only what has not gone out yet." },
                // "Mouse click" alone left the operator asking "click on what?" - the row named no
                // target at all. The buttons are named in the right-hand cell rather than the left,
                // which is shared with every other row and would be squeezed to fit the longest label.
                new[] { "Mouse click",  "On one of the keyer's macro buttons: puts its text into the typing row, and it goes out from there." },
                new[] { "Right-click",  "On one of the keyer's macro buttons: edits its text." }
            };

            // BOTH SPELLINGS OF EACH, because until now only the single character was shown and the
            // long form was written down nowhere but in the source. A man who writes his messages in
            // N1MM types {MYCALL} and {EXCH}; HolyLogger has always understood them, and had no way of
            // telling him so. {ZONE} is ours - N1MM keeps the zone inside {EXCH} and has no macro of
            // its own for it - and it is the one that still works with no contest selected.
            var macros = new[]
            {
                new[] { "*  or  {MYCALL}", "Your Station Callsign." },
                new[] { "!  or  {CALL}",   "The DX Callsign." },
                new[] { "#",               "The serial number you are sending. Needs contest mode." },
                new[] { "$  or  {EXCH}",   "The rest of your sent exchange. Needs contest mode." },
                new[] { "{ZONE}",          "Your CQ zone. Works with or without contest mode." },
                new[] { "{SENTRST}",       "The RST you are sending." },
                new[] { "{GRID}",          "Your My Locator." },
                new[] { "{GRIDSQUARE}",    "His DX Locator." },
                new[] { "{NAME}",          "His Name." },
                new[] { "{LASTCALL}",      "The callsign of the last QSO logged." },
                new[] { "{LOG}",           "Logs the QSO, the same as pressing Add. It keys nothing itself, so TU {LOG} sends TU and logs. A text that is only {LOG} logs without transmitting." },
                new[] { "{WIPE}",          "Clears the entry form, the same as pressing Clear. It keys nothing either." }
            };

            // ONE RUN OF ROWS, NOT TWO LISTS SIDE BY SIDE. Keys left and macros right left the left
            // column ending half way down the window while the right ran on past the bottom of it.
            // The rows now flow: the left column is filled first, and what will not fit continues in
            // the right, headings included - so both columns end at about the same depth.
            var rows = new List<HelpRow>();
            rows.Add(HelpRow.Heading("What the keys do"));
            foreach (string[] k in keys) rows.Add(HelpRow.Pair(k[0], k[1]));
            rows.Add(HelpRow.Heading("What you can put in a text"));
            foreach (string[] m in macros) rows.Add(HelpRow.Pair(m[0], m[1]));

            int split = HelpSplitPoint(rows);
            var leftRows = rows.Take(split).ToList();
            var rightRows = rows.Skip(split).ToList();

            // The break can fall in the middle of a section. A column that opens with rows standing
            // under no heading at all says nothing about what they are, so the heading comes across
            // with them.
            if (rightRows.Count > 0 && !rightRows[0].IsHeading)
            {
                string section = leftRows.Where(r => r.IsHeading).Select(r => r.Label).LastOrDefault();
                if (!string.IsNullOrEmpty(section))
                {
                    rightRows.Insert(0, HelpRow.Heading(section + " (continued)"));
                }
            }

            Grid leftTable = BuildHelpTable(leftRows);
            Grid rightTable = BuildHelpTable(rightRows);
            AddHelpExample(rightTable, rightTable.RowDefinitions.Count);   // last of the flow

            var both = new Grid();
            both.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            both.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            both.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(leftTable, 0);
            both.Children.Add(leftTable);
            Grid.SetColumn(rightTable, 2);
            both.Children.Add(rightTable);

            var separated = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 18, 0, 0),
                Padding = new Thickness(0, 14, 0, 0),
                Child = both
            };
            separated.SetResourceReference(Border.BorderBrushProperty, "MutedTextBrush");
            return separated;
        }

        // A row of the help: either a heading, or a label and what it means.
        private sealed class HelpRow
        {
            public string Label;
            public string Meaning;
            public bool IsHeading;

            public static HelpRow Heading(string text)
            {
                return new HelpRow { Label = text, IsHeading = true };
            }

            public static HelpRow Pair(string label, string meaning)
            {
                return new HelpRow { Label = label, Meaning = meaning };
            }
        }

        // WHERE THE LEFT COLUMN ENDS. Counting rows would not do it - one row of the list is a single
        // short line and another wraps to three - so each row is weighed by how many lines it is
        // likely to take at this column width, and the break comes as soon as the left half is full.
        // A row added or reworded later moves the break by itself, with nothing to remember here.
        private static int HelpSplitPoint(List<HelpRow> rows)
        {
            const double CharsPerLine = 44;   // the meaning column at half of a 1000-wide window

            var weights = new double[rows.Count];
            double total = 0;

            for (int i = 0; i < rows.Count; i++)
            {
                double lines = rows[i].IsHeading
                    ? 1.6                                    // a heading plus the space under it
                    : Math.Max(1, Math.Ceiling((rows[i].Meaning ?? string.Empty).Length / CharsPerLine));
                weights[i] = lines;
                total += lines;
            }

            double running = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                running += weights[i];

                // Never break just after a heading: a heading at the foot of a column with its first
                // row over in the other one reads as a heading over nothing.
                if (running >= total / 2 && !rows[i].IsHeading) return i + 1;
            }

            return rows.Count;
        }

        private static TextBlock HelpHeading(string text)
        {
            var heading = new TextBlock
            {
                Text = text,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return heading;
        }

        // One column of the help: bold label, a gap, and the wrapping meaning; a heading spans the lot.
        private static Grid BuildHelpTable(IEnumerable<HelpRow> rows)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int i = 0;
            foreach (HelpRow row in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                if (row.IsHeading)
                {
                    TextBlock heading = HelpHeading(row.Label);

                    // Space above it, unless it is the first thing in the column - there the rule
                    // above the whole block is the separation.
                    heading.Margin = new Thickness(0, i == 0 ? 0 : 14, 0, 8);
                    Grid.SetRow(heading, i);
                    Grid.SetColumn(heading, 0);
                    Grid.SetColumnSpan(heading, 3);
                    grid.Children.Add(heading);
                    i++;
                    continue;
                }

                var key = new TextBlock
                {
                    Text = row.Label,
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
                    Text = row.Meaning,
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 3)
                };
                meaning.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                Grid.SetRow(meaning, i);
                Grid.SetColumn(meaning, 2);
                grid.Children.Add(meaning);
                i++;
            }

            return grid;
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

    // ── WHICH RADIOS CAN SEND CW, AS A PAGE OF ITS OWN ──────────────────────────────
    //
    // Reached from Help, and from a link in the CW Keyer's settings. It sat inside those settings at
    // first, which put a page of reading between the operator and the two boxes he had opened the
    // dialog to change.
    //
    // HIS OWN RADIO COMES FIRST and in the accent colour, because it is the only line he is really
    // asking about. The four makers follow, for the day he is choosing a radio or helping somebody
    // else with one.
    // ONE PAGE, TWO QUESTIONS. "Can my radio send CW" and "can my radio play a recorded voice
    // message" are the same question asked about a different command, and they want the same page:
    // his own radio first, the makers under it, and a line saying where the list came from. Written
    // once and told what to say, rather than copied and left to drift apart.
    internal sealed class CwRadiosWindow : Window
    {
        internal static void Show(Window owner)
        {
            new CwRadiosWindow(
                "Which radios can send CW",
                main => main.CwKeyingForThisRadio(),
                new[]
                {
                    new[] { "Icom",     "IC-705, IC-7300, IC-7300MK2, IC-7610, IC-9700" },
                    new[] { "Kenwood",  "TS-480, TS-590S, TS-590SG, TS-890S, TS-990S" },
                    new[] { "Elecraft", "K3, K3S, K4, KX2, KX3" },
                    new[] { "Yaesu",    "FT-891, FT-991, FT-991A, FTDX10, FTDX101D, FTDX101MP, FT-710" }
                })
            { Owner = owner }.ShowDialog();
        }

        // ── WHICH RADIOS CAN PLAY A RECORDED VOICE MESSAGE ──────────────────────────────────────
        //
        // The four Msg buttons do not record anything and send no audio: they tell the radio to play
        // a message its owner recorded into the radio itself. Every model below was read out of its
        // maker's own command document - Icom's CI-V Reference Guides and the IC-7300 Full Manual
        // (command 28 00, "Transmits the Voice TX memory content"), Yaesu's CAT Operation Reference
        // Manuals ("PB PLAY BACK"), Kenwood's PC Control Command Reference Guides ("PB", voice and
        // CW message playback) and Elecraft's Programmer's References.
        //
        // NOT EVERY RADIO OF A MAKE. An IC-7100 records voice messages and plays them from its own
        // keys, but its CI-V guide has no command for it, so it is not here.
        internal static void ShowVoice(Window owner)
        {
            new CwRadiosWindow(
                "Which radios can play a voice message",
                main => main.VoiceMessageForThisRadio(),
                new[]
                {
                    new[] { "Icom",     "IC-705, IC-7300, IC-7300MK2, IC-7610, IC-7760, IC-9700" },
                    new[] { "Kenwood",  "TS-590S, TS-590SG, TS-480 (three messages, not four)" },
                    new[] { "Elecraft", "K3, K3S, K4" },
                    new[] { "Yaesu",    "FT-891, FT-991, FT-991A, FTDX10, FTDX101D, FTDX101MP, "
                                        + "FT-710, FTDX3000" }
                })
            { Owner = owner }.ShowDialog();
        }

        private readonly Func<MainWindow, string> _answerForThisRadio;
        private readonly string[][] _makers;

        private CwRadiosWindow(string title, Func<MainWindow, string> answerForThisRadio, string[][] makers)
        {
            _answerForThisRadio = answerForThisRadio;
            _makers = makers;

            Title = title;
            Width = 620;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBg");

            var stack = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
            stack.Children.Add(Body());

            var close = new Button
            {
                Content = "Close",
                FontSize = 16,
                Height = 34,
                MinWidth = 110,
                Margin = new Thickness(0, 16, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                IsDefault = true,
                IsCancel = true
            };
            close.Click += (s, e) => Close();
            stack.Children.Add(close);

            // Grows to its content, but never past the screen: what goes over the bottom edge of a
            // monitor is the Close button.
            Content = new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 120)
            };
        }

        private UIElement Body()
        {
            var box = new StackPanel();

            // Asked of the main window, the only place that knows what OmniRig is running. Reached
            // through the owner where there is one, and by looking for it where there is not - this
            // window opens both from Help and from the keyer, which is itself owned by the main window.
            string rigName = null, mine = null;
            try
            {
                MainWindow main = Owner as MainWindow;
                if (main == null && Owner != null) main = Owner.Owner as MainWindow;
                if (main == null && Application.Current != null)
                    main = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                if (main != null)
                {
                    rigName = main.ConnectedRigName();
                    mine = _answerForThisRadio(main);
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // TWO LINES, NOT ONE. The radio's name and the answer about it were run together, and the
            // name - the thing he is looking for - was buried in the middle of a sentence. The name
            // stands on its own line and the answer sits under it.
            if (!string.IsNullOrEmpty(rigName))
            {
                var yours = new TextBlock
                {
                    Text = "Your radio: " + rigName,
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                yours.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                box.Children.Add(yours);
            }

            if (!string.IsNullOrEmpty(mine))
            {
                var answer = new TextBlock
                {
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 14)
                };

                // THE ANSWER IS THE FIRST WORD, so it is the word carrying the weight. He came here
                // to learn yes or no, and the rest is why - which he reads afterwards, or not at all.
                //
                // Matched on "Yes" or "No" followed by a stop or a comma, and no further: "No radio
                // is connected" opens with the same two letters and is not an answer about a radio's
                // commands at all, so bolding its "No" would be answering a question nobody asked.
                var opening = System.Text.RegularExpressions.Regex.Match(mine, @"^(Yes|No)[.,]");
                if (opening.Success)
                {
                    answer.Inlines.Add(new System.Windows.Documents.Run(opening.Value)
                    {
                        FontWeight = FontWeights.Bold
                    });
                    answer.Inlines.Add(new System.Windows.Documents.Run(mine.Substring(opening.Length)));
                }
                else
                {
                    answer.Inlines.Add(new System.Windows.Documents.Run(mine));
                }

                answer.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                box.Children.Add(answer);
            }

            // ── THE MODELS, EACH ONE READ OUT OF ITS MAKER'S OWN DOCUMENT ────────────────────────
            //
            // Not from memory and not from what a forum says. Every model here was checked against the
            // manufacturer's published command reference:
            //
            //   Icom      CI-V Reference Guides (IC-705, IC-7610, IC-9700, IC-7300MK2) and the IC-7300
            //             Full Manual: command 17, "Send CW messages", up to 30 characters.
            //   Kenwood   PC Control Command References (TS-480, TS-590S/SG, TS-890S, TS-990S):
            //             "KY - Converts the entered characters into morse code while keying."
            //   Elecraft  Programmer's References (K3S/K3/KX3/KX2 rev. G5, K4 rev. D5):
            //             "KY*[text];" - 24 characters on the K3 family, 60 on the K4.
            //   Yaesu     CAT References (FT-991, FT-991A, FT-891, FTDX101): KY plays back a memory
            //             and takes a digit. There is no command that keys typed text.
            //
            // A SHORT LIST ON PURPOSE. It is the popular radios, not every model ever made: a list that
            // tries to be complete from memory is a list with mistakes in it, and a mistake here tells
            // a man his radio cannot do something it can.
            var makers = _makers;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < makers.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var maker = new TextBlock
                {
                    Text = makers[i][0],
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 4, 0, 4)
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
                    Margin = new Thickness(0, 4, 0, 4)
                };
                what.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                Grid.SetRow(what, i);
                Grid.SetColumn(what, 2);
                grid.Children.Add(what);
            }

            box.Children.Add(grid);

            var note = new TextBlock
            {
                Text = "Every model above was checked in its own maker's command manual. The list is the "
                     + "popular radios rather than every model ever made - if yours is missing, ask for "
                     + "it with Help \u2192 Support and it can be checked and added.",
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 14, 0, 0)
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            box.Children.Add(note);

            return box;
        }
    }
}
