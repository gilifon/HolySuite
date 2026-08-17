using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using DXCCManager;
using HolyParser;

namespace HolyLogger
{
    // BOLD INSIDE A BOUND LINE OF TEXT. A TextBlock's Text is one flat string, so a sentence that
    // needs one phrase to stand out has to be built from Runs - and a DataTemplate cannot do that from
    // a binding. This carries the sentence as markup instead: everything between ** comes out bold,
    // everything else inherits whatever the TextBlock was given.
    public static class RichNote
    {
        public static readonly DependencyProperty MarkupProperty =
            DependencyProperty.RegisterAttached("Markup", typeof(string), typeof(RichNote),
                new PropertyMetadata(null, OnMarkupChanged));

        public static void SetMarkup(DependencyObject d, string value) { d.SetValue(MarkupProperty, value); }
        public static string GetMarkup(DependencyObject d) { return (string)d.GetValue(MarkupProperty); }

        private static void OnMarkupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var block = d as TextBlock;
            if (block == null) return;

            block.Inlines.Clear();
            string text = e.NewValue as string;
            if (string.IsNullOrEmpty(text)) return;

            // {{r:...}} and {{g:...}} are the verdict words - "do not agree" and "agree". They carry
            // the whole meaning of the line and are the reason the operator either works through a
            // pile by hand or ticks it in one go, so they are coloured as well as bold.
            foreach (Match m in ColouredPart.Matches(text))
            {
                if (m.Groups["plain"].Success) { AddBoldMarkup(block, m.Groups["plain"].Value); continue; }

                var run = new Run(m.Groups["text"].Value) { FontWeight = FontWeights.Bold };
                run.Foreground = m.Groups["colour"].Value == "r" ? Red() : Green();
                block.Inlines.Add(run);
            }
        }

        private static readonly Regex ColouredPart =
            new Regex(@"\{\{(?<colour>[rg]):(?<text>[^}]*)\}\}|(?<plain>(?:(?!\{\{[rg]:).)+)",
                      RegexOptions.Singleline);

        // Everything between ** comes out bold; the rest inherits.
        private static void AddBoldMarkup(TextBlock block, string text)
        {
            bool bold = false;
            foreach (string part in text.Split(new[] { "**" }, StringSplitOptions.None))
            {
                if (part.Length > 0)
                {
                    var run = new Run(part);
                    if (bold) run.FontWeight = FontWeights.Bold;
                    block.Inlines.Add(run);
                }
                bold = !bold;
            }
        }

        // Readable on both schemes: the light colours are too dark to see on the dark background and
        // the dark ones wash out on the light.
        private static Brush Red()
        {
            return Frozen(ThemeManager.IsDark ? "#FF6B6B" : "#C62828");
        }

        private static Brush Green()
        {
            return Frozen(ThemeManager.IsDark ? "#54D66A" : "#2E7D32");
        }

