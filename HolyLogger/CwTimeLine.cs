using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace HolyLogger
{
    // PLAIN AND NET ON ONE TIME LINE (2026-10-17, his request: "no matter what is decoded it is printed
    // exactly in the same position in the text row"). The view in Both.
    //
    // Every letter goes in the column for the moment it was sent, in both boxes alike, so the two
    // readings of the same sound sit one exactly above the other and a letter one of them missed leaves
    // a hole. The two boxes always hold the same lines, and scroll together.
    //
    // A COLUMN IS SlotDits DITS of the speed Plain hears when the line is begun - about one letter, so
    // ordinary sending reads as ordinary text. A short letter close behind another (E E) finds its
    // column taken and moves one along. NEVER FURTHER: moving each letter on to the next free column was
    // tried first, and a burst of noise letters from Plain pushed everything after it many columns right
    // of its time - so the real letters after the noise no longer sat over Net's. Now a letter whose
    // column and the next are both taken takes its own column over: the later letter wins, and every
    // letter stays within one column of its time. A gap longer than a whole line starts the next line at
    // the letter after it, rather than printing empty lines through a silence.
    //
    // Both readers are timed by the END of the letter: Plain by its last mark, Net by where it says the
    // letter less 0.8 dit (see CwDeepDecoder.LetterTimed). Net's letters arrive some seconds after Plain's
    // and fill in under them; a letter only Net heard gets a line of its own if no line covers its time.
    //
    // SAME LETTER, SAME MOMENT, SAME COLUMN (his check, 2026-10-17). The two readers' times for the same
    // letter were measured within 0.03 s of each other, yet 5 of 35 letters still landed a column apart:
    // a letter on the border between two columns, or Plain's letter moved along by a noise letter in its
    // column. So a Net letter within SnapSeconds of the SAME letter from Plain, one column at most from
    // its own, goes in exactly that letter's column. Snapping to any Plain letter nearby was tried first
    // and was wrong: in a noisy stretch Plain has a letter every few hundredths of a second, and Net's
    // were scattered among them.
    //
    // THE LINES ARE STRIPED (his request): white and pale blue in turn, the same in both boxes, so the
    // eye finds the Net line that goes with a Plain line at once.
    public sealed class CwTimeLine
    {
        public const double SlotDits = 7;
        const int MostLines = 120;
        const int PlainRow = 0, NetRow = 1;
        const double SnapSeconds = 0.1;

        static readonly Brush StripeA = Brushes.White;
        static readonly Brush StripeB = Frozen(Color.FromRgb(0xDC, 0xEB, 0xFA));

        static Brush Frozen(Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
        }

        sealed class Line
        {
            public double T0, Slot;
            public char[][] Rows;
            public double[] PlainTimes;          // when each of Plain's letters was sent, by its first column; NaN where none
            public Paragraph[] Paras;            // this line in each box
            public bool Changed;
            public double End { get { return T0 + Slot * Rows[0].Length; } }
        }

        readonly List<Line> _lines = new List<Line>();
        readonly RichTextBox _plain, _net;
        bool _dirty, _restripe, _syncing;
        double _charWidth;

        public Control PlainBox { get { return _plain; } }
        public Control NetBox { get { return _net; } }

        public CwTimeLine()
        {
            _plain = MakeBox();
            _net = MakeBox();
            _plain.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) => Follow(_plain, _net)));
            _net.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) => Follow(_net, _plain)));
        }

        // NO WRAPPING: each line of letters is one line on screen, or the two boxes would fall out of
        // step. The page is made far wider than the box for that; nothing is ever written out there.
        static RichTextBox MakeBox()
        {
            var box = new RichTextBox
            {
                FontSize = 18,
                FontFamily = new FontFamily("Consolas"),
                IsReadOnly = true,
                IsTabStop = false,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Background = Brushes.White,
                Foreground = Brushes.Black
            };
            box.Document = new FlowDocument { PagePadding = new Thickness(4, 2, 4, 2), PageWidth = 4000, LineHeight = double.NaN };
            box.Document.Blocks.Clear();
            CwStripes.Apply(box.Document);      // same row height as the other boxes; its lines are striped by paragraph
            return box;
        }

        // One box scrolled by hand: the other goes to the same place, so the lines stay paired.
        void Follow(TextBoxBase from, TextBoxBase to)
        {
            if (_syncing) return;
            _syncing = true;
            try { to.ScrollToVerticalOffset(from.VerticalOffset); }
            finally { _syncing = false; }
        }

        public void Clear()
        {
            _lines.Clear();
            _plain.Document.Blocks.Clear();
            _net.Document.Blocks.Clear();
            _dirty = true;
        }

        public void AddPlain(string letter, double seconds, double wpm) { Add(PlainRow, letter, seconds, wpm, 0); }

        public void AddNet(string letter, double seconds, double wpm)
        {
            if (letter == " ") return;            // the gaps show by themselves here
            if (!PutUnderPlain(letter, seconds)) Add(NetRow, letter, seconds, wpm, 0);
        }

        bool PutUnderPlain(string letter, double t)
        {
            Line best = null;
            int bestCol = -1;
            double bestGap = SnapSeconds;
            foreach (Line line in _lines)
            {
                if (line.End < t - 1 || line.T0 > t + 1) continue;
                int own = (int)((t - line.T0) / line.Slot);
                for (int c = Math.Max(0, own - 1); c <= own + 1 && c < line.PlainTimes.Length; c++)
                {
                    double gap = Math.Abs(line.PlainTimes[c] - t);
                    if (!(gap <= bestGap) || !SameLetter(line.Rows[PlainRow], c, letter)) continue;
                    bestGap = gap; best = line; bestCol = c;
                }
            }
            if (best == null || bestCol + letter.Length > best.Rows[NetRow].Length) return false;
            for (int k = 0; k < letter.Length; k++) if (best.Rows[NetRow][bestCol + k] != ' ') return false;
            for (int k = 0; k < letter.Length; k++) best.Rows[NetRow][bestCol + k] = letter[k];
            best.Changed = true;
            _dirty = true;
            return true;
        }

        static bool SameLetter(char[] row, int col, string letter)
        {
            if (col + letter.Length > row.Length) return false;
            for (int k = 0; k < letter.Length; k++) if (row[col + k] != letter[k]) return false;
            return true;
        }

        void Add(int row, string letter, double t, double wpm, int depth)
        {
            if (string.IsNullOrEmpty(letter) || depth > 3) return;
            Line line = LineAt(t, wpm);
            if (line == null) return;

            int need = letter.Length, cols = line.Rows[row].Length;
            int col = Math.Max(0, (int)((t - line.T0) / line.Slot));
            if (col + need > cols)
            {
                // No room left on this line: the start of the next.
                Add(row, letter, line.End + 1e-6, wpm, depth + 1);
                return;
            }
            int at = col;
            if (!Free(line.Rows[row], col, need) && col + 1 + need <= cols && Free(line.Rows[row], col + 1, need)) at = col + 1;
            for (int k = 0; k < need; k++)
            {
                line.Rows[row][at + k] = letter[k];
                if (row == PlainRow) line.PlainTimes[at + k] = k == 0 ? t : double.NaN;
            }
            line.Changed = true;
            _dirty = true;
        }

        static bool Free(char[] row, int col, int need)
        {
            for (int k = 0; k < need; k++) if (row[col + k] != ' ') return false;
            return true;
        }

        Line LineAt(double t, double wpm)
        {
            int i = 0;
            while (i < _lines.Count && _lines[i].End <= t) i++;
            if (i < _lines.Count && _lines[i].T0 <= t) return _lines[i];

            Line prev = i > 0 ? _lines[i - 1] : null;
            Line next = i < _lines.Count ? _lines[i] : null;
            double slot = SlotDits * 1.2 / (wpm >= 5 && wpm <= 60 ? wpm : 20);
            int cols = Columns();
            double t0 = t - slot * 0.5;          // half a column of room for a reader that times it a shade early
            if (prev != null && t - prev.End < slot * cols) { t0 = prev.End; slot = prev.Slot; }
            if (next != null)
            {
                int room = (int)Math.Floor((next.T0 - t0) / slot);
                if (room < 1 || t0 + room * slot <= t) return next;
                cols = Math.Min(cols, room);
            }
            var line = new Line { T0 = t0, Slot = slot, Rows = new[] { Blank(cols), Blank(cols) }, PlainTimes = new double[cols], Changed = true };
            for (int c = 0; c < cols; c++) line.PlainTimes[c] = double.NaN;
            line.Paras = new[] { NewPara(), NewPara() };
            if (next != null)
            {
                _plain.Document.Blocks.InsertBefore(next.Paras[PlainRow], line.Paras[PlainRow]);
                _net.Document.Blocks.InsertBefore(next.Paras[NetRow], line.Paras[NetRow]);
            }
            else
            {
                _plain.Document.Blocks.Add(line.Paras[PlainRow]);
                _net.Document.Blocks.Add(line.Paras[NetRow]);
            }
            _lines.Insert(i, line);
            while (_lines.Count > MostLines)
            {
                _plain.Document.Blocks.Remove(_lines[0].Paras[PlainRow]);
                _net.Document.Blocks.Remove(_lines[0].Paras[NetRow]);
                _lines.RemoveAt(0);
            }
            _restripe = true;
            _dirty = true;
            return line;
        }

        static Paragraph NewPara()
        {
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Inlines.Add(new Run(string.Empty));
            return p;
        }

        static char[] Blank(int n)
        {
            var a = new char[n];
            for (int i = 0; i < n; i++) a[i] = ' ';
            return a;
        }

        // How many letters fit across the box. The same for both: they are the same width.
        int Columns()
        {
            if (_charWidth <= 0)
            {
                var ft = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(_plain.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    _plain.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(_plain).PixelsPerDip);
                _charWidth = ft.WidthIncludingTrailingWhitespace;
            }
            double width = _plain.ActualWidth - 8 - SystemParameters.VerticalScrollBarWidth - 6;
            int cols = (int)(width / _charWidth);
            return cols >= 10 ? cols : 60;
        }

        /// <summary>Puts what changed on screen. On the screen timer.</summary>
        public void Show()
        {
            if (!_dirty) return;
            _dirty = false;
            bool atEnd = _plain.VerticalOffset + _plain.ViewportHeight >= _plain.ExtentHeight - 2;
            for (int i = 0; i < _lines.Count; i++)
            {
                Line line = _lines[i];
                if (_restripe)
                {
                    Brush stripe = i % 2 == 0 ? StripeA : StripeB;
                    line.Paras[PlainRow].Background = stripe;
                    line.Paras[NetRow].Background = stripe;
                }
                if (!line.Changed) continue;
                line.Changed = false;
                ((Run)line.Paras[PlainRow].Inlines.FirstInline).Text = new string(line.Rows[PlainRow]).TrimEnd();
                ((Run)line.Paras[NetRow].Inlines.FirstInline).Text = new string(line.Rows[NetRow]).TrimEnd();
            }
            _restripe = false;
            if (atEnd) { _plain.ScrollToEnd(); _net.ScrollToEnd(); }
        }

        // For the research harness: the lines as text.
        internal string Text(bool net)
        {
            var sb = new System.Text.StringBuilder();
            foreach (Line line in _lines) sb.AppendLine(new string(line.Rows[net ? NetRow : PlainRow]).TrimEnd());
            return sb.ToString();
        }
    }
}
