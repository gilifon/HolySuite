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
    // "There" is by time: both readers time a letter by its end, and for the same letter the two times
    // were measured within 0.03 s of each other (see CwTimeLine), so SameLetterSeconds is plenty.
    //
    // CALLSIGNS, AS IN THE OTHER BOXES (his request): a word shaped like a callsign gets the blue patch, is
    // checked on QRZ.com by itself and turns green if QRZ knows it; double-click logs it, right-click shows
    // its QRZ card. Same test and colours as CwDecodedText. A word is judged only once the network has
    // settled every letter in it (and a space has closed it), so the call checked is the CORRECTED one; its
    // grey letters are left out of it - grey means the network heard nothing there.
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
        }

        readonly RichTextBox _box;
        readonly Paragraph _paragraph;
        readonly List<Entry> _entries = new List<Entry>();
        readonly List<KeyValuePair<string, double>> _netWaiting = new List<KeyValuePair<string, double>>();
        readonly List<double> _netGaps = new List<double>();      // when the network heard a word gap, the last minute
        bool _changed;

        public Control Box { get { return _box; } }

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
            QrzLookup.ForgetAll();
            _changed = true;
        }

        /// <summary>One of Plain's letters (or " " between words), in the order Plain printed them.</summary>
        public void AddPlain(string text, double t)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool space = text == " ";
            if (space && (_entries.Count == 0 || _entries[_entries.Count - 1].Text == " ")) return;
            var e = new Entry { Text = text, T = space ? double.NaN : t, Run = new Run(text) };
            _paragraph.Inlines.Add(e.Run);
            _entries.Add(e);
            if (_entries.Count > MostLetters)
            {
                var gone = new List<string>();
                for (int i = 0; i < 500; i++)
                {
                    _paragraph.Inlines.Remove(_entries[i].Run);
                    if (_entries[i].Call != null) gone.Add(_entries[i].Call);
                }
                _entries.RemoveRange(0, 500);
                // Its QRZ answer goes with it, unless the same call is still further down.
                foreach (string call in gone)
                    if (!_entries.Exists(x => x.Call == call)) QrzLookup.Forget(call);
            }
            _changed = true;
        }

        /// <summary>One of the network's letters; held until the stretch it is in has been read whole.</summary>
        public void AddNet(string letter, double t)
        {
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
                    if (netGap && spaces == 0)
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

        static bool NetBacked(Entry e)
        {
            return e.Settled && !e.Doubted && !double.IsNaN(e.T);
        }

        // Every word closed by a space and with all its letters settled: a callsign or not?
        void JudgeWords()
        {
            int i = 0;
            while (i < _entries.Count)
            {
                if (_entries[i].Text == " ") { i++; continue; }
                int j = i;
                bool settled = true;
                var text = new System.Text.StringBuilder();
                for (; j < _entries.Count && _entries[j].Text != " "; j++)
                {
                    if (!_entries[j].Settled) settled = false;
                    if (!_entries[j].Doubted) text.Append(_entries[j].Text);
                }
                if (j < _entries.Count && settled)
                {
                    string word = text.ToString();
                    string call = word.IndexOf('<') < 0 && CwDecodedText.IsOfferableCall(word) ? word : null;
                    if (call != _entries[i].Call) Paint(i, j, call);
                }
                i = j;
            }
        }

        void Paint(int from, int to, string call)
        {
            _entries[from].Call = call;
            for (int k = from + 1; k < to; k++) _entries[k].Call = null;
            var runs = new List<Run>();
            for (int k = from; k < to; k++)
            {
                Entry e = _entries[k];
                Run r = e.Run;
                runs.Add(r);
                if (call != null)
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
        public void Show()
        {
            if (!_changed) return;
            _changed = false;
            if (_box.VerticalOffset + _box.ViewportHeight >= _box.ExtentHeight - 30) _box.ScrollToEnd();
        }
    }
}
