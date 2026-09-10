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
        }

        /// <summary>Adds newly decoded text, colouring any callsign as its last letter arrives.</summary>
        public void Append(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            foreach (char c in text)
            {
                if (c == ' ' || c == '\r' || c == '\n')
                {
                    FinishWord();
                    _paragraph.Inlines.Add(new Run(c.ToString()));
                    continue;
                }

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

        // A word has ended: decide whether it is a callsign worth offering.
        void FinishWord()
        {
            var word = _word;
            _word = null;
            if (word == null || word.Text.Length == 0) return;

            string call = word.Text.Trim();
            if (!CallsignIdentity.LooksLikeCallsign(call)) return;

            // Never his own, and never a stroke variant of it either - a man does not log himself.
            string mine = null;
            try { mine = Properties.Settings.Default.my_callsign; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (!string.IsNullOrWhiteSpace(mine) && CallsignIdentity.Same(call, mine)) return;

            word.Foreground = CallsignInk;
            word.Background = CallsignPatch;
            word.FontWeight = FontWeights.Bold;
            word.Tag = call;
            word.Cursor = System.Windows.Input.Cursors.Hand;
            word.ToolTip = "Double-click to put " + call + " in the DX Callsign box";
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
