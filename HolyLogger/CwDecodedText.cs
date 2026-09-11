using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HolyLogger
{
    // THE DECODED TEXT, WITH THE CALLSIGNS IN IT PICKED OUT AND READY TO BE LOGGED.
    //
    // This is where reading turns into logging. A decoder that only prints leaves the operator to
    // read a callsign off the screen and type it again; picking it out and letting him double-click
    // it into the log is the whole difference between a curiosity and a tool.
    //
    // COLOURED MEANS "SHAPED LIKE A CALLSIGN", NOT "CORRECT". The decoder gets characters wrong, so
    // a wrong group can look every bit as much like a callsign as a right one - 4Z5SR for 4Z5SL. It
    // is therefore never put into the log by itself: the operator double-clicks the one he means,
    // having heard the station himself. The colour is an offer, not an answer.
    //
    // The test for what looks like a callsign is CallsignIdentity's, the same one the entry form
    // warns with and the Log Fixer repairs by - so this window and the rest of the program cannot
    // disagree about what a callsign is. His own callsign is never offered back to him, and the same
    // class knows 4Z5SL/M is still him.
    public class CwDecodedText
    {
        // Words are only judged when they are finished, so a callsign is coloured as its last letter
        // arrives rather than flickering through every wrong shape on the way.
        const int MostBlocks = 400;
        const int BlocksTrimmedAtOnce = 100;

        // WHAT PROVES A "K" MEANT "OVER".
        //
        // Not the K. K is a letter as much as a signal - it turns up alone in spaced-out sending, in
        // a callsign read back letter by letter, and in whatever the decoder makes of a burst of
        // noise. Breaking the line on the K alone chops transmissions in half.
        //
        // Silence after it was the second guess and it was wrong too, for the opposite reason: when
        // a K really does mean over, the other station comes straight back, often inside a second.
        // Waiting for a long quiet misses exactly the handovers it was meant to catch.
        //
        // WHAT ACTUALLY CHANGES IS THE STATION. A different operator is on a slightly different note
        // - nobody zero-beats exactly - and sends at a different speed. Both are already measured,
        // every moment, by the decoder feeding this window. So the question "did the turn change?"
        // is answered by "is this a different man sending?", which is the same question and a far
        // easier one.
        //
        // A tone this far apart is a different station. The note is now measured to a few Hz - see
        // InterpolatedNote in CwDecoder, which fits a curve through the strongest filter and its two
        // neighbours instead of rounding to the nearest 25 - so fifteen is comfortably outside what
        // the measurement itself can wobble by, and well inside how far apart two operators net.
        const double DifferentNoteHz = 15.0;

        // And a speed this much apart, as a fraction. Operators an eighth apart in speed are not the
        // same operator; the same operator does not change by an eighth in one second.
        const double DifferentSpeedFraction = 0.12;

        // With the note and speed both unchanged - two stations truly nose to nose - there is
        // nothing left but the gap. Long, because a gap this size inside one transmission would be
        // remarkable, and a wrong break costs more than a missed one.
        static readonly TimeSpan SilenceThatProvesOver = TimeSpan.FromSeconds(2.0);

        // How long a pause has to be, counted in dits so it follows the speed, before it can be the
        // turnaround between two stations rather than a gap between two words. A word gap is seven
        // dits; twelve is clear of it at any speed and still short enough for a smart turnaround.
        const double PauseInDits = 12.0;

        // WHITE ON BLUE, not blue text. A callsign in the middle of decoded text has to be findable
        // at a glance while the operator is listening, and a coloured word among black ones is easy
        // to miss; a filled patch is not. It also reads the same on every colour scheme, where blue
        // text on dark paper does not.
        static readonly Brush CallsignInk = Brushes.White;
        static readonly Brush CallsignPatch = Frozen(Color.FromRgb(0x00, 0x77, 0xCC));

        static Brush Frozen(Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
        }

        readonly RichTextBox _box;
        readonly Paragraph _paragraph;
        Run _word;
        bool _atLineStart = true;
        DateTime _handedOverAt = DateTime.MinValue;
        double _noteAtHandover, _speedAtHandover;
        bool _lastWasProsignEnd;
        DateTime _lastLetterAt = DateTime.MinValue;
        double _lastNote, _lastSpeed;

        /// <summary>
        /// The note and speed the decoder is hearing right now. Set by the window before each
        /// Append, and used to tell a new station from the same one carrying on - see the note above
        /// DifferentNoteHz.
        /// </summary>
        public double ToneHz { get; set; }
        public double Wpm { get; set; }

        /// <summary>A callsign the operator double-clicked, ready for the log.</summary>
        public event Action<string> CallsignChosen;

        public RichTextBox Box { get { return _box; } }

        public CwDecodedText(RichTextBox box)
        {
            _box = box;
            _paragraph = new Paragraph { Margin = new Thickness(0) };
            _box.Document = new FlowDocument(_paragraph)
            {
                PagePadding = new Thickness(4, 2, 4, 2),
                LineHeight = double.NaN
            };
            _box.MouseDoubleClick += OnDoubleClick;
        }

        public void Clear()
        {
            _paragraph.Inlines.Clear();
            _word = null;
            _atLineStart = true;
            _handedOverAt = DateTime.MinValue;
            _noteAtHandover = 0; _speedAtHandover = 0;
            _lastLetterAt = DateTime.MinValue; _lastNote = 0; _lastSpeed = 0;
        }

        /// <summary>Adds newly decoded text, colouring any callsign as its last letter arrives.</summary>
        public void Append(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            foreach (char c in text)
            {
                // A PROSIGN IS A WORD OF ITS OWN, whatever is written against it. They arrive with no
                // space around them - "E74MW<SK>EE" is what came off the air - so without this the
                // callsign, the prosign and whatever follows are one long word: not a callsign, so
                // not offered for the log, and not <SK>, so the turn never ended. The brackets are
                // the boundary, because only a prosign has them.
                if (c == '<') EndWordHere();
                if (_lastWasProsignEnd) { EndWordHere(); _lastWasProsignEnd = false; }
                _lastWasProsignEnd = c == '>';

                if (c == ' ' || c == '\r' || c == '\n')
                {
                    // A TURN ENDS, SO THE LINE ENDS - but the K alone does not prove the turn ended.
                    // Noted here, and acted on below only if he really did stop.
                    EndWordHere();

                    // No run of blank space at the start of a fresh line.
                    if (_atLineStart) continue;

                    _paragraph.Inlines.Add(new Run(c.ToString()));
                    continue;
                }

                // IS THIS THE SAME MAN STILL SENDING, OR THE ONE ANSWERING HIM? That is the whole
                // question, and it is asked HERE - as the next letter arrives after a K - rather
                // than at the K itself, because the K alone proves nothing. The two rules that were
                // tried before this one, and why each was wrong, are at the top of this file.
                // A TURN CHANGES WHETHER OR NOT THE "K" SURVIVED THE DECODING.
                //
                // Waiting for a clean K is too fragile: it arrives welded to whatever came before -
                // "E74MW<SK>EE", "-HW?BK" - because the space in front of it was never decoded, and
                // sometimes it is simply misread. So the K is no longer the only way in.
                //
                // The other way needs no K at all: a PAUSE, and then a DIFFERENT MAN sending. Both
                // halves are required. A pause alone is just a gap between words; a different note
                // alone is the signal drifting or fading. Together they are somebody else taking the
                // frequency, which is exactly what a new line is for.
                if (_handedOverAt == DateTime.MinValue && _lastLetterAt != DateTime.MinValue)
                {
                    double dit = Wpm > 0 ? 1200.0 / Wpm : 60.0;
                    bool realPause = (DateTime.UtcNow - _lastLetterAt).TotalMilliseconds > dit * PauseInDits;

                    if (realPause && SomebodyElse(_lastNote, _lastSpeed) && !_atLineStart)
                    {
                        _paragraph.Inlines.Add(new LineBreak());
                        _atLineStart = true;
                        EndWordHere();
                    }
                }

                _lastLetterAt = DateTime.UtcNow;
                if (ToneHz > 0) _lastNote = ToneHz;
                if (Wpm > 0) _lastSpeed = Wpm;

                if (_handedOverAt != DateTime.MinValue)
                {
                    bool differentNote = _noteAtHandover > 0 && ToneHz > 0
                                         && Math.Abs(ToneHz - _noteAtHandover) >= DifferentNoteHz;

                    bool differentSpeed = _speedAtHandover > 0 && Wpm > 0
                                          && Math.Abs(Wpm - _speedAtHandover)
                                             >= _speedAtHandover * DifferentSpeedFraction;

                    bool longEnoughGap = DateTime.UtcNow - _handedOverAt >= SilenceThatProvesOver;

                    if ((differentNote || differentSpeed || longEnoughGap) && !_atLineStart)
                    {
                        _paragraph.Inlines.Add(new LineBreak());
                        _atLineStart = true;
                    }
                    _handedOverAt = DateTime.MinValue;
                }

                _atLineStart = false;

                if (_word == null)
                {
                    _word = new Run(string.Empty);
                    _paragraph.Inlines.Add(_word);
                }
                _word.Text += c;
            }

            Trim();
            _box.ScrollToEnd();
        }

        // Is this a different operator from the one who was sending a moment ago? The same two
        // tests as after a K - the note he nets on and the speed of his fist.
        bool SomebodyElse(double wasNote, double wasSpeed)
        {
            bool note = wasNote > 0 && ToneHz > 0 && Math.Abs(ToneHz - wasNote) >= DifferentNoteHz;
            bool speed = wasSpeed > 0 && Wpm > 0
                         && Math.Abs(Wpm - wasSpeed) >= wasSpeed * DifferentSpeedFraction;
            return note || speed;
        }

        // Closes the word being gathered without writing a space - used at the two edges of a
        // prosign, which has no spaces around it but is a word all the same.
        void EndWordHere()
        {
            if (_word == null || _word.Text.Length == 0) return;

            if (FinishWord())
            {
                _handedOverAt = DateTime.UtcNow;
                _noteAtHandover = ToneHz;
                _speedAtHandover = Wpm;
            }
        }

        // THE WORDS THAT MEAN "OVER TO YOU". Only when one of them stands ALONE as a word: K is a
        // letter as well as a signal, and KM72OR and TNX FER QSO OM both hold one without anybody
        // handing over. A CQ call ends in K too - that is an invitation to everybody rather than to
        // one station, but it is still the end of a turn, so it earns its new line just the same.
        static readonly string[] HandOver = { "K", "<KN>", "<AR>", "<SK>", "BK" };

        // A word has ended: decide whether it is a callsign worth offering, and whether it hands the
        // frequency over. Returns true when the turn is finished.
        bool FinishWord()
        {
            var word = _word;
            _word = null;
            if (word == null || word.Text.Length == 0) return false;

            string call = word.Text.Trim();

            foreach (string over in HandOver)
                if (string.Equals(call, over, StringComparison.Ordinal)) return true;

            if (!CallsignIdentity.LooksLikeCallsign(call)) return false;

            // Never his own, and never a stroke variant of it either - a man does not log himself.
            string mine = null;
            try { mine = Properties.Settings.Default.my_callsign; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (!string.IsNullOrWhiteSpace(mine) && CallsignIdentity.Same(call, mine)) return false;

            word.Foreground = CallsignInk;
            word.Background = CallsignPatch;
            word.FontWeight = FontWeights.Bold;
            word.Tag = call;
            word.Cursor = System.Windows.Input.Cursors.Hand;
            word.ToolTip = "Double-click to put " + call + " in the DX Callsign box";
            return false;
        }

        // Anything the operator double-clicks that was marked as a callsign goes to the log. A
        // double-click rather than a single one: the text is there to be read, and a stray click
        // while reading must not quietly change what is about to be logged.
        void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                var position = _box.GetPositionFromPoint(e.GetPosition(_box), false);
                if (position == null) return;

                var run = position.Parent as Run;
                if (run == null || run.Tag == null) return;

                var handler = CallsignChosen;
                if (handler == null) return;

                handler(run.Tag.ToString());
                e.Handled = true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The document is trimmed from the front, so the newest text is always kept and a window
        // left open all evening never becomes the reason the program is slow.
        void Trim()
        {
            if (_paragraph.Inlines.Count <= MostBlocks) return;

            for (int i = 0; i < BlocksTrimmedAtOnce && _paragraph.Inlines.Count > 0; i++)
            {
                var first = _paragraph.Inlines.FirstInline;
                if (first == null || ReferenceEquals(first, _word)) break;
                _paragraph.Inlines.Remove(first);
            }
        }
    }
}
