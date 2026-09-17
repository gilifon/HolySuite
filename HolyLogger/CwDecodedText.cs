using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

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
        const double DifferentNoteHz = 25.0;

        // And a speed this much apart, as a fraction. Operators an eighth apart in speed are not the
        // same operator; the same operator does not change by an eighth in one second.
        const double DifferentSpeedFraction = 0.20;

        // With the note and speed both unchanged - two stations truly nose to nose - there is
        // nothing left but the gap. Long, because a gap this size inside one transmission would be
        // remarkable, and a wrong break costs more than a missed one.
        static readonly TimeSpan SilenceThatProvesOver = TimeSpan.FromSeconds(2.0);

        // How long a pause has to be, counted in dits so it follows the speed, before it can be the
        // turnaround between two stations rather than a gap between two words.
        //
        // TWELVE WAS FAR TOO EAGER. A word gap is seven dits, so twelve leaves almost no margin: at
        // 32 WPM it is 450 milliseconds against a word gap of 262, and an operator who pauses to
        // think beats it every time. Lines were breaking three and four times inside one
        // transmission - after "BE", after "EE", after a lone C. Thirty is about a second at 32 WPM
        // and over a second and a half at 20, which is a turnaround and not a hesitation.
        const double PauseInDits = 30.0;

        // WHITE ON BLUE, not blue text. A callsign in the middle of decoded text has to be findable
        // at a glance while the operator is listening, and a coloured word among black ones is easy
        // to miss; a filled patch is not. It also reads the same on every colour scheme, where blue
        // text on dark paper does not.
        static readonly Brush CallsignInk = Brushes.White;
        static readonly Brush CallsignPatch = Frozen(Color.FromRgb(0x00, 0x77, 0xCC));

        // GREEN MEANS QRZ.COM KNOWS IT. Blue says only "shaped like a callsign", and the decoder can
        // make a wrong one look every bit as right - 4Z5SR for 4Z5SL. A callsign with a QRZ record
        // is far more likely to be what was sent, so it is checked by itself as it is decoded and
        // turns green when QRZ answers yes. Still an offer and not an answer: a misread can land on
        // somebody else's real call. Stays blue when QRZ says no, and when it cannot be asked.
        static readonly Brush RegisteredPatch = Frozen(Color.FromRgb(0x1E, 0x8E, 0x3E));

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

        /// <summary>
        /// The turn has changed hands - raised at the same moment the line breaks, and for the same
        /// reasons: a proven K, BK or prosign, or a pause followed by somebody else sending.
        ///
        /// This window is where the handover is worked out, and until now the decoder never heard
        /// about it: it went on using the old operator's speed and misread the first seconds of the
        /// new one. So what is decided here is told back to it - see ForgetTheOperator.
        /// </summary>
        public event Action TurnChanged;

        void TurnHasChanged()
        {
            _paragraph.Inlines.Add(new LineBreak());
            _atLineStart = true;
            var told = TurnChanged;
            if (told != null) told();
        }

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
            _box.ContextMenuOpening += OnContextMenuOpening;
        }

        public void Clear()
        {
            _paragraph.Inlines.Clear();
            QrzLookup.ForgetAll();
            _word = null;
            _atLineStart = true;
            _handedOverAt = DateTime.MinValue;
            _noteAtHandover = 0; _speedAtHandover = 0;
            _lastLetterAt = DateTime.MinValue; _lastNote = 0; _lastSpeed = 0;
            _recentCalls.Clear(); _recentCallOrder.Clear();
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
                        TurnHasChanged();
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
                        TurnHasChanged();
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

        // NO AMATEUR CALLSIGN BEGINS WITH TWO DIGITS. A prefix is letters, or a letter and a digit,
        // or a digit and a letter - 4Z5SL, 9A1A, 2E0ABC - but never digit-digit, because no such
        // prefix has ever been allocated.
        //
        // The program's own callsign test does not know that, and it is right not to: it exists to
        // tell a callsign from a LoTW username, where being generous costs nothing. Here it costs
        // something, because run-together words get offered for the log. "73 TU EE" arriving glued
        // as "73TUEE" reads as prefix 7, digit 3, suffix TUE-E - a perfectly good callsign shape.
        //
        // This is the extra question, asked only here, and it turns that whole family down without
        // touching what the rest of the program believes. The underlying fault is still the missing
        // spaces; this only stops them being offered as somebody's call.
        // AND NO PREFIX HAS THREE LETTERS BEFORE ITS FIRST DIGIT. Every prefix ever allocated is one
        // letter, two letters, a letter and a digit, or a digit and a letter - G3, LY2, 4Z5, 2E0 -
        // so the run of letters at the front of a callsign is never longer than two.
        //
        // This is the same fault as the two digits above wearing a different hat: run-together words
        // arriving with the space missed. "N H EEE 50" came off the air glued as "EEE50", which has
        // the shape of prefix EEE, digit 5, suffix 0 - and was offered for the log. Three letters
        // then a digit turns the whole family down.
        // A STROKE CALL IS TESTED PIECE BY PIECE. "BW/JA1APE" is a Japanese operator in Taiwan, and
        // reading it as one run makes "BW/JA" four letters before the digit - so the rule threw out
        // three thousand perfectly good calls in his own list until each side of the stroke was
        // asked separately. Only a piece that has a digit in it is a callsign; the others are the
        // country he is in or the /M and /P that say how, and they prove nothing either way.
        static bool CouldStartACallsign(string call)
        {
            string s = (call ?? string.Empty).TrimStart('<');
            if (s.Length < 2) return false;

            bool foundTheCallsign = false;

            foreach (string piece in s.Split('/'))
            {
                bool hasALetter = false, hasADigit = false;
                foreach (char c in piece)
                {
                    if (char.IsDigit(c)) hasADigit = true;
                    else if (char.IsLetter(c)) hasALetter = true;
                }

                // "BW", "M", "P" - where he is and how. "1", "70" - which call area. Neither is the
                // callsign, and neither says anything about whether this is one.
                if (!hasALetter || !hasADigit) continue;

                foundTheCallsign = true;
                if (!IsCallsignShaped(piece)) return false;
            }

            return foundTheCallsign;
        }

        static bool IsCallsignShaped(string piece)
        {
            if (piece.Length < 2) return false;

            // No prefix has ever been allocated as digit-digit.
            if (char.IsDigit(piece[0]) && char.IsDigit(piece[1])) return false;

            // And none has three letters in front of its first digit.
            int letters = 0;
            while (letters < piece.Length && !char.IsDigit(piece[letters])) letters++;
            return letters <= 2;
        }

        // Ends the line here and now, with nothing further to prove.
        void BreakLineNow()
        {
            if (_atLineStart) return;

            _paragraph.Inlines.Add(new LineBreak());
            _atLineStart = true;
            _handedOverAt = DateTime.MinValue;
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
        // A PROSIGN NEEDS NO PROOF. These are not letters - <SK> is six elements run together, and
        // nobody sends one by accident or decodes one out of noise. When one arrives the turn is
        // over, and the line breaks there and then.
        static readonly string[] ProsignHandOver = { "<KN>", "<AR>", "<SK>", "<AS>" };

        // THESE DO need proof, because they are ordinary letters as well as signals. A bare K turns
        // up inside spaced-out sending and in a callsign read back letter by letter; BK is two
        // common letters. So they only note that a turn MIGHT have ended, and the line breaks later
        // if the next letter comes from a different man - see the note at the top of this file.
        static readonly string[] LetterHandOver = { "K", "BK" };

        // A word has ended: decide whether it is a callsign worth offering, and whether it hands the
        // frequency over. Returns true when the turn is finished.
        bool FinishWord()
        {
            var word = _word;
            _word = null;
            if (word == null || word.Text.Length == 0) return false;

            string call = word.Text.Trim();

            foreach (string over in ProsignHandOver)
                if (string.Equals(call, over, StringComparison.Ordinal)) { BreakLineNow(); return false; }

            foreach (string over in LetterHandOver)
                if (string.Equals(call, over, StringComparison.Ordinal)) return true;

            if (IsOfferableCall(call))
            {
                ColourAsCallsign(word, call);
                RememberCall(call, new[] { word });
            }
            else JoinPiecesEndingWith(word);

            return false;
        }

        // Shaped like a callsign, and not his own - never a stroke variant of it either, a man does
        // not log himself.
        static bool IsOfferableCall(string call)
        {
            if (!CallsignIdentity.LooksLikeCallsign(call)) return false;
            if (!CouldStartACallsign(call)) return false;

            string mine = null;
            try { mine = Properties.Settings.Default.my_callsign; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return string.IsNullOrWhiteSpace(mine) || !CallsignIdentity.Same(call, mine);
        }

        void ColourAsCallsign(Run word, string call)
        {
            word.Foreground = CallsignInk;
            word.Background = CallsignPatch;
            word.FontWeight = FontWeights.Bold;
            word.Tag = call;
            word.Cursor = System.Windows.Input.Cursors.Hand;
            word.ToolTip = "Double-click to put " + call + " in the DX Callsign box.\nRight-click to check it on QRZ.com.";

            if (QrzLookup.HasLogin) MarkIfRegistered(word, call);
        }

        // A CALLSIGN SENT WITH PAUSES IN IT - "HB9 D NP", then "HB 9 D NP", then "H B9 DNP".
        //
        // Every letter was heard right; what the decoder got wrong were the spaces. Many operators
        // pause inside their own call, and a pause a little longer than the gap between letters is
        // read as the gap between words. So the call never arrives as one word and is never offered,
        // while a man listening picks it up at once - because the same letters keep coming back.
        //
        // That is exactly the test used here, and nothing is guessed: the short pieces just printed
        // are joined, and if they make a callsign shape AND the same callsign has already been seen
        // nearby - whole, or joined from other pieces - every piece is coloured as that callsign, and
        // a double-click on any of them logs the whole call. The text on screen stays exactly as it
        // was heard; not one letter is added or changed. Seen only once, it is not offered: one
        // chance joining of words is too easy to make.
        const int MostPiecesJoined = 4;
        const int LongestJoinedCall = 10;
        const int RecentCallsKept = 30;

        // Words that are part of every exchange and never part of a callsign sent in pieces.
        // Without them "UR 5NN", sent in every QSO, joins into UR5NN - a perfectly good callsign shape.
        static readonly HashSet<string> NeverAPiece = new HashSet<string>(StringComparer.Ordinal)
        {
            "DE", "UR", "RST", "5NN", "599", "TU", "ES", "CQ", "BK", "73", "TNX", "FB", "HR", "OP",
            "NAME", "QTH", "GM", "GA", "GE", "PSE", "AGN", "QSL", "QRZ", "TEST"
        };

        readonly Dictionary<string, List<Run[]>> _recentCalls =
            new Dictionary<string, List<Run[]>>(StringComparer.OrdinalIgnoreCase);
        readonly Queue<string> _recentCallOrder = new Queue<string>();

        void JoinPiecesEndingWith(Run last)
        {
            var pieces = new List<Run>();
            int length = 0;

            for (Inline at = last; at != null && pieces.Count < MostPiecesJoined; at = at.PreviousInline)
            {
                var run = at as Run;
                if (run == null) break;                      // a line break: the turn changed
                string text = run.Text.Trim();
                if (text.Length == 0) continue;              // the space between two words
                if (text.IndexOf('<') >= 0 || NeverAPiece.Contains(text)) break;
                // A word that is a callsign by itself stays one. A piece already coloured as part of
                // a JOINED call may yet belong to a longer one - see WhyTheLongerCallWins.
                if (run.Tag != null && string.Equals(run.Tag.ToString(), text, StringComparison.OrdinalIgnoreCase)) break;

                pieces.Insert(0, run);
                length += text.Length;
                if (length > LongestJoinedCall) break;
                if (pieces.Count < 2) continue;

                string joined = string.Concat(pieces.Select(p => p.Text.Trim()));
                if (IsOfferableCall(joined)) RememberCall(joined, pieces.ToArray());
            }
        }

        void RememberCall(string call, Run[] pieces)
        {
            List<Run[]> seen;
            if (!_recentCalls.TryGetValue(call, out seen))
            {
                seen = new List<Run[]>();
                _recentCalls[call] = seen;
                _recentCallOrder.Enqueue(call);
                while (_recentCallOrder.Count > RecentCallsKept)
                    _recentCalls.Remove(_recentCallOrder.Dequeue());
            }
            seen.Add(pieces);

            // Seen twice: now every copy sent in pieces is shown as the callsign it is, the earlier
            // ones included.
            if (seen.Count < 2) return;
            foreach (Run[] group in seen)
            {
                if (group.Length < 2) continue;
                foreach (Run piece in group)
                    if (piece.Tag == null || WhyTheLongerCallWins(piece.Tag.ToString(), call))
                        ColourAsCallsign(piece, call);
            }
        }

        // THE LONGER CALL WINS. Pieces arrive one at a time, so the front of a callsign can be proved
        // before its end has even been sent: "HB9 D NP" twice over holds "HB9 D" twice, and HB9D is a
        // perfectly good callsign shape. It was offered - measured on exactly that text - and the NP
        // left hanging. So a piece coloured as a joined call gives way to a longer joined call that
        // contains it. A call already the same is simply coloured again.
        static bool WhyTheLongerCallWins(string was, string now)
        {
            if (string.Equals(was, now, StringComparison.OrdinalIgnoreCase)) return true;
            return now.Length > was.Length && now.IndexOf(was, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Asks QRZ.com in the background and turns the patch green if it knows the callsign. The text
        // keeps coming while it asks; a word already trimmed off the top by then is simply not seen.
        async void MarkIfRegistered(Run word, string call)
        {
            try
            {
                QrzResult result = await QrzLookup.CheckAsync(call);
                if (result.Answer != QrzAnswer.Registered) return;

                word.Background = RegisteredPatch;
                word.ToolTip = call + " is registered on QRZ.com.\nDouble-click to put it in the DX Callsign box.\nRight-click to see it.";
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
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

        // A RIGHT-CLICK ON A CALLSIGN SHOWS WHETHER QRZ.COM KNOWS IT. The blue patch only says
        // "shaped like a callsign"; a QRZ record is a quick hint that the decoder read it right -
        // 4Z5SR with no record is probably 4Z5SL misheard. No menu in between: the right-click IS the
        // question. Anywhere else the box keeps its usual menu.
        void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                var position = _box.GetPositionFromPoint(System.Windows.Input.Mouse.GetPosition(_box), false);
                var run = position == null ? null : position.Parent as Run;
                if (run == null || run.Tag == null) return;

                string call = run.Tag.ToString().Trim().ToUpperInvariant();
                if (call.Length == 0) return;

                e.Handled = true;
                QrzCallsignWindow.Open(call, Window.GetWindow(_box));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The document is trimmed from the front, so the newest text is always kept and a window
        // left open all evening never becomes the reason the program is slow.
        void Trim()
        {
            if (_paragraph.Inlines.Count <= MostBlocks) return;

            var gone = new System.Collections.Generic.List<string>();

            for (int i = 0; i < BlocksTrimmedAtOnce && _paragraph.Inlines.Count > 0; i++)
            {
                var first = _paragraph.Inlines.FirstInline;
                if (first == null || ReferenceEquals(first, _word)) break;
                if (first.Tag != null) gone.Add(first.Tag.ToString());
                _paragraph.Inlines.Remove(first);
            }

            // Its QRZ answer goes with it - unless the same callsign is still further down the text,
            // as it usually is for the station being worked, which sends it over after over.
            foreach (string call in gone)
            {
                bool stillThere = false;
                foreach (Inline left in _paragraph.Inlines)
                    if (left.Tag != null && string.Equals(left.Tag.ToString(), call, StringComparison.OrdinalIgnoreCase))
                    { stillThere = true; break; }

                if (!stillThere) QrzLookup.Forget(call);
            }
        }
    }

    // A SMALL CARD SAYING WHETHER QRZ.COM KNOWS A CALLSIGN - opened by right-clicking a blue callsign
    // in the decoded text.
    //
    // Not the QRZ web page: that is a full browser window over the logging screen, when all the
    // operator wants to know is "is this a real station?". The photo, name, town and country answer
    // that at a glance, and the card is gone with Esc.
    //
    // It uses the same QRZ login as the DX Callsign lookup (Options), through the XML service, so no
    // browser is involved. Without a login there is no way to ask, and the web page opens instead.
    //
    // It lives in this file rather than its own only because the project file could not be edited
    // while Visual Studio had it open; it can move to QrzCallsignWindow.cs at any time.
    public class QrzCallsignWindow : Window
    {
        static readonly Brush HeaderPatch = Frozen(Color.FromRgb(0x00, 0x77, 0xCC));
        static readonly Brush FoundInk = Frozen(Color.FromRgb(0x1E, 0x8E, 0x3E));
        static readonly Brush NotFoundInk = Frozen(Color.FromRgb(0xC6, 0x28, 0x28));
        static readonly Brush LabelInk = Frozen(Color.FromRgb(0x6B, 0x75, 0x80));
        static readonly Brush ValueInk = Frozen(Color.FromRgb(0x1E, 0x2A, 0x34));
        static readonly Brush PhotoFrame = Frozen(Color.FromRgb(0xD5, 0xDB, 0xE1));

        static Brush Frozen(Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
        }

        // One card at a time. Right-clicking another callsign while it is open shows that one in the
        // same card, instead of stacking a pile of them over the decoder.
        static QrzCallsignWindow _open;

        readonly TextBlock _callText, _status;
        Button _fullPage;
        readonly Image _photo;
        readonly Border _photoFrame;
        readonly StackPanel _details;
        string _call;
        int _revision;

        public static void Open(string call, Window owner)
        {
            if (!QrzLookup.HasLogin)
            {
                OpenWebPage(call);
                return;
            }

            if (_open == null)
            {
                _open = new QrzCallsignWindow();
                if (owner != null && owner.IsVisible)
                {
                    _open.Owner = owner;
                    _open.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                else _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                _open.Closed += (s, a) => _open = null;
                _open.Lookup(call);
                _open.Show();
            }
            else
            {
                _open.Lookup(call);
                _open.Activate();
            }
        }

        static void OpenWebPage(string call)
        {
            try { System.Diagnostics.Process.Start("https://www.qrz.com/db/" + call); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        QrzCallsignWindow()
        {
            Title = "QRZ.com";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Brushes.White;
            FontSize = 16;

            _callText = new TextBlock
            {
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas")
            };
            var header = new Border
            {
                Background = HeaderPatch,
                Padding = new Thickness(16, 10, 16, 10),
                Child = _callText
            };

            _status = new TextBlock
            {
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 12, 16, 4)
            };

            _photo = new Image { Stretch = Stretch.Uniform, MaxWidth = 130, MaxHeight = 160 };
            _photoFrame = new Border
            {
                BorderBrush = PhotoFrame,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(3),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = _photo,
                Visibility = Visibility.Collapsed
            };

            _details = new StackPanel();

            var body = new DockPanel { Margin = new Thickness(16, 8, 16, 8) };
            DockPanel.SetDock(_photoFrame, Dock.Left);
            body.Children.Add(_photoFrame);
            body.Children.Add(_details);

            var fullPage = new Button
            {
                Content = "Full QRZ.com page",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            fullPage.Click += (s, a) => OpenWebPage(_call);
            _fullPage = fullPage;

            var close = new Button { Content = "Close", Padding = new Thickness(18, 4, 18, 4), IsCancel = true };
            close.Click += (s, a) => Close();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 8, 16, 14)
            };
            buttons.Children.Add(fullPage);
            buttons.Children.Add(close);

            var page = new StackPanel();
            page.Children.Add(header);
            page.Children.Add(_status);
            page.Children.Add(body);
            page.Children.Add(buttons);
            Content = page;
        }

        async void Lookup(string call)
        {
            int revision = ++_revision;
            _call = call;
            Title = "QRZ.com - " + call;
            _callText.Text = call;
            _status.Text = "Looking up on QRZ.com...";
            _status.Foreground = LabelInk;
            _details.Children.Clear();
            _photo.Source = null;
            _photoFrame.Visibility = Visibility.Collapsed;
            _fullPage.Visibility = Visibility.Visible;

            try
            {
                QrzResult result = await QrzLookup.CheckAsync(call);
                if (revision != _revision) return;

                if (result.Answer == QrzAnswer.CouldNotAsk)
                {
                    _status.Text = "QRZ.com could not be reached.";
                    _status.Foreground = LabelInk;
                    return;
                }

                if (result.Answer == QrzAnswer.NotRegistered)
                {
                    _status.Text = "Not a QRZ.COM registered callsign";
                    _status.Foreground = NotFoundInk;
                    _fullPage.Visibility = Visibility.Collapsed;
                    return;
                }

                ShowRecord(result.Record);
            }
            catch (Exception swallowed)
            {
                Log.Swallow(swallowed);
                if (revision != _revision) return;
                _status.Text = "QRZ.com could not be reached.";
                _status.Foreground = LabelInk;
            }
        }

        void ShowRecord(XElement record)
        {
            XNamespace ns = record.Name.Namespace;
            Func<string, string> field = tag => ((string)record.Element(ns + tag) ?? string.Empty).Trim();

            string found = field("call").ToUpperInvariant();
            if (found.Length > 0 && !string.Equals(found, _call, StringComparison.OrdinalIgnoreCase))
                _callText.Text = _call + "  (" + found + ")";

            _status.Text = "✔ Registered on QRZ.com";
            _status.Foreground = FoundInk;

            string name = (field("fname") + " " + field("name")).Trim();
            string nickname = field("nickname");
            if (nickname.Length > 0 && name.IndexOf(nickname, StringComparison.OrdinalIgnoreCase) < 0)
                name += " \"" + nickname + "\"";

            AddRow("Name", name);
            AddRow("Town", field("addr2"));
            AddRow("State", field("state"));
            AddRow("Country", field("country"));
            AddRow("Grid", field("grid").ToUpperInvariant());

            string image = field("image");
            if (image.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(image);
                    bitmap.DecodePixelWidth = 260;
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.DownloadFailed += (s, a) => _photoFrame.Visibility = Visibility.Collapsed;
                    _photo.Source = bitmap;
                    _photoFrame.Visibility = Visibility.Visible;
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        void AddRow(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = label, Foreground = LabelInk, FontSize = 16 });
            row.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = ValueInk,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            _details.Children.Add(row);
        }
    }

    public enum QrzAnswer { Registered, NotRegistered, CouldNotAsk }

    public sealed class QrzResult
    {
        public QrzAnswer Answer;
        public XElement Record;         // the QRZ record when Registered, otherwise null
    }

    // DOES QRZ.COM KNOW THIS CALLSIGN? Asked ONCE per callsign per session, and the one answer is
    // shared by the green patch in the decoded text and the card behind the right-click.
    //
    // ONCE, because the decoder offers the same callsign over and over - he sends it at the start of
    // every over and the other station reads it back - and each of those must not be a trip to QRZ.
    // The card then opens instantly, because the answer is already here.
    //
    // "COULD NOT ASK" IS NEVER REMEMBERED. No network for a minute must not leave a callsign blue for
    // the rest of the evening; the next time it is decoded it is asked again. Only QRZ's real
    // answers - registered, or not - are kept.
    public static class QrzLookup
    {
        static readonly System.Net.Http.HttpClient Http =
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // AN ANSWER IS KEPT ONLY WHILE ITS CALLSIGN IS STILL IN THE DECODED TEXT. The operator's idea,
        // and better than any fixed limit: the window already throws its oldest text away once it
        // grows long, and a callsign gone from the window can no longer be right-clicked, so there is
        // nothing left to remember it for. See Forget, called as the window trims and clears. The
        // list can therefore never hold more callsigns than the window can show, however long the
        // decoder runs. Text that has merely scrolled out of sight is still in the window - he can
        // scroll back and right-click it - so it is kept.
        static readonly object Gate = new object();
        static readonly System.Collections.Generic.Dictionary<string, Task<QrzResult>> Asked =
            new System.Collections.Generic.Dictionary<string, Task<QrzResult>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The callsign has left the decoded text; its answer is no longer needed.</summary>
        public static void Forget(string call)
        {
            if (string.IsNullOrWhiteSpace(call)) return;
            lock (Gate) Asked.Remove(call.Trim().ToUpperInvariant());
        }

        /// <summary>The decoded text was cleared; nothing in it needs an answer any more.</summary>
        public static void ForgetAll()
        {
            lock (Gate) Asked.Clear();
        }

        // One login for the whole session, shared by every lookup running at once. QRZ keys last
        // hours; when one expires the lookup is asked again once with a fresh key.
        static string _sessionKey;
        static Task<string> _loggingIn;

        /// <summary>Is there a QRZ login in Options to ask with? Without one nothing can be checked.</summary>
        public static bool HasLogin
        {
            get
            {
                return !string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_username)
                       && !string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_password);
            }
        }

        public static Task<QrzResult> CheckAsync(string call)
        {
            call = (call ?? string.Empty).Trim().ToUpperInvariant();
            lock (Gate)
            {
                Task<QrzResult> asked;
                if (Asked.TryGetValue(call, out asked)) return asked;
                asked = AskAsync(call);
                Asked[call] = asked;
                return asked;
            }
        }

        static async Task<QrzResult> AskAsync(string call)
        {
            QrzResult result;
            try
            {
                result = await FindAsync(call);

                // A stroke call often has no record of its own - BW/JA1APE is filed under JA1APE.
                string bare = HolyParser.Services.getBareCallsign(call);
                if (result.Answer == QrzAnswer.NotRegistered
                    && !string.IsNullOrEmpty(bare)
                    && !string.Equals(bare, call, StringComparison.OrdinalIgnoreCase))
                    result = await FindAsync(bare);
            }
            catch (Exception swallowed)
            {
                Log.Swallow(swallowed);
                result = new QrzResult { Answer = QrzAnswer.CouldNotAsk };
            }

            if (result.Answer == QrzAnswer.CouldNotAsk)
                lock (Gate) Asked.Remove(call);

            return result;
        }

        static async Task<string> SessionKeyAsync()
        {
            Task<string> login;
            lock (Gate)
            {
                if (!string.IsNullOrEmpty(_sessionKey)) return _sessionKey;
                if (_loggingIn == null) _loggingIn = Helper.LoginToQRZAsync();
                login = _loggingIn;
            }

            string key = await login;
            lock (Gate)
            {
                if (ReferenceEquals(_loggingIn, login)) _loggingIn = null;   // a failed login is tried again next time
                if (!string.IsNullOrEmpty(key)) _sessionKey = key;
            }
            return key;
        }

        static async Task<QrzResult> FindAsync(string call)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string key = await SessionKeyAsync();
                if (string.IsNullOrEmpty(key)) return new QrzResult { Answer = QrzAnswer.CouldNotAsk };

                string url = "https://xmldata.qrz.com/xml/current/?s=" + key
                             + ";callsign=" + Uri.EscapeDataString(call);
                string xml = await Task.Run(() => Http.GetStringAsync(url));

                XDocument doc = XDocument.Parse(xml);
                XNamespace ns = doc.Root.GetDefaultNamespace();

                XElement record = doc.Root.Element(ns + "Callsign");
                if (record != null) return new QrzResult { Answer = QrzAnswer.Registered, Record = record };

                XElement session = doc.Root.Element(ns + "Session");
                string error = session == null ? null : (string)session.Element(ns + "Error");
                bool keyGone = session == null || session.Element(ns + "Key") == null;

                if (!string.IsNullOrEmpty(error) && error.StartsWith("Not found", StringComparison.OrdinalIgnoreCase))
                    return new QrzResult { Answer = QrzAnswer.NotRegistered };

                // An expired or refused key: log in again and ask once more.
                if (keyGone && attempt == 0)
                {
                    lock (Gate) { if (_sessionKey == key) _sessionKey = null; }
                    continue;
                }

                return new QrzResult { Answer = QrzAnswer.CouldNotAsk };
            }

            return new QrzResult { Answer = QrzAnswer.CouldNotAsk };
        }
    }
}