        private static Brush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }

    // Checks a whole log and offers corrections, one tick at a time.
    //
    // A logged QSO keeps the country that was worked out when it was saved, and that answer can be years
    // out of date: K9W was logged as United States in 2013 because its prefix says so, while Club Log
    // records that for those two weeks K9W was Wake Island, and the QSO's own Name field says "2013 WAKE
    // ISLAND COMMEMORATIVE". Nothing in the program ever re-asked the question. This window asks it for
    // every QSO, on the QSO's own date, and shows what it finds.
    //
    // Two rules govern everything here:
    //   * Nothing is written until the operator ticks a row and presses Apply, and the log is copied to a
    //     .bak first.
    //   * A finding that cannot be corrected automatically (a callsign Club Log lists as never valid, an
    //     impossible date) is reported as FYI with no tick box, rather than guessed at.
    public partial class LogVerifierWindow : Window
    {
        // One thing found wrong with one QSO. Apply is the only mutable part, so INotifyPropertyChanged
        // exists purely to keep the button's count honest.
        public class Finding : INotifyPropertyChanged
        {
            public QSO Qso;
            public string Field;          // which QSO field the fix would write
            public string NewValue;       // the value to write (Field-specific)
            public string Program;      // for Field == "Activity": IOTA / SOTA / POTA / WWFF
            public int NewCq, NewItu;     // zones that travel with a country correction (0 = leave alone)
            public string NewContinent;

            // THE ENTITY ITSELF, which travels with the country name and used not to. Correcting the
            // name alone left the QSO holding the old entity - the thing every count of countries is
            // actually made of - so a log could read "Puerto Rico" while still counting as the USA.
            public string NewDxcc;

            // THE ENTITY NUMBER, which is what every count of countries is actually made of. NewDxcc
            // above writes the legacy DXCC string; this writes the dxcc column the statistics read.
            // Correcting the name and the string but not this left the log reading one country and
            // counting as another - which is exactly the fault Verify exists to find.
            public int NewCode;

            // THE ONE FINDING THAT TAKES A QSO OUT INSTEAD OF PUTTING A FIELD RIGHT. A contact held
            // twice is not a wrong value anywhere - both copies are correct - so there is nothing to
            // write; the second copy simply should not be there. Ticking it removes it.
            public bool Deletes;

            // The duplicate group this copy belongs to: which contact stays, and whether the copies
            // carry different comments - in which case nothing is removed until the operator has been
            // shown them and said which to keep.
            public DupGroup Group;

            public string Call { get; set; }
            public string Time { get; set; }
            public string DateText { get; set; }
            public string Problem { get; set; }
            public string Current { get; set; }
            public string Suggested { get; set; }
            public string Evidence { get; set; }
            public bool Fixable { get; set; }

            // The QSO field a report-only finding is about. Findings that CAN be fixed name their field
            // in Field, because that is what tells the fix where to go; these have nothing to write, so
            // they carry the name for the reader alone.
            public string Where;

            // WHICH FIELD the pair of halves beside it is about. The table shows only what the problem
            // touches, so this is the column that says what "Now / Would become" is naming - without it
            // a row reading "20M" over "40M" could be about anything.
            public string FieldLabel
            {
                get
                {
                    if (!string.IsNullOrEmpty(Where)) return Where;
                    switch (Field)
                    {
                        case "DXCall": return "Callsign";
                        case "Band": return "Band";
                        case "Continent": return "Continent";
                        case "Activity": return Program;
                        case "CountryName": return "Country";
                        case "Country": return "Country";
                        default: return "—";
                    }
                }
            }

            private bool apply;
            public bool Apply
            {
                get { return apply; }
                set
                {
                    if (apply == value) return;
                    apply = value && Fixable;
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Apply"));
                    if (ApplyChanged != null) ApplyChanged();
                }
            }

            public Action ApplyChanged;
            public event PropertyChangedEventHandler PropertyChanged;
        }

        // ONE KIND OF PROBLEM, and how many QSOs have it. The frame at the top of the window is a list
        // of these, because the first decision an operator makes is not about a QSO, it is about a kind:
        // put the wrong countries right, leave the spellings alone. Ticking one here ticks every QSO of
        // that kind below, which can then be overruled row by row.
        public class ProblemKind : INotifyPropertyChanged
        {
            public string Name { get; set; }
            public int Count { get; set; }
            public bool Fixable { get; set; }      // false = nothing to propose, so nothing to tick

            public string CountText { get { return Count.ToString("N0"); } }

            // WHAT THIS KIND ACTUALLY MEANS, beside its own count, so nobody has to guess it from the
            // rows. Every kind has a sentence - the ones the program can put right and the ones only the
            // operator can judge - and for the second sort the sentence says so in as many words ("only
            // you can fix it", "only you know which band it was"), which is also the answer to why that
            // tick box is grey. It replaced one sentence that appeared on every hand-judged kind alike
            // and merely repeated the window's own heading.
            public string HandNote
            {
                get { return Explain(Name); }
            }

            // THE COUNTRY KINDS READ ALIKE UNTIL YOU KNOW WHICH HALF IS WRONG. Each of them shows a
            // country name in red and another in green, so on the screen they look like the same
            // complaint three times; what actually differs is whether the COUNTRY CODE - the thing an
            // award is counted from - is wrong, right, or right with the wrong words beside it.
            //
            // "Country code", never "entity number", because Country Code is what the column at the top
            // of the table below is called and what the operator therefore calls it.
            private static string Explain(string kind)
            {
                // "Comment holds a WWFF reference" - the program's name is part of the kind, so this one
                // is matched on its opening words rather than whole.
                if (kind != null && kind.StartsWith("Comment holds a", StringComparison.Ordinal))
                    return "The comment holds a reference AND other text, so only you can separate them";

                switch (kind)
                {
                    // the callsign
                    case "No callsign":
                        return "The QSO has no callsign at all, so nothing else about it can be checked";
                    case "Damaged callsign":
                        return "Stray characters are stuck to the front or back of the callsign; the fix will take them off";
                    case "Callsign holds odd characters":
                        return "Odd characters inside the callsign — only you can fix it";

                    // date and time
                    case "Unreadable date":
                        return "What stands in the date field is not a date";
                    case "Date in the future":
                        return "The QSO is dated later than today";
                    case "Impossible date":
                        return "The QSO is dated before amateur radio existed";
                    case "Impossible time":
                        return "What stands in the time field is not a time of day";

                    // band, frequency, mode
                    case "Frequency is on no amateur band":
                        return "That frequency belongs to no amateur band, so the band cannot be worked out from it";
                    case "No band":
                        return "The band is empty, and the frequency logged says which one it was";
                    case "No band and no frequency":
                        return "Neither was logged, so only you know which band it was";
                    case "Band does not match the frequency":
                        return "The two disagree, and the frequency is the one the radio measured";
                    case "No mode":
                        return "No mode was logged";

                    // the worked station's grid
                    case "DX Locator is wrong":
                        return "The grid is not a grid: two letters, two digits, and usually two more letters";

                    // the comment
                    case "Reference sitting in the comment":
                        return "A park or summit reference is sitting in the comment; it belongs in its own field";

                    // Club Log
                    case "Club Log: operation did not count":
                        return "Club Log says this operation earned no award credit on that date";

                    // the country
                    // The two country files are named in bold: they are what the operator is being
                    // asked to weigh, and in a line of italic blue they would otherwise be just words.
                    case CountryBothAgree:
                        return "**cty.dat** and **Club Log** {{g:agree}}. The country in the log is wrong — safe to accept HolyLogger's recommendation";
                    case CountryNeedsDecision:
                        return "**cty.dat** and **Club Log** {{r:do not agree}}. The country in the log may be wrong — press ? on a row before you tick it";
                    case "Different country":
                        return "The country code in the log is wrong, so the QSO counts as the wrong country";
                    case "No country code":
                        return "The fix will add the missing country code";
                    case "No country":
                        return "The fix will add the country and its code";
                    case "Wrong country name":
                        return "The country code is right — only the name belongs to another country";
                    case "Country spelled differently":
                        return "The same country in other words — nothing counts wrongly";
                    case "Wrong continent":
                        return "The continent does not match the country the QSO counts for";
                }
                return "";
            }

            private bool @checked;
            public bool Checked
            {
                get { return @checked; }
                set
                {
                    if (@checked == value) return;
                    @checked = value && Fixable;
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Checked"));
                    if (CheckedChanged != null) CheckedChanged(this);
                }
            }

            // Highlighted while this kind is the one the table is showing, so it is obvious that the
            // table below is a slice and which slice it is.
            private bool selected;
            public bool Selected
            {
                get { return selected; }
                set
                {
                    if (selected == value) return;
                    selected = value;
                    Raise("Selected");
                }
            }

            private void Raise(string name)
            {
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name));
            }

            public Action<ProblemKind> CheckedChanged;
            public event PropertyChangedEventHandler PropertyChanged;
        }

        // ── THE TABLE: ONE QSO, TWO ROWS, A COLUMN PER FIELD ─────────────────────────────────────
        //
        // The table is shaped like the log itself rather than like a list of complaints. Each QSO gets
        // one entry drawn as two halves: the log as it stands on top, what it would become underneath.
        // Only the cells actually in question carry colour, so the eye lands on the fault without a
        // column having to spell it out in words - which is why there is no "what is wrong" column any
        // more. The heading and the red cell say it between them.
        //
        // And only the columns that some finding TOUCHES are built at all: a scan that turned up no
        // band trouble shows no Band column. That is what keeps a table this wide readable.
        private sealed class Cell : INotifyPropertyChanged
        {
            // PROPERTIES, NOT FIELDS. WPF binds to properties only, and says nothing when it cannot
            // find one - the whole table came up blank because these two were fields.
            public string Current { get; set; }
            public bool Wrong { get; set; }

            public Cell() { Current = ""; }

            // TYPEABLE. The lower half is a text box, so a fault the program cannot answer is still
            // settled here rather than sending the operator off to another window: they read the red
            // value, type the right one underneath, tick the row and press Fix.
            //
            // UserEdited separates what they typed from what the program suggested. Both are written,
            // but a suggestion is written by the finding that made it - which knows to carry the entity
            // number and the zones along with a country - while a typed value is written to that one
            // field and nothing else. Confusing the two would let a hand-typed country quietly leave
            // the QSO counting as the old one.
            private string proposed = "";
            public string Proposed
            {
                get { return proposed; }
                set
                {
                    string v = value ?? "";
                    if (proposed == v) return;
                    proposed = v;
                    UserEdited = true;
                    Raise("Proposed"); Raise("ThenBg"); Raise("ThenWeight"); Raise("NoteVisible"); Raise("EchoVisible");
                    if (Changed != null) Changed();
                }
            }

            public bool UserEdited;
            public Action Changed;

            // WHAT IS WRONG, for a fault with no answer to offer. A red cell on its own says only that
            // something is the matter - "Club Log lists this as never valid" is not something anyone
            // can read out of a red callsign. It sits in the lower half as a grey note and vanishes the
            // moment the operator types there, so the cell is still theirs to fill.
            public string Note { get; set; }
            public Visibility NoteVisible
            {
                get
                {
                    return !string.IsNullOrEmpty(Note) && proposed.Length == 0
                        ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            // THE LOWER HALF REPEATS WHAT IS NOT CHANGING. With only the mended cells filled in, the
            // second line was a row of blanks with one green word in it, and the operator had to look up
            // to the line above to see WHICH QSO it belonged to. Now every other cell shows its own value
            // again, in grey: the lower half reads as the whole contact as it would stand after the fix.
            //
            // Grey and behind the text box, exactly like the note - so it is plainly not a proposal (no
            // green), it is not something that gets written, and it disappears the moment anything is
            // typed there, leaving the cell the operator's.
            public Visibility EchoVisible
            {
                get
                {
                    return proposed.Length == 0 && string.IsNullOrEmpty(Note) && !string.IsNullOrEmpty(Current)
                        ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            // Filling a suggestion in from a finding is not the operator typing.
            public void Suggest(string value)
            {
                proposed = value ?? "";
                Raise("Proposed"); Raise("ThenBg"); Raise("ThenWeight"); Raise("EchoVisible");
            }

            private void Raise(string name)
            {
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name));
            }
            public event PropertyChangedEventHandler PropertyChanged;

            // Filled only where there is something to say. Everywhere else the cell is transparent and
            // the row reads as ordinary log data, which is exactly what it is.
            public Brush NowBg { get { return Wrong ? WrongBg : Brushes.Transparent; } }
            public Brush ThenBg { get { return Proposed.Length > 0 ? RightBg : Brushes.Transparent; } }
            public Brush NowFg { get { return Wrong ? WrongFg : DimFg; } }
            public FontWeight ThenWeight { get { return Proposed.Length > 0 ? FontWeights.Bold : FontWeights.Normal; } }

            // A proper alarm red, not the pale wash it started as - a fault has to look like one.
            private static readonly Brush WrongBg = Freeze(Color.FromRgb(0xFF, 0x8A, 0x8A));
            private static readonly Brush RightBg = Freeze(Color.FromRgb(0xA5, 0xE8, 0xA8));
            private static readonly Brush WrongFg = Freeze(Color.FromRgb(0x5D, 0x00, 0x00));
            private static readonly Brush DimFg = Freeze(Color.FromRgb(0x33, 0x33, 0x33));
            private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        }

        private sealed class FixRow : INotifyPropertyChanged
        {
            public QSO Qso;
            public readonly List<Finding> Findings = new List<Finding>();
            // A PROPERTY, for the same reason as Cell.Current: the cell templates bind Cells[Date] and
            // so on, and a binding to a field finds nothing and reports nothing.
            public Dictionary<string, Cell> Cells { get; private set; }

            public FixRow() { Cells = new Dictionary<string, Cell>(); }

            // A row can be put right either because the program proposed something or because the
            // operator typed something. Recomputed as they type, so the tick box comes alive under
            // their hands rather than staying grey until the window is reopened.
            private bool fixable;
            public bool Fixable
            {
                get { return fixable; }
                set
                {
                    if (fixable == value) return;
                    fixable = value;
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Fixable"));
                }
            }

            public void Recompute()
            {
                bool any = false;
                foreach (Finding f in Findings) if (f.Fixable) { any = true; break; }
                if (!any)
                    foreach (var kv in Cells)
                        if (kv.Value.UserEdited && kv.Value.Proposed.Trim().Length > 0) { any = true; break; }
                Fixable = any;
            }

            private bool apply;
            public bool Apply
            {
                get { return apply; }
                set
                {
                    if (apply == value) return;
                    apply = value && Fixable;
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Apply"));
                    if (ApplyChanged != null) ApplyChanged();
                }
            }

            public Action ApplyChanged;
            public event PropertyChangedEventHandler PropertyChanged;

            public bool Has(string kind)
            {
                foreach (Finding f in Findings)
                    if (string.Equals(f.Problem, kind, StringComparison.Ordinal)) return true;
                return false;
            }
        }

        // Every column the table can show, in the order the log table shows them. Date, Time and
        // Callsign always appear - they are how an operator recognises the contact; the rest appear
        // only when something is wrong with them.
        private static readonly string[] AlwaysColumns = { "Date", "Time", "Callsign" };
        private static readonly string[] IssueColumns =
        {
            "Band", "Mode", "Country", "Country Code", "Continent", "DX Locator", "Comment",
            "IOTA", "SOTA", "POTA", "WWFF"
        };

        private readonly ObservableCollection<FixRow> _rows = new ObservableCollection<FixRow>();

        private readonly List<QSO> _qsos;
        private readonly string _logName;
        private readonly ObservableCollection<Finding> _findings = new ObservableCollection<Finding>();
        private readonly ObservableCollection<ProblemKind> _kinds = new ObservableCollection<ProblemKind>();

        // True while a kind's tick box is pushing its state down onto the rows, so the rows pushing
        // their own count back up cannot turn into a loop.
        private bool _syncingKind;

        // A callsign may legitimately hold letters, digits and strokes - anything else is damage, and the
        // log has at least one row that arrived from an import with rubbish bytes in front of the call.
        private static readonly Regex LegalCallChars = new Regex("^[A-Z0-9/]+$", RegexOptions.Compiled);

        // Maidenhead: field, square, and optionally subsquare and extended square.
        private static readonly Regex LegalLocator =
            new Regex("^[A-R]{2}[0-9]{2}([A-X]{2}([0-9]{2})?)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // A Holyland square (one letter, two digits, two letters - K07YZ) is not a Maidenhead locator, but
        // Holyland contest QSOs in this log do carry one in the DX locator field. It is deliberate data,
        // not damage, so it is left in peace.
        private static readonly Regex HolylandSquare =
            new Regex("^[A-Z][0-9]{2}[A-Z]{2}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // True when the QSO already carries a reference somewhere. A comment is only worth offering to
        // move when the proper fields are still empty - otherwise the reference is already recorded and
        // the comment is just an old note about it.
        private static bool HasAnyActivityReference(QSO q)
        {
            return !string.IsNullOrWhiteSpace(q.Iota)
                || !string.IsNullOrWhiteSpace(q.SotaRef)
                || !string.IsNullOrWhiteSpace(q.PotaRef)
                || !string.IsNullOrWhiteSpace(q.WwffRef)
                || !string.IsNullOrWhiteSpace(q.Sig);
        }

        // Which program a piece of a COMMENT belongs to - a stricter question than asking it of a box
        // the operator filled in on purpose.
        //
        // Three of the four formats cannot be mistaken for ordinary writing: an island reference is a
        // continent code and three digits, a summit always has a stroke, a nature one always has "FF-".
        // A park reference is just letters, a hyphen and four digits - and so is "FT-1000", which is a
        // radio, not a park. This log contains exactly that. So a park is only believed when the
        // comment says somewhere that it is talking about one.
        private static string ProgramInComment(string piece, string wholeComment)
        {
            string program = MainWindow.ProgramOf(piece);
            if (program != "POTA") return program;
            string all = (wholeComment ?? string.Empty).ToUpperInvariant();
            return all.Contains("POTA") || all.Contains("PARK") ? program : null;
        }

        private static bool IsCallChar(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '/';
        }

        public LogVerifierWindow(IEnumerable<QSO> qsos, string logName = null)
        {
            InitializeComponent();
            _qsos = (qsos ?? Enumerable.Empty<QSO>()).Where(q => q != null).ToList();
            _logName = string.IsNullOrWhiteSpace(logName) ? "" : logName.Trim();
            Title = string.IsNullOrEmpty(_logName) ? "Log Fixer" : "Log Fixer — " + _logName;

            // The same header look as the QSO log, the cluster and the Logs window, from the one place
            // that defines it: the LogHeaderBg palette token with black text. Its background is a
            // DynamicResource, so switching colour scheme or editing Customize Colors repaints this
            // header live too.
            FindingsGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();

            FindingsGrid.ItemsSource = _findings;

            // Where the operator put this window, and how big they made it, is remembered - the table
            // below is the whole point of the window and how much of it you can see is a personal choice.
            // The XAML height is only the FIRST-EVER size; it is deliberately tall enough for six QSOs,
            // because each one is drawn as two half rows and at the old height only three fitted.
            double firstTime = Height;
            WindowBounds.Attach(this, "LogFixer");
            if (Math.Abs(Height - firstTime) < 0.5) ShrinkToFitScreen();
        }

        // ── OPENED ON DIFFERENCES SOMEBODY ELSE FOUND ────────────────────────────────────────────
        //
        // Same window, same table, same Fix - but the rows come from a comparison this window did not
        // make. The first user of it is LoTW: a confirmation whose station IS in the log but whose band,
        // mode or date does not agree with what was uploaded. The log is on top in red, LoTW's version
        // underneath in green, and the operator ticks the ones where LoTW is right.
        private LogVerifierWindow(List<Finding> prepared, string title, string headline, string summary)
        {
            InitializeComponent();
            _qsos = new List<QSO>();
            _logName = "";
            Title = title;

            FindingsGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();
            FindingsGrid.ItemsSource = _findings;

            _prepared = prepared ?? new List<Finding>();
            _preparedHeadline = headline;
            _preparedSummary = summary;

            // Its own remembered placement: this is a different job from the scan, opened at a different
            // moment, and the size that suits one need not suit the other.
            double firstTime = Height;
            WindowBounds.Attach(this, "LogFixerLotw");
            if (Math.Abs(Height - firstTime) < 0.5) ShrinkToFitScreen();
        }

        // WHERE THE LOG AND LoTW DISAGREE ABOUT A CONTACT BOTH OF THEM HAVE.
        //
        // Each of these confirmations was matched to nothing, but its callsign IS in the log - so the
        // contact exists and one of the two records has a detail wrong. Adding it as a new QSO would
        // double-log it; that is why the missing ones were separated out before either was shown.
        //
        // The TIME is deliberately never proposed. LoTW carries the OTHER station's logged time, which
        // routinely differs from ours by a minute or two - the matcher ignores it for that very reason,
        // and offering to overwrite our own clock with theirs would be a correction in the wrong
        // direction. Band, mode, date and the country are offered.
        public static int ShowLotwDifferences(Window owner, IEnumerable<QSO> logQsos,
                                              IEnumerable<DataAccess.LotwConfirmation> nearMisses)
        {
            var qsos = (logQsos ?? Enumerable.Empty<QSO>()).Where(q => q != null && !string.IsNullOrWhiteSpace(q.DXCall)).ToList();
            var misses = (nearMisses ?? Enumerable.Empty<DataAccess.LotwConfirmation>())
                         .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Call)).ToList();
            if (qsos.Count == 0 || misses.Count == 0) return 0;

            var byCall = new Dictionary<string, List<QSO>>(StringComparer.OrdinalIgnoreCase);
            foreach (QSO q in qsos)
            {
                string key = CallsignIdentity.Base(q.DXCall.Trim());
                List<QSO> list;
                if (!byCall.TryGetValue(key, out list)) { list = new List<QSO>(); byCall[key] = list; }
                list.Add(q);
            }

            var findings = new List<Finding>();
            var taken = new HashSet<QSO>();
            int sameOnEveryField = 0;

            foreach (var c in misses)
            {
                List<QSO> candidates;
                if (!byCall.TryGetValue(CallsignIdentity.Base(c.Call.Trim()), out candidates)) continue;

                QSO q = BestMatch(candidates, c, taken);
                if (q == null) continue;

                string band = (c.Band ?? "").Trim().ToUpperInvariant();
                string mode = (c.Mode ?? "").Trim().ToUpperInvariant();
                string date = (c.QsoDate ?? "").Trim();

                bool any = false;
                if (band.Length > 0 && !string.Equals(Text(q.Band), band, StringComparison.OrdinalIgnoreCase))
                { findings.Add(Difference(q, "LoTW logged another band", "Band", Text(q.Band), band)); any = true; }

                if (mode.Length > 0 && !string.Equals(Text(q.Mode), mode, StringComparison.OrdinalIgnoreCase))
                { findings.Add(Difference(q, "LoTW logged another mode", "Mode", Text(q.Mode), mode)); any = true; }

                if (date.Length == 8 && !string.Equals(Text(q.Date), date, StringComparison.Ordinal))
                { findings.Add(Difference(q, "LoTW logged another date", "Date", FormatDate(q.Date), FormatDate(date))); any = true; }

                // THE COUNTRY, WHICH USED NOT TO BE COMPARED AT ALL. The log works the country out from
                // the callsign; LoTW is TOLD it by the operator, who had to declare where he was
                // transmitting from and prove it. When the two disagree the log is usually the one that
                // is wrong, and until now nothing said so - band and mode were checked and the entity,
                // which is what every count of countries is made of, was passed over in silence.
                //
                // JUDGED ON THE ENTITY NUMBER, NEVER THE NAME. LoTW writes "FED. REP. OF GERMANY" where
                // we write "Federal Republic of Germany"; comparing names would report thousands of
                // disagreements that are nothing but two spellings of one country. Both sides must have
                // a number - a log QSO with none is the plain scan's business, not LoTW's.
                if (c.DxccCode > 0 && q.DxccCode > 0 && c.DxccCode != q.DxccCode)
                {
                    // The NAME goes in the Country cell and the NUMBER in the Country Code cell beside
                    // it - the table already pairs the two for a country correction, so putting the
                    // number inside the name as well would print it twice.
                    Finding f = Difference(q, "LoTW logged another country", "Country",
                                           Text(q.Country), Text(c.Country));
                    // The number, continent and zones travel with the name, exactly as they do when the
                    // country databases propose a correction. A log left saying one country while
                    // counting as another is the fault this is here to end.
                    f.NewCode = c.DxccCode;
                    f.NewContinent = Text(c.Continent).Length > 0 ? Text(c.Continent).ToUpperInvariant() : null;
                    int cq, itu;
                    if (int.TryParse(Text(c.CqZone), out cq) && cq > 0) f.NewCq = cq;
                    if (int.TryParse(Text(c.ItuZone), out itu) && itu > 0) f.NewItu = itu;
                    findings.Add(f);
                    any = true;
                }

                if (any) taken.Add(q);
                else sameOnEveryField++;
            }

            if (findings.Count == 0) return 0;

            int rows = findings.Select(f => f.Qso).Distinct().Count();
            string headline = rows.ToString("N0") + (rows == 1 ? " QSO does not agree with LoTW" : " QSOs do not agree with LoTW");
            string summary = "Green is what LoTW holds. Tick the ones where LoTW is right and press Fix; "
                           + "leave the others and the log keeps what it has."
                           + (sameOnEveryField > 0
                                ? "  (" + sameOnEveryField.ToString("N0") + " more differ only in which of your "
                                  + "callsigns was used, which is not something to change from here.)"
                                : "");

            var win = new LogVerifierWindow(findings, "Log Fixer — where LoTW disagrees", headline, summary);
            if (owner != null) win.Owner = owner;
            win.ShowDialog();
            return rows;
        }

        // The QSO a confirmation is most likely to BE. Same date is the strongest sign, then the band,
        // then the mode; a QSO already claimed by another confirmation is passed over, so two
        // confirmations for the same station on the same day cannot both land on one QSO.
        // Which logged QSO a card is talking about. Only ever one on the SAME DAY.
        //
        // The cards that reach this window are now the ones whose station the log holds on that very
        // date, so a same-day QSO always exists. Without the restriction below, a card whose same-day
        // QSO had already been claimed by an earlier card would fall back to that station's QSO from
        // some other year and be reported as "logged another date" - years apart, about two contacts
        // that have nothing to do with each other. That is precisely the noise this pairing is meant to
        // avoid, so a candidate on another day is not considered at all.
        private static QSO BestMatch(List<QSO> candidates, DataAccess.LotwConfirmation c, HashSet<QSO> taken)
        {
            QSO best = null;
            int bestScore = -1;
            string cardDate = (c.QsoDate ?? "").Trim();
            foreach (QSO q in candidates)
            {
                if (taken.Contains(q)) continue;
                if (cardDate.Length == 8 && !string.Equals(Text(q.Date), cardDate, StringComparison.Ordinal))
                    continue;
                int score = 0;
                if (string.Equals(Text(q.Date), (c.QsoDate ?? "").Trim(), StringComparison.Ordinal)) score += 4;
                if (string.Equals(Text(q.Band), (c.Band ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) score += 2;
                if (string.Equals(Text(q.Mode), (c.Mode ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) score += 1;
                if (score > bestScore) { bestScore = score; best = q; }
            }
            return best;
        }

        // "?" ON A ROW: what each country database said about this callsign, on this QSO's own date.
        //
        // Worked out here and now rather than during the scan - it is two extra lookups for ONE
        // callsign, against 28,000 QSOs if the scan did it for every row on the chance of being asked.
        private void Btn_Why_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            var row = button == null ? null : button.Tag as FixRow;
            if (row == null || row.Qso == null) return;

            string call = Text(row.Qso.DXCall);
            if (call.Length == 0) return;

            CountryLookup.Explanation x;
            try { x = CountryLookup.Shared.Explain(call, CountryLookup.QsoDate(row.Qso.Date)); }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                HolyMessageBox.ShowWarning("The country databases could not be asked about " + call + ".",
                                           "Why this country?", this);
                return;
            }

            // Worded in CountryLookup.Explanation, not here: a report written about a log says exactly
            // this, and two places composing the same paragraph is how they come to differ.
            string text = x.Report(Text(row.Qso.Country), row.Qso.DxccCode, FormatDate(row.Qso.Date));

            HolyMessageBox.Show(text, "Why this country?", HolyMsgType.Info, this, 640);
        }

        private static Finding Difference(QSO q, string problem, string field, string current, string proposed)
        {
            Finding f = New(q, problem, current.Length == 0 ? "(empty)" : current, proposed, "LoTW");
            f.Field = field;
            f.NewValue = proposed;
            f.Fixable = true;
            return f;
        }

        // A first-open height of 1010 is taller than some screens. Nothing else in the window can be
        // trusted to notice - WPF will happily place a window whose bottom is off the desktop - so on the
        // first open only, the height is cut to the work area of the monitor this window landed on.
        // Screen.FromHandle, not SystemParameters.WorkArea: that one answers for the PRIMARY monitor only
        // and would give the wrong figure whenever HolyLogger is being run on the second screen.
        private void ShrinkToFitScreen()
        {
            SourceInitialized += (s, e) =>
            {
                try
                {
                    IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd == IntPtr.Zero) return;

                    var src = System.Windows.PresentationSource.FromVisual(this);
                    double sy = src != null && src.CompositionTarget != null
                        ? src.CompositionTarget.TransformToDevice.M22 : 1.0;
                    if (sy <= 0) sy = 1.0;

                    double usable = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea.Height / sy;
                    if (Height > usable - 20) Height = Math.Max(MinHeight, usable - 20);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            };
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // A window opened with findings already made does not scan anything: there is nothing to
            // look for, the differences were handed to it. Everything after the scan is the same code.
            if (_prepared != null) { ShowPrepared(); return; }

            TB_Header.Text = "Checking " + _qsos.Count.ToString("N0") + " QSOs…";
            TB_Summary.Text = "working…";
            await RunCheck();
        }

        // ── THE SAME TABLE, FILLED FROM SOMEWHERE ELSE ───────────────────────────────────────────
        //
        // The Fixer's SCAN has nothing to say about LoTW - it checks a QSO against the country
        // databases and against itself. Its TABLE has everything to say about it: the log on top, what
        // is proposed underneath, only the columns in question, a tick per row, one Fix that copies the
        // database first. So the LoTW differences are handed in as findings and the window skips
        // straight to drawing them, rather than a second window being written that looks like this one.
        private List<Finding> _prepared;
        private string _preparedHeadline;
        private string _preparedSummary;

        private void ShowPrepared()
        {
            // The paragraph under the heading belongs to the SCAN - "every QSO was checked against the
            // country databases", "where the program has no answer, type into the green row". None of it
            // is true here: nothing was scanned and every green cell already holds LoTW's own value.
            if (TB_Intro != null)
            {
                TB_Intro.Inlines.Clear();
                TB_Intro.Inlines.Add(new Run("These contacts are in your log AND in LoTW, but a detail does "
                    + "not agree. Red is what the log holds, green is what LoTW was sent. Tick the ones "
                    + "where LoTW is right and press "));
                TB_Intro.Inlines.Add(new Run("Fix selected")
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = ThemeManager.Brush("Danger")
                });
                TB_Intro.Inlines.Add(new Run(". Your whole database — every log in it — is copied first, "
                    + "so nothing here is final; you can view it in "));
                TB_Intro.Inlines.Add(new Run("File → Backups & Restore") { FontWeight = FontWeights.Bold });
                TB_Intro.Inlines.Add(new Run(". Anything you leave unticked stays exactly as it is."));
            }

            List<Finding> found = _prepared;
            foreach (Finding f in found) _findings.Add(f);

            List<string> columns = BuildRows(found);
            BuildColumns(columns);
            FindingsGrid.ItemsSource = _rows;
            BuildKinds(found);

            TB_Header.Text = _preparedHeadline ?? "";
            TB_Summary.Text = _preparedSummary ?? "";
            UpdateFixButton();
        }

        private async Task RunCheck()
        {
            _findings.Clear();

            List<QSO> snapshot = _qsos;
            // The country lookup parses two databases and every QSO is resolved twice, so the whole scan
            // runs off the UI thread - a 40,000-QSO log would otherwise freeze the window.
            List<Finding> found = await Task.Run(() => Scan(snapshot));

            // Before anything is drawn: the locators this machine cannot answer are asked of QRZ, so
            // the table appears with those suggestions already in it rather than with a button to go
            // and fetch them.
            await FillLocatorsFromQrz(found);

            foreach (Finding f in found) _findings.Add(f);

            List<string> columns = BuildRows(found);
            BuildColumns(columns);
            FindingsGrid.ItemsSource = _rows;
            BuildKinds(found);

            int suggested = found.Count(f => f.Fixable);
            TB_Header.Text = found.Count == 0
                ? "No problems found in " + _qsos.Count.ToString("N0") + " QSOs."
                : found.Count.ToString("N0") + " problem" + (found.Count == 1 ? "" : "s")
                  + " found in " + _qsos.Count.ToString("N0") + " QSOs";
            TB_Summary.Text = found.Count == 0
                ? "Nothing to fix."
                : suggested.ToString("N0") + " can be put right here, "
                  + (found.Count - suggested).ToString("N0") + " are for you to judge. "
                  + "Double-click a row to open the QSO.";
            UpdateFixButton();
        }

        private static List<Finding> Scan(List<QSO> qsos)
        {
            var findings = new List<Finding>();
            CountryLookup lookup = CountryLookup.Shared;
            DateTime today = DateTime.UtcNow.Date.AddDays(1);   // tomorrow: a QSO dated later cannot exist

            foreach (QSO q in qsos)
            {
                string call = (q.DXCall ?? string.Empty).Trim();
                string dateRaw = (q.Date ?? string.Empty).Trim();

                // --- the callsign itself -------------------------------------------------------------
                if (call.Length == 0)
                {
                    findings.Add(Fyi(q, "No callsign", "(empty)", "cannot be guessed", "the log", "Callsign"));
                    continue;   // nothing else can be judged without a callsign
                }

                if (!LegalCallChars.IsMatch(call.ToUpperInvariant()))
                {
                    // Junk at the front or the back is padding that arrived with an import and can be
                    // trimmed off with confidence. Junk in the MIDDLE is a note the operator squeezed
                    // into the callsign - "N1WON/P(KP2)" means he was in KP2 - and deleting the
                    // brackets would fuse it into nonsense, so that is only reported.
                    string trimmed = call.ToUpperInvariant().Trim();
                    while (trimmed.Length > 0 && !IsCallChar(trimmed[0])) trimmed = trimmed.Substring(1);
                    while (trimmed.Length > 0 && !IsCallChar(trimmed[trimmed.Length - 1]))
                        trimmed = trimmed.Substring(0, trimmed.Length - 1);

                    if (trimmed.Length >= 3 && LegalCallChars.IsMatch(trimmed))
                    {
                        Finding f = New(q, "Damaged callsign", call, trimmed, "illegal characters");
                        f.Field = "DXCall";
                        f.NewValue = trimmed;
                        f.Fixable = true;
                        findings.Add(f);
                    }
                    else
                    {
                        findings.Add(Fyi(q, "Callsign holds odd characters", call,
                                         "check what was really worked", "the log", "Callsign"));
                    }
                }

                // --- date and time ------------------------------------------------------------------
                DateTime when;
                bool dateOk = DateTime.TryParseExact(dateRaw, "yyyyMMdd", CultureInfo.InvariantCulture,
                                  DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when);
                if (!dateOk)
                    findings.Add(Fyi(q, "Unreadable date", dateRaw.Length == 0 ? "(empty)" : dateRaw,
                                     "a real date (YYYYMMDD)", "the log", "Date"));
                else if (when >= today)
                    findings.Add(Fyi(q, "Date in the future", when.ToString("dd-MM-yyyy"),
                                     "a date that has happened", "the log", "Date"));
                else if (when.Year < 1920)
                    findings.Add(Fyi(q, "Impossible date", when.ToString("dd-MM-yyyy"),
                                     "amateur radio is not that old", "the log", "Date"));

                string timeRaw = (q.Time ?? string.Empty).Trim();
                if (timeRaw.Length >= 4)
                {
                    int hh, mm;
                    bool timeOk = int.TryParse(timeRaw.Substring(0, 2), out hh)
                               && int.TryParse(timeRaw.Substring(2, 2), out mm)
                               && hh <= 23 && mm <= 59;
                    if (!timeOk)
                        findings.Add(Fyi(q, "Impossible time", timeRaw, "00:00 to 23:59", "the log", "Time"));
                }

                // --- band and frequency -------------------------------------------------------------
                // One finding per QSO, not two: an empty band and an unusable frequency are the same
                // fault seen twice, and the operator wants to be told once what is actually wrong.
                string freq = (q.Freq ?? string.Empty).Trim();
                string band = (q.Band ?? string.Empty).Trim();
                string mhz = freq.Length == 0 ? "" : HolyLogParser.NormalizeFreqToMhz(freq);
                string fromFreq = string.IsNullOrWhiteSpace(mhz) ? "" : HolyLogParser.convertFreqToBand(mhz);

                if (freq.Length > 0 && fromFreq.Length == 0)
                {
                    // The frequency is on no amateur band at all, so it cannot say which band this was
                    // and nothing can be derived from it. Only the operator knows what he was on.
                    findings.Add(Fyi(q, "Frequency is on no amateur band",
                                     freq + " MHz" + (band.Length == 0 ? "  (and no band)" : "  (band " + band + ")"),
                                     "set the band by hand - the frequency cannot say", "the log", "Band"));
                }
                else if (band.Length == 0 && fromFreq.Length > 0)
                {
                    Finding f = New(q, "No band", "(empty)   (" + freq + ")", fromFreq,
                                    "the frequency logged");
                    f.Field = "Band";
                    f.NewValue = fromFreq;
                    f.Fixable = true;
                    findings.Add(f);
                }
                else if (band.Length == 0)
                {
                    findings.Add(Fyi(q, "No band and no frequency", "(both empty)",
                                     "set the band by hand", "the log", "Band"));
                }
                else if (fromFreq.Length > 0 && !string.Equals(fromFreq, band, StringComparison.OrdinalIgnoreCase))
                {
                    Finding f = New(q, "Band does not match the frequency",
                                    band + "  (" + freq + ")", fromFreq, "the frequency logged");
                    f.Field = "Band";
                    f.NewValue = fromFreq;
                    f.Fixable = true;
                    findings.Add(f);
                }
                if (string.IsNullOrWhiteSpace(q.Mode))
                    findings.Add(Fyi(q, "No mode", "(empty)", "a mode", "the log", "Mode"));

                // --- the worked station's grid ------------------------------------------------------
                string grid = (q.DXLocator ?? string.Empty).Trim();
                if (grid.Length > 0 && !LegalLocator.IsMatch(grid) && !HolylandSquare.IsMatch(grid))
                    // "Grid is not a locator" said nothing: to an operator those two words mean the same
                    // thing, so the sentence read as a contradiction rather than a fault. What is
                    // actually wrong is that the text in the box is not shaped like a grid square at
                    // all - so say that, and show the shape it should have.
                    findings.Add(Fyi(q, "DX Locator is wrong", grid,
                                     "two letters, two digits, and usually two more letters — KM72OR",
                                     "the log", "DX Locator"));

                // --- an activity reference typed into the comment -----------------------------------
                //
                // Before HolyLogger had boxes for these, the only place to put an island or park
                // reference was the comment, so that is where they are. The four formats cannot be
                // confused with one another, so the program is known for certain - no guessing.
                //
                // Offered ONLY when the comment is the reference and nothing else. A comment that also
                // holds real words is reported and left alone: moving the reference out would decide on
                // the operator's behalf what the rest of their sentence was worth.
                string comment = (q.Comment ?? string.Empty).Trim();
                if (comment.Length > 0 && !HasAnyActivityReference(q))
                {
                    string program = ProgramInComment(comment, comment);
                    if (program != null)
                    {
                        Finding f = New(q, "Reference sitting in the comment", comment,
                                        program + " = " + comment.ToUpperInvariant(),
                                        "the " + program + " format");
                        f.Field = "Activity";
                        f.Program = program;
                        f.NewValue = comment.ToUpperInvariant();
                        f.Fixable = true;
                        findings.Add(f);
                    }
                    else if (comment.Length <= 60)
                    {
                        // A reference hiding among other words: worth pointing at, not worth moving.
                        foreach (string word in comment.Split(new[] { ' ', ',', ';', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string p = ProgramInComment(word, comment);
                            if (p == null) continue;
                            findings.Add(Fyi(q, "Comment holds a " + p + " reference", comment,
                                             "comment has other text too — move " + word.ToUpperInvariant()
                                             + " yourself",
                                             "the " + p + " format", "Comment"));
                            break;
                        }
                    }
                }

                // --- the country, on the QSO's own date ---------------------------------------------
                if (!dateOk) continue;

                // A COUNTRY THAT HAD ALREADY CEASED TO EXIST. Checked against the number the LOG carries,
                // before anything is resolved, because the resolver now refuses to name a dead entity and
                // would simply say "Unknown" - so nobody would ever be told what the log actually holds.
                //
                // 1B1AB, 2 July 2007, filed under entity 23: Blenheim Reef, which ended on 30 June 1975.
                // The contact is real; it is the COUNTRY that cannot be. Reported and never touched -
                // changing what a QSO says is the Log Fixer's job, and only ever with the operator
                // watching.
                int loggedCode = q.DxccCode;
                if (loggedCode > 0 && lookup.EntityHadCeasedBy(loggedCode, when))
                {
                    DateTime ended = lookup.EntityEndUtc(loggedCode);
                    string named = (q.Country ?? string.Empty).Trim();
                    if (named.Length == 0) named = "entity " + loggedCode;

                    findings.Add(Fyi(q,
                        "Country did not exist on QSO date",
                        named + "  (entity " + loggedCode + ")",
                        "check what was really worked",
                        "Club Log: the entity ended " + ended.ToString("dd-MM-yyyy"),
                        "Country"));
                }

                DXCC dated;
                try { dated = lookup.Resolve(call, when); }
                catch { continue; }
                if (dated == null) continue;

                if (dated.InvalidOperation)
                    // NOT "never valid" - that said the callsign itself was bad, and it is not. Club Log
                    // marks an OPERATION: XY2A is a perfectly good Myanmar callsign, and Club Log holds
                    // one exception for it covering 19 to 25 January 2003. A QSO inside that week did
                    // not count; the same callsign a day later did. The date is the whole point, so the
                    // message has to name it.
                    findings.Add(Fyi(q, "Club Log: operation did not count", call,
                                     "no award credit", "Club Log", "Callsign"));

                if (string.IsNullOrEmpty(dated.Name) || dated.Name == "Unknown") continue;

                string storedCountry = (q.Country ?? string.Empty).Trim();
                string storedCont = (q.Continent ?? string.Empty).Trim();

                // THE COUNTRY IS ITS NUMBER, NOT ITS NAME. Every count of countries this program makes
                // is made of entity codes, so that is what is checked here - and the three things that
                // can be wrong with one are three different findings, because they are three different
                // sizes of problem and the operator must be able to tell them apart:
                //
                //   the code disagrees   the QSO counts for the wrong country. The serious one.
                //   the code is missing  the QSO counts for nobody. Old rows, and files with no <DXCC>.
                //   only the name differs  the count is already right; the log simply spells it its
                //                          own way. Offered, never urgent - and never ticked by
                //                          default, because 3,846 of them is not a correction, it is
                //                          rewriting somebody's log for them.
                int storedCode = q.DxccCode;
                int ourCode = EntityCodeOf(dated);

                // THE OPERATOR'S OWN <DXCC> SETTLES A STROKE. When the callsign has a stroke there are
                // two entities it could name, one per side, and which is meant is a convention rather
                // than anything in the callsign - this program picks a side and is sometimes wrong.
                // M/ON4CJK is England to one reading and Belgium to the other. If the number the log
                // carries is ONE OF THOSE TWO it came from the person who was there, and it is not our
                // place to argue with it.
                //
                // This is only ever a reason to say NOTHING. A stored entity that is neither side of
                // the stroke is still reported, and a callsign with no stroke has no second candidate
                // to defer to, so nothing is weakened by it.
                if (ourCode > 0 && storedCode > 0 && storedCode != ourCode && call.IndexOf('/') >= 0)
                {
                    HashSet<int> sides;
                    try { sides = lookup.CandidateEntityCodes(call, when); }
                    catch { sides = null; }
                    if (sides != null && sides.Contains(storedCode)) continue;
                }

                if (ourCode > 0 && storedCode > 0 && storedCode != ourCode)
                {
                    // TWO KINDS, NOT ONE, because they are two different decisions. Where cty.dat and
                    // Club Log name the same entity the proposal rests on both witnesses and a whole
                    // kind can be ticked at once. Where they do not, it rests on one - and the operator
                    // was being asked to take those on the same trust, with nothing on screen to say
                    // which was which. Two extra lookups per country finding, dozens of them in a log
                    // of 28,000, and only for findings that got this far.
                    CountryLookup.Explanation why = null;
                    try { why = lookup.Explain(call, when); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }

                    string kind = why != null && !why.Agree
                        ? CountryNeedsDecision
                        : CountryBothAgree;

                    Finding f = New(q, kind,
                                    Named(storedCode, storedCountry) + ZoneSuffix(q.CQZone, q.ITUZone),
                                    Named(ourCode, dated.Name) + ZoneSuffix(
                                        dated.CqZone > 0 ? dated.CqZone.ToString() : q.CQZone,
                                        dated.ItuZone > 0 ? dated.ItuZone.ToString() : q.ITUZone),
                                    EvidenceFor(dated, call));
                    FillCountryFix(f, dated, ourCode);
                    findings.Add(f);
                }
                else if (ourCode > 0 && storedCode <= 0)
                {
                    Finding f = New(q, "No country code",
                                    storedCountry.Length == 0 ? "(empty)" : storedCountry + "  (no number)",
                                    Named(ourCode, dated.Name), EvidenceFor(dated, call));
                    FillCountryFix(f, dated, ourCode);
                    findings.Add(f);
                }
                else if (storedCountry.Length > 0 && ourCode > 0 && storedCode == ourCode
                         && !string.Equals(storedCountry, dated.Name, StringComparison.OrdinalIgnoreCase))
                {
                    // The entity NUMBER is right and only the words differ - but there are two very
                    // different reasons for that, and calling both of them a spelling was wrong.
                    //
                    //   "Fed. Rep. of Germany" against "Germany" is a spelling. Two databases, two
                    //   wordings, one country; nothing counts wrongly and it can be left alone for ever.
                    //
                    //   "United States" against entity 71 is not. That name belongs to a different
                    //   country altogether - the QSO is Galapagos by its own number - so the log READS
                    //   as a country it does not count as. K7ST/HC8, KU9C/VP9 (Bermuda logged as United
                    //   States) and I1GDH/IS0 (Sardinia logged as Italy) are all of this kind.
                    //
                    // The two are told apart by asking what entity the stored WORDING itself names: if
                    // it names another entity, the name is simply wrong.
                    int nameCode = 0;
                    try { nameCode = lookup.EntityCodeForCountry(storedCountry, when); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                    bool namesAnother = nameCode > 0 && nameCode != storedCode;

                    Finding f = New(q, namesAnother ? "Wrong country name"
                                                    : "Country spelled differently",
                                    namesAnother ? Named(nameCode, storedCountry) : storedCountry,
                                    namesAnother ? Named(storedCode, dated.Name) : dated.Name,
                                    EvidenceFor(dated, call));
                    f.Field = "CountryName";
                    f.NewValue = dated.Name;
                    f.Fixable = true;
                    findings.Add(f);
                }
                else if (storedCountry.Length == 0 && !string.IsNullOrEmpty(dated.Name))
                {
                    Finding f = New(q, "No country", "(empty)", Named(ourCode, dated.Name), EvidenceFor(dated, call));
                    FillCountryFix(f, dated, ourCode);
                    findings.Add(f);
                }
                else if (!string.IsNullOrEmpty(dated.Continent) && dated.Continent != "XX"
                         && !string.Equals(storedCont, dated.Continent, StringComparison.OrdinalIgnoreCase))
                {
                    // Country agrees, so only the continent is adrift - usually an old row that was
                    // saved before the field was filled in reliably.
                    Finding f = New(q, "Wrong continent",
                                    storedCont.Length == 0 ? "(empty)" : storedCont,
                                    dated.Continent, EvidenceFor(dated, call));
                    f.Field = "Continent";
                    f.NewValue = dated.Continent;
                    f.Fixable = true;
                    findings.Add(f);
                }
            }

            // --- the same contact logged twice ---------------------------------------------------
            //
            // Every check above asks its question of ONE QSO. This one is about the log as a whole,
            // and it is the fault that quietly inflates everything the program counts: a station
            // worked once but held twice is two contacts to the statistics, two to the awards, and
            // two records uploaded to LoTW.
            //
            // "The same contact" means here exactly what it means everywhere else - DataAccess.MatchKey,
            // the one rule the import's merge and Tools > Remove Duplicates both answer to. A second
            // definition living in this window is how the two halves of a program start to disagree.
            //
            // THE FIRST OF EACH GROUP IS NEVER TOUCHED. Only the copies after it are offered up, so
            // ticking every row of this kind can never empty a contact out of the log altogether -
            // the same guarantee Tools > Remove Duplicates gives.
            var copies = new HashSet<QSO>();
            foreach (DupGroup g in DuplicateScan.Find(qsos))
            {
                QSO first = g.Keep;
                string where = "the same contact is already in this log at "
                               + FormatDate(first.Date) + " " + FormatTime(first.Time) + ". ";

                // A GROUP WHOSE COMMENTS DISAGREE IS NOT REMOVED HERE. Somebody wrote two different
                // things about one contact and only the operator knows which is worth keeping, so the
                // tick means "take me to that question", not "delete this". The screen that asks it is
                // the same one Tools > Remove Duplicates uses, so the answer is given in one place.
                string what = g.NeedsChoice
                    ? where + "The two copies carry different comments — ticking this row asks you "
                              + "which comment to keep before anything is removed"
                    : where + "Ticking this row removes this copy";

                foreach (QSO q in g.Extras)
                {
                    Finding f = New(q, "Duplicate contact", Text(q.DXCall), what,
                                    "callsign, date, band, mode and minute all match");
                    f.Deletes = true;
                    f.Fixable = true;      // the tick means REMOVE, not write
                    f.Where = "Callsign";  // which cell carries the note
                    f.Group = g;
                    findings.Add(f);
                    copies.Add(q);
                }
            }

            // A COPY CARRIES ONE FINDING AND ONE ONLY. The table ticks by ROW, and one tick applies
            // every fixable finding on that row - so a copy that also had a misspelled country would be
            // DELETED by an operator who ticked "Country spelled differently" and never looked at the
            // callsign column. Its other problems come off: there is nothing to gain by correcting a
            // contact that should not be in the log, and the copy that stays behind carries the same
            // problems and gets them put right on its own row.
            if (copies.Count > 0)
                findings.RemoveAll(f => !f.Deletes && f.Qso != null && copies.Contains(f.Qso));

            // Worst first: a wrong country matters more than a missing continent, and within a kind the
            // oldest QSO first so a run through the list reads chronologically.
            return findings
                .OrderBy(f => Rank(f.Problem))
                .ThenBy(f => f.Qso != null ? (f.Qso.Date ?? "") : "")
                .ThenBy(f => f.Qso != null ? (f.Qso.Time ?? "") : "")
                .ToList();
        }

        // The entity number behind an answer. DXCC.DxccCode is filled by Club Log; cty.dat's side names
        // the country and leaves the number to be looked up, so both are tried before giving up. Zero
        // means the answer is not a DXCC entity at all - a maritime-mobile station, an invalid
        // operation - and nothing is ever suggested from one of those.
        private static int EntityCodeOf(DXCC d)
        {
            if (d == null || !d.IsDxccEntity) return 0;
            if (d.DxccCode > 0) return d.DxccCode;
            try { return CountryLookup.Shared.EntityCodeForCountry(d.Name); }
            catch { return 0; }
        }

        // WHAT THE ANSWER RESTS ON, not merely which database gave it. Two EA8 callsigns were pushed
        // opposite ways on the same screen - EA8AAH out of Canary Islands into Spain, EA8SG the other
        // way - and "cty.dat" against both explained nothing. The reason is that cty.dat carries a
        // hand-curated entry for the exact callsign EA8AAH, placing that one station in Spain, while
        // EA8SG simply follows the EA8 prefix. Both answers are right; only the evidence was mute.
        private static string EvidenceFor(DXCC d, string call)
        {
            if (d == null) return "";
            string src = string.IsNullOrEmpty(d.ResolvedBy) ? "the databases" : d.ResolvedBy;
            string c = (call ?? "").Trim().ToUpperInvariant();
            int n = d.MatchedLength;

            if (n <= 0 || c.Length == 0) return src;
            if (n >= c.Length) return src + ": this exact callsign";
            return src + ": " + c.Substring(0, n) + " prefix";
        }

        // "United States (291)" - the number is the part that counts, so it is never left off.
        private static string Named(int code, string name)
        {
            string n = string.IsNullOrWhiteSpace(name) ? "(no name)" : name.Trim();
            return code > 0 ? n + " (" + code + ")" : n;
        }

        // Everything a country correction writes, in one place: the name, the entity NUMBER, the
        // continent and the zones. They are one fact about the QSO and must never be written apart -
        // a log that says "Puerto Rico" while still counting as the United States is the harder error
        // of the two to ever notice.
        private static void FillCountryFix(Finding f, DXCC dated, int code)
        {
            f.Field = "Country";
            f.NewValue = dated.Name;
            f.NewCode = code;
            f.NewDxcc = dated.Entity;
            f.NewContinent = dated.Continent != null && dated.Continent != "XX" ? dated.Continent : null;
            f.NewCq = dated.CqZone;
            f.NewItu = dated.ItuZone;
            f.Fixable = true;
        }

        private static int Rank(string problem)
        {
            if (problem.StartsWith("Club Log:")) return 0;
            // A contact held twice is read first: it is the only fault here that makes the log hold
            // something that never happened, and it inflates every count the program shows.
            if (problem == "Duplicate contact") return 1;
            // The ones needing a judgement are read FIRST: they are the shorter list and the only one
            // that costs the operator anything. The agreed pile is a single tick, so it can wait.
            // Given their own ranks rather than sharing one, because a tie is broken alphabetically
            // and that put "agreed" above the pile he actually has to work through.
            if (problem == CountryNeedsDecision) return 2;
            if (problem == CountryBothAgree) return 3;
            if (problem == "Different country") return 3;
            if (problem == "No country code") return 4;
            if (problem == "No country") return 5;
            if (problem == "Damaged callsign") return 6;
            if (problem.StartsWith("Band") || problem.StartsWith("Frequency")) return 7;
            if (problem == "Wrong continent") return 8;
            // A name that belongs to another country is worth reading before a mere wording, because a
            // log that SAYS United States while counting as Galapagos misleads whoever reads it.
            if (problem == "Wrong country name") return 9;
            if (problem == "Country spelled differently") return 11;   // last: nothing counts wrongly
            return 10;
        }

        private static string ZoneSuffix(string cq, string itu)
        {
            cq = (cq ?? string.Empty).Trim();
            itu = (itu ?? string.Empty).Trim();
            if (cq.Length == 0 && itu.Length == 0) return string.Empty;
            return "   (CQ " + (cq.Length == 0 ? "-" : cq) + ", ITU " + (itu.Length == 0 ? "-" : itu) + ")";
        }

        private static Finding New(QSO q, string problem, string current, string suggested, string evidence)
        {
            return new Finding
            {
                Qso = q,
                Call = (q.DXCall ?? string.Empty).Trim(),
                Time = FormatTime(q.Time),
                DateText = FormatDate(q.Date),
                Problem = problem,
                Current = current,
                Suggested = suggested,
                Evidence = evidence
            };
        }

        // A finding the program must not act on by itself. "Check by hand" rather than "FYI": the label
        // has to say what the operator should do, in words that need no explaining.
        // `where` names the QSO field the finding is about. These findings write nothing, so they have
        // no Field to write - but the table still has a column saying WHICH field each row is talking
        // about, and leaving it blank made a row read "Grid is not a locator — " with a dash that
        // explained nothing. Named at each call site rather than guessed from the problem text, so
        // rewording a message can never quietly empty the column.
        private static Finding Fyi(QSO q, string problem, string current, string note, string evidence,
                                   string where = null)
        {
            Finding f = New(q, problem, current, note, evidence);
            f.Fixable = false;
            f.Where = where;
            f.Suggested = "Check by hand — " + note;
            return f;
        }

        private static string FormatDate(string yyyymmdd)
        {
            string s = (yyyymmdd ?? string.Empty).Trim();
            if (s.Length != 8) return s;
            return s.Substring(6, 2) + "-" + s.Substring(4, 2) + "-" + s.Substring(0, 4);
        }

        private static string FormatTime(string hhmmss)
        {
            string s = (hhmmss ?? string.Empty).Trim();
            if (s.Length < 4) return s;
            return s.Substring(0, 2) + ":" + s.Substring(2, 2);
        }

        // Which COLUMN a finding belongs in. FieldLabel already names the field for the reader; this
        // turns that into the column key, and adds the one case where a single finding fills two cells:
        // a country correction changes the name AND the entity number, and both must be visible or the
        // operator cannot see that the number is the part that matters.
        private static string ColumnOf(Finding f)
        {
            switch (f.Field)
            {
                case "DXCall": return "Callsign";
                case "Band": return "Band";
                case "Mode": return "Mode";
                case "Date": return "Date";
                case "Time": return "Time";
                case "Continent": return "Continent";
                case "DXLocator": return "DX Locator";
                case "Country": return "Country";
                case "CountryName": return "Country";
                case "Activity": return f.Program;      // IOTA / SOTA / POTA / WWFF
            }
            // Report-only findings carry their field name in Where.
            return string.IsNullOrEmpty(f.Where) ? "Callsign" : f.Where;
        }

        private static string Text(string s) { return (s ?? "").Trim(); }

        // The one line that goes under a red cell nobody can answer for the operator. The Problem is
        // what to say - "Club Log lists this as never valid" - and Suggested carries the advice that
        // went with it, minus the "Check by hand" prefix, which the grey note already implies.
        private static string NoteFor(Finding f)
        {
            string problem = Text(f.Problem);
            string advice = Text(f.Suggested);
            const string prefix = "Check by hand — ";
            if (advice.StartsWith(prefix, StringComparison.Ordinal)) advice = advice.Substring(prefix.Length);
            if (advice.Length == 0) return problem;
            return problem + " — " + advice;
        }

        private static Cell CellFor(FixRow row, string key)
        {
            Cell c;
            if (!row.Cells.TryGetValue(key, out c)) { c = new Cell(); row.Cells[key] = c; }
            return c;
        }

        // One row per QSO, with its cells filled from the QSO as it stands and from whatever its
        // findings propose. Cells nobody complained about carry the value and no colour.
        private List<string> BuildRows(List<Finding> found)
        {
            _rows.Clear();
            _rowByQso.Clear();
            var used = new HashSet<string>();

            foreach (var g in found.Where(f => f.Qso != null).GroupBy(f => f.Qso))
            {
                QSO q = g.Key;
                var row = new FixRow { Qso = q };
                row.Findings.AddRange(g);
                // The Fix button's count and the header's tick-all box both follow a tick. Neither
                // touches the grid's selection: the row paints itself from Apply through the RowStyle
                // trigger, which costs nothing even when four thousand rows change at once.
                // Unticking one row must be able to clear its kind's box up in the panel: the kind no
                // longer holds true. Cheap enough for a single row - it is the bulk paths that must not
                // do this per row, and they are guarded.
                FixRow thisRow = row;   // captured, or every row's callback would close over the last
                row.ApplyChanged = () =>
                {
                    SyncSelectionFromApply(thisRow);
                    UpdateFixButton();
                    UpdateFixAllBox();
                    UpdateKindBoxes();
                };

                CellFor(row, "Date").Current = FormatDate(q.Date);
                CellFor(row, "Time").Current = FormatTime(q.Time);
                CellFor(row, "Callsign").Current = Text(q.DXCall);

                foreach (Finding f in g)
                {
                    string key = ColumnOf(f);
                    used.Add(key);

                    Cell cell = CellFor(row, key);
                    cell.Wrong = true;
                    if (cell.Current.Length == 0) cell.Current = CurrentOf(q, key);
                    // A removal has no value to propose - the green half would be empty - so it says in
                    // words what ticking it does, exactly as a report-only finding does.
                    if (f.Deletes) cell.Note = NoteFor(f);
                    else if (f.Fixable && !string.IsNullOrEmpty(f.NewValue)) cell.Suggest(f.NewValue);
                    else if (!f.Fixable) cell.Note = NoteFor(f);

                    // The entity number travels with the country name, and is the half that decides
                    // what the QSO counts as.
                    if (f.Field == "Country")
                    {
                        used.Add("Country Code");
                        Cell code = CellFor(row, "Country Code");
                        code.Wrong = true;
                        if (code.Current.Length == 0) code.Current = q.DxccCodeText;
                        if (f.NewCode > 0) code.Suggest(f.NewCode.ToString());

                        if (!string.IsNullOrEmpty(f.NewContinent))
                        {
                            used.Add("Continent");
                            Cell cont = CellFor(row, "Continent");
                            if (cont.Current.Length == 0) cont.Current = Text(q.Continent);
                            if (!string.Equals(cont.Current, f.NewContinent, StringComparison.OrdinalIgnoreCase))
                            {
                                cont.Wrong = true;
                                cont.Suggest(f.NewContinent);
                            }
                        }
                    }
                }

                // The columns this row does not own still show what the QSO holds, so the pair reads
                // as a QSO rather than as three coloured boxes floating in space.
                foreach (string key in IssueColumns)
                    if (used.Contains(key) && !row.Cells.ContainsKey(key))
                        CellFor(row, key).Current = CurrentOf(q, key);

                // Every cell tells the row when it is typed into, so the tick box wakes up as soon as
                // the operator supplies an answer the program could not.
                FixRow captured = row;
                foreach (var kv in row.Cells)
                    kv.Value.Changed = () => { captured.Recompute(); UpdateFixButton(); };
                row.Recompute();

                _rowByQso[q] = row;
                _rows.Add(row);
            }

            // Fill in any column that became used AFTER a row was built.
            foreach (FixRow row in _rows)
                foreach (string key in IssueColumns)
                    if (used.Contains(key) && !row.Cells.ContainsKey(key))
                        CellFor(row, key).Current = CurrentOf(row.Qso, key);

            // WHAT CAN BE PUT RIGHT COMES FIRST. The report-only findings - the ones with a dead tick box
            // - happened to sort to the top, so ticking a kind or the header box left the operator
            // looking at a screenful of empty boxes with the ticked rows far below, and the window
            // appeared to have done nothing. Sorted ONCE, here, as the list is built: a live sort would
            // move rows out from under the operator the moment a typed value made one fixable.
            // Stable, so within each group the scan's own order survives.
            var ordered = _rows.Where(r => r.Fixable).Concat(_rows.Where(r => !r.Fixable)).ToList();
            _rows.Clear();
            foreach (FixRow r in ordered) _rows.Add(r);

            var columns = new List<string>(AlwaysColumns);
            foreach (string key in IssueColumns) if (used.Contains(key)) columns.Add(key);
            return columns;
        }

        // Which row belongs to which QSO, for the fix to find what was typed into it.
        private readonly Dictionary<QSO, FixRow> _rowByQso = new Dictionary<QSO, FixRow>();

        // A value the operator typed, written to the one field its column stands for. Date and time go
        // back through the display format they were shown in; a country code that is not a number is
        // ignored rather than stored as rubbish.
        private static void WriteField(QSO q, string key, string v)
        {
            if (q == null) return;
            switch (key)
            {
                case "Callsign": q.DXCall = v.ToUpperInvariant(); break;
                case "Date": { string d = UnformatDate(v); if (d != null) q.Date = d; break; }
                case "Time": { string t = UnformatTime(v); if (t != null) q.Time = t; break; }
                case "Band": q.Band = v.ToUpperInvariant(); break;
                case "Mode": q.Mode = v.ToUpperInvariant(); break;
                case "Country": q.Country = v; break;
                case "Country Code": { int c; if (int.TryParse(v, out c) && c > 0) q.DxccCode = c; break; }
                case "Continent": q.Continent = v.ToUpperInvariant(); break;
                case "DX Locator": q.DXLocator = v.ToUpperInvariant(); break;
                case "Comment": q.Comment = v; break;
                case "IOTA": q.Iota = v.ToUpperInvariant(); break;
                case "SOTA": q.SotaRef = v.ToUpperInvariant(); break;
                case "POTA": q.PotaRef = v.ToUpperInvariant(); break;
                case "WWFF": q.WwffRef = v.ToUpperInvariant(); break;
            }
        }

        // "21-01-2003" back to "20030121". Null when it is not a date, so a slip of the keyboard cannot
        // replace a real date with nonsense.
        private static string UnformatDate(string shown)
        {
            DateTime d;
            if (DateTime.TryParseExact((shown ?? "").Trim(), "dd-MM-yyyy", CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out d))
                return d.ToString("yyyyMMdd");
            string digits = new string((shown ?? "").Where(char.IsDigit).ToArray());
            return digits.Length == 8 ? digits : null;
        }

        // "13:55" back to "1355".
        private static string UnformatTime(string shown)
        {
            string digits = new string((shown ?? "").Where(char.IsDigit).ToArray());
            if (digits.Length != 4 && digits.Length != 6) return null;
            int hh, mm;
            if (!int.TryParse(digits.Substring(0, 2), out hh) || hh > 23) return null;
            if (!int.TryParse(digits.Substring(2, 2), out mm) || mm > 59) return null;
            return digits;
        }

        private static string CurrentOf(QSO q, string key)
        {
            if (q == null) return "";
            switch (key)
            {
                case "Callsign": return Text(q.DXCall);
                case "Date": return FormatDate(q.Date);
                case "Time": return FormatTime(q.Time);
                case "Band": return Text(q.Band);
                case "Mode": return Text(q.Mode);
                case "Country": return Text(q.Country);
                case "Country Code": return q.DxccCodeText;
                case "Continent": return Text(q.Continent);
                case "DX Locator": return Text(q.DXLocator);
                case "Comment": return Text(q.Comment);
                case "IOTA": return Text(q.Iota);
                case "SOTA": return Text(q.SotaRef);
                case "POTA": return Text(q.PotaRef);
                case "WWFF": return Text(q.WwffRef);
            }
            return "";
        }

        // The columns are made here rather than in the XAML because WHICH of them exist depends on what
        // the scan found. Each is the two-half cell; the tick column is built once at the front.
        private void BuildColumns(List<string> columns)
        {
            FindingsGrid.Columns.Clear();

            // THE BOX HANDLES ITS OWN CLICK. A click anywhere in a DataGrid cell is taken by the grid
            // first, to work out the selection - and the selection then drives the tick. Landing on
            // the box itself therefore did nothing visible: the box toggled, the grid re-made the
            // selection underneath it, and the tick came back to where it started. Pressing the box is
            // now a decision the box makes, and the mouse event stops there.
            //
            // Built in code rather than parsed from a string for the same reason as the "?" column:
            // XamlReader cannot wire a handler that lives in this class.
            var tick = new DataGridTemplateColumn { Width = 54, CanUserSort = false };

            var box = new FrameworkElementFactory(typeof(CheckBox));
            box.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            box.SetBinding(UIElement.IsEnabledProperty, new System.Windows.Data.Binding("Fixable"));
            box.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new System.Windows.Data.Binding("Apply")
                {
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                });
            box.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                           new System.Windows.Input.MouseButtonEventHandler(Chk_Fix_PreviewMouseDown));
            tick.CellTemplate = new DataTemplate { VisualTree = box };

            // THE TICK-ALL BOX HAS TO BE BUILT HERE, not in the XAML. This method CLEARS the columns and
            // makes them again on every scan, so a header declared in the window is thrown away the
            // moment there is anything to show - which is why three attempts at putting a box there in
            // the XAML never appeared on screen.
            var fixAll = new CheckBox
            {
                IsThreeState = true,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = "Tick every row on show, or clear them all"
            };
            fixAll.Click += Chk_FixAll_Click;
            _fixAllBox = fixAll;

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            headerPanel.Children.Add(fixAll);
            headerPanel.Children.Add(new TextBlock
            {
                Text = "Fix",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            tick.Header = headerPanel;

            FindingsGrid.Columns.Add(tick);

            // WHY THIS ANSWER. A country proposal is the one finding the operator cannot check for
            // himself: two databases were asked, one of them won, and the table shows only the winner.
            // "?" opens what each of them said and how much of the callsign each recognised - which is
            // the whole argument in one line ("Club Log only matched CQ and cty.dat matched CQ1").
            var why = new DataGridTemplateColumn
            {
                Header = "?",
                Width = 40,
                CanUserSort = false
            };
            // Built in code, not parsed from a string: XamlReader has no idea what Btn_Why_Click is -
            // event handlers are wired by the compiled x:Class, which a runtime parse does not have.
            var button = new FrameworkElementFactory(typeof(Button));
            button.SetValue(ContentControl.ContentProperty, "?");
            button.SetValue(FrameworkElement.WidthProperty, 26.0);
            button.SetValue(FrameworkElement.HeightProperty, 26.0);
            button.SetValue(Control.FontSizeProperty, 16.0);
            button.SetValue(Control.FontWeightProperty, FontWeights.Bold);
            button.SetValue(Control.PaddingProperty, new Thickness(0));
            button.SetValue(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand);
            button.SetValue(FrameworkElement.ToolTipProperty, "Why this country?");
            button.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            button.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            button.SetBinding(FrameworkElement.TagProperty, new System.Windows.Data.Binding());
            button.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                              new RoutedEventHandler(Btn_Why_Click));
            why.CellTemplate = new DataTemplate { VisualTree = button };
            FindingsGrid.Columns.Add(why);

            foreach (string key in columns)
            {
                string safe = key.Replace("&", "&amp;");
                string xaml =
                    "<DataGridTemplateColumn xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
                    // MaxWidth, because Auto sizes to the WIDEST thing in the column and the widest
                    // thing is the explanation under a red cell. One long note pushed the Callsign
                    // column across half the window and shoved everything else off the edge. Capped,
                    // the note wraps onto a second line and the table keeps its shape.
                    + "Header='" + safe + "' Width='Auto' MinWidth='70' MaxWidth='230' CanUserSort='False'>"
                    + "<DataGridTemplateColumn.CellTemplate><DataTemplate>"
                    + "<Grid><Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/>"
                    + "</Grid.RowDefinitions>"
                    + "<Border Grid.Row='0' Background='{Binding Cells[" + safe + "].NowBg}' Padding='7,3,7,3' "
                    + "MinHeight='24' BorderBrush='#C0C0C0' BorderThickness='0,0,0,1'>"
                    + "<TextBlock Text='{Binding Cells[" + safe + "].Current}' TextWrapping='Wrap' "
                    + "Foreground='{Binding Cells[" + safe + "].NowFg}'/></Border>"
                    // THE LOWER HALF IS ALWAYS THERE, even when the program has nothing to propose.
                    // An empty line under every contact is what makes the table read as pairs; letting
                    // it collapse would leave the by-hand rows looking like a different kind of thing
                    // when they are simply the ones the operator must answer.
                    + "<Border Grid.Row='1' Background='{Binding Cells[" + safe + "].ThenBg}' Padding='4,1,4,1' "
                    + "MinHeight='24'>"
                    // A BORDERLESS, TRANSPARENT TEXT BOX, so the lower half looks like part of the
                    // table until it is clicked. Typing into it is how a fault the program cannot
                    // answer gets settled without leaving this window.
                    // The note sits BEHIND the box, so it reads as a hint in an empty cell and is gone
                    // as soon as anything is typed - the cell never stops being the operator's.
                    + "<Grid><TextBlock Text='{Binding Cells[" + safe + "].Note}' TextWrapping='Wrap' "
                    + "Visibility='{Binding Cells[" + safe + "].NoteVisible}' Margin='3,2,3,2' "
                    + "FontStyle='Italic' FontSize='14' Foreground='#8A0000' IsHitTestVisible='False'/>"
                    // AND THE REST OF THE QSO, repeated in grey behind the box wherever nothing is being
                    // proposed, so the lower half reads as the whole contact rather than as one green
                    // word among blanks. Not hit-testable, so a click still lands in the text box.
                    + "<TextBlock Text='{Binding Cells[" + safe + "].Current}' TextWrapping='Wrap' "
                    + "Visibility='{Binding Cells[" + safe + "].EchoVisible}' Margin='3,2,3,2' "
                    + "Foreground='#8C8C8C' IsHitTestVisible='False'/>"
                    + "<TextBox Text='{Binding Cells[" + safe + "].Proposed, Mode=TwoWay, "
                    + "UpdateSourceTrigger=PropertyChanged}' Background='Transparent' BorderThickness='0' "
                    + "Padding='3,2,3,2' Foreground='#0B4A0E' "
                    + "FontWeight='{Binding Cells[" + safe + "].ThenWeight}'/></Grid></Border>"
                    + "</Grid></DataTemplate></DataGridTemplateColumn.CellTemplate></DataGridTemplateColumn>";
                var col = (DataGridTemplateColumn)XamlReader.Parse(xaml);
                // What Ctrl+C takes from this column: the value as it stands. A template column copies
                // nothing at all unless told, and the top half is the log - which is what an operator
                // pasting into a mail is asking for.
                col.ClipboardContentBinding = new System.Windows.Data.Binding("Cells[" + key + "].Current");
                FindingsGrid.Columns.Add(col);
            }
        }

        // THE WINDOW OPENS WIDE ENOUGH TO READ THE KINDS PANEL IN ONE LINE EACH.
        //
        // Those sentences are how an operator decides which pile is his work, and a sentence that wraps
        // to a second line pushes the list taller and reads as an afterthought. The width they need is
        // not a constant either - it changes every time one of them is reworded, and it did: the two
        // country lines grew and the window that had fitted them stopped fitting.
        //
        // So it is MEASURED, not guessed, and only ever grows the window - never shrinks one the
        // operator has sized himself - and never past the screen it is on.
        private void WidenForKindNotes()
        {
            try
            {
                if (_kinds == null || _kinds.Count == 0) return;

                double dpi = 1.0;
                try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                // Bold throughout, which is the worst case: parts of every sentence are bold and bold
                // is the wider face.
                var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Italic,
                                        FontWeights.Bold, FontStretches.Normal);
                var chipFace = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                                            FontWeights.SemiBold, FontStretches.Normal);

                double widestNote = 0, widestChip = 0;
                foreach (ProblemKind k in _kinds)
                {
                    string note = (k.HandNote ?? string.Empty).Replace("**", "");
                    note = System.Text.RegularExpressions.Regex.Replace(note, @"\{\{[rg]:([^}]*)\}\}", "$1");
                    widestNote = Math.Max(widestNote, Measure(note, face, 16, dpi));
                    widestChip = Math.Max(widestChip, Measure(k.Name ?? "", chipFace, 16, dpi) + 24);
                }
                if (widestNote <= 0) return;

                double needed = 34            // the tick box column
                              + Math.Max(300, widestChip)
                              + 14 + 70       // the count, and the gap before it
                              + 18 + widestNote + 16
                              + 20            // the panel's own scroll bar
                              + 60;           // frame, borders and the outer margins

                double most = double.MaxValue;
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
                        var src = PresentationSource.FromVisual(this);
                        double sx = src != null && src.CompositionTarget != null
                                  ? src.CompositionTarget.TransformToDevice.M11 : 1.0;
                        if (sx <= 0) sx = 1.0;
                        most = wa.Width / sx;
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                double want = Math.Min(needed, most);
                if (want > Width) Width = want;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static double Measure(string text, Typeface face, double size, double dpi)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                       face, size, Brushes.Black, dpi);
            return ft.WidthIncludingTrailingWhitespace;
        }

        // The kinds frame, built from the findings themselves rather than from a list of kinds kept
        // somewhere - a check added to Scan appears here without anyone remembering to register it.
        // Ordered the way the table is ordered, so the frame and the rows read in the same sequence.
        private void BuildKinds(List<Finding> found)
        {
            _kinds.Clear();
            // EVERYTHING THE PROGRAM CAN PUT RIGHT COMES FIRST, and only then the kinds that are the
            // operator's own to judge. Those two halves are what he does different things with - tick the
            // first, read and answer the second - so they are not left interleaved. Inside each half the
            // old order stands: worst first, by Rank.
            foreach (var g in found.GroupBy(f => f.Problem)
                                   .OrderBy(g => g.Any(f => f.Fixable) ? 0 : 1)
                                   .ThenBy(g => Rank(g.Key))
                                   .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var kind = new ProblemKind
                {
                    Name = g.Key,
                    Count = g.Count(),
                    // The tick box is alive when the program has an answer for AT LEAST ONE QSO of this
                    // kind - not for all of them. Requiring all would grey out a whole kind because a
                    // single QSO in it cannot be answered, and the rest would then have to be ticked one
                    // by one for no reason. Ticking a mixed kind is still honest: the rows with nothing
                    // to propose are left alone (KindChecked, and Finding.Apply refuses a tick it cannot
                    // honour), and each of them can still be answered by typing into its green row.
                    Fixable = g.Any(f => f.Fixable)
                };
                kind.CheckedChanged = KindChecked;
                _kinds.Add(kind);
            }
            IC_Kinds.ItemsSource = _kinds;
            WidenForKindNotes();

            // A rebuilt list has no filter, and the summary line says what to do with it.
            ApplyKindFilter(null);
            if (_kinds.Count == 0) TB_KindsSummary.Text = "";
        }

        // A kind was ticked or unticked: push it down onto every row of that kind. Rows whose finding
        // has nothing to propose stay untouched - Finding.Apply refuses a tick it cannot honour.
        // Set while the kinds' own boxes are being brought in line with the rows, so that writing one
        // does not send us back round through KindChecked - which would re-tick the rows and, worse,
        // move the view.
        private bool _settingKindBoxes;

        private void KindChecked(ProblemKind kind)
        {
            if (kind == null || _settingKindBoxes) return;

            // TICKING A KIND SHOWS THAT KIND FIRST, exactly as pressing its button would. Without this
            // the operator ticks 4,366 rows while the table is still showing a different kind entirely -
            // every row on screen stays unticked and uncoloured, and the window looks broken while it is
            // in fact doing precisely what was asked. The rows being ticked have to be the rows in front
            // of them.
            // Only on the way IN: unticking is a retraction, and it should not drag the view somewhere.
            if (kind.Checked && !string.Equals(_filterKind, kind.Name, StringComparison.Ordinal))
                ApplyKindFilter(kind.Name);

            _syncingKind = true;
            try
            {
                // A row is ticked when it HAS a problem of this kind. Ticking it puts every red cell on
                // that row right, which is what the two coloured halves promise - so a QSO that also has
                // a bad locator gets that mended too. Untick it and settle it by hand if that is wrong.
                foreach (FixRow r in _rows)
                    if (r.Has(kind.Name)) r.Apply = kind.Checked;
            }
            finally { _syncingKind = false; }
            UpdateFixButton();
            UpdateFixAllBox();
            UpdateKindBoxes();
            ShowFirstTickedRow();
        }

        // HIGHLIGHTING A ROW TICKS IT, as in the Log Workshop's table - two tables that look alike must
        // behave alike. Safe here because the tick is a DECISION, not an action: nothing is written until
        // "Fix selected" is pressed, so a stray click costs a tick, never a change. A row that cannot be
        // fixed cannot be ticked either; Apply refuses the value unless Fixable is true.
        //
        // Only the rows the operator actually clicked pass through here, so this is cheap. The bulk paths
        // (a kind, the header box) never touch the grid's selection at all - see the RowStyle trigger.
        private bool _syncingApply;

        private void FindingsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingApply || _syncingKind) return;
            try
            {
                _syncingApply = true;
                foreach (object item in e.RemovedItems)
                {
                    var row = item as FixRow;
                    if (row != null) row.Apply = false;
                }
                foreach (object item in e.AddedItems)
                {
                    var row = item as FixRow;
                    if (row != null) row.Apply = true;     // ignored when the row is not fixable
                }
            }
            catch (Exception ex) { Log.Swallow(ex); }
            finally { _syncingApply = false; }

            UpdateFixButton();
            UpdateFixAllBox();
        }

        // A CLICK ON THE TICK BOX IS THE WHOLE OF WHAT THAT CLICK MEANS. Toggled here and stopped here,
        // so the grid never sees it and never re-decides the selection - which is what made pressing
        // the box itself appear to do nothing at all. SyncSelectionFromApply then brings the row's
        // highlight into line, so the two halves still agree.
        private void Chk_Fix_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var box = sender as CheckBox;
            var row = box == null ? null : box.DataContext as FixRow;
            if (row == null) return;

            if (row.Fixable) row.Apply = !row.Apply;
            e.Handled = true;      // a row that cannot be fixed swallows the click rather than jumping
        }

        // ...AND THE SAME THING BACKWARDS. Clicking a row ticked it, but clearing the tick box left the
        // row still SELECTED - so it stayed blue while its box was empty, saying two opposite things at
        // once. Selection and tick are meant to be one state; only half of it was wired.
        //
        // One row at a time, and never from a bulk path: the kind boxes and the header box are guarded
        // by _syncingKind and deliberately leave the selection alone, because SelectedItems.Add is
        // quadratic and four thousand of them took seconds.
        private void SyncSelectionFromApply(FixRow row)
        {
            if (row == null || _syncingApply || _syncingKind || FindingsGrid == null) return;
            try
            {
                _syncingApply = true;
                bool selected = FindingsGrid.SelectedItems.Contains(row);
                if (row.Apply && !selected) FindingsGrid.SelectedItems.Add(row);
                else if (!row.Apply && selected) FindingsGrid.SelectedItems.Remove(row);
            }
            catch (Exception ex) { Log.Swallow(ex); }
            finally { _syncingApply = false; }
        }

        // The header's box, built in BuildColumns because the columns are remade on every scan.
        private CheckBox _fixAllBox;

        // Nothing ticked -> tick every row on show that CAN be fixed; anything ticked -> clear the lot.
        // Only the rows currently listed, never the ones a kind filter is hiding.
        private void Chk_FixAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var rows = FindingsGrid.Items.OfType<FixRow>().ToList();
                bool tickAll = !rows.Any(r => r.Apply);

                _syncingKind = true;                       // one update at the end, not one per row
                try
                {
                    foreach (FixRow r in rows) r.Apply = tickAll;
                }
                finally { _syncingKind = false; }
            }
            catch (Exception ex) { Log.Swallow(ex); }

            UpdateFixButton();
            UpdateFixAllBox();
            UpdateKindBoxes();
            ShowFirstTickedRow();
        }

        // AFTER A BULK TICK, SHOW A TICKED ROW. The list opens on the report-only findings - the ones
        // that can never be ticked - so ticking everything left the operator looking at a screenful of
        // empty boxes and no sign that anything had happened at all. Scrolling to the first row that DID
        // get ticked is the difference between "it worked" and "it ignored me".
        private void ShowFirstTickedRow()
        {
            try
            {
                foreach (FixRow r in FindingsGrid.Items.OfType<FixRow>())
                {
                    if (!r.Apply) continue;
                    FindingsGrid.ScrollIntoView(r);
                    return;
                }
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        // THE KINDS' OWN BOXES FOLLOW THE ROWS. Ticking everything from the header must tick the kinds
        // too, or the panel above says "nothing chosen" over a table where everything is - and unticking
        // a kind up there then has nothing visible to undo. A kind is ticked when every one of its
        // fixable rows is ticked, which is exactly what its own box means when the operator sets it.
        private void UpdateKindBoxes()
        {
            // NEVER DURING A SWEEP. This walks every kind against every row, so running it once per
            // ticked row is the whole list squared: ticking 4,558 rows called it 4,558 times, about 145
            // million passes, and one click on the header box took 5.15 seconds - measured. The bulk
            // paths call it once, at the end.
            if (_kinds == null || _syncingKind) return;
            try
            {
                _settingKindBoxes = true;
                foreach (ProblemKind k in _kinds)
                {
                    if (!k.Fixable) continue;
                    bool any = false, all = true;
                    foreach (FixRow r in _rows)
                    {
                        if (!r.Has(k.Name) || !r.Fixable) continue;
                        any = true;
                        if (!r.Apply) { all = false; break; }
                    }
                    k.Checked = any && all;
                }
            }
            catch (Exception ex) { Log.Swallow(ex); }
            finally { _settingKindBoxes = false; }
        }

        // All, none, or the filled square for a partial choice.
        private void UpdateFixAllBox()
        {
            if (_fixAllBox == null || _syncingKind) return;
            try
            {
                int fixable = 0, ticked = 0;
                foreach (FixRow r in FindingsGrid.Items.OfType<FixRow>())
                {
                    if (!r.Fixable) continue;
                    fixable++;
                    if (r.Apply) ticked++;
                }
                _fixAllBox.IsChecked = ticked == 0 ? (bool?)false
                                     : ticked == fixable ? (bool?)true
                                     : null;
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        // CLICKING A KIND SHOWS ONLY THAT KIND. Fifteen bad locators among four thousand spellings are
        // not findable by scrolling, and telling an operator to type the answer into a row they cannot
        // reach is not an instruction. Clicking again shows everything.
        private string _filterKind;

        private void Kind_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            var kind = el == null ? null : el.DataContext as ProblemKind;
            if (kind == null) return;

            ApplyKindFilter(string.Equals(_filterKind, kind.Name, StringComparison.Ordinal) ? null : kind.Name);
        }

        private void Btn_ShowAll_Click(object sender, RoutedEventArgs e)
        {
            ApplyKindFilter(null);
        }

        private void ApplyKindFilter(string kindName)
        {
            _filterKind = kindName;

            foreach (ProblemKind k in _kinds)
                k.Selected = kindName != null && string.Equals(k.Name, kindName, StringComparison.Ordinal);

            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_rows);
            if (view != null)
            {
                view.Filter = kindName == null
                    ? (Predicate<object>)null
                    : o => { var r = o as FixRow; return r != null && r.Has(kindName); };
                view.Refresh();
            }

            if (Btn_ShowAll != null)
                Btn_ShowAll.Visibility = kindName == null ? Visibility.Collapsed : Visibility.Visible;

            TB_KindsSummary.Text = kindName == null
                ? "Click a kind to show only its QSOs. Checking a kind selects them all; you can then "
                  + "uncheck any single one before pressing Fix."
                : "Showing only: " + kindName + ".  Click it again, or Show all kinds, to see the rest.";
        }

        private void UpdateFixButton()
        {
            if (Btn_Fix == null) return;
            if (_syncingKind) return;          // one update at the end of the sweep, not one per row
            int n = _rows.Count(r => r.Apply);
            Btn_Fix.IsEnabled = n > 0;
            Btn_Fix.Content = n == 0
                ? "Fix selected"
                : "Fix " + n.ToString("N0") + " selected";
        }

        // The turning ring. Kept as a field so it can be stopped again - an animation left running on a
        // hidden element goes on waking the render thread for as long as the window is open.
        private System.Windows.Media.Animation.Storyboard _fixSpin;

        private void ShowFixOverlay(int total, string title = "Fixing your log…", string unit = "QSOs")
        {
            TB_FixTitle.Text = title;
            _overlayUnit = unit;
            TB_FixProgress.Text = "0 of " + total.ToString("N0") + " " + unit;
            PB_Fix.Value = 0;
            FixOverlay.Visibility = Visibility.Visible;

            if (_fixSpin == null)
            {
                var turn = new System.Windows.Media.Animation.DoubleAnimation(0, 360,
                    new Duration(TimeSpan.FromSeconds(1.1)))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                System.Windows.Media.Animation.Storyboard.SetTarget(turn, FixSpin);
                System.Windows.Media.Animation.Storyboard.SetTargetProperty(turn,
                    new PropertyPath(System.Windows.Media.RotateTransform.AngleProperty));
                _fixSpin = new System.Windows.Media.Animation.Storyboard();
                _fixSpin.Children.Add(turn);
            }
            _fixSpin.Begin(this, true);
        }

        private string _overlayUnit = "QSOs";

        private void UpdateFixOverlay(int done, int total)
        {
            TB_FixProgress.Text = done.ToString("N0") + " of " + total.ToString("N0") + " " + _overlayUnit;
            PB_Fix.Value = total > 0 ? 100.0 * done / total : 0;
        }

        private void HideFixOverlay()
        {
            if (_fixSpin != null) _fixSpin.Stop(this);
            FixOverlay.Visibility = Visibility.Collapsed;
        }

        // The one problem a database on this machine cannot answer: where the other station actually
        // was. QRZ can, so it is asked - but only when the operator presses the button, and only for
        // the rows that need it.
        //
        // QRZ answers with the grid the station has TODAY, which is not necessarily where it was during
        // a QSO made twenty years ago. That is precisely why the answer becomes an ordinary suggestion,
        // to be ticked or ignored, rather than being written.
        //
        // NOTHING ON SCREEN NAMES QRZ. The evidence column says "callsign lookup" and the overlay says
        // "Looking up the locators" - the operator still learns that the grid was fetched rather than
        // worked out, and still sees which rows rest on it, without the window advertising which service
        // this station has an account with. Asked for by the operator.
        // The two halves of "Different country". Named here because the scan writes them, Rank orders
        // them and the kinds panel explains them - three places that must never spell one differently.
        // SHORT ON THE CHIP, FULL IN THE SENTENCE BESIDE IT. The chips are sized by their longest label
        // and sit in a column, so "Different country — cty.dat and Club Log agree" stretched the whole
        // panel to fit one of them. The naming of the two files belongs in the explanation on the
        // right, where there is room for it.
        private const string CountryBothAgree = "Different country — safe to accept";
        private const string CountryNeedsDecision = "Different country — needs a decision";

        private const string LocatorProblem = "DX Locator is wrong";

        // Run as part of the scan, not from a button. A wrong locator is the one fault this machine
        // cannot answer on its own, and asking the operator to press something to find out what the
        // answer might be is asking them to do the program's work. So the moment the scan knows which
        // locators are wrong, it goes and looks - and whatever QRZ says arrives in the green half like
        // any other suggestion, to be ticked or ignored.
        //
        // Quiet when there is nothing to ask about, no subscription, or no network: QrzGridFor returns
        // null for all of those and the rows simply stay by-hand, which is what they were anyway.
        // ASKED ONCE PER CALLSIGN, for as long as this window is open. The scan runs again after every
        // Fix - that is how the list shrinks to what is still wrong - and the lookup sat inside it, so
        // every fix sent the same callsigns back to QRZ. The ones QRZ has no grid for are exactly the
        // ones that never leave the list, so those were re-asked every single time.
        //
        // A miss is remembered as well as a hit: "QRZ has nothing for this call" is an answer, and
        // asking again in the same sitting will not change it.
        private readonly Dictionary<string, string> _qrzGrid =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private async Task FillLocatorsFromQrz(List<Finding> found)
        {
            List<Finding> rows = found
                .Where(f => string.Equals(f.Problem, LocatorProblem, StringComparison.Ordinal)
                            && !f.Fixable && f.Qso != null && !string.IsNullOrWhiteSpace(f.Qso.DXCall))
                .ToList();
            if (rows.Count == 0) return;

            List<string> toAsk = rows.Select(f => f.Qso.DXCall.Trim())
                                     .Where(c => !_qrzGrid.ContainsKey(c))
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .ToList();

            if (toAsk.Count > 0)
            {
                ShowFixOverlay(toAsk.Count, "Looking up the locators…", "callsigns");
                int done = 0;
                foreach (string call in toAsk)
                {
                    string g = null;
                    try { g = await MainWindow.QrzGridFor(call); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                    _qrzGrid[call] = g;                 // null is remembered too
                    UpdateFixOverlay(++done, toAsk.Count);
                }
                HideFixOverlay();
            }

            int asked = 0;
            try
            {
                foreach (Finding f in rows)
                {
                    string grid;
                    if (!_qrzGrid.TryGetValue(f.Qso.DXCall.Trim(), out grid)) grid = null;
                    asked++;

                    if (string.IsNullOrWhiteSpace(grid) || !LegalLocator.IsMatch(grid))
                    {
                        // WHY there is nothing to offer, said on the row itself. "No suggestion" alone
                        // leaves an operator wondering whether the program failed or simply cannot
                        // know - and here it is the second: we asked, and QRZ had nothing.
                        f.Evidence = "no grid found";
                        f.Suggested = "Check by hand — no grid found for this call";
                        continue;
                    }
                    f.Field = "DXLocator";
                    f.NewValue = grid;
                    f.Suggested = grid;
                    // Named, because QRZ knows where a station is TODAY and not necessarily where it
                    // was during an old QSO. The operator seeing "QRZ" against a 2003 contact knows
                    // exactly how much weight to give it.
                    f.Evidence = "callsign lookup";
                    f.Fixable = true;
                }
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
            }
        }

        private async void Btn_Fix_Click(object sender, RoutedEventArgs e)
        {
            // THE ROWS DECIDE, NOT THE FINDINGS. A row can be worth writing for either of two reasons:
            // the program proposed something, or the operator typed something. Gating on findings alone
            // threw away every hand-typed answer - a checked row whose only finding was report-only
            // (Club Log's "did not count", say) fell straight through this and Fix appeared to do
            // nothing at all, which is exactly what it did.
            // AND WHEN THERE IS NOTHING TO WRITE, SAY SO. Both of these used to return in silence, so
            // pressing Fix with nothing ticked - or with rows ticked that carry no answer yet - looked
            // exactly like a Fix that had run and reported nothing.
            List<FixRow> rows = _rows.Where(r => r.Apply && r.Fixable && r.Qso != null).ToList();
            if (rows.Count == 0)
            {
                HolyMessageBox.Show("Nothing was written, because nothing is ticked.\n\n"
                    + "Tick a kind of problem to take all of its QSOs, or tick single rows in the table.",
                    "Log Fixer", HolyMsgType.Info, this);
                return;
            }

            List<Finding> chosen = rows.SelectMany(r => r.Findings).Where(f => f.Fixable).ToList();
            int typedCells = rows.Sum(r => r.Cells.Count(kv => kv.Value.UserEdited
                                                               && kv.Value.Proposed.Trim().Length > 0));

            // A REMOVAL IS NOT A CORRECTION, and counting it as one would tell the operator that 284
            // values are about to be written when 284 contacts are about to disappear. The two are
            // separated here so the confirmation can name each for what it is, and so the writing loop
            // never bothers updating fields on a QSO it is about to take out.
            var removals = new HashSet<FixRow>(rows.Where(r => r.Findings.Any(f => f.Fixable && f.Deletes)));

            // AND THE COPIES THAT CANNOT GO YET. A group whose comments disagree is held back from this
            // whole run: the plain copies are removed first, then the operator is shown the comments
            // side by side and asked which to keep. Nothing about these rows is written or deleted
            // until he has answered.
            var pending = new List<DupGroup>();
            foreach (FixRow r in rows)
            {
                Finding d = r.Findings.FirstOrDefault(f => f.Fixable && f.Deletes && f.Group != null
                                                           && f.Group.NeedsChoice);
                if (d != null && !pending.Contains(d.Group)) pending.Add(d.Group);
            }
            var held = new HashSet<FixRow>(rows.Where(r => r.Findings.Any(f => f.Fixable && f.Deletes
                                                            && f.Group != null && f.Group.NeedsChoice)));
            removals.ExceptWith(held);

            if (chosen.Count == 0 && typedCells == 0)
            {
                HolyMessageBox.Show("Nothing was written: the ticked rows have no value to write.\n\n"
                    + "These are the ones the program cannot answer for you - type the right value into "
                    + "the green row and the row will then be written with the rest.",
                    "Log Fixer", HolyMsgType.Info, this);
                return;
            }

            var dal = DataAccess.GetInstance();
            if (dal == null)
            {
                HolyMessageBox.ShowWarning("The log database is not open.", "Log Fixer", this);
                return;
            }

            // The count of QSOs, not of findings: two problems on one contact are one row rewritten,
            // and "83 corrections" against "80 QSOs" would look like a discrepancy. Hand-typed values
            // are corrections too, and are counted with the rest.
            int qsoCount = rows.Count - removals.Count - held.Count;
            int fixes = chosen.Count(f => !f.Deletes) + typedCells;

            // WHAT IS ABOUT TO HAPPEN, IN THE ORDER IT MATTERS. Contacts leaving the log is the graver
            // half, so it is the first thing said and it is said in plain words - "removed from the
            // log", not "fixed".
            string what = "";
            if (removals.Count > 0)
                what += removals.Count.ToString("N0") + " duplicate contact"
                        + (removals.Count == 1 ? " will be" : "s will be") + " REMOVED from the log";
            if (fixes > 0)
            {
                if (what.Length > 0) what += ", and ";
                what += fixes.ToString("N0") + " correction" + (fixes == 1 ? "" : "s")
                        + " will be written to " + qsoCount.ToString("N0") + " QSO" + (qsoCount == 1 ? "" : "s");
            }
            if (what.Length == 0) what = "Nothing will be written yet";
            if (pending.Count > 0)
                what += ".\n\nThen you will be asked about " + pending.Count.ToString("N0") + " group"
                        + (pending.Count == 1 ? "" : "s")
                        + " where the copies carry different comments — nothing in "
                        + (pending.Count == 1 ? "it is" : "those is") + " removed until you have chosen";

            if (!HolyMessageBox.ShowConfirm(
                    what
                    // Backups & Restore DOES list these now - they go into the Backups folder with the
                    // daily ones and restore by the same button - so the message can name the window
                    // rather than describing a file-rename.
                    + ".\n\nYour WHOLE DATABASE — every log in it, not only this one — is copied first, "
                    + "into\n" + (string.IsNullOrEmpty(dal.BackupsFolder) ? "your Backups folder"
                                                                         : dal.BackupsFolder)
                    + "\nas logDB.db.pre-fix-<date>.bak, where Tools > Backups & Restore lists it "
                    + "alongside the daily backups and can put it back for you. Restoring it undoes "
                    + "everything done since.\n\nFix them now?",
                    "Log Fixer", HolyMsgType.Warning, this))
                return;

            string backup = SaveBackup(dal);
            if (backup == null &&
                !HolyMessageBox.ShowConfirm("The safety copy of the log could not be written.\n\nFix them "
                    + "anyway?", "Log Fixer", HolyMsgType.Warning, this))
                return;

            Btn_Fix.IsEnabled = false;

            // Driven by the CHECKED ROWS. Grouping the findings instead skipped any row that had no
            // fixable finding on it - which is precisely the row somebody typed an answer into.
            int total = rows.Count;
            ShowFixOverlay(total);

            int written = 0;
            var gone = new List<QSO>();      // the contacts actually removed, for the windows showing them
            try
            {

                // ONE TRANSACTION, ONE TRIP TO THE BACKGROUND. This used to await a separate Task per
                // QSO, each committing on its own: 4,376 spelling corrections meant 4,376 commits and
                // 4,376 hops back to the UI thread, and the window sat on "fixing…" for minutes.
                await Task.Run(() => dal.RunInTransaction(() =>
                {
                    foreach (FixRow r in rows)
                    {
                        QSO qso = r.Qso;

                        // Held back for the comment question: not written, not deleted, not counted.
                        if (held.Contains(r)) continue;

                        // A contact being taken out is not also corrected: writing fields into a row
                        // that is about to be deleted is work nobody will ever see.
                        if (removals.Contains(r))
                        {
                            // The one comment the group had moves to the contact that stays, so a note
                            // is never deleted along with the row that happened to carry it.
                            Finding d = r.Findings.First(f => f.Fixable && f.Deletes);
                            if (d.Group != null)
                            {
                                string carry = (d.Group.ChosenComment ?? string.Empty).Trim();
                                if (carry.Length > 0 && !string.Equals(carry,
                                        (d.Group.Keep.Comment ?? string.Empty).Trim(), StringComparison.Ordinal))
                                {
                                    d.Group.Keep.Comment = carry;
                                    dal.Update(d.Group.Keep);
                                }
                            }

                            dal.Delete(qso.id);
                            gone.Add(qso);
                            written++;
                            if (written % 50 == 0 || written == total)
                            {
                                int at = written;
                                Dispatcher.BeginInvoke(new Action(() => UpdateFixOverlay(at, total)));
                            }
                            continue;
                        }

                        foreach (Finding f in r.Findings) if (f.Fixable) ApplyTo(qso, f);

                        // Anything the operator typed is written AFTER the suggestions, so their word
                        // is the last one. A suggestion is applied by the finding that made it, which
                        // knows to carry the entity number and the zones along with a country name; a
                        // typed value goes to that one field alone.
                        foreach (var kv in r.Cells)
                            if (kv.Value.UserEdited && kv.Value.Proposed.Trim().Length > 0)
                                WriteField(qso, kv.Key, kv.Value.Proposed.Trim());

                        dal.Update(qso);
                        written++;

                        // A count that moves, so a long job is visibly a long job and not a dead one.
                        if (written % 50 == 0 || written == total)
                        {
                            int done = written;
                            Dispatcher.BeginInvoke(new Action(() => UpdateFixOverlay(done, total)));
                        }
                    }
                }));
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                HideFixOverlay();
                HolyMessageBox.ShowError("Something went wrong while writing the corrections:\n\n" + ex.Message
                    + "\n\nNothing was written — the whole job is one transaction, so the log is exactly "
                    + "as it was."
                    + (backup != null ? "\n\nThe copy taken before it started is in:\n" + backup : ""),
                    "Log Fixer", this);
                await RunCheck();
                return;
            }

            // Down before the message box, so the operator is not reading "fixing" behind "fixed".
            HideFixOverlay();

            // ── STEP TWO: the groups whose comments disagree ─────────────────────────────────────
            //
            // The plain copies are gone by now. What is left is the handful the program refuses to
            // decide: one contact, two different things written about it. The same screen Tools >
            // Remove Duplicates uses puts them side by side with a Keep box against each comment.
            // A group he ticks nothing in is left whole - both contacts stay in the log.
            if (pending.Count > 0)
            {
                var choose = new DuplicatesWindow(pending) { Owner = this };
                if (choose.ShowDialog() == true)
                {
                    foreach (DupGroup g in pending)
                    {
                        if (g.Skipped) continue;
                        try
                        {
                            string keepText = (g.ChosenComment ?? string.Empty).Trim();
                            if (keepText.Length > 0 && !string.Equals(keepText,
                                    (g.Keep.Comment ?? string.Empty).Trim(), StringComparison.Ordinal))
                            {
                                g.Keep.Comment = keepText;
                                dal.Update(g.Keep);
                            }
                            foreach (QSO extra in g.Extras)
                            {
                                dal.Delete(extra.id);
                                gone.Add(extra);
                                written++;
                            }
                        }
                        catch (Exception swallowed) { Log.Swallow(swallowed); }
                    }
                }
            }

            // THE PATH IS CLICKABLE, so "where is my safety copy" is answered by pressing it rather
            // than by copying a line of text into Explorer. Clicking selects the file in its folder,
            // which is what somebody who wants to keep it, move it or restore it needs to see.
            // SAY WHAT IT IS FOR. "Your database as it was before this" over a long path told the
            // operator a fact about a file and left them to work out what to do with it. What they
            // want to know is how to change their mind - so the sentence is about undoing, and the
            // path underneath is the evidence that it exists.
            // THE CONTACTS THAT ARE GONE MUST GO FROM EVERY WINDOW SHOWING THEM. This window's own list
            // first, or the re-check below would scan rows that no longer exist and report them as
            // duplicates all over again; then the log table and the Log Workshop, which are holding QSO
            // objects read before the delete and would otherwise show contacts the database has lost.
            if (gone.Count > 0)
            {
                var goneIds = new HashSet<int>(gone.Select(q => q.id));
                _qsos.RemoveAll(q => goneIds.Contains(q.id));

                try
                {
                    // ReloadActiveLogQsos re-reads the log table AND re-points the Log Workshop at the
                    // fresh collection, so one call covers both windows.
                    var main = Application.Current == null ? null
                             : Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                    if (main != null) main.ReloadActiveLogQsos();
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

            int removed = gone.Count;
            int corrected = written - removed;
            string report =
                  (removed > 0 ? removed.ToString("N0") + " duplicate contact"
                                 + (removed == 1 ? "" : "s") + " removed from the log." : "")
                + (removed > 0 && corrected > 0 ? "\n" : "")
                + (corrected > 0 ? corrected.ToString("N0") + " QSO" + (corrected == 1 ? "" : "s")
                                   + " fixed." : "")
                + "\n\nClose and reopen the log window to see the new values."
                + (backup == null ? "" :
                    "\n\nChanged your mind? A copy of your database from just before this was saved. "
                    + "Open **Tools → Backups & Restore**, pick the newest one — it says "
                    + "**before a fix** — and restore it.");

            if (backup != null)
            {
                var links = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>(
                        "That copy is this file, if you want to see it:", backup)
                };
                HolyMessageBox.ShowWithLinks(report, "Log Fixer", HolyMsgType.Success, this,
                                             links, ShowInFolder, 620);
            }
            else
            {
                HolyMessageBox.ShowSuccess(report, "Log Fixer", this);
            }

            // Re-check, so what is left on screen is what is still wrong.
            await RunCheck();
        }

        private static void ApplyTo(QSO qso, Finding f)
        {
            switch (f.Field)
            {
                case "DXCall":
                    qso.DXCall = f.NewValue;
                    break;
                case "Band":
                    qso.Band = f.NewValue;
                    break;
                // The date and the time are held as the operator READS them - "18-03-2012", "21:42" -
                // because that same string is what the green cell shows and what a typed answer arrives
                // as. Converted here, by the same pair of functions the typed path uses, so a suggestion
                // and a hand-typed value can never be stored in different formats.
                case "Mode":
                    qso.Mode = f.NewValue;
                    break;
                case "Date":
                    { string d = UnformatDate(f.NewValue); if (d != null) qso.Date = d; }
                    break;
                case "Time":
                    { string t = UnformatTime(f.NewValue); if (t != null) qso.Time = t; }
                    break;
                case "Continent":
                    qso.Continent = f.NewValue;
                    break;
                case "DXLocator":
                    qso.DXLocator = f.NewValue;
                    break;
                case "Activity":
                    // Moved, not copied: the comment held the reference only because there was nowhere
                    // else to put it, and leaving a copy behind would show it twice in every export.
                    if (f.Program == "IOTA") qso.Iota = f.NewValue;
                    else if (f.Program == "SOTA") qso.SotaRef = f.NewValue;
                    else if (f.Program == "POTA") qso.PotaRef = f.NewValue;
                    else if (f.Program == "WWFF") qso.WwffRef = f.NewValue;
                    qso.Comment = string.Empty;
                    break;
                case "CountryName":
                    // Spelling only. The entity number is already right, so nothing else moves - and
                    // in particular the zones are left alone, because they belong to the entity and
                    // the entity has not changed.
                    qso.Country = f.NewValue;
                    break;
                case "Country":
                    qso.Country = f.NewValue;
                    // The entity travels with the name. Without this the log said one country and
                    // counted as another, which is the harder error of the two to ever notice.
                    if (f.NewCode > 0) qso.DxccCode = f.NewCode;
                    if (!string.IsNullOrEmpty(f.NewDxcc)) qso.DXCC = f.NewDxcc;
                    if (!string.IsNullOrEmpty(f.NewContinent)) qso.Continent = f.NewContinent;
                    // The zones belong to the entity, so a country correction carries them along - a
                    // Wake Island QSO cannot keep the CQ zone of the United States.
                    if (f.NewCq > 0) qso.CQZone = f.NewCq.ToString();
                    if (f.NewItu > 0) qso.ITUZone = f.NewItu.ToString();
                    break;
            }
        }

        // Opens Explorer with the file already selected, rather than merely opening the folder and
        // leaving the operator to find it among the database's neighbours.
        private static void ShowInFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (File.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // A plain file copy of the WHOLE DATABASE - every log in it, not only the one being fixed,
        // because logDB.db is one file and there is no way to copy a single log out of it. Restoring
        // it therefore undoes everything done since, which is why the confirmation says so.
        //
        // Named like the program's other safety copies, but note that Backups & Restore lists only the
        // dated automatic backups (logDB-yyyy-MM-dd.db) and does NOT show these - the messages point at
        // the file itself rather than at that window.
        // Returns the path, or null when it could not be made.
        private static string SaveBackup(DataAccess dal)
        {
            try
            {
                string path = dal.DbPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string backup = dal.SafetyCopyPath("fix");
                if (backup == null) return null;
                File.Copy(path, backup, false);
                return backup;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // Double-clicking a row opens that QSO in the full editor, which is the only way to settle a
        // "Check by hand" finding - a missing band nobody can derive, a callsign with a note buried in
        // it. Saving there writes through DataAccess, so the log is updated wherever it is displayed;
        // the scan is then run again so the row disappears if the edit settled it.
        private async void FindingsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // A FixRow, not a Finding. The table used to hold one row per problem and this cast said
            // Finding; when it became one row per QSO the cast started failing on every double-click
            // and the window simply did nothing - while the line at the foot went on inviting it.
            FixRow row = FindingsGrid.SelectedItem as FixRow;

            // Double-clicking usually lands on a cell rather than on an already-selected row, so fall
            // back to whatever row was actually under the mouse.
            if (row == null)
            {
                var cell = GridCopy.CellFrom(e.OriginalSource);
                if (cell != null) row = cell.DataContext as FixRow;
            }
            if (row == null || row.Qso == null) return;

            try
            {
                var editor = new QsoEditWindow(row.Qso) { Owner = this };
                bool? saved = editor.ShowDialog();
                if (saved == true) await RunCheck();
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                HolyMessageBox.ShowWarning("This QSO could not be opened for editing:\n\n" + ex.Message,
                                           "Log Fixer", this);
            }
        }

        // ESCAPE CLEARS THE HIGHLIGHT, and does nothing else. A selected row could not be deselected at
        // all before - clicking elsewhere in a DataGrid only moves the selection - and Escape did the
        // one thing it must not, which is close a window holding a page of ticks.
        //
        // Handled in Preview so it is caught before the grid or a cell's text box can act on it, and
        // marked handled so it never reaches anything else. Escape now has exactly one meaning here.
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            if (FindingsGrid != null)
            {
                FindingsGrid.UnselectAll();
                FindingsGrid.UnselectAllCells();
            }
            e.Handled = true;
        }

        // This table has no right-click menu of its own, so the two copy commands ARE its menu. Built
        // fresh on every click because the cell under the mouse is part of what it offers.
        private void FindingsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var menu = new ContextMenu { FontSize = 16 };
            var cell = GridCopy.CellFrom(e.OriginalSource);
            menu.Items.Add(GridCopy.CopyCellItem(GridCopy.TextOf(cell)));
            menu.Items.Add(GridCopy.CopyRowsItem(FindingsGrid));
            FindingsGrid.ContextMenu = menu;
        }

        private void Btn_Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

