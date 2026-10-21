using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HolyLogger
{
    // MIX: PLAIN AT ONCE, CORRECTED BY THE NETWORK (2026-10-17, his idea, after seeing DeepCW do it).
    //
    // Plain is quick and often right; our network is slower and better on the hard parts. So Plain
    // types every letter the moment it is read, in ordinary weight, and when the network has read that
    // stretch (some seconds later - see CwDeepDecoder) each letter is settled:
    //
    //   the network read the same letter there        -> it turns bold: confirmed
    //   the network read a different letter there      -> Plain's is replaced by the network's, bold
    //   the network read a letter Plain missed         -> it is put in, bold - but only where Plain heard
    //                                                     something close by (NearPlainSeconds), because
    //                                                     on an empty band the network invents letters
    //   the network read nothing there                 -> Plain's turns GREY: probably noise (his choice,
    //                                                     2026-10-17). Grey, not removed: the network does
    //                                                     miss real letters - on his IC-7610 recordings it
    //                                                     found fewer words than Plain (27 of 34 vs 32) - so
    //                                                     a grey letter is doubtful, not wrong
    //   not read by the network yet                    -> ordinary black
    //
    // HIDDEN INSTEAD OF GREY, if he ticks it (Options > General > CW Decoder, 2026-10-20): on a QSO with one
    // strong and one very weak station the grey letters were most of the screen - noise read as E and T in the
    // gaps. A word left with no letter showing takes its space with it, and a line with nothing showing its
    // line break, so the junk does not leave holes either. Nothing is thrown away: untick it and they are back.
    //
    // "There" is by time: both readers time a letter by its end, and for the same letter the two times
    // were measured within 0.03 s of each other (see CwTimeLine), so SameLetterSeconds is plenty.
    //
    // CALLSIGNS, AS IN THE OTHER BOXES (his request): a word shaped like a callsign gets the blue patch, is
    // checked on QRZ.com by itself and turns green if QRZ knows it; double-click logs it, right-click shows
    // its QRZ card. Same test and colours as CwDecodedText.
    //
    // JUDGED AT ONCE ON PLAIN'S WORD, AND AGAIN AFTER THE NETWORK (his rule, 2026-10-17). Plain is right
    // most of the time, and an operator should not wait seconds for a call it already has: so a word is
    // judged the moment a space closes it, and checked on QRZ then. When the network has read it, it is
    // judged again on the corrected word - grey letters left out, grey meaning the network heard nothing
    // there - and the patch moves to the corrected call, or goes if it is no callsign after all.
    //
    // A NEW LINE FOR EVERY TRANSMISSION, as in the other boxes (agreed long ago; Mix lacked it - his screen,
    // 2026-10-17). The same rules as CwDecodedText, judged by the letters' own times instead of the clock:
    // after a prosign that ends a turn (<AR> <KN> <SK> <AS>) at once; after a K or BK standing alone, when
    // the next letter comes from somebody else (another note or speed) or 2 s later; and after a pause of
    // PauseInDits dits when the next letter comes from somebody else. A line break is also a word gap.
    //
    // THE NETWORK'S WORD GAPS (his choice, 2026-10-17). Plain's spaces split "LY2PX" into "L Y2PX", and Y2PX
    // was offered as a call. Between two letters the network has settled (bold), its word gaps are taken
    // instead of Plain's: a space Plain put where the network heard none is taken out, and one the network
    // heard where Plain had none is put in. Next to a grey letter Plain's spacing stays.
    public sealed class CwMixText
    {
        const double SameLetterSeconds = 0.12;
        const double NearPlainSeconds = 1.0;
        const int MostLetters = 4000;
        static readonly Brush DoubtInk = Brushes.Gray;
        static readonly Brush DoubtOnPatch = Frozen(Color.FromRgb(0xA8, 0xC8, 0xE8));

        static Brush Frozen(Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
        }

        /// <summary>A callsign the operator double-clicked, ready for the log.</summary>
        public event Action<string> CallsignChosen;

        sealed class Entry
        {
            public string Text;
            public double T;              // NaN for a word space
            public Run Run;
            public bool Settled, FromNet, Doubted;   // Doubted: grey - a network letter just over the edge of the next stretch may still claim it
            public string Call;                      // on a word's first letter: the callsign the word is shown as, or null
            public string Judged;                    // on a word's first letter: the word as it read when last judged
            public string SplitSeen;                 // on a word's first letter: the word as it read when last looked at for 5NN, QSB ...
            public LineBreak Break;                  // a new transmission: Text is " " (a word gap), Run is empty
        }

        readonly RichTextBox _box;
        readonly Paragraph _paragraph;
        readonly List<Entry> _entries = new List<Entry>();
        readonly List<KeyValuePair<string, double>> _netWaiting = new List<KeyValuePair<string, double>>();
        readonly List<double> _netGaps = new List<double>();      // when the network heard a word gap, the last minute
        bool _changed;

        public Control Box { get { return _box; } }

        bool _hide;

        /// <summary>Hide the letters the network did not confirm (grey ones), instead of showing them grey.</summary>
        public bool HideUnconfirmed
        {
            get { return _hide; }
            set { if (_hide == value) return; _hide = value; ApplyHide(); }
        }

        // Every letter, space and line break shown or not, by the rule above.
        void ApplyHide()
        {
            bool wordShown = false, lineShown = false;
            foreach (Entry e in _entries)
            {
                if (e.Break != null)
                {
                    bool show = !_hide || lineShown;
                    if (show && e.Break.Parent == null) _paragraph.Inlines.InsertAfter(e.Run, e.Break);
                    else if (!show && e.Break.Parent != null) _paragraph.Inlines.Remove(e.Break);
                    lineShown = false; wordShown = false;
                    continue;
                }
                if (e.Text == " ")
                {
                    string want = !_hide || wordShown ? " " : string.Empty;
                    if (e.Run.Text != want) e.Run.Text = want;
                    wordShown = false;
                    continue;
                }
                string text = _hide && e.Doubted ? string.Empty : e.Text;
                if (e.Run.Text != text) e.Run.Text = text;
                if (text.Length > 0) { wordShown = true; lineShown = true; }
            }
            _changed = true;
        }

        public CwMixText()
        {
            _box = new RichTextBox
            {
                FontSize = 18,
                FontFamily = new FontFamily("Consolas"),
                IsReadOnly = true,
                IsTabStop = false,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.White,
                Foreground = Brushes.Black
            };
            _paragraph = new Paragraph { Margin = new Thickness(0) };
            _box.Document = new FlowDocument(_paragraph) { PagePadding = new Thickness(4, 2, 4, 2), LineHeight = double.NaN };
            CwStripes.Apply(_box.Document);
            _box.MouseDoubleClick += OnDoubleClick;
            _box.ContextMenuOpening += OnContextMenuOpening;
        }

        public void Clear()
        {
            _paragraph.Inlines.Clear();
            _entries.Clear();
            _netWaiting.Clear();
            _netGaps.Clear();
            _seen.Clear();
            _breakNext = false; _handOver = false; _newLetter = false;
            _lastLetterT = double.NaN; _lastNote = 0; _lastWpm = 0;
            QrzLookup.ForgetAll();
            _changed = true;
        }

        static readonly string[] ProsignHandOver = { "<KN>", "<AR>", "<SK>", "<AS>" };
        const double SilenceThatProvesOver = 2.0;
        bool _breakNext, _handOver, _newLetter;
        double _lastLetterT = double.NaN, _lastNote, _lastWpm;

        /// <summary>One of Plain's letters (or " " between words), in the order Plain printed them, with the
        /// note and speed Plain hears as it prints it.</summary>
        public void AddPlain(string text, double t, double note, double wpm)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool space = text == " ";
            if (space)
            {
                if (_entries.Count == 0 || _entries[_entries.Count - 1].Text == " ") return;
                string closed = LastWord();
                if (closed == "K" || closed == "BK") _handOver = true;
            }
            else
            {
                if (NewTransmission(t, note, wpm)) AddBreak();
                _breakNext = Array.IndexOf(ProsignHandOver, text) >= 0;
                _handOver = false;
                _lastLetterT = t;
                if (note > 0) _lastNote = note;
                if (wpm > 0) _lastWpm = wpm;
                _newLetter = true;
            }
            var e = new Entry { Text = text, T = space ? double.NaN : t, Run = new Run(text) };
            _paragraph.Inlines.Add(e.Run);
            _entries.Add(e);
            if (_entries.Count > MostLetters)
            {
                var gone = new List<string>();
                for (int i = 0; i < 500; i++)
                {
                    _paragraph.Inlines.Remove(_entries[i].Run);
                    if (_entries[i].Break != null && _entries[i].Break.Parent != null) _paragraph.Inlines.Remove(_entries[i].Break);
                    if (_entries[i].Call != null) gone.Add(_entries[i].Call);
                }
                _entries.RemoveRange(0, 500);
                // Its QRZ answer goes with it, unless the same call is still further down.
                foreach (string call in gone)
                    if (!_entries.Exists(x => x.Call == call)) QrzLookup.Forget(call);
            }
            _changed = true;
            if (space) JudgeWords();          // Plain has just closed a word
        }

        bool NewTransmission(double t, double note, double wpm)
        {
            if (_entries.Count == 0 || _entries[_entries.Count - 1].Break != null) return false;
            if (_breakNext) return true;
            if (double.IsNaN(_lastLetterT) || double.IsNaN(t)) return false;
            bool somebodyElse = (_lastNote > 0 && note > 0 && Math.Abs(note - _lastNote) >= CwDecodedText.DifferentNoteHz)
                || (_lastWpm > 0 && wpm > 0 && Math.Abs(wpm - _lastWpm) >= _lastWpm * CwDecodedText.DifferentSpeedFraction);
            double gap = t - _lastLetterT;
            if (_handOver && (somebodyElse || gap >= SilenceThatProvesOver)) return true;
            double dit = 1.2 / (_lastWpm > 0 ? _lastWpm : 20);
            return somebodyElse && gap >= dit * CwDecodedText.PauseInDits;
        }

        // The last word Plain closed (its letters since the last word gap).
        string LastWord()
        {
            var sb = new System.Text.StringBuilder();
            for (int k = _entries.Count - 1; k >= 0 && _entries[k].Text != " "; k--) sb.Insert(0, _entries[k].Text);
            return sb.ToString();
        }

        void AddBreak()
        {
            if (_entries.Count > 0 && _entries[_entries.Count - 1].Text == " " && _entries[_entries.Count - 1].Break == null)
            {
                // Plain's own word gap just before: it becomes the line break.
                Entry last = _entries[_entries.Count - 1];
                last.Break = new LineBreak();
                last.Run.Text = string.Empty;
                _paragraph.Inlines.InsertAfter(last.Run, last.Break);
                return;
            }
            var e = new Entry { Text = " ", T = double.NaN, Run = new Run(string.Empty), Settled = true, Break = new LineBreak() };
            _paragraph.Inlines.Add(e.Run);
            _paragraph.Inlines.Add(e.Break);
            _entries.Add(e);
        }

        /// <summary>One of the network's letters; held until the stretch it is in has been read whole.</summary>
        public void AddNet(string letter, double t)
        {
            // The BT sign (-...-) is "=" from Plain and "<BT>" from the network - one sign, two spellings. Measured
            // on his 20 m session (2026-10-21, mix_truth.py): every "=" Plain read right was "replaced" by "<BT>".
            // Written Plain's way, it is confirmed like any other letter.
            if (letter == "<BT>") letter = "=";
            _netWaiting.Add(new KeyValuePair<string, double>(letter, t));
        }

        /// <summary>The network has finished reading [from, to): settle every letter in it.</summary>
        public void Covered(double from, double to)
        {
            var net = new List<KeyValuePair<string, double>>();
            for (int i = _netWaiting.Count - 1; i >= 0; i--)
                if (_netWaiting[i].Value < to) { net.Add(_netWaiting[i]); _netWaiting.RemoveAt(i); }
            net.Sort((a, b) => a.Value.CompareTo(b.Value));

            foreach (var n in net)
            {
                if (n.Key == " ") { _netGaps.Add(n.Value); continue; }
                Entry best = null;
                double bestGap = SameLetterSeconds;
                bool plainNear = false;
                foreach (Entry e in _entries)
                {
                    if (double.IsNaN(e.T) || e.FromNet) continue;
                    double gap = Math.Abs(e.T - n.Value);
                    if (gap <= NearPlainSeconds) plainNear = true;
                    if ((!e.Settled || e.Doubted) && gap <= bestGap) { bestGap = gap; best = e; }
                }
                if (best != null)
                {
                    if (best.Text != n.Key) { best.Text = n.Key; best.Run.Text = n.Key; }
                    best.Run.FontWeight = FontWeights.Bold;
                    best.Settled = true;
                    if (best.Doubted) { best.Doubted = false; best.Run.ClearValue(TextElement.ForegroundProperty); }
                    if (best.Run.Tag != null) best.Run.Foreground = CwDecodedText.CallsignInk;
                    _changed = true;
                }
                else if (plainNear)
                    Insert(n.Key, n.Value);
            }

            // Plain's letters in the stretch that the network did not read: doubtful.
            foreach (Entry e in _entries)
            {
                if (e.Settled || e.FromNet || double.IsNaN(e.T) || e.T < from || e.T >= to) continue;
                e.Run.Foreground = DoubtInk;
                e.Doubted = true;
                e.Settled = true;
                _changed = true;
            }

            TakeNetSpacing(from, to);
            JudgeWords();
            if (_hide) ApplyHide();
        }

        // Between each two network-settled letters, the second of them in [from, to): a space exactly
        // where the network heard a word gap.
        void TakeNetSpacing(double from, double to)
        {
            while (_netGaps.Count > 0 && _netGaps[0] < to - 60) _netGaps.RemoveAt(0);
            Entry prev = null;
            int prevAt = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                if (e.Text == " ") continue;
                if (prev != null && e.T >= from && e.T < to && NetBacked(prev) && NetBacked(e))
                {
                    bool netGap = _netGaps.Exists(g => g > prev.T && g < e.T);
                    int spaces = i - prevAt - 1;                         // Plain's spaces between them
                    bool lineBreak = false;
                    for (int k = prevAt + 1; k < i; k++) if (_entries[k].Break != null) lineBreak = true;
                    if (lineBreak) { }                                  // a new transmission stays one
                    else if (netGap && spaces == 0)
                    {
                        var space = new Entry { Text = " ", T = double.NaN, Run = new Run(" "), Settled = true };
                        _paragraph.Inlines.InsertBefore(e.Run, space.Run);
                        _entries.Insert(i, space);
                        i++;
                        _changed = true;
                    }
                    else if (!netGap && spaces > 0)
                    {
                        for (int k = 0; k < spaces; k++) _paragraph.Inlines.Remove(_entries[prevAt + 1 + k].Run);
                        _entries.RemoveRange(prevAt + 1, spaces);
                        i -= spaces;
                        _changed = true;
                    }
                }
                prev = e;
                prevAt = i;
            }
        }

        readonly SeenCalls _seen = new SeenCalls();

        double WordTime(int from, int to)
        {
            double t = double.NaN;
            for (int k = from; k < to; k++) if (!double.IsNaN(_entries[k].T)) t = _entries[k].T;
            return t;
        }

        static bool NetBacked(Entry e)
        {
            return e.Settled && !e.Doubted && !double.IsNaN(e.T);
        }

        // THE THREE-LETTER WORDS OF A CW CONTACT STAND ALONE (his rule, 2026-10-21): "5NN5NQSB" is "5NN 5N QSB" -
        // 5NN and QSB are whole words of their own, so wherever one comes out glued to other letters (a word gap
        // not heard) it gets a space before and after. He knows it can now and then be wrong, and takes that
        // small risk. Kept safe: only these words, which do not turn up inside ordinary English; never inside a
        // word of digits only (a serial number); and a Q-code is never taken out of a callsign - it can end one
        // (DL1QSL). 5NN, 599 and TNX are taken out even of a word shaped like a call: no callsign holds them,
        // and "5NN5NQSB" itself is call-shaped (5N is Nigeria) - it got the blue patch on his screen.
        static readonly string[] NeverInCalls = { "5NN", "599", "TNX" };
        static readonly string[] QCodes =
        {
            "QRG", "QRL", "QRM", "QRN", "QRO", "QRP", "QRQ", "QRS", "QRT", "QRU", "QRV", "QRX", "QRZ",
            "QSB", "QSK", "QSL", "QSO", "QSP", "QSY", "QTH", "QTR"
        };

        // Only the last RecentEntries letters and spaces: older words are settled and were looked at already.
        const int RecentEntries = 400;

        void SplitStandAloneWords()
        {
            int first = Math.Max(0, _entries.Count - RecentEntries);
            while (first > 0 && _entries[first - 1].Text != " ") first--;     // from the start of a word
            for (int i = first; i < _entries.Count; )
            {
                if (_entries[i].Text == " " || IsProsign(_entries[i])) { i++; continue; }
                int j = i;
                while (j < _entries.Count && _entries[j].Text != " " && !IsProsign(_entries[j])) j++;
                if (j < _entries.Count) i = SplitWord(i, j);   // only a word already closed
                else i = j;
            }
        }

        // The word [from, to): spaces put in around every stand-alone word glued inside it. -> where the
        // next word starts.
        int SplitWord(int from, int to)
        {
            // The letters as they show (a hidden grey letter is not there for the eye), and where each sits.
            var shown = new System.Text.StringBuilder();
            var at = new List<int>();
            for (int k = from; k < to; k++)
            {
                if (_hide && _entries[k].Doubted) continue;
                foreach (char c in _entries[k].Text) { shown.Append(c); at.Add(k); }
            }
            string word = shown.ToString();
            // ONCE PER WORD, AND CHEAP FIRST (his frame log, 2026-10-21: the waterfall got worse the longer it ran).
            // Every word on the screen was looked at again whenever a word ended, each time with the callsign test
            // and its country lookup, so the work grew with the text. A word is now looked at again only when it
            // reads differently, and the callsign test runs only where a Q-code is actually inside it.
            if (_entries[from].SplitSeen == word) return to;
            _entries[from].SplitSeen = word;
            if (word.Length <= 3 || !HasAny(word, NeverInCalls) && !HasAny(word, QCodes)) return to;
            bool digits = true;
            foreach (char c in word) if (!char.IsDigit(c)) digits = false;
            if (digits) return to;

            // The cuts, as positions in the word, then as entries a space goes in front of: first round the
            // words no call holds, then round the Q-codes in each piece that is not a callsign.
            var cuts = new SortedSet<int>();
            CutAround(word, 0, word.Length, NeverInCalls, cuts);
            int start = 0;
            foreach (int end in new List<int>(cuts) { word.Length })
            {
                string piece = word.Substring(start, end - start);
                if (HasAny(piece, QCodes) && !CwDecodedText.IsOfferableCall(piece)) CutAround(word, start, end, QCodes, cuts);
                start = end;
            }
            if (cuts.Count == 0) return to;

            var before = new SortedSet<int>();
            foreach (int p in cuts) before.Add(at[p]);
            int added = 0;
            foreach (int k in before.Reverse())
            {
                if (k <= from) continue;
                var space = new Entry { Text = " ", T = double.NaN, Run = new Run(" "), Settled = true };
                _paragraph.Inlines.InsertBefore(_entries[k].Run, space.Run);
                _entries.Insert(k, space);
                added++;
            }
            if (added > 0)
            {
                _entries[from].Judged = null;
                _changed = true;
            }
            return to + added;
        }

        static bool HasAny(string word, string[] words)
        {
            foreach (string w in words) if (word.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // Positions before and after every one of `words` found in word[from, to) - the ends of the stretch excepted.
        static void CutAround(string word, int from, int to, string[] words, SortedSet<int> cuts)
        {
            foreach (string w in words)
                for (int p = word.IndexOf(w, from, to - from, StringComparison.Ordinal); p >= 0 && p + w.Length <= to;
                     p = p + 1 < to ? word.IndexOf(w, p + 1, to - p - 1, StringComparison.Ordinal) : -1)
                {
                    if (p > from) cuts.Add(p);
                    if (p + w.Length < to) cuts.Add(p + w.Length);
                }
        }

        // Every word closed by a space - Plain's as it stands, or the network's correction of it once it
        // has one: a callsign or not? A word already judged as it now reads is not asked again.
        void JudgeWords()
        {
            SplitStandAloneWords();
            int i = 0;
            while (i < _entries.Count)
            {
                if (_entries[i].Text == " ") { i++; continue; }
                // A PROSIGN IS A WORD OF ITS OWN, as in CwDecodedText: "LY2PX<AR>" came in glued (no gap heard
                // before the <AR>) and the call lost its patch.
                if (IsProsign(_entries[i]))
                {
                    if (_entries[i].Run.Tag != null) Paint(i, i + 1, null);
                    i++;
                    continue;
                }
                int j = i;
                var text = new System.Text.StringBuilder();
                var all = new System.Text.StringBuilder();
                for (; j < _entries.Count && _entries[j].Text != " " && !IsProsign(_entries[j]); j++)
                {
                    if (!_entries[j].Doubted) text.Append(_entries[j].Text);
                    all.Append(_entries[j].Text);
                    if (j > i) _entries[j].Judged = null;       // was the start of a word before two were joined
                }
                string word = text.ToString();
                if (j < _entries.Count && word != _entries[i].Judged)
                {
                    _entries[i].Judged = word;
                    // A call sent twice with no gap: tried with its grey letters too, as one grey letter in one
                    // copy ("IT9ECYIT9ECY" with the first Y grey) would otherwise hide the repeat.
                    string call = word.IndexOf('<') >= 0 ? null
                        : CwDecodedText.IsOfferableCall(word) ? word
                        : CwDecodedText.RepeatedCall(word) ?? CwDecodedText.RepeatedCall(all.ToString());
                    double at = WordTime(i, j);
                    if (call != null) { _seen.Saw(call, at); if (call != _entries[i].Call) Paint(i, j, call); }
                    else
                    {
                        // A call heard a moment ago, inside this word: only its own letters get the patch.
                        string inside = _seen.Inside(word, at) ?? _seen.Inside(all.ToString(), at);
                        int start = inside == null ? -1 : all.ToString().IndexOf(inside, StringComparison.Ordinal);
                        int a = j, b = j;
                        if (start >= 0)
                        {
                            int pos = 0;
                            for (int k = i; k < j; k++)
                            {
                                int len = _entries[k].Text.Length;
                                if (pos + len > start && pos < start + inside.Length) { if (a == j) a = k; b = k + 1; }
                                pos += len;
                            }
                        }
                        else if (inside != null) { a = i; b = j; }
                        Paint(i, j, inside, a, b);
                    }
                }
                i = j;
            }
        }

        static bool IsProsign(Entry e)
        {
            return e.Text.Length > 1 && e.Text[0] == '<';
        }

        // The word [from, to) shows the callsign `call` (or none): the letters [paintFrom, paintTo) get the
        // patch - all of them unless told otherwise - and the rest lose it.
        void Paint(int from, int to, string call, int paintFrom = -1, int paintTo = -1)
        {
            if (paintFrom < 0) { paintFrom = from; paintTo = to; }
            _entries[from].Call = call;
            for (int k = from + 1; k < to; k++) { _entries[k].Call = null; _entries[k].Judged = null; }
            var runs = new List<Run>();
            for (int k = from; k < to; k++)
            {
                Entry e = _entries[k];
                Run r = e.Run;
                runs.Add(r);
                if (call != null && k >= paintFrom && k < paintTo)
                {
                    r.Background = CwDecodedText.CallsignPatch;
                    r.Foreground = e.Doubted ? DoubtOnPatch : CwDecodedText.CallsignInk;
                    r.Tag = call;
                    r.Cursor = System.Windows.Input.Cursors.Hand;
                    r.ToolTip = "Double-click to put " + call + " in the DX Callsign box.\nRight-click to check it on QRZ.com.";
                }
                else
                {
                    r.ClearValue(TextElement.BackgroundProperty);
                    if (e.Doubted) r.Foreground = DoubtInk; else r.ClearValue(TextElement.ForegroundProperty);
                    r.Tag = null;
                    r.ClearValue(FrameworkContentElement.CursorProperty);
                    r.ToolTip = null;
                }
            }
            _changed = true;
            if (call != null && QrzLookup.HasLogin) MarkIfRegistered(runs, call);
        }

        // Asks QRZ.com in the background; green if it knows the call (and the word still shows that call).
        async void MarkIfRegistered(List<Run> runs, string call)
        {
            try
            {
                QrzResult result = await QrzLookup.CheckAsync(call);
                if (result.Answer != QrzAnswer.Registered) return;
                foreach (Run r in runs)
                {
                    if (r.Tag == null || r.Tag.ToString() != call) continue;
                    r.Background = CwDecodedText.RegisteredPatch;
                    r.ToolTip = call + " is registered on QRZ.com.\nDouble-click to put it in the DX Callsign box.\nRight-click to see it.";
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        Run RunUnderMouse(Point at)
        {
            var position = _box.GetPositionFromPoint(at, false);
            return position == null ? null : position.Parent as Run;
        }

        void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                Run run = RunUnderMouse(e.GetPosition(_box));
                if (run == null || run.Tag == null) return;
                var handler = CallsignChosen;
                if (handler == null) return;
                handler(run.Tag.ToString());
                e.Handled = true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                Run run = RunUnderMouse(System.Windows.Input.Mouse.GetPosition(_box));
                if (run == null || run.Tag == null) return;
                string call = run.Tag.ToString().Trim().ToUpperInvariant();
                if (call.Length == 0) return;
                e.Handled = true;
                QrzCallsignWindow.Open(call, Window.GetWindow(_box));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // A letter Plain missed, put in by its time among Plain's.
        void Insert(string letter, double t)
        {
            int at = _entries.FindIndex(e => !double.IsNaN(e.T) && e.T > t);
            var entry = new Entry { Text = letter, T = t, Run = new Run(letter) { FontWeight = FontWeights.Bold }, Settled = true, FromNet = true };
            if (at < 0)
            {
                _paragraph.Inlines.Add(entry.Run);
                _entries.Add(entry);
            }
            else
            {
                // Before a space that comes just ahead of the later letter, so it joins the word it belongs to.
                if (at > 0 && _entries[at - 1].Text == " " && at - 2 >= 0 && !double.IsNaN(_entries[at - 2].T)
                    && t - _entries[at - 2].T < _entries[at].T - t) at--;
                _paragraph.Inlines.InsertBefore(_entries[at].Run, entry.Run);
                _entries.Insert(at, entry);
            }
            _changed = true;
        }

        /// <summary>Keeps the newest text in sight. On the screen timer.</summary>
        // A NEW LETTER IS ALWAYS SEEN (his rule, 2026-10-21): it used to follow only while the view was at the
        // bottom, so after he scrolled up new letters were typed out of sight. Now a new letter from Plain brings
        // the last line back into view; the network settling older letters does not move the view.
        public void Show()
        {
            if (!_changed) return;
            _changed = false;
            bool newLetter = _newLetter;
            _newLetter = false;
            if (newLetter || _box.VerticalOffset + _box.ViewportHeight >= _box.ExtentHeight - 30) _box.ScrollToEnd();
        }
    }
}
