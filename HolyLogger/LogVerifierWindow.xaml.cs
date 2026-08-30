using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
                    case WrongCountryCode:
                        return "The country name is right — only its code is wrong, so the QSO counts for another country";
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
        // WHICH OF THE TWO LINES THE AI CAME DOWN ON. Nothing until it has been asked; then the
        // upper half (the log as it stands) or the lower half (what HolyLogger proposes), or Unsure
        // when it looked and could not tell - which is an answer, and is shown as one.
        // NEITHER: the AI worked the country out and it is not the one in the log NOR the one
        // HolyLogger proposes. It wears the same face as Unsure - a "?" on the row, nothing tickable,
        // nothing written - because in both cases the program has no value it may safely put in. What
        // differs is the reason, and there it names the country it believes.
        private enum AiSide { None, Now, Then, Neither, Unsure }

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

            // NOTHING HERE TO ANSWER. The lower half is a text box because a fault the program cannot
            // answer is settled by typing into it - but a list of contacts already put right is a
            // record, not a question, and a caret blinking in it invites an answer nobody reads.
            public bool ReadOnly { get; set; }

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

            // THE AI'S VOTE, WORN BY THE CELL. The row holds the verdict; each cell that is actually
            // part of the argument wears it, and the cells that are only along for the ride - the
            // date, the callsign - keep their ordinary colours. A whole row going green would say the
            // AI had approved the contact, and it has approved one field of it.
            private AiSide ai = AiSide.None;
            public void SetAi(AiSide side)
            {
                if (ai == side) return;
                ai = side;
                Raise("NowBg"); Raise("ThenBg"); Raise("NowFg"); Raise("ThenFg");
            }

            // A CELL THAT IS NOT A VALUE. The history list needs one column that says nothing about the
            // contact at all - it only names the two lines, was and now - and painting it in the red
            // and green of the argument going on beside it would make a label look like a verdict.
            // Set, it wins over everything below: both halves take this colour and neither is red,
            // green or grey.
            public Brush Tint;

            // Only a cell with something at stake changes colour: one that is red, or one that has a
            // proposal under it. Anything else is untouched log data and stays looking like it.
            private bool InPlay { get { return Wrong || Proposed.Length > 0; } }

            // Filled only where there is something to say. Everywhere else the cell is transparent and
            // the row reads as ordinary log data, which is exactly what it is.
            public Brush NowBg
            {
                get
                {
                    if (Tint != null) return Tint;
                    if (InPlay && ai == AiSide.Now) return RightBg;
                    if (InPlay && ai == AiSide.Then) return DeadBg;
                    return Wrong ? WrongBg : Brushes.Transparent;
                }
            }
            public Brush ThenBg
            {
                get
                {
                    if (Tint != null) return Tint;
                    if (Proposed.Length == 0) return Brushes.Transparent;
                    return ai == AiSide.Now ? DeadBg : RightBg;
                }
            }
            public Brush NowFg
            {
                get
                {
                    if (Tint != null) return LabelFg;
                    if (InPlay && ai == AiSide.Now) return RightFg;
                    if (InPlay && ai == AiSide.Then) return DeadFg;
                    return Wrong ? WrongFg : DimFg;
                }
            }
            // The lower half's text used to be green in the template itself, which left a greyed-out
            // proposal reading as green writing on a grey ground - the one combination that says
            // nothing at all.
            public Brush ThenFg
            {
                get { return Tint != null ? LabelGreenFg : ai == AiSide.Now ? DeadFg : RightFg; }
            }

            // THE UPPER LINE IS NORMALLY LOG DATA and log data is not shouted. The label column is the
            // exception: both its words are the heading of a line, and a heading in the same weight as
            // the values it names is a heading nobody sees.
            public FontWeight NowWeight { get { return Tint != null ? FontWeights.Bold : FontWeights.Normal; } }
            public FontWeight ThenWeight { get { return Proposed.Length > 0 ? FontWeights.Bold : FontWeights.Normal; } }

            // A proper alarm red, not the pale wash it started as - a fault has to look like one.
            private static readonly Brush WrongBg = Freeze(Color.FromRgb(0xFF, 0x8A, 0x8A));
            private static readonly Brush RightBg = Freeze(Color.FromRgb(0xA5, 0xE8, 0xA8));
            private static readonly Brush WrongFg = Freeze(Color.FromRgb(0x5D, 0x00, 0x00));
            private static readonly Brush DimFg = Freeze(Color.FromRgb(0x33, 0x33, 0x33));
            // THE LOSING SIDE. Grey, not white: the value is still there to be read - the operator may
            // well disagree with the AI - it has simply stopped being the answer.
            private static readonly Brush DeadBg = Freeze(Color.FromRgb(0xDE, 0xDE, 0xDE));
            private static readonly Brush DeadFg = Freeze(Color.FromRgb(0x6E, 0x6E, 0x6E));
            private static readonly Brush RightFg = Freeze(Color.FromRgb(0x0B, 0x4A, 0x0E));
            // The history column: a pale blue that belongs to neither side of the argument, and a navy
            // to write on it in.
            public static readonly Brush LabelBg = Freeze(Color.FromRgb(0xD6, 0xE7, 0xFA));
            // BLUE AND GREEN THAT LOOK IT. The first pair of these was a navy and a bottle green, and
            // at this size on a pale blue ground both read as black - two words the same colour, which
            // is the one thing this column exists not to be.
            private static readonly Brush LabelFg = Freeze(Color.FromRgb(0x15, 0x46, 0xA0));
            private static readonly Brush LabelGreenFg = Freeze(Color.FromRgb(0x11, 0xA5, 0x2B));
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

            // ── PUTTING IT BACK ─────────────────────────────────────────────────────────────
            //
            // What the Log Fixer recorded about this contact, oldest press first, carried on the row
            // in the list of contacts already corrected. A revert writes these values back, so it is
            // reading from the log's own record rather than working the old value out from the new
            // one - which cannot be done at all once a country name has been replaced.
            public List<FixNote> History;

            // A contact fixed before any of this was written down has nothing to go back TO, so its
            // box is not offered. Not a grey box either - see the tick column - but an empty cell.
            public bool CanRevert { get { return History != null && History.Count > 0; } }

            private bool revert;
            public bool Revert
            {
                get { return revert; }
                set
                {
                    if (revert == value) return;
                    revert = value;
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Revert"));
                    if (RevertChanged != null) RevertChanged();
                }
            }
            public Action RevertChanged;

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
                // AND NOT WHEN THE AI HAS BACKED THE LOG. There is nothing left to write: the value
                // the row would put in is the one the screen has just greyed out. A row left tickable
                // here is a trap - tick the lot, press Fix, and the contact the AI said was right gets
                // overwritten with the one it rejected.
                Fixable = any && !AiBacksLog;
            }

            // WHAT THE AI SAID ABOUT THIS ROW, AND WHAT THE ROW DOES ABOUT IT.
            //
            // The verdict is worn by every cell that is part of the argument, so the two halves change
            // colour together across the country, its entity number and its continent rather than one
            // cell at a time. And when the vote goes to the log, the row's tick is dropped as well as
            // greyed: a tick set before the AI was asked must not survive the answer.
            private AiSide aiSide = AiSide.None;
            public string AiReason { get; private set; }

            // WHICH AI SAID IT, kept on the row and not once for the window. Two services can answer
            // the same log in one sitting - the free allowance runs out and the paid one takes over,
            // or he simply wants a second opinion - and they do NOT always agree: Gemini said 5 and 1
            // where OpenRouter said 4 and 2 on the same six QSOs. A verdict whose author is not
            // recorded cannot be weighed later, or traced when it turns out to be wrong.
            public string AiWho { get; private set; }
            public bool AiBacksLog { get { return aiSide == AiSide.Now; } }
            public bool AiAsked { get { return aiSide != AiSide.None; } }
            public bool AiCouldNotTell { get { return aiSide == AiSide.Unsure; } }
            public bool AiSaysNeither { get { return aiSide == AiSide.Neither; } }

            // THE VERDICT IN WORDS. On screen it is a tick standing beside one of the two values,
            // which says everything and can be written down nowhere. The report needs it as a
            // sentence, and the reasoning it came with is the whole point of putting it there.
            public string AiSaid
            {
                get
                {
                    if (aiSide == AiSide.None) return string.Empty;

                    // "BACKS THE LOG" IS THE LANGUAGE OF A COMMITTEE VOTE, and a man reading a
                    // report about his own log should not have to work out who is backing whom. What
                    // he wants to know is which of the two values is right, so that is what it says -
                    // in the same words as the tally he was shown when the run finished.
                    string who = aiSide == AiSide.Now ? "your log is correct"
                               : aiSide == AiSide.Then ? "HolyLogger's correction is right"
                               : aiSide == AiSide.Neither ? "neither country is right"
                               : "no answer";

                    return AiReason.Length > 0 ? who + " - " + AiReason : who;
                }
            }

            // ── ONE VERDICT PER AI, KEPT ────────────────────────────────────────────────────────
            //
            // A second opinion is only worth asking for if the first one is still there to compare it
            // with. Each AI's answer is kept under its own name (service AND model - OpenRouter alone
            // offers dozens), so asking again does not overwrite what the last one said: where they
            // agree the row is settled, and where they split THAT is the row worth reading. Asking the
            // same model twice replaces its own answer rather than counting it twice.
            public sealed class AiVerdict
            {
                public string Who;
                public AiSide Side;
                public string Reason;
            }

            private readonly List<AiVerdict> _verdicts = new List<AiVerdict>();
            public List<AiVerdict> AiVerdictList { get { return _verdicts; } }
            public int AiCount { get { return _verdicts.Count; } }

            public bool AskedBy(string who)
            {
                if (string.IsNullOrWhiteSpace(who)) return false;
                foreach (AiVerdict v in _verdicts)
                    if (string.Equals(v.Who, who.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            // True when the verdicts do not all say the same thing. The row the operator must read.
            public bool AiSplit { get; private set; }

            // What the AI column says once more than one has answered. One answer keeps the plain tick
            // it always had; there is nothing to tally.
            public string AiTally
            {
                get
                {
                    if (_verdicts.Count < 2) return string.Empty;
                    if (!AiSplit) return _verdicts.Count + " AI agree";

                    int top = 0, second = 0;
                    foreach (AiSide s in new[] { AiSide.Now, AiSide.Then, AiSide.Neither, AiSide.Unsure })
                    {
                        int n = 0;
                        foreach (AiVerdict v in _verdicts) if (v.Side == s) n++;
                        if (n > top) { second = top; top = n; }
                        else if (n > second) second = n;
                    }
                    return "AI split " + top + " – " + second;
                }
            }

            public Visibility AiTallyVisible
            {
                get { return _verdicts.Count >= 2 ? Visibility.Visible : Visibility.Collapsed; }
            }

            public void SetAi(AiSide side, string reason, string who)
            {
                who = (who ?? string.Empty).Trim();

                AiVerdict mine = null;
                foreach (AiVerdict v in _verdicts)
                    if (who.Length > 0 && string.Equals(v.Who, who, StringComparison.OrdinalIgnoreCase)) { mine = v; break; }

                if (mine == null) _verdicts.Add(mine = new AiVerdict { Who = who });
                mine.Side = side;
                mine.Reason = (reason ?? string.Empty).Trim();

                // THE ROW'S OWN SIDE IS THE MAJORITY OF THEM, and a tie is not a verdict: with one AI
                // for the log and one for the correction there is no answer to paint, so the row is
                // marked unsure and the split is what the column shows. Anything else would put a
                // green tick on a value half the witnesses rejected.
                int nNow = 0, nThen = 0, nNeither = 0, nUnsure = 0;
                foreach (AiVerdict v in _verdicts)
                {
                    if (v.Side == AiSide.Now) nNow++;
                    else if (v.Side == AiSide.Then) nThen++;
                    else if (v.Side == AiSide.Neither) nNeither++;
                    else if (v.Side == AiSide.Unsure) nUnsure++;
                }

                int best = Math.Max(Math.Max(nNow, nThen), Math.Max(nNeither, nUnsure));
                int winners = (nNow == best ? 1 : 0) + (nThen == best ? 1 : 0)
                            + (nNeither == best ? 1 : 0) + (nUnsure == best ? 1 : 0);

                AiSplit = _verdicts.Count > 1 && best < _verdicts.Count;

                aiSide = winners > 1 ? AiSide.Unsure
                       : nNow == best ? AiSide.Now
                       : nThen == best ? AiSide.Then
                       : nNeither == best ? AiSide.Neither
                       : AiSide.Unsure;

                // The reason shown on the row is the winning side's; the ? panel lists them all.
                AiReason = string.Empty;
                foreach (AiVerdict v in _verdicts)
                    if (v.Side == aiSide && v.Reason.Length > 0) { AiReason = v.Reason; break; }

                var names = new List<string>();
                foreach (AiVerdict v in _verdicts)
                    if (v.Who.Length > 0 && !names.Contains(v.Who)) names.Add(v.Who);
                AiWho = string.Join(", ", names);

                foreach (var kv in Cells) kv.Value.SetAi(aiSide);

                if (aiSide == AiSide.Now) Apply = false;
                Recompute();

                Raise("AiOnNow"); Raise("AiOnThen"); Raise("AiUnsure"); Raise("AiNeither");
                Raise("AiReason"); Raise("AiReasonVisible");
                Raise("AiTally"); Raise("AiTallyVisible"); Raise("AiSplit");
            }

            public Visibility AiOnNow
            {
                get { return aiSide == AiSide.Now ? Visibility.Visible : Visibility.Collapsed; }
            }
            public Visibility AiOnThen
            {
                get { return aiSide == AiSide.Then ? Visibility.Visible : Visibility.Collapsed; }
            }
            public Visibility AiUnsure
            {
                get { return aiSide == AiSide.Unsure ? Visibility.Visible : Visibility.Collapsed; }
            }
            // A MARK OF ITS OWN, because it is not a shrug. "? AI" says the AI looked and could not
            // tell; this says it DID tell, and the answer is that both countries on the row are
            // wrong. Wearing the same "?" would bury the most useful thing it ever says under the
            // least useful one.
            public Visibility AiNeither
            {
                get { return aiSide == AiSide.Neither ? Visibility.Visible : Visibility.Collapsed; }
            }

            public Visibility AiReasonVisible
            {
                get
                {
                    return aiSide != AiSide.None && !string.IsNullOrEmpty(AiReason)
                        ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            private void Raise(string name)
            {
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name));
            }

            private bool apply;
            public bool Apply
            {
                get { return apply; }
                set
                {
                    if (apply == value) return;
                    apply = value && Fixable;
                    if (apply) Keep = false;      // the two boxes are the two answers; one at a time
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Apply"));
                    if (ApplyChanged != null) ApplyChanged();
                }
            }

            // ── THE OTHER ANSWER, AND IT HAD NO BOX ─────────────────────────────────────────────
            //
            // "Leave it as it is" was said by NOT ticking, which is not saying anything: a row he had
            // read and agreed with looked exactly like a row he had not reached yet, and the button
            // that acted on the difference - "User approved 136" - could only be understood by
            // somebody who already knew the rule. Two boxes, two answers, and a row with neither
            // ticked is what it looks like: not answered.
            //
            // Every row can take this one, including the ones nothing can be written to. "Club Log
            // says this operation did not count" is not fixable and is still something he can read
            // and be done with.
            private bool keep;
            public bool Keep
            {
                get { return keep; }
                set
                {
                    if (keep == value) return;
                    keep = value;
                    if (keep) Apply = false;
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Keep"));
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

        // The header over the date the Fixer ran, in the list of contacts it has already put right.
        // Named once: it is a column key, and a column key spelt two ways is a column that never fills.
        private const string WhenColumn = "Fixed on";

        // AND THE COLUMN THAT SAYS WHICH LINE IS WHICH.
        //
        // Every cell in this window is two lines, and in a list of contacts already put right the two
        // are the contact BEFORE and the contact AFTER - which the colours alone do not say. Red and
        // green mean "wrong" and "proposed" everywhere else in the Fixer, so a man arriving at this
        // list has every reason to read the red line as a fault still standing. The leftmost column
        // names the pair on every row - was, now - in a pale blue of its own, because it is a label
        // and not a value the log ever held.
        private const string WasNowColumn = "History";
        private static readonly string[] IssueColumns =
        {
            "Band", "Mode", "Country", "Country Code", "Continent", "DX Locator", "Comment",
            "IOTA", "SOTA", "POTA", "WWFF"
        };

        private readonly ObservableCollection<FixRow> _rows = new ObservableCollection<FixRow>();

        private readonly List<QSO> _qsos;
        private readonly string _logName;

        // WHETHER THIS WINDOW STILL HAS TO DECIDE WHAT TO LEAVE OUT.
        //
        // When the Fixer opens itself - after an import - it is handed the whole log and drops the
        // contacts it has already put right. When it is opened from the Log Workshop the operator has
        // just been asked that question by name, and the list he chose is the list he gets: asking
        // for the ones already fixed and being shown none of them is the window arguing with him.
        //
        // NOT READONLY, because it is only true of the FIRST scan. He asked to see the ones already
        // approved, was shown them, and answered them - and the re-check that follows put all five
        // straight back on the screen, as problems, one second after he had settled them. What he
        // chose in the chooser is what to OPEN on; once he has pressed the green button, a row he has
        // just answered is a row that is done.
        private bool _hideAlreadySettled = true;

        // The contacts this run found something to say about. Everything else it scanned is clean.
        private HashSet<QSO> _withFindings = new HashSet<QSO>();

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

        // Opened to LIST contacts rather than to check them - what the two "already checked" buttons
        // in the chooser ask for. The headline is the window's whole message.
        public static LogVerifierWindow AsList(IEnumerable<QSO> qsos, string title, string headline)
        {
            var win = new LogVerifierWindow(qsos, null);
            win._listHeadline = headline;
            win.Title = title;
            return win;
        }

        private string _listHeadline;

        public LogVerifierWindow(IEnumerable<QSO> qsos, string logName = null, bool hideAlreadySettled = true)
        {
            InitializeComponent();
            _qsos = (qsos ?? Enumerable.Empty<QSO>()).Where(q => q != null).ToList();
            _hideAlreadySettled = hideAlreadySettled;
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
            try { x = CountryLookup.Shared.Explain(call, CountryLookup.QsoDate(row.Qso.Date, row.Qso.Time)); }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                HolyMessageBox.ShowWarning(
                    "The country databases could not be asked about " + call + ".\n\n"
                    + ex.Message + "\n\n"
                    + "HolyLogger fetches cty.dat and the Club Log file itself. Close it, make sure "
                    + "you are online, and open it again.",
                    "Why this country?", this);
                return;
            }

            // Worded in CountryLookup.Explanation, not here: a report written about a log says exactly
            // this, and two places composing the same paragraph is how they come to differ.
            string text = x.Report(Text(row.Qso.Country), row.Qso.DxccCode, FormatDate(row.Qso.Date));

            // AND WHAT THE AI MADE OF IT, WHERE HE IS ALREADY LOOKING.
            //
            // The "?" is where an operator comes to settle this row, so everything bearing on it
            // belongs in this one window: what each database matched, and then the opinion that was
            // fetched precisely because those two could not agree. Kept in a tooltip, the reasoning is
            // read by accident or not at all - and it is the reasoning, not the verdict, that lets him
            // disagree with the AI on grounds of his own.
            string said = AiParagraph(row);
            if (said.Length > 0) text = text.TrimEnd() + Environment.NewLine + Environment.NewLine + said;

            HolyMessageBox.Show(text, "Why this country?", HolyMsgType.Info, this, 640);
        }

        // WHAT THE AI SAID ABOUT THIS ROW, WRITTEN OUT. Empty when it was never asked, which is every
        // row until the button is pressed and most rows afterwards - the AI is only ever put the one
        // question, and only about the contacts where the two databases disagree.
        //
        // The country is named rather than left as "the log's value": a man reading this window has
        // two countries in front of him and the whole point is which of the two was chosen.
        private static string AiParagraph(FixRow row)
        {
            if (row == null || !row.AiAsked) return string.Empty;

            Cell country;
            row.Cells.TryGetValue("Country", out country);

            string backed = country == null
                ? string.Empty
                : Text(row.AiBacksLog ? country.Current : country.Proposed);

            var sb = new StringBuilder();
            sb.AppendLine(row.AiWho.Length > 0
                ? "What the AI said (" + row.AiWho + ")"
                : "What the AI said");
            sb.AppendLine();

            if (row.AiSaysNeither)
                sb.AppendLine("It worked the country out from the callsign and the date, and it is "
                              + "NEITHER of the two on this row. What it believes is in the reason "
                              + "below. Nothing here can be ticked, because the program has no value "
                              + "it may safely write - that decision is yours.");
            else if (row.AiCouldNotTell)
                sb.AppendLine("It weighed the two and could not tell which is right. The decision "
                              + "is yours, and nothing here has been changed.");
            else if (row.AiBacksLog)
                sb.AppendLine("The country already in your log is the correct one"
                              + (backed.Length > 0 ? ", " + backed : string.Empty)
                              + " - so this row cannot be ticked, and Fix will leave it alone.");
            else
                sb.AppendLine("HolyLogger's correction is the right one"
                              + (backed.Length > 0 ? ", " + backed : string.Empty)
                              + " - tick the row and Fix writes it.");

            if (row.AiReason.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Why: " + row.AiReason);
            }

            // EVERY VERDICT, EACH UNDER ITS OWN NAME, once more than one AI has answered. The line
            // above is the majority's; this is the whole vote, which is the reason for asking twice.
            if (row.AiCount > 1)
            {
                sb.AppendLine();
                sb.AppendLine(row.AiSplit ? "They do not agree:" : "Both were asked, and they agree:");
                foreach (FixRow.AiVerdict v in row.AiVerdictList)
                {
                    string said = v.Side == AiSide.Now ? "your log is correct"
                                : v.Side == AiSide.Then ? "HolyLogger's correction is right"
                                : v.Side == AiSide.Neither ? "neither country is right"
                                : "could not tell";
                    sb.AppendLine("  " + (v.Who.Length > 0 ? v.Who : "AI") + ": " + said
                                  + (v.Reason.Length > 0 ? " - " + v.Reason : string.Empty));
                }
            }

            sb.AppendLine();
            sb.AppendLine("An AI can be wrong. It is one more opinion beside the two databases, "
                          + "not a ruling over them.");

            return sb.ToString().TrimEnd();
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

            // Nor does one opened to LIST what has already been settled.
            if (_listHeadline != null) { ShowList(_listHeadline); return; }

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

            // The rows are about to be built again from nothing, and an undo pointing at the old ones
            // would put ticks back on objects no longer on screen.
            _tickUndo.Clear();

            List<QSO> snapshot = _qsos;
            // The country lookup parses two databases and every QSO is resolved twice, so the whole scan
            // runs off the UI thread - a 40,000-QSO log would otherwise freeze the window.
            List<Finding> found = await Task.Run(() => Scan(snapshot));

            // ── THE ONES THE FIXER HAS ALREADY PUT RIGHT ────────────────────────────────────────
            //
            // Dropped AFTER the scan, never before it. Duplicates are found by comparing contacts with
            // each other, so a log with a corrected QSO taken out of the scan would stop seeing the pair
            // it belongs to - and report the survivor as fine. Everything is scanned; what has already
            // been dealt with is simply not shown.
            // ANYTHING BUT 0 HIDES A ROW, because every other state is an answer he gave with the Fix
            // button under his hand: 1 he corrected, 2 he approved as it stood, 4 he approved with an
            // AI backing him, 3 the scan found nothing wrong with. None of that used to be true of 2 -
            // an old build wrote it from a question on the way out of the window - so the 2s from that
            // build are cleared out of the file once, and every one read here is his own decision.
            //
            // And nothing is hidden at all when the operator has just been asked which contacts he
            // wants; the Log Workshop hands over exactly the list he chose.
            //
            // WHICH CONTACTS HAD SOMETHING TO SAY ABOUT THEM, kept before the hiding: the ones that
            // raised nothing are the clean ones, and pressing Fix marks them as checked so that "never
            // checked" comes to mean "logged since the last time I answered this window".
            _withFindings = new HashSet<QSO>(found.Where(f => f.Qso != null).Select(f => f.Qso));

            var kept = new List<Finding>(found.Count);
            var hidden = new HashSet<QSO>();
            foreach (Finding f in found)
            {
                    if (_hideAlreadySettled && f.Qso != null && f.Qso.ReviewState != 0)
                { hidden.Add(f.Qso); continue; }
                kept.Add(f);
            }
            int settled = hidden.Count;
            found = kept;

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

            // AN EMPTY TABLE MUST SAY WHY IT IS EMPTY.
            //
            // "No problems found in 28,580 QSOs" over an empty table is the right sentence only when the
            // scan really found nothing. When the problems were found and then held back because the
            // Fixer has already corrected those contacts, that sentence is untrue in the way that
            // matters most: it tells a man his log is clean when what happened is that it was tidied.
            TB_Header.Text =
                found.Count > 0
                    ? found.Count.ToString("N0") + " problem" + (found.Count == 1 ? "" : "s")
                      + " found in " + _qsos.Count.ToString("N0") + " QSOs"
                    : settled > 0
                        ? "Nothing left to fix. " + settled.ToString("N0") + " QSO"
                          + (settled == 1 ? " was" : "s were") + " put right already."
                        : "No problems found in " + _qsos.Count.ToString("N0") + " QSOs.";

            // ONE INSTRUCTION, AND NOTHING ELSE. This line used to carry the tally as well - so many can
            // be put right, so many are for you to judge - and every one of those numbers is already on
            // the screen, counted by kind, in the panel above. Saying them twice made a line nobody
            // finished reading, and the one thing on it that is NOT written anywhere else is how to open
            // a QSO. So that is all it says.
            TB_Summary.Text = found.Count == 0
                ? "Nothing to fix."
                : "Double-click a row to open the QSO.";
            UpdateFixButton();

            // THE WHOLE LIST, IN A FILE. The window answers one row at a time; a log with hundreds of
            // findings needs the set somewhere it can be read away from the screen, printed, or worked
            // through on paper. Off the UI thread because the country explanations are two database
            // lookups each, and awaited so a check that is still writing cannot be overtaken by the next.
            if (found.Count > 0)
            {
                List<Finding> forReport = found;
                await Task.Run(() => WriteFixerReport(forReport, null, null));   // the path is not needed here
            }
        }

        // How many rows a section of the report names one by one before it stops. A log can hold tens of
        // thousands of findings of one kind - a file that big is one nobody opens - and the counts in the
        // headings stay complete either way, which is what tells the operator the size of the job.
        // 10,000, raised from 2,000 the first time a real log met it: a 28,513-QSO import produced 4,369
        // country findings, so a third of them were never named - and the operator searching the file for
        // one callsign found nothing and had no way to tell whether that meant "not a problem" or "past
        // the limit". A file of this size is still an ordinary text file; the point of the cap is only to
        // stop a few hundred thousand imported QSOs producing one nobody can open.
        private const int MaxFixerReportRows = 10000;

        private const string FixerReportRule =
            "────────────────────────────────────────────────────────────────────";

        // THE SAME FINDINGS THE WINDOW SHOWS, WRITTEN OUT. It lands in the same Reports folder as the
        // import report and is announced the same way, so File → Open Reports Folder finds both.
        //
        // NONE OF THE WORDING IS WRITTEN AGAIN HERE. Each section's note is the kind's own HandNote -
        // the sentence beside its chip in the window - and the country explanation is
        // Explanation.PlainReport, which is the "?" dialog's own text with the emphasis markers taken
        // out. Two places composing the same paragraph is exactly how they come to differ.
        // `aiSaid` IS EMPTY THE FIRST TIME AND FULL THE SECOND. The report is written once when the
        // scan ends, before the AI has been asked anything, and again after it has answered - so the
        // second file holds the whole argument: what each database matched, and what the AI made of
        // the two of them. The first is left where it is; both were true when they were written.
        private string WriteFixerReport(List<Finding> found, Dictionary<QSO, string> aiSaid, string aiWho)
        {
            try
            {
                if (found == null || found.Count == 0) return null;

                var sb = new StringBuilder();
                sb.AppendLine("HolyLogger — Log Fixer report");
                sb.AppendLine(DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm"));
                sb.AppendLine();
                if (!string.IsNullOrEmpty(_logName))
                    sb.AppendLine("Log               : " + _logName);
                sb.AppendLine("QSOs checked      : " + _qsos.Count.ToString("N0"));
                sb.AppendLine("Problems found    : " + found.Count.ToString("N0"));
                int fixable = found.Count(f => f.Fixable);
                sb.AppendLine("Can be fixed here : " + fixable.ToString("N0"));
                sb.AppendLine("For you to judge  : " + (found.Count - fixable).ToString("N0"));
                sb.AppendLine();
                sb.AppendLine("Nothing in this file has been changed in your log. The Log Fixer only");
                sb.AppendLine("reports; a QSO is altered when you tick it and press Fix selected.");
                if (aiSaid != null && aiSaid.Count > 0)
                {
                    sb.AppendLine();
                    if (!string.IsNullOrEmpty(aiWho))
                        sb.AppendLine("Answered by " + aiWho);
                    sb.AppendLine("The AI was asked about the QSOs where the two databases disagree.");
                    sb.AppendLine("Its verdict and its reasoning are under those rows, marked AI.");
                }
                sb.AppendLine();

                // Biggest kind first: the size of a group is the first thing worth knowing about it.
                foreach (var group in found.GroupBy(f => f.Problem ?? string.Empty)
                                           .OrderByDescending(g => g.Count()))
                {
                    sb.AppendLine(FixerReportRule);
                    sb.AppendLine(group.Key + "   (" + group.Count().ToString("N0") + ")");
                    sb.AppendLine(FixerReportRule);

                    string note = PlainKindNote(group.Key);
                    if (note.Length > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine(note);
                    }
                    sb.AppendLine();

                    // WHAT EACH DATABASE MATCHED, AS TWO MORE COLUMNS. They were two sentences under the
                    // row - "cty.dat matched R which is European Russia (54)" - which is a fact about a
                    // column of a table written out as prose. As columns they line up down the page, so
                    // the section can be read by running an eye down cty.dat and then down Club Log,
                    // which is the comparison the operator is actually making.
                    //
                    // Only the two country kinds have them: nothing else in the report is an argument
                    // between two databases. Resolved ONCE here and kept, because each one costs two
                    // database lookups and the widths have to be measured before a single row is printed.
                    bool explains = group.Key == CountryBothAgree || group.Key == CountryNeedsDecision;

                    var rows = group.Take(MaxFixerReportRows).ToList();
                    var why = new Dictionary<Finding, CountryLookup.Explanation>();
                    if (explains)
                    {
                        foreach (Finding g in rows)
                        {
                            if (g.Qso == null) continue;
                            string gc = Text(g.Qso.DXCall);
                            if (gc.Length == 0) continue;
                            try { why[g] = CountryLookup.Shared.Explain(gc, CountryLookup.QsoDate(g.Qso.Date, g.Qso.Time)); }
                            catch (Exception swallowed) { Log.Swallow(swallowed); }
                        }
                    }

                    // EVERY COLUMN IS AS WIDE AS THIS SECTION NEEDS, and no wider. A fixed width has to
                    // be chosen for the worst case in the whole file, so a section whose longest country
                    // is "ASIATIC RUSSIA (15)" was padded out to fit "Bonaire, Curacao (Neth Antilles)"
                    // from a different section entirely - half the row was empty space.
                    //
                    // The ZONES are not measured: they sit on their own line underneath and are always
                    // shorter than the country above them.
                    // THE HEADING NAMES THE FIELD THIS SECTION IS ABOUT. It said "Country In Log" on
                    // every section, which is true of the country ones and plainly wrong over a column
                    // of Maidenhead locators - the DX Locator section was headed Country and filled with
                    // grid squares. Taken from the findings themselves, and only when they all agree
                    // about which field they are: a section that mixed two would be headed by whichever
                    // row happened to come first.
                    string field = null;
                    bool oneField = true;
                    foreach (Finding g in rows)
                    {
                        string label = (g.FieldLabel ?? string.Empty).Trim();
                        if (label.Length == 0 || label == "—") continue;
                        if (field == null) field = label;
                        else if (field != label) { oneField = false; break; }
                    }
                    string nowHeader = (oneField && !string.IsNullOrEmpty(field)) ? field + " In Log" : "In Log";

                    // A CONTINENT IS TWO LETTERS UNDER A SIXTEEN-LETTER HEADING. Left-aligned it sits in
                    // the corner of a column of white space and the eye has to hunt for it down the page;
                    // centred, the pairs line up under the middle of their own heading. Only for values
                    // this short - a country name or a locator fills its column and centring one would
                    // just move it about.
                    bool centre = oneField && string.Equals(field, "Continent", StringComparison.OrdinalIgnoreCase);

                    int nowWidth = nowHeader.Length;
                    int newWidth = "Holylogger suggest".Length;
                    int ctyWidth = "cty.dat matched".Length;
                    foreach (Finding g in rows)
                    {
                        string gh, gz;
                        SplitZones(Text(g.Current), out gh, out gz);
                        if (gh.Length > nowWidth) nowWidth = gh.Length;

                        SplitZones(Text(g.Suggested), out gh, out gz);
                        if (gh.Length > newWidth) newWidth = gh.Length;

                        CountryLookup.Explanation gx;
                        if (why.TryGetValue(g, out gx) && gx != null)
                        {
                            string s = MatchedPart(gx.CtySays, "cty.dat");
                            if (s.Length > ctyWidth) ctyWidth = s.Length;
                        }
                    }
                    nowWidth = Math.Min(nowWidth, 38) + 2;   // past 38 a name is cut, not carried
                    newWidth = Math.Min(newWidth, 38) + 2;
                    ctyWidth = Math.Min(ctyWidth, 40) + 2;

                    // "In Log", not "In File": this report is the verifier's, and the verifier reads the
                    // LOG. The import report is the one that speaks about a file, and it no longer judges
                    // countries at all. The last column has no width - nothing follows it to push out.
                    sb.AppendLine("  " + Col("Date", 12) + Col("Time", 7) + Col("Callsign", 14)
                                  + Pad(nowHeader, nowWidth, centre)
                                  + (explains ? Col("Holylogger suggest", newWidth)
                                                + Col("cty.dat matched", ctyWidth) + "Club Log matched"
                                              : Pad("Holylogger suggest", newWidth, centre).TrimEnd()));
                    sb.AppendLine("  " + Col(new string('-', 10), 12) + Col(new string('-', 5), 7)
                                  + Col(new string('-', 12), 14)
                                  + Col(new string('-', nowWidth - 2), nowWidth)
                                  + (explains ? Col(new string('-', newWidth - 2), newWidth)
                                                + Col(new string('-', ctyWidth - 2), ctyWidth) + new string('-', 30)
                                              : new string('-', 30)));

                    int printed = 0;
                    foreach (Finding f in rows)
                    {
                        printed++;

                        // THE ZONES GO ON A LINE OF THEIR OWN, under the country they belong to. They are
                        // part of what a country fix would write, so they cannot be dropped - but carried
                        // on the same line they made both country columns half as wide again.
                        string nowHead, nowZones, newHead, newZones;
                        SplitZones(Text(f.Current), out nowHead, out nowZones);
                        SplitZones(Text(f.Suggested), out newHead, out newZones);

                        string ctyPart = string.Empty, clubPart = string.Empty;
                        CountryLookup.Explanation x;
                        if (why.TryGetValue(f, out x) && x != null)
                        {
                            ctyPart = MatchedPart(x.CtySays, "cty.dat");
                            clubPart = MatchedPart(x.ClubSays, "Club Log");
                        }

                        sb.AppendLine(("  " + Col(FormatDate(f.Qso == null ? "" : f.Qso.Date), 12)
                                      + Col(Text(f.Time), 7)
                                      + Col(Text(f.Call), 14)
                                      + Pad(nowHead, nowWidth, centre)
                                      + (explains ? Col(newHead, newWidth) + Col(ctyPart, ctyWidth) + clubPart
                                                  : Pad(newHead, newWidth, centre))).TrimEnd());

                        // THE CONTINUATION LINES, FILLED ACROSS RATHER THAN ONE UNDER THE OTHER.
                        //
                        // The zones and Club Log's notes used to take a line each, in that order - so in
                        // the Club Log column the row's own answer was on one line, NOTHING on the next,
                        // and the note on the one after. Read down that column there was a blank line in
                        // the middle of a single QSO, which is exactly what a blank line is supposed to
                        // mean the end of. They share the line now: zones on the left, note on the right.
                        var notes = SplitNotes(x == null ? null : x.PlainExtraNotes);
                        bool hasZones = nowZones.Length > 0 || newZones.Length > 0;
                        int extraLines = Math.Max(hasZones ? 1 : 0, notes.Count);
                        bool manyLines = extraLines > 0;

                        for (int k = 0; k < extraLines; k++)
                        {
                            string zNow = (k == 0 && hasZones) ? nowZones : string.Empty;
                            string zNew = (k == 0 && hasZones) ? newZones : string.Empty;
                            string clubNote = k < notes.Count ? notes[k] : string.Empty;

                            string line = "  " + new string(' ', 33) + Col(zNow, nowWidth)
                                        + (explains ? Col(zNew, newWidth) + Col(string.Empty, ctyWidth) + clubNote
                                                    : zNew);
                            sb.AppendLine(line.TrimEnd());
                        }

                        // WHAT ELSE CLUB LOG HAS TO SAY, under the Club Log column it belongs to.
                        //
                        // All three of these notes are facts about Club Log and nothing else: that it
                        // holds this exact callsign rather than just its prefix, that the prefix it
                        // holds did not apply on this date, or that no record of its covers the date at
                        // all. Left at the margin they read as a remark about the whole row; under the
                        // column they read as more of what that column already says.
                        //
                        // They keep a line rather than joining the cell because they have no fixed
                        // shape - and they are on a small minority of rows, so the cost is small. The
                        // one that says "Club Log has an entry for this exact callsign" is worth
                        // seeing: it is the difference between an answer read off a prefix and one
                        // recorded against that very station.
                        // A BLANK LINE ONLY WHERE A CONTACT TOOK MORE THAN ONE. Where a finding runs to
                        // three lines - the row, its zones, and whatever Club Log adds - nothing else
                        // shows where one QSO ends and the next begins. Where every finding is a single
                        // line, a blank between them doubles the length of the section and makes it
                        // harder to run an eye down, not easier: "Country spelled differently" alone is
                        // 4,369 rows. Decided per finding rather than per section, so a section that
                        // happens to hold one long row is spaced only around that row.
                        // WHAT THE AI MADE OF IT, AND WHY IT SAID SO.
                        //
                        // The rest of this row is the argument: the log's value, HolyLogger's, and what
                        // each database matched to reach them. This is the last word in it - the one
                        // that was asked for precisely because the two databases could not agree - and
                        // without it the file stops at the disagreement and leaves the reader where the
                        // operator was before he pressed the button.
                        //
                        // Indented under the row rather than squeezed into a column: the reasoning is a
                        // sentence of ordinary English and there is no width that would hold it.
                        string verdict;
                        if (aiSaid != null && f.Qso != null
                            && aiSaid.TryGetValue(f.Qso, out verdict) && verdict.Length > 0)
                        {
                            foreach (string piece in WrapFor("AI: " + verdict, 92))
                                sb.AppendLine("      " + piece);
                            manyLines = true;
                        }

                        if (manyLines) sb.AppendLine();
                    }

                    if (group.Count() > printed)
                        sb.AppendLine("  … and " + (group.Count() - printed).ToString("N0")
                                      + " more of this kind, not listed one by one.");

                    sb.AppendLine();
                }

                // SECONDS IN THE NAME. Two runs inside one minute wrote to the same file and the
                // second quietly replaced the first - which is exactly what happens when several
                // models are put to the same question one after another, and it cost a comparison
                // that had already been paid for.
                return Reports.Write("holylogger_fixer_report_"
                                     + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".txt", sb.ToString());
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // ONE LONG SENTENCE BROKEN TO THE PAGE, at spaces and never inside a word. The report is a
        // plain text file meant to be read in a window or printed, and a line that runs to three
        // hundred characters is a line that is read by scrolling sideways, which nobody does.
        private static List<string> WrapFor(string text, int width)
        {
            var lines = new List<string>();
            string rest = (text ?? string.Empty).Trim();

            while (rest.Length > width)
            {
                int cut = rest.LastIndexOf(' ', width);

                // A single word longer than the whole width - a callsign list, an address - is let
                // out past the margin rather than chopped in half.
                if (cut <= 0) { lines.Add(rest); return lines; }

                lines.Add(rest.Substring(0, cut));
                rest = rest.Substring(cut + 1).TrimStart();
            }

            if (rest.Length > 0) lines.Add(rest);
            return lines;
        }

        // "cty.dat matched R which is European Russia (54)" -> "R which is European Russia (54)".
        //
        // The database's name is the COLUMN HEADING now, so repeating it on every row underneath is the
        // one word on the line that carries no information at all. Taken off by name rather than by
        // cutting a fixed number of characters, so a wording change in CountryLookup cannot silently
        // start eating the first letters of the answer.
        private static string MatchedPart(string says, string who)
        {
            string s = (says ?? string.Empty).Trim();
            string prefix = who + " matched ";
            if (s.StartsWith(prefix, StringComparison.Ordinal)) return s.Substring(prefix.Length);

            // The honest shorter forms: "cty.dat says X" when there are no matched letters to point at,
            // and "cty.dat has nothing for this callsign". Both keep their own wording, minus the name.
            prefix = who + " ";
            if (s.StartsWith(prefix, StringComparison.Ordinal)) return s.Substring(prefix.Length);
            return s;
        }

        // BREAKS A LONG NOTE IN TWO, at the comma before "but".
        //
        //   Club Log knows UH8 = Asiatic Russia (15)
        //   but only from 21-01-2010
        //
        // The comma goes with the break rather than staying at the end of the first line: it was there
        // to join two halves of one sentence, and once they are on separate lines the line break does
        // that job. A comma hanging off the end of a line is punctuation for a sentence that no longer
        // runs on.
        //
        // Those notes are the widest thing in the report and they sit in the last column, so one of them
        // pushes the line far past everything else. The break is at ", but " and NOT simply at the first
        // comma: several DXCC names carry one of their own - "Bonaire, Curacao (Neth Antilles)" - and a
        // rule that broke at the first comma would cut a country in half.
        //
        // The two lines are consecutive, so in the Club Log column they read as one remark on two lines.
        private static List<string> SplitNotes(List<string> notes)
        {
            var lines = new List<string>();
            if (notes == null) return lines;

            foreach (string note in notes)
            {
                string s = (note ?? string.Empty).Trim();
                if (s.Length == 0) continue;

                int at = s.IndexOf(", but ", StringComparison.Ordinal);
                if (at < 0) { lines.Add(s); continue; }

                lines.Add(s.Substring(0, at).TrimEnd());   // without the comma
                lines.Add(s.Substring(at + 2).Trim());     // "but only from ..."
            }
            return lines;
        }

        // A cell, left-aligned or centred in its column. Centring is by whole spaces and an odd
        // remainder goes to the RIGHT, so a column of two-letter continents lands on one line down the
        // page rather than wobbling by a character between rows.
        private static string Pad(string text, int width, bool centre)
        {
            if (!centre) return Col(text, width);

            string s = text ?? string.Empty;
            if (s.Length >= width) return Col(s, width);

            int left = (width - s.Length) / 2;
            return new string(' ', left) + s + new string(' ', width - s.Length - left);
        }

        // SPLITS "ASIATIC RUSSIA (15)   (CQ 17, ITU 20)" into the country and its zones.
        //
        // Cut at the exact separator ZoneSuffix writes - three spaces before "(CQ " - and not by hunting
        // for a bracket: a DXCC name can hold brackets of its own ("Bonaire, Curacao (Neth Antilles)"),
        // and a rule that looked for the first "(" would cut that country in half.
        private static void SplitZones(string value, out string head, out string zones)
        {
            head = value ?? string.Empty;
            zones = string.Empty;

            int at = head.IndexOf("   (CQ ", StringComparison.Ordinal);
            if (at < 0) return;

            zones = head.Substring(at).Trim();
            head = head.Substring(0, at).Trim();
        }

        // ONE CELL OF THE TABLE: padded to its column, and cut with an ellipsis when it will not fit.
        // Cutting matters more than showing every letter here - a single long DXCC name ("Bonaire,
        // Curacao (Neth Antilles)") would otherwise push every column after it out of line for that row
        // alone, and a table that stops lining up is no longer a table.
        private static string Col(string text, int width)
        {
            string s = text ?? string.Empty;
            if (s.Length >= width) return s.Substring(0, Math.Max(1, width - 2)) + "… ";
            return s.PadRight(width);
        }

        // A kind's own sentence, with the screen's emphasis and colour markers taken out. Built from a
        // ProblemKind so the report cannot drift from the chip in the window - both ask HandNote.
        private static string PlainKindNote(string kindName)
        {
            try
            {
                string note = new ProblemKind { Name = kindName }.HandNote ?? string.Empty;
                note = note.Replace("**", "");
                return Regex.Replace(note, @"\{\{[rg]:([^}]*)\}\}", "$1").Trim();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return string.Empty; }
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

                // THE OPERATOR'S OWN <DXCC> SETTLES A STROKE. M/ON4CJK is England to one reading and
                // Belgium to the other; if the number the log carries is one of the two, it came from
                // the person who was there.
                //
                // The rule is in CountryLookup and NOT written out here, because the ADIF import asks
                // exactly the same question of the file it is reading. It used to be written here only,
                // and the two duly disagreed: T9/VE6PR was left alone by this window and proposed for
                // correction by the import, in two reports the operator was reading side by side.
                if (ourCode > 0 && storedCode > 0 && storedCode != ourCode
                    && lookup.StrokeSettledByLog(call, storedCode, when))
                    continue;

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

                    // THE NAME MAY ALREADY BE RIGHT, AND THEN THIS IS NOT A DIFFERENT COUNTRY AT ALL.
                    // IZ5TJD/P7 logged as "Italy" with code 344: the word Italy is correct and only the
                    // number is not, so the QSO counts for another country while reading perfectly well.
                    // Calling that "Different country" describes something the operator cannot see - he
                    // looks at the row, reads Italy on both lines, and is asked to accept a change of
                    // country. It is the mirror of "Wrong country name", and it is named for what is
                    // actually wrong with it.
                    bool nameAlreadyRight = storedCountry.Length > 0
                        && string.Equals(storedCountry, dated.Name, StringComparison.OrdinalIgnoreCase);

                    string kind = nameAlreadyRight
                        ? WrongCountryCode
                        : why != null && !why.Agree
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
            // Right beside the two above: the QSO counts for the wrong country just the same, it only
            // says the right name while doing it.
            if (problem == WrongCountryCode) return 3;
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
        // ── THE WINDOW AS A LIST, WITH NOTHING TO DECIDE ────────────────────────────────────────
        //
        // "Already checked and fixed" is a question about the past: which contacts did I settle? Run
        // through the scan it answered with an empty table - of course it did, there is nothing wrong
        // with a contact that has been put right - so the button could only ever look broken.
        //
        // Here the QSOs are simply listed, as the log now holds them. No scan, no proposals, no tick
        // boxes and no green button: there is nothing on this screen to answer.
        public void ShowList(string headline)
        {
            _listOnly = true;
            _findings.Clear();
            _rows.Clear();
            _rowByQso.Clear();
            _kinds.Clear();

            // ── WHAT WAS DONE TO EACH OF THEM ───────────────────────────────────────────────
            //
            // The whole point of this list. review_state = 1 says a contact was corrected and nothing
            // more; the values it held before are not in the QSO any longer. They are here, one row
            // per field per press, and the cells show the pair the operator already knows how to read:
            // red on top is what the contact said, green underneath is what it says now.
            var dal = DataAccess.GetInstance();
            Dictionary<int, List<FixNote>> history = null;
            try
            {
                if (dal != null)
                    history = dal.GetFixHistory(_qsos.Where(q => q != null).Select(q => q.id));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            if (history == null) history = new Dictionary<int, List<FixNote>>();

            // ONLY THE COLUMNS SOMETHING ACTUALLY HAPPENED IN. A fixed set of five put a column of
            // Comments on screen that nothing had ever been written to, and left the CQ zone - which a
            // country correction really does move - off the edge of the window entirely.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in history)
                foreach (FixNote n in kv.Value)
                    if (!string.IsNullOrEmpty(n.Field)) used.Add(n.Field);

            var columns = new List<string> { WasNowColumn };
            columns.AddRange(AlwaysColumns);
            columns.Add(WhenColumn);
            // In the order the operator meets them elsewhere in this window, so the table does not
            // rearrange itself between one press of the chooser and the next.
            foreach (string key in FieldsAFixCanTouch)
                if (used.Contains(key) && !columns.Contains(key)) columns.Add(key);
            // And anything written by a build that knew a field this one does not, rather than
            // dropping it silently.
            foreach (string key in used)
                if (!columns.Contains(key)) columns.Add(key);

            int recorded = 0;
            foreach (QSO q in _qsos)
            {
                if (q == null) continue;
                var row = new FixRow { Qso = q };
                CellFor(row, "Date").Current = FormatDate(q.Date);
                CellFor(row, "Time").Current = FormatTime(q.Time);
                CellFor(row, "Callsign").Current = Text(q.DXCall);

                List<FixNote> notes;
                if (history.TryGetValue(q.id, out notes) && notes.Count > 0)
                {
                    recorded++;
                    // Kept on the row, because putting the contact back means writing these values
                    // into it again and nothing else in the window knows them.
                    row.History = notes;
                    FixRow reverting = row;   // captured, or every row would close over the last
                    row.RevertChanged = () => UpdateRevertButton();
                    // The key to the two lines, painted in the same red and green as the values it is
                    // standing beside, so it reads as part of them and not as another field of the QSO.
                    Cell key = CellFor(row, WasNowColumn);
                    key.Current = "was";
                    key.Suggest("now");
                    key.Tint = Cell.LabelBg;
                    // The LAST press, not the first: if a contact has been through the Fixer twice,
                    // the date that matters is the one that left it looking as it does now.
                    CellFor(row, WhenColumn).Current = WhenText(notes[notes.Count - 1].When);
                    foreach (FixNote n in notes)
                    {
                        if (string.IsNullOrEmpty(n.Field)) continue;
                        Cell cell = CellFor(row, n.Field);
                        cell.Current = n.Was ?? "";
                        cell.Wrong = true;
                        cell.Suggest(n.Now ?? "");
                    }
                }
                else
                {
                    // FIXED BEFORE ANYTHING WAS WRITTEN DOWN. Said in the cell rather than left blank:
                    // a blank would read as a contact nothing was done to, which is a different thing.
                    CellFor(row, WhenColumn).Note = "not recorded";
                }

                // The columns this contact was not touched in still show what it holds, so each line
                // reads as a QSO rather than as two coloured boxes floating in space.
                foreach (string key in columns)
                    if (!row.Cells.ContainsKey(key)) CellFor(row, key).Current = CurrentOf(q, key);

                // Nothing on this window writes, so nothing on it is typed into either.
                foreach (var kv in row.Cells) kv.Value.ReadOnly = true;

                _rows.Add(row);
                _rowByQso[q] = row;
            }

            BuildColumns(columns);

            // LEFTMOST MEANS LEFTMOST, AND REVERT COMES NEXT. BuildColumns puts its own four - the
            // three box columns and the "?" - in front of every data column, so the key to the rows
            // would have sat fifth. Set in this order: History takes 0 and pushes the rest right,
            // then Revert takes 1 and the "?" lands where it belongs, third.
            foreach (var c in FindingsGrid.Columns)
                if ((c.Header as string) == WasNowColumn) { c.DisplayIndex = 0; break; }
            if (_revertColumn != null)
            {
                _revertColumn.Visibility = Visibility.Visible;
                _revertColumn.DisplayIndex = 1;
            }

            FindingsGrid.ItemsSource = _rows;

            TB_Header.Text = headline;
            TB_Summary.Text = recorded > 0
                ? "Red is what the contact said before the Log Fixer changed it; green is what it says "
                  + "now.  Double-click a row to open the QSO."
                : "Double-click a row to open the QSO.";
            // THE FRAME THAT WAS STANDING EMPTY. In a list there are no kinds to tick, so the panel
            // above the table held nothing at all - and that is where the one thing this screen can
            // DO belongs, with the sentence that explains it on the left and the button on the right.
            TB_KindsSummary.Text = recorded > 0
                ? "You can revert any QSO by checking the Revert check box"
                : "";
            if (TB_Intro != null) TB_Intro.Inlines.Clear();
            if (Btn_Fix != null) { Btn_Fix.Visibility = Visibility.Collapsed; }
            if (Btn_Ai != null) Btn_Ai.Visibility = Visibility.Collapsed;
            if (Btn_ShowAll != null) Btn_ShowAll.Visibility = Visibility.Collapsed;
            if (Btn_Revert != null)
                Btn_Revert.Visibility = recorded > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowAnswerColumns(false);
            UpdateRevertButton();
        }

        // True while the window is a list of settled contacts rather than a check. Nothing writes,
        // nothing rescans, and the scan is never started at all.
        private bool _listOnly;

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
                    UpdateKeepAllBox();
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

        // ── WHAT A FIX ACTUALLY CHANGED, WRITTEN DOWN BEFORE IT IS FORGOTTEN ────────────────
        //
        // Every field the Fixer is able to write, under the name the operator READS at the top of the
        // column. Three of them - the DXCC string and the two zones - have no column of their own:
        // they travel behind a country correction, and a man told his country was mended and not told
        // his CQ zone moved with it has been told half of it.
        private static readonly string[] FieldsAFixCanTouch =
        {
            "Callsign", "Date", "Time", "Band", "Mode", "Country", "Country Code", "DXCC",
            "Continent", "CQ Zone", "ITU Zone", "DX Locator", "Comment", "IOTA", "SOTA", "POTA", "WWFF"
        };

        // CurrentOf knows the columns; these three are not columns.
        private static string ValueOf(QSO q, string key)
        {
            if (q == null) return "";
            switch (key)
            {
                case "DXCC": return Text(q.DXCC);
                case "CQ Zone": return Text(q.CQZone);
                case "ITU Zone": return Text(q.ITUZone);
                default: return CurrentOf(q, key);
            }
        }

        private static Dictionary<string, string> Snapshot(QSO q)
        {
            var was = new Dictionary<string, string>();
            foreach (string key in FieldsAFixCanTouch) was[key] = ValueOf(q, key);
            return was;
        }

        // BY COMPARISON, NOT BY ASKING THE FINDINGS WHAT THEY MEANT TO DO. A country correction writes
        // five fields through one finding, a typed cell writes one through another path, and a finding
        // whose value turns out to be the one already in the log writes nothing at all. Reading the
        // contact before and after catches all three cases in the same eight lines, and cannot drift
        // out of step with ApplyTo the way a hand-kept list of "what this finding changes" would.
        private static void NoteChanges(QSO q, Dictionary<string, string> before, string when,
                                        List<FixNote> into)
        {
            if (q == null || before == null || into == null) return;
            foreach (string key in FieldsAFixCanTouch)
            {
                string now = ValueOf(q, key);
                string then;
                if (!before.TryGetValue(key, out then)) then = "";
                if (string.Equals(then ?? "", now ?? "", StringComparison.Ordinal)) continue;
                into.Add(new FixNote
                {
                    QsoId = q.id,
                    When = when,
                    Field = key,
                    Was = then ?? "",
                    Now = now ?? ""
                });
            }
        }

        // "20260830 1455" as the operator writes a date and a time. Anything else - an empty stamp, a
        // stamp from a build that wrote it differently - is handed back as it stands rather than
        // thrown away: an odd-looking date is still evidence, a blank cell is not.
        private static string WhenText(string stamp)
        {
            stamp = (stamp ?? "").Trim();
            if (stamp.Length == 0) return "";
            string[] parts = stamp.Split(' ');
            if (parts.Length != 2 || parts[0].Length != 8) return stamp;
            string date = FormatDate(parts[0]);
            string time = FormatTime(parts[1]);
            return time.Length > 0 ? date + "  " + time : date;
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

            // ── AND THE COLUMN THAT SAYS "I HAVE READ THIS AND IT STAYS" ────────────────────────
            //
            // Beside Fix, because the two are one decision taken once, looking at one row: correct it,
            // or approve it as it stands. Its own header box does the whole pile at once, which is what
            // a man does with ninety-six continent rows he has just satisfied himself about.
            // 170, not 120: the header is a tick box AND the words "Do not change", and at 120 the
            // header read "Do not chang".
            var keep = new DataGridTemplateColumn { Width = 170, CanUserSort = false };

            var keepBox = new FrameworkElementFactory(typeof(CheckBox));
            keepBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            keepBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            keepBox.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new System.Windows.Data.Binding("Keep")
                {
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                });
            keepBox.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                               new System.Windows.Input.MouseButtonEventHandler(Chk_Keep_PreviewMouseDown));
            keep.CellTemplate = new DataTemplate { VisualTree = keepBox };

            var keepAll = new CheckBox
            {
                IsThreeState = true,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = "Approve every row on show as it is, or clear them all"
            };
            keepAll.Click += Chk_KeepAll_Click;
            _keepAllBox = keepAll;

            var keepHeader = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            keepHeader.Children.Add(keepAll);
            keepHeader.Children.Add(new TextBlock
            {
                Text = "Do not change",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            keep.Header = keepHeader;

            FindingsGrid.Columns.Add(keep);

            // ── AND THE COLUMN THAT PUTS ONE BACK ───────────────────────────────────────────────
            //
            // Only ever on show in the list of contacts already corrected, and shown there ALWAYS -
            // it is the one thing that list is for beyond reading. A contact with nothing recorded
            // shows no box at all rather than a dead one, exactly as a kind with no answer does up in
            // the panel: a box that cannot be pressed is an invitation the window then refuses.
            var revert = new DataGridTemplateColumn { Width = 80, CanUserSort = false };
            var revertBox = new FrameworkElementFactory(typeof(CheckBox));
            revertBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            revertBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            revertBox.SetValue(FrameworkElement.ToolTipProperty,
                               "Put this QSO back to the values it held before the Log Fixer changed it");
            revertBox.SetBinding(UIElement.VisibilityProperty,
                new System.Windows.Data.Binding("CanRevert")
                {
                    Converter = new System.Windows.Controls.BooleanToVisibilityConverter()
                });
            revertBox.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new System.Windows.Data.Binding("Revert")
                {
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                });
            revert.CellTemplate = new DataTemplate { VisualTree = revertBox };

            var revertAll = new CheckBox
            {
                IsThreeState = true,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = "Put every QSO on show back, or clear them all"
            };
            revertAll.Click += Chk_RevertAll_Click;
            _revertAllBox = revertAll;

            var revertHeader = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            revertHeader.Children.Add(revertAll);
            revertHeader.Children.Add(new TextBlock
            {
                Text = "Revert",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            revert.Header = revertHeader;

            _revertColumn = revert;
            revert.Visibility = _listHeadline != null ? Visibility.Visible : Visibility.Collapsed;
            FindingsGrid.Columns.Add(revert);

            // NEITHER BOX EXISTS UNTIL THERE IS A KIND TO ANSWER. With every kind listed the table is a
            // summary of the work and nothing on this window will write - so two columns of boxes that
            // do nothing when pressed are two invitations the window then refuses. They appear with the
            // kind he chooses, which is also the moment they start meaning something.
            _fixColumn = tick;
            _keepColumn = keep;
            ShowAnswerColumns(_filterKind != null);

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
                    + "FontWeight='{Binding Cells[" + safe + "].NowWeight}' "
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
                    + "IsReadOnly='{Binding Cells[" + safe + "].ReadOnly}' "
                    + "Padding='3,2,3,2' Foreground='{Binding Cells[" + safe + "].ThenFg}' "
                    + "FontWeight='{Binding Cells[" + safe + "].ThenWeight}'/></Grid></Border>"
                    + "</Grid></DataTemplate></DataGridTemplateColumn.CellTemplate></DataGridTemplateColumn>";
                var col = (DataGridTemplateColumn)XamlReader.Parse(xaml);
                // What Ctrl+C takes from this column: the value as it stands. A template column copies
                // nothing at all unless told, and the top half is the log - which is what an operator
                // pasting into a mail is asking for.
                col.ClipboardContentBinding = new System.Windows.Data.Binding("Cells[" + key + "].Current");
                FindingsGrid.Columns.Add(col);
            }

            AddAiColumn(columns);
        }

        // THE COLUMNS A VERDICT FROM THE AI IS ABOUT. It is asked one question and one only - which of
        // two countries it believes - and these are the cells that question moves.
        private static readonly string[] AiAnswersAbout = { "Country", "Country Code", "Continent" };

        // THE AI'S MARK, BESIDE THE VALUES IT IS AN OPINION ABOUT.
        //
        // Two halves like every other column, so the tick lands on the LINE it belongs to: against the
        // log's own value on top, against HolyLogger's proposal underneath. That is the whole message -
        // which of the two it came down on - and putting it anywhere but level with the value would
        // leave the operator working it out from the colours.
        //
        // AND BESIDE THEM SIDEWAYS TOO, which it was not. A new column goes at the far right, and this
        // one did - out past DX Locator and Comment, columns the AI is never asked about. So the man
        // reading the red and green country cells had the verdict on those cells outside his field of
        // vision, and a tick nobody looks at might as well not be there. It now goes straight after
        // the last country column on show, and falls back to the far right only when a scan turned up
        // no country columns at all - there being nothing then for it to sit beside.
        //
        // Empty until the button is pressed, and empty afterwards for any contact the AI did not
        // answer about. A blank here means nobody said anything, which is different from a "?" - and
        // the "?" is what it says when it looked and could not tell.
        private void AddAiColumn(List<string> columns)
        {
            const string xaml =
                "<DataGridTemplateColumn xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
                + "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' "
                + "Header='AI' Width='Auto' MinWidth='58' CanUserSort='False'>"
                + "<DataGridTemplateColumn.CellTemplate><DataTemplate>"
                + "<Grid ToolTip='{Binding AiReason}'>"
                + "<Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/>"
                + "</Grid.RowDefinitions>"
                + "<Border Grid.Row='0' Padding='7,3,7,3' MinHeight='24' "
                + "BorderBrush='#C0C0C0' BorderThickness='0,0,0,1'>"
                // SIDE BY SIDE, NOT ON TOP OF EACH OTHER. All of these used to share one Grid cell, so
                // the tally was printed over the tick and the two were unreadable together. A row of
                // them: only one mark is ever visible, and the tally follows whichever it is.
                + "<StackPanel Orientation='Horizontal'>"
                + "<TextBlock Text='&#x2714; AI' Visibility='{Binding AiOnNow}' "
                + "FontWeight='Bold' Foreground='#0B4A0E'/>"
                // The doubt sits on the upper line for want of a better place: it belongs to the row,
                // not to either value, and the top is where the eye starts.
                + "<TextBlock Text='? AI' Visibility='{Binding AiUnsure}' "
                + "FontWeight='Bold' Foreground='#8A6D00'/>"
                // NEITHER OF THEM. Red, and an exclamation rather than a question: the row needs
                // looking at, and it needs looking at more than the ones the AI simply agreed with.
                + "<TextBlock Text='&#x2260; AI' Visibility='{Binding AiNeither}' "
                + "FontWeight='Bold' Foreground='#B00020'/>"
                // MORE THAN ONE AI HAS ANSWERED, so a tick alone no longer tells the whole story:
                // "2 AI agree" is a settled row and "AI split 2 - 1" is one to read. Shown only from
                // the second answer on - a single verdict has nothing to tally - and turned WHITE on a
                // selected row, where the blue swallows the amber it is written in.
                + "<TextBlock Text='{Binding AiTally}' Visibility='{Binding AiTallyVisible}' "
                + "FontSize='12' Margin='8,0,0,0' VerticalAlignment='Center'>"
                // WHITE ON A BLUE ROW. The blue is not the grid's selection - it is painted by the row
                // style on any row whose Fix box is ticked (see the RowStyle in the XAML) - so the
                // trigger watches Apply, the same thing that turns the row blue. The selection is
                // watched too, for a row that is blue for that reason instead.
                + "<TextBlock.Style><Style TargetType='TextBlock'>"
                + "<Setter Property='Foreground' Value='#8A6D00'/>"
                + "<Style.Triggers>"
                + "<DataTrigger Binding='{Binding Apply}' Value='True'>"
                + "<Setter Property='Foreground' Value='White'/>"
                + "</DataTrigger>"
                + "<DataTrigger Binding='{Binding IsSelected, RelativeSource={RelativeSource AncestorType={x:Type DataGridRow}}}' Value='True'>"
                + "<Setter Property='Foreground' Value='White'/>"
                + "</DataTrigger>"
                + "</Style.Triggers></Style></TextBlock.Style>"
                + "</TextBlock>"
                + "</StackPanel></Border>"
                + "<Border Grid.Row='1' Padding='7,3,7,3' MinHeight='24'>"
                + "<TextBlock Text='&#x2714; AI' Visibility='{Binding AiOnThen}' "
                + "FontWeight='Bold' Foreground='#0B4A0E'/>"
                + "</Border>"
                + "</Grid></DataTemplate></DataGridTemplateColumn.CellTemplate></DataGridTemplateColumn>";

            var col = (DataGridTemplateColumn)XamlReader.Parse(xaml);
            col.ClipboardContentBinding = new System.Windows.Data.Binding("AiReason");

            int after = -1;
            if (columns != null)
                for (int i = 0; i < columns.Count; i++)
                    if (AiAnswersAbout.Contains(columns[i], StringComparer.OrdinalIgnoreCase))
                        after = i;

            if (after < 0) { FindingsGrid.Columns.Add(col); return; }

            // The columns standing ahead of the data ones - the tick box and the "?" - are COUNTED
            // rather than assumed to be two. Adding a third one day would otherwise slide this
            // column quietly one place left and nobody would connect the two changes.
            int lead = FindingsGrid.Columns.Count - columns.Count;
            FindingsGrid.Columns.Insert(lead + after + 1, col);
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

            PushTickUndo(_rows.Where(r => r.Has(kind.Name)));

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

            ClearStaleHighlight();

            UpdateFixButton();
            UpdateFixAllBox();
            UpdateKeepAllBox();
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

            if (row.Fixable)
            {
                PushTickUndo(new[] { row });
                row.Apply = !row.Apply;
            }
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

        // Same as the Fix box beside it, except that no row is barred from it: approving a row the
        // program cannot write to is still a decision, and the commonest one on a report-only kind.
        private void Chk_Keep_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var box = sender as CheckBox;
            var row = box == null ? null : box.DataContext as FixRow;
            if (row == null) return;

            PushTickUndo(new[] { row });
            row.Keep = !row.Keep;
            e.Handled = true;
        }

        // Nothing approved -> approve every row on show; anything approved -> clear the lot. Only the
        // rows currently listed, so a kind filter's hidden rows are never swept in.
        private void Chk_KeepAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var rows = FindingsGrid.Items.OfType<FixRow>().ToList();
                bool keepAll = !rows.Any(r => r.Keep);
                PushTickUndo(rows);

                _syncingKind = true;                       // one update at the end, not one per row
                try
                {
                    foreach (FixRow r in rows) r.Keep = keepAll;
                }
                finally { _syncingKind = false; }
            }
            catch (Exception ex) { Log.Swallow(ex); }

            UpdateFixButton();
            UpdateFixAllBox();
            UpdateKeepAllBox();
            UpdateKindBoxes();
        }

        private void UpdateKeepAllBox()
        {
            if (_keepAllBox == null || _syncingKind) return;
            try
            {
                int rows = 0, kept = 0;
                foreach (FixRow r in FindingsGrid.Items.OfType<FixRow>())
                {
                    rows++;
                    if (r.Keep) kept++;
                }
                _keepAllBox.IsChecked = kept == 0 ? (bool?)false
                                      : kept == rows ? (bool?)true
                                      : null;
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        // The header's box, built in BuildColumns because the columns are remade on every scan.
        private CheckBox _fixAllBox;
        private CheckBox _keepAllBox;
        private CheckBox _revertAllBox;

        // The two answer columns, hidden until a kind is on show. Remade with the columns on every scan.
        private DataGridColumn _fixColumn;
        private DataGridColumn _keepColumn;
        // And the third, which is the other way round: on show ONLY in the list of contacts already
        // corrected, where the other two mean nothing.
        private DataGridColumn _revertColumn;

        // Nothing ticked -> tick every row that has something to go back to; anything ticked -> clear
        // the lot. The same header box as Fix and Do not change, and it obeys the same rule.
        private void Chk_RevertAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var rows = FindingsGrid.Items.OfType<FixRow>().Where(r => r.CanRevert).ToList();
                bool all = !rows.Any(r => r.Revert);
                _syncingKind = true;                       // one update at the end, not one per row
                try
                {
                    foreach (FixRow r in rows) r.Revert = all;
                }
                finally { _syncingKind = false; }
            }
            catch (Exception ex) { Log.Swallow(ex); }

            UpdateRevertButton();
        }

        // GREY UNTIL ONE IS TICKED, AND GREY AGAIN WHEN THE LAST IS CLEARED. The count goes into the
        // label, so the press he is about to make is a number he has read rather than one he has
        // counted himself.
        private void UpdateRevertButton()
        {
            if (Btn_Revert == null) return;
            int n = 0;
            try { n = FindingsGrid.Items.OfType<FixRow>().Count(r => r.Revert && r.CanRevert); }
            catch (Exception ex) { Log.Swallow(ex); }

            Btn_Revert.IsEnabled = n > 0;
            Btn_Revert.Content = n > 0 ? "Revert " + n.ToString("N0") + " selected" : "Revert Selected";

            if (_revertAllBox != null && !_syncingKind)
            {
                try
                {
                    int rows = FindingsGrid.Items.OfType<FixRow>().Count(r => r.CanRevert);
                    _revertAllBox.IsChecked = n == 0 ? (bool?)false
                                            : n == rows ? (bool?)true
                                            : null;
                }
                catch (Exception ex) { Log.Swallow(ex); }
            }
        }

        private void ShowAnswerColumns(bool show)
        {
            var how = show ? Visibility.Visible : Visibility.Collapsed;
            if (_fixColumn != null) _fixColumn.Visibility = how;
            if (_keepColumn != null) _keepColumn.Visibility = how;
        }

        // Nothing ticked -> tick every row on show that CAN be fixed; anything ticked -> clear the lot.
        // Only the rows currently listed, never the ones a kind filter is hiding.
        private void Chk_FixAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var rows = FindingsGrid.Items.OfType<FixRow>().ToList();
                bool tickAll = !rows.Any(r => r.Apply);
                PushTickUndo(rows);

                _syncingKind = true;                       // one update at the end, not one per row
                try
                {
                    foreach (FixRow r in rows) r.Apply = tickAll;
                }
                finally { _syncingKind = false; }

                ClearStaleHighlight();
            }
            catch (Exception ex) { Log.Swallow(ex); }

            UpdateFixButton();
            UpdateFixAllBox();
            UpdateKeepAllBox();
            UpdateKindBoxes();
            ShowFirstTickedRow();
        }

        // THE HIGHLIGHT MUST END UP SAYING WHAT THE TICKS SAY - AND IT SAYS IT BY GETTING OUT OF THE WAY.
        //
        // The row-by-row sync is switched off during a bulk tick, so a row the operator had merely
        // CLICKED ON, to read it, stayed blue while its box was empty: blue in this table means "this
        // one is going to be fixed", so the window said the opposite of the truth about a row the AI had
        // just told him to leave alone. That is what this call is for.
        //
        // IT USED TO ADD EVERY TICKED ROW TO THE SELECTION, and that was the coffee break: one click on
        // the header box selected 4,370 rows one at a time, and SelectedItems.Add on a twelve-column
        // template grid is quadratic - the same measurement that put SelectAll at 2.3 seconds on this
        // very table. Nothing was gained by it either. A ticked row is already painted by the RowStyle
        // trigger on Apply, which costs nothing because WPF styles only the rows on screen. So the
        // selection is simply emptied: what is blue afterwards is exactly what is ticked, and getting
        // there touches nothing but the handful of rows that were selected before.
        private void ClearStaleHighlight()
        {
            if (FindingsGrid == null) return;
            try
            {
                _syncingApply = true;
                FindingsGrid.SelectedItems.Clear();
            }
            catch (Exception ex) { Log.Swallow(ex); }
            finally { _syncingApply = false; }
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

        // ─── ASKING THE AI, FOR THE ONE PILE WORTH ASKING ABOUT ────────────────────────────────────
        //
        // Not the whole table. Of everything Verify reports, almost all of it is settled by a table
        // the program already holds: a grid that is not a grid, a continent that does not follow its
        // country, a spelling. Sending those to an AI would replace an answer with an opinion, and
        // spend the day's allowance doing it - 4,370 spellings would be 437 requests.
        //
        // What is left is the COUNTRY, in both its kinds: the pile where cty.dat and Club Log disagree,
        // and the pile where they agree with each other and against the log. The second was kept back
        // at first - two files agreeing looked like certainty - but they agree because they are built
        // from the same prefix tables, and an AI knows things no prefix table holds. Those two kinds
        // are what this button asks about, and no others.
        private CancellationTokenSource _aiRunning;

        // WHICH AI OR AIS ANSWERED WHAT IS IN THIS REPORT. Usually one, and named. It can be two -
        // a free allowance that ran out mid-log and a paid service that finished it - and then both
        // are named, because a report that says "answered by the AI" answers nothing at all.
        private string AiAuthors()
        {
            var names = new List<string>();

            foreach (FixRow r in _rows)
            {
                if (r == null || !r.AiAsked) continue;
                if (r.AiWho.Length == 0) continue;
                if (!names.Contains(r.AiWho)) names.Add(r.AiWho);
            }

            if (names.Count == 0) return string.Empty;
            if (names.Count == 1) return names[0];
            return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
        }

        // A count set against the widest of the counts beside it, so their units share an edge.
        private static string Count(int value, int widest)
        {
            return value.ToString(CultureInfo.InvariantCulture).PadLeft(widest);
        }

        // THE LINE THE OPERATOR WATCHES, WITH THE MODEL NAME IN BOLD.
        //
        // Which model is answering is the one word on that line worth finding at a glance - it is
        // what he is choosing between, and what makes 5-1 into 4-2. A TextBlock shows one weight of
        // text unless it is given Inlines, so **...** is turned into a bold run and everything else
        // is left as it stands.
        private void SaySummary(string text)
        {
            if (TB_KindsSummary == null) return;

            TB_KindsSummary.Inlines.Clear();

            string rest = text ?? string.Empty;
            bool bold = false;

            while (rest.Length > 0)
            {
                int mark = rest.IndexOf("**", StringComparison.Ordinal);
                string piece = mark < 0 ? rest : rest.Substring(0, mark);

                if (piece.Length > 0)
                {
                    var run = new Run(piece);
                    if (bold) run.FontWeight = FontWeights.Bold;
                    TB_KindsSummary.Inlines.Add(run);
                }

                if (mark < 0) break;

                rest = rest.Substring(mark + 2);
                bold = !bold;
            }
        }

        // THE VERDICTS AS THEY STAND, TAKEN HERE AND NOT IN THE WRITER. The report is written off the
        // UI thread - the country explanations are two database lookups apiece - and the rows it would
        // have to read them from are the window's own, changing as he ticks. Copied out first, what
        // the writer sees is one moment, whole.
        private Dictionary<QSO, string> AiVerdicts()
        {
            var said = new Dictionary<QSO, string>();

            foreach (FixRow r in _rows)
            {
                if (r == null || r.Qso == null || !r.AiAsked) continue;

                string words = r.AiSaid;
                if (words.Length > 0) said[r.Qso] = words;
            }

            return said;
        }

        private async void Btn_Ai_Click(object sender, RoutedEventArgs e)
        {
            // PRESSED WHILE IT IS RUNNING, THE BUTTON STOPS IT.
            //
            // It used to do nothing at all - the button was greyed and the press went nowhere - so a
            // run that hung left the operator with no way out of it but killing the program. The
            // token was there the whole time and nobody ever pulled it.
            if (_aiRunning != null)
            {
                try { _aiRunning.Cancel(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                TB_KindsSummary.Text = "Stopping...";
                return;
            }

            // Written out rather than escaped into the sentences: these messages are long, and a
            // paragraph break is easier to see as a name than as a pair of characters inside a string.
            string gap = Environment.NewLine + Environment.NewLine;

            // ── BOTH COUNTRY KINDS, NOT ONLY THE ARGUMENT ───────────────────────────────────────
            //
            // It used to ask only where cty.dat and Club Log disagree, on the reasoning that where the
            // two files agree the program knows exactly. They agree because they are built from the
            // same prefix tables, which is not the same as being right: an AI knows things neither
            // file holds - what a special callsign was issued for, where an expedition actually
            // operated from - and can disagree with both of them together, correctly.
            //
            // So both country kinds go to it. Everything else in the list - a locator that is not a
            // locator, a continent that does not match its own country - is arithmetic, and there is
            // nothing there for an opinion to add.
            // ── AND ONE KIND AT A TIME, LIKE EVERYTHING ELSE IN THIS WINDOW ─────────────────────
            //
            // The green button acts on the kind he is looking at. This one read the whole table, so a
            // screen showing six contacts sent twenty-six to the AI - twenty of them a kind he had not
            // opened, at his expense, on an allowance of twenty a day. What is on the screen is what
            // the window is about.
            var onShow = RowsOnShow();
            var ofThisKind = onShow.Where(r => r.Has(CountryNeedsDecision) || r.Has(CountryBothAgree))
                                   .ToList();
            if (ofThisKind.Count == 0)
            {
                HolyMessageBox.Show(_filterKind == null
                    ? "Choose a kind first." + gap
                      + "The AI is asked about the QSOs on screen, and with every kind listed together "
                      + "there is no one question to put to it. Click '" + CountryNeedsDecision
                      + "' or '" + CountryBothAgree + "' above, then press Check with AI."
                    : "There is nothing here for the AI to settle." + gap
                      + "It is asked about the QSOs whose COUNTRY is in question - the two kinds marked "
                      + "'" + CountryNeedsDecision + "' and '" + CountryBothAgree + "'. This kind is "
                      + "not a matter of opinion: a locator either is a locator or it is not.",
                    "Check with AI", HolyMsgType.Info, this);
                return;
            }

            // ASKED ONCE, NOT ONCE PER PRESS.
            //
            // The window stays open after a run, and pressing the button again used to send every
            // contact of this kind back for a second opinion - including the ones already answered,
            // whose question had not changed by a word: nothing is written to the log until Fix, so
            // the AI would be shown the same two countries and asked the same thing. On a free
            // allowance of twenty requests a day, that is paying twice for one answer.
            //
            // ASKED ONCE PER AI, NOT ONCE PER ROW FOR EVER.
            //
            // The same model is never asked the same question twice - nothing is written to the log
            // until Fix, so its answer would be word for word the one already on the row, bought again
            // out of a free allowance of twenty a day. But ANOTHER model is a second witness, and that
            // is worth having: the two disagree often enough to matter (Gemini said 5 and 1 where
            // OpenRouter said 4 and 2 on the same six QSOs), and a row they split on is exactly the
            // one to read. So what is skipped is what THIS AI has answered, not what any AI has.
            string current = AiServices.Current.ShortName + " (" + AiServices.Current.Model + ")";
            var rows = ofThisKind.Where(r => !r.AskedBy(current)).ToList();
            int already = ofThisKind.Count - rows.Count;

            // "Check with another AI" MEANS SHOW HIM THE CHOOSER, NOT A NOTICE. When the AI in use has
            // already answered every row on screen, the only thing the press can mean is "let me pick a
            // different one" - so it goes straight to the run dialog, which IS the chooser, with the
            // reason across the top. A message saying "go to Options and choose another" was an errand
            // where the program could simply have opened the door.
            bool pickAnother = rows.Count == 0;

            // NO KEY: SAY SO, AND OFFER THE PLACE IT IS SET.
            //
            // This used to open a small chooser of its own. There is now one page that does the
            // whole job - Options > AI Service, with the credit, the pictures, the key box and the
            // model - and a second window that does half of it is a second place to keep right. So
            // the message says what is missing and the button goes straight there, at the AI page,
            // rather than dropping him at the top of Options to find it.
            if (!AiQsoCheck.HasKey)
            {
                if (!HolyMessageBox.ShowConfirm(
                        "No API key is set yet." + gap
                        + "The AI needs a key of your own before it can check anything. Set it up in "
                        + "Options > AI Service - it takes a couple of minutes and there are pictures "
                        + "for every step.",
                        "Check with AI", HolyMsgType.Info, this, 0, "Set it up now", "Cancel")) return;

                var options = new OptionsWindow { Owner = this };
                options.AiItem.IsSelected = true;
                options.ShowDialog();

                // He may have pasted a key, picked a service that already had one, or closed it and
                // thought better of the whole thing. Only the first two mean carrying on, and asking
                // again is how we tell.
                if (!AiQsoCheck.HasKey) return;
            }

            // WHAT IT WILL COST, BEFORE IT IS SPENT. The free allowance is counted in requests per
            // day, and an operator who has just watched it run out deserves the number in front of him
            // rather than behind him.
            // THREE SHORT LINES, IN HIS OWN WORDS. This was a paragraph explaining how the colours
            // work and what a request costs, put in front of a man whose question is only "how many,
            // will it touch my log, and how long". Everything else it said is true and is said
            // elsewhere - the colours in the window itself, the reasoning under "?".
            //
            // The count of those already answered stays, and only when there are any: without it the
            // number on the first line is five where he can plainly see six, and he would be right to
            // wonder what happened to the other one.
            // THE SERVICE IS PART OF THE QUESTION, so it is named here and can be changed here.
            // AiRunPrompt is this dialog with the chooser standing in it.
            string asking = pickAnother
                ? "**" + current + "** has already answered all " + already
                  + (already == 1 ? " of these QSOs." : " of these QSOs.")
                  + Environment.NewLine + "Choose a different AI model to re check the " + already
                  + " QSO" + (already == 1 ? "" : "s") + "."
                  + Environment.NewLine + "Both answers are kept, and the AI column says where they disagree."
                : "**" + rows.Count + " QSO" + (rows.Count == 1 ? "" : "s") + "** will be checked by AI."
                + (already > 0
                    ? Environment.NewLine + already + (already == 1 ? " has" : " have")
                      + " been answered already and will not be asked again."
                    : string.Empty)
                + Environment.NewLine + "No QSO will be changed."
                + Environment.NewLine + "The process may take a few minutes.";

            if (!AiRunPrompt.Ask(this, asking)) return;

            // THE SERVICE CAN BE CHANGED IN THAT DIALOG - it has the chooser standing in it - so the
            // list is worked out again against whatever he settled on. Without this, switching to a
            // model that had already answered would buy its answers a second time.
            string chosen = AiServices.Current.ShortName + " (" + AiServices.Current.Model + ")";
            if (pickAnother || !string.Equals(chosen, current, StringComparison.OrdinalIgnoreCase))
            {
                rows = ofThisKind.Where(r => !r.AskedBy(chosen)).ToList();
                if (rows.Count == 0)
                {
                    // He came here to pick another and pressed OK on the same one. Nothing is spent, and
                    // the way out is the same button - so say which one it is and leave it at that.
                    HolyMessageBox.Show(
                        chosen + " has already answered about all of these." + gap
                        + "Press Check with another AI again and choose a service or a model it has not "
                        + "seen - its answers are then kept beside the ones already there.",
                        "Check with AI", HolyMsgType.Info, this);
                    return;
                }
            }

            var questions = new List<AiCountryVote.Question>();
            foreach (FixRow r in rows)
            {
                Cell c;
                // The country cell is what the two halves on screen are showing; without it there is
                // no question to ask, only a row to leave alone.
                if (!r.Cells.TryGetValue("Country", out c) || c == null) { questions.Add(null); continue; }
                questions.Add(new AiCountryVote.Question
                {
                    Qso = r.Qso,
                    Logged = c.Current,
                    Suggested = c.Proposed
                });
            }

            // Rows with no country cell drop out here, and the rows list drops with them so the two
            // stay in step - the answers come back keyed by position, and a list that has slipped by
            // one would paint every verdict onto the wrong contact.
            for (int i = questions.Count - 1; i >= 0; i--)
                if (questions[i] == null) { questions.RemoveAt(i); rows.RemoveAt(i); }

            if (questions.Count == 0) return;

            _aiRunning = new CancellationTokenSource();

            // "REQUEST 1 OF 12" IS ONLY WORTH SAYING IF THE 12 CAN BE READ.
            //
            // The progress used to be written into the button itself, which is 170 pixels wide and
            // holds "Check with AI". "Asking the AI - request 1 of 12..." does not fit in that, and a
            // button clips what it cannot show - so the operator watched a count with the total cut
            // off the end, which is the half that tells him whether to wait or go and make coffee.
            //
            // The summary line beside it runs the width of the frame and is doing nothing while the
            // AI is out, so the progress goes there and the button just stays a button.

            // The button stays alive and becomes the way out. Everything the run has already been
            // given - the rows it answered before it hung - is kept. Its own label is not saved: when
            // the run settles, UpdateAiButton writes the one that fits what has happened since.
            Btn_Ai.Content = "Stop AI check";

            // A LINE THAT NEVER CHANGES IS A WINDOW THAT LOOKS HUNG.
            //
            // Ten QSOs go into each request, so the ordinary run is ONE request: the errand says what
            // it is doing, once, and then that sentence sits there motionless for as long as the AI
            // takes to think - half a minute, longer if the allowance makes it wait. That is an exact
            // picture of a frozen program, and it is what it looked like.
            //
            // The seconds live INSIDE the spinner now, the way they do on the splash screen, rather
            // than being written onto the end of the sentence. The words say what is being done; the
            // turning ring and the number in it say it is still being done, and for how long.
            DateTime started = DateTime.UtcNow;

            PB_Ai.Visibility = Visibility.Visible;
            AiSeconds.Text = "0";

            var turn = new System.Windows.Media.Animation.DoubleAnimation(
                0, 360, new Duration(TimeSpan.FromSeconds(1.1)))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            AiSpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, turn);

            // Four times a second, so the number turns over close to when it should: a one-second
            // timer competing with a busy window can be most of a second late.
            var ticker = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            ticker.Tick += (t, a) =>
                AiSeconds.Text = ((int)(DateTime.UtcNow - started).TotalSeconds)
                                     .ToString(CultureInfo.InvariantCulture);
            ticker.Start();

            // THE SPINNER MUST STOP BEFORE ANYTHING IS SHOWN ON TOP OF IT.
            //
            // Every message this run puts up - the tally, a refusal, "stopped" - is a modal dialog
            // opened from inside the run, and the tidying-up used to wait in the finally underneath
            // it. So the bar went on turning and the seconds went on climbing BEHIND the window that
            // said the work was done, and the operator reading "6 of 6" could see the program still
            // apparently searching. Called the moment there is an answer, and again from the finally
            // in case something threw before it got here - which is why it can be called twice.
            // NAMED BEFORE THE RUN, NOT AS EACH ANSWER LANDS. He can work the service dropdown while
            // the request is in the air, and a verdict labelled with the service he switched TO would
            // be a lie about who said it.
            string who = AiServices.Current.ShortName + " (" + AiServices.Current.Model + ")";

            // Set when he picks another model after an allowance ran out, so the run restarts with
            // it instead of leaving him at a dead end.
            bool startAgain = false;

            bool settled = false;
            Action settle = () =>
            {
                if (settled) return;
                settled = true;

                ticker.Stop();
                AiSpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                PB_Ai.Visibility = Visibility.Collapsed;
                // Not the line it had before the run: an AI has answered since, and the line now has
                // something better to say than what to press.
                UpdateKindsSummary();
                // NOT put back to the label it had. It said "Check with AI" when the run started and an
                // AI has answered since, so the button's next press means another one - UpdateAiButton
                // writes the words as well as the enabled state. Restoring wasAiLabel here first was
                // harmless but read as though this line decided the text; it does not.
                UpdateAiButton();
            };

            try
            {
                Dictionary<int, AiCountryVote.Answer> answers =
                    await AiCountryVote.AskAsync(questions,
                        text => Dispatcher.Invoke(new Action(() => SaySummary(text))),
                        _aiRunning.Token,

                        // EACH VERDICT PAINTED AS IT ARRIVES. The answers come back one line at a
                        // time now, so the first row turns green while the model is still writing
                        // about the second. The tally at the end still walks every row - this is
                        // what he watches, that is what he is told.
                        (index, a) => Dispatcher.Invoke(new Action(() =>
                        {
                            if (a == null || index < 0 || index >= rows.Count) return;
                            _aiAnswered = true;   // an AI has spoken in this window; the button says so
                            switch (a.Backs)
                            {
                                case AiCountryVote.Backs.Log:
                                    rows[index].SetAi(AiSide.Now, a.Reason, who); break;
                                case AiCountryVote.Backs.Suggested:
                                    rows[index].SetAi(AiSide.Then, a.Reason, who); break;
                                case AiCountryVote.Backs.Neither:
                                    rows[index].SetAi(AiSide.Neither, a.Reason, who); break;
                                default:
                                    rows[index].SetAi(AiSide.Unsure, a.Reason, who); break;
                            }
                        })));

                settle();

                int backedLog = 0, backedUs = 0, neither = 0, unsure = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    AiCountryVote.Answer a;
                    // A contact the AI never answered about is left exactly as it was. Silence is not
                    // agreement, and colouring it either way would say something nobody said.
                    if (!answers.TryGetValue(i, out a) || a == null) continue;

                    // THE OTHER ROUTE A VERDICT TAKES. A service that streams its answers has already
                    // set this in the callback above; one that returns them all at once arrives here,
                    // AFTER settle - which is why the line under the kinds, written in settle, still
                    // said "press this" over a table full of verdicts.
                    _aiAnswered = true;

                    switch (a.Backs)
                    {
                        case AiCountryVote.Backs.Log:
                            rows[i].SetAi(AiSide.Now, a.Reason, who); backedLog++; break;
                        case AiCountryVote.Backs.Suggested:
                            rows[i].SetAi(AiSide.Then, a.Reason, who); backedUs++; break;
                        case AiCountryVote.Backs.Neither:
                            rows[i].SetAi(AiSide.Neither, a.Reason, who); neither++; break;
                        default:
                            rows[i].SetAi(AiSide.Unsure, a.Reason, who); unsure++; break;
                    }
                }

                int silent = rows.Count - backedLog - backedUs - neither - unsure;

                // ── THE AI'S ANSWERS, ALREADY TICKED ────────────────────────────────────────────
                //
                // Asking an AI and then being handed a table of empty boxes is being asked the same
                // question twice. Its answer IS a recommendation for these two columns - correct this
                // one, keep that one - so the boxes arrive holding it, and the work left is only the
                // rows he disagrees with.
                //
                // Only the two clear verdicts. "It could be a different country again" and "I have no
                // answer for you" are not recommendations, and a box ticked on either would be putting
                // an opinion in the AI's mouth; those rows stay empty, for him.
                //
                // On the undo stack as one press, so Ctrl+Z takes the whole set back if he would
                // rather start from nothing.
                PushTickUndo(rows);
                _syncingKind = true;
                try
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        AiCountryVote.Answer a;
                        if (!answers.TryGetValue(i, out a) || a == null) continue;
                        if (a.Backs == AiCountryVote.Backs.Log) rows[i].Keep = true;
                        else if (a.Backs == AiCountryVote.Backs.Suggested) rows[i].Apply = true;
                    }
                }
                finally { _syncingKind = false; }

                UpdateFixButton();
                UpdateFixAllBox();
                UpdateKeepAllBox();
                UpdateKindBoxes();

                // THE WHOLE ARGUMENT, WRITTEN DOWN.
                //
                // The report was made when the scan finished, before the AI had been asked anything -
                // so what it holds is two databases disagreeing and nothing to settle it. Now there is
                // a verdict and the reasoning behind it, and a tooltip is no place to keep reasoning:
                // it cannot be printed, searched, or read through a hundred contacts at a time. So the
                // report is written again with all of it in. The first file is left alone.
                Dictionary<QSO, string> verdicts = AiVerdicts();
                List<Finding> withAi = _findings.ToList();

                // TIMED SEPARATELY FROM THE AI. This report is built from every finding in the log -
                // 4,522 of them on a real log - and the country sections cost two database lookups a
                // row. It is written BEFORE the result is shown, so its time is time the operator
                // spends staring at nothing and blaming the AI for.
                var reportClock = System.Diagnostics.Stopwatch.StartNew();

                string reportPath = await Task.Run(() => WriteFixerReport(withAi, verdicts, AiAuthors()));

                Log.Warn("Log Fixer, check with AI: the report took " + reportClock.ElapsedMilliseconds
                         + " ms for " + withAi.Count + " finding(s)");

                // THE THREE ANSWERS, ONE TO A LINE, WITH THE COUNT AT THE END OF EACH.
                //
                // It was a sentence - "backed your log on 5, HolyLogger on 1, and could not tell on
                // 0" - and a sentence has to be read through to be counted. Three lines are read at a
                // glance, and the one that matters, the number of contacts he now has to look at, is
                // at the end of its own line instead of buried in the middle of a clause.
                // THE TALLY IS A TABLE AND IS DRAWN AS ONE, by the dialog, in two real columns.
                // Written into the message as label-spaces-number it lined up in no font this
                // program uses. Related: BuildCounts in HolyMessageBox.
                var counts = new List<KeyValuePair<string, int>>
                {
                    new KeyValuePair<string, int>("The AI thinks your log is correct", backedLog),
                    new KeyValuePair<string, int>("The AI suggests a correction", backedUs),
                    new KeyValuePair<string, int>("The AI says it is a different country", neither),
                    new KeyValuePair<string, int>("The AI has no answer for you", unsure),
                };

                if (silent > 0)
                    counts.Add(new KeyValuePair<string, int>("Never answered, and left untouched", silent));

                // THREE SHORT LINES, ONE FACT EACH, and the last of them the one that matters most:
                // that none of this has touched his log yet. A paragraph saying the same thing has
                // to be read to the end before it says so.
                string message =
                      "Green is the one the AI thinks is correct." + Environment.NewLine
                    + "Grey is the one it does not recommend." + Environment.NewLine
                    + "Your log has not been changed yet.";

                // AND WHAT TO DO ABOUT IT. The verdict was on screen and the next move was not: a man
                // who has just been told the AI wants six contacts changed has no way of knowing from
                // this message that HE has to tick them, one by one, before anything happens. Said only
                // when there is something to tick - on a run where the AI backed the log every time,
                // pointing at a Fix column he does not need is an instruction to ignore.
                if (backedLog > 0 || backedUs > 0)
                    message += Environment.NewLine + Environment.NewLine
                             + "The boxes are already ticked to match: **Fix** where the AI suggests a "
                             + "correction, **Do not change** where it agrees with your log. Change any "
                             + "you disagree with, then press the green button.";

                // AND WHO SAID IT. He ran the same six QSOs past two services and got two different
                // answers, and this window - the one he actually reads - was the only place that did
                // not say which of them he was looking at.
                // BOLD, AND WITH THE TIME IT TOOK. Which model answered is the difference between
                // 5-1 and 4-2 on the same six QSOs, so it is the line worth finding at a glance -
                // and how long it took is what he decides the next run on.
                string author = AiAuthors();
                if (author.Length > 0)
                {
                    int seconds = (int)(DateTime.UtcNow - started).TotalSeconds;

                    message += gap + "Answered by **" + author + "**"
                             + ", in " + seconds + (seconds == 1 ? " second." : " seconds.");
                }

                // THE PATH IS THE BUTTON. A report announced as words in a folder is a report the
                // operator has to go and find; printed as a link it is one press away, and still
                // readable and copyable as a path.
                if (!string.IsNullOrEmpty(reportPath))
                {
                    var links = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>(
                            "The full report on this log, with what the AI said and why:", reportPath)
                    };
                    HolyMessageBox.ShowWithLinks(message, "Check with AI", HolyMsgType.Success, this,
                                                 links, ShowInFolder, 620, null, counts);
                }
                else
                {
                    HolyMessageBox.ShowWithLinks(message, "Check with AI", HolyMsgType.Success, this,
                                                 null, null, 620, null, counts);
                }
            }
            catch (OperationCanceledException)
            {
                // STOPPED ON PURPOSE, OR THE WINDOW WENT AWAY UNDER IT. Only the first is worth a
                // word, and only while there is still a window to say it in. What matters to him is
                // that stopping cost him nothing: the verdicts already given are on the rows, and
                // the next press asks about the rest and no more.
                settle();

                if (IsLoaded)
                    HolyMessageBox.Show(
                        "Stopped." + gap
                        + "Whatever the AI had already answered is kept. Press Check with AI again "
                        + "and it will ask about the ones it never got to.",
                        "Check with AI", HolyMsgType.Info, this);
            }
            catch (Exception ex)
            {
                settle();

                Log.Warn("Log Fixer, check with AI: " + ex.GetType().Name + ": " + ex.Message);

                // AN ALLOWANCE THAT HAS RUN OUT IS NOT NEWS, IT IS A DECISION TO MAKE. Saying so and
                // leaving him with an OK button means he has to go and find the chooser himself - so
                // the offer is made here, and taking it opens the thing.
                bool spent = ex.Message.IndexOf("allowance", StringComparison.OrdinalIgnoreCase) >= 0;
                if (spent && HolyMessageBox.ShowConfirm(
                        ex.Message + gap + "Choose a different AI service now?",
                        "Check with AI", HolyMsgType.Warning, this))
                {
                    // STRAIGHT BACK TO THE RUN DIALOG, WHICH IS ALREADY THE CHOOSER.
                    //
                    // Saying yes here used to open a window to pick a model in, and pressing its
                    // button then opened the run dialog to press OK in - two windows and three
                    // presses to answer one question. The run dialog holds the service, the model,
                    // the credit and an OK: it is the only window this needed.
                    startAgain = true;
                }
                else if (!spent)
                {
                    HolyMessageBox.ShowWarning(ex.Message, "Check with AI", this);
                }
            }
            finally
            {
                if (_aiRunning != null) { _aiRunning.Dispose(); _aiRunning = null; }
                settle();

                // AND AGAIN HERE, OUTSIDE settle. settle runs once and it runs EARLY - as soon as the
                // service answers, before the verdicts are counted, before the report and before the
                // message the operator reads. Both of these were therefore decided while the run was
                // still finishing, and the second call, in this finally, was swallowed by settle's own
                // once-only guard. These are not.
                UpdateAiButton();
                UpdateKindsSummary();

                // Queued rather than called: this one is still inside its own finally, and starting
                // the next before that has finished would meet its own half-cleared state.
                if (startAgain && IsLoaded)
                    Dispatcher.BeginInvoke(new Action(() => Btn_Ai_Click(this, null)),
                                           System.Windows.Threading.DispatcherPriority.Background);
                // The ticks may have moved under it: every row the AI gave to the log has just lost
                // its own, so the count on the Fix button is no longer the one it was showing.
                UpdateFixButton();
            }
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

            UpdateKindsSummary();

            // The two answer columns belong to a kind, and so does the green button's offer.
            ShowAnswerColumns(kindName != null);

            // The header boxes describe the rows on show, and the rows on show have just changed.
            UpdateFixButton();
            UpdateFixAllBox();
            UpdateKeepAllBox();
            UpdateAiButton();
        }

        // THE AI IS ASKED ABOUT ONE KIND, SO IT CANNOT BE PRESSED WITHOUT ONE. The question put to it
        // is written for the kind on screen - which country is right for this callsign on this date -
        // and with every kind listed together there is no such question to ask. It used to be pressable
        // at any time and answer with a message explaining why it would not run; a button that can only
        // say no is better greyed, with the reason in its tooltip.
        //
        // Only the two COUNTRY kinds qualify. The rest are not matters of opinion: a locator either is
        // a locator or it is not, a band either matches the frequency or it does not.
        // THE LINE UNDER THE KINDS, AND WHAT IT IS FOR AT THIS MOMENT.
        //
        // Before the AI is asked it says what to do next. Afterwards it says the one thing a man
        // looking at a table of blue and grey lines wants to know: which of them the press will
        // change. Two short lines, one fact each - see the standing rule about plain wording.
        private void UpdateKindsSummary()
        {
            if (TB_KindsSummary == null) return;

            // ONLY AFTER THE AI HAS ANSWERED. These two lines explain a result, and put on screen
            // before there is one they describe colours the operator has not seen the meaning of yet.
            // Until then the line says what to press, which is what he needs at that moment.
            if (_aiAnswered)
            {
                TB_KindsSummary.Text =
                      "AI suggests to correct the QSOs in the blue lines only." + Environment.NewLine
                    + "QSOs in the grey lines remain unchanged.";
                return;
            }

            TB_KindsSummary.Text = _filterKind == null
                ? "Click a kind above to work through its QSOs."
                : "Showing one kind only. Press Show all kinds to see the rest.";
        }

        private string _lastAiButtonLabel;

        // True from the first verdict this window ever receives. Set in the answer callback, so it does
        // not depend on finding the answered rows again afterwards.
        private bool _aiAnswered;

        private void UpdateAiButton()
        {
            if (Btn_Ai == null) return;

            bool aiKind = string.Equals(_filterKind, CountryNeedsDecision, StringComparison.Ordinal)
                       || string.Equals(_filterKind, CountryBothAgree, StringComparison.Ordinal);

            // ONCE ONE AI HAS ANSWERED, THE BUTTON OFFERS THE NEXT ONE. The words matter: "Check with
            // AI" on a screen full of green ticks reads as "do it again", and he would expect the same
            // answers back. "Check with another AI" says what a second press is actually for.
            // ASKED OF EVERY ROW IN THE WINDOW, not only the ones on show. RowsOnShow answers nothing
            // at all while no kind is chosen, so the label fell back to "Check with AI" every time he
            // pressed Show all kinds and came back - after an AI had plainly answered.
            int withAnswer = 0;
            foreach (FixRow r in _rows) if (r != null && r.AiCount > 0) withAnswer++;

            // OR SIMPLY: AN AI HAS ANSWERED IN THIS WINDOW. The count above is the honest measure and
            // it is kept, but the label must not depend on finding those rows again - it went on saying
            // "Check with AI" over a screen of green ticks, and that is the one thing the button must
            // never do. _aiAnswered is set as each verdict lands and never cleared.
            bool answered = withAnswer > 0 || _aiAnswered;
            string label = answered ? "Check with another AI" : "Check with AI";

            // WRITTEN DOWN WHEN IT CHANGES. The label stayed at "Check with AI" on a screen full of
            // verdicts and the reason was not visible from a screenshot - this line says what this
            // method saw and what it wrote, so the next time it is answered from the operator's own log
            // instead of by reading pixels.
            if (!string.Equals(_lastAiButtonLabel, label, StringComparison.Ordinal))
            {
                _lastAiButtonLabel = label;
                Log.Warn("Log Fixer: AI button -> \"" + label + "\"  (rows holding an AI answer: "
                         + withAnswer + " of " + _rows.Count + ")");
            }

            Btn_Ai.Content = label;

            Btn_Ai.IsEnabled = aiKind;
            Btn_Ai.ToolTip = aiKind
                ? (answered
                    ? "Ask a second AI about the same QSOs. Both answers are kept, and the AI column "
                      + "says where they disagree. Pick the service or model in the next dialog."
                    : "Ask the AI about the QSOs listed below.")
                : "Click \"" + CountryNeedsDecision + "\" or \"" + CountryBothAgree + "\" above first. "
                  + "The AI is asked which country is right for a callsign on the date of the QSO, so it "
                  + "is only offered for those two kinds.";
        }

        // ── THE PRESS THAT WRITES NOTHING AND STILL SETTLES SOMETHING ───────────────────────────────
        //
        // He read the rows and agreed with the log about every one of them. No contact changes, so no
        // safety copy is taken and no report is filed - the whole of it is the note that these were
        // looked at and approved, and the re-check afterwards, which takes them off the screen.
        private async Task ApproveOnly()
        {
            List<FixRow> approving = RowsToApprove();
            if (approving.Count == 0)
            {
                // TWO DIFFERENT REASONS FOR AN EMPTY PRESS, and they need two different answers. One
                // kind at a time is the rule, so with every kind listed there is nothing this button
                // could act on and the way out is to choose a kind - not to hunt for a box to tick.
                HolyMessageBox.Show(_filterKind == null
                    ? "Nothing was written, because no kind of problem is chosen.\n\n"
                      + "The Log Fixer works through one kind at a time. Click a kind above — Wrong "
                      + "continent, DX Locator is wrong — and the table shows only those QSOs."
                    : "Nothing was written, because nothing is ticked.\n\n"
                      + "Tick Fix on the rows to correct, or Do not change on the ones to keep as "
                      + "they are.",
                    "Log Fixer", HolyMsgType.Info, this);
                return;
            }

            var dal = DataAccess.GetInstance();
            if (dal == null)
            {
                HolyMessageBox.ShowWarning(
                    "The log database is not open.\n\nClose HolyLogger and open it again.",
                    "Log Fixer", this);
                return;
            }

            int byAi = approving.Count(r => r.AiBacksLog);
            string one = approving.Count == 1 ? "QSO" : "QSOs";
            if (!HolyMessageBox.ShowConfirm(
                    "Nothing will be changed in your log.\n\n"
                    + approving.Count.ToString("N0") + " " + one + " ticked in Do not change will be marked "
                    + (byAi == 0            ? "APPROVED BY YOU"
                     : byAi == approving.Count ? "APPROVED, WITH THE AI BACKING YOU"
                     : "APPROVED — " + byAi.ToString("N0") + " of them with the AI backing you")
                    + ", which means the Log Fixer stops raising "
                    + (approving.Count == 1 ? "it" : "them") + ".\n\nApprove "
                    + (approving.Count == 1 ? "it" : "them") + " now?",
                    "Log Fixer", HolyMsgType.Info, this))
                return;

            try
            {
                foreach (var whoApproved in approving.GroupBy(r => r.AiBacksLog))
                {
                    int state = whoApproved.Key ? 4 : 2;
                    List<QSO> qsos = whoApproved.Select(r => r.Qso).Distinct().ToList();
                    if (qsos.Count == 0) continue;
                    dal.SetReviewState(qsos.Select(q => q.id), state);
                    foreach (QSO q in qsos) q.ReviewState = state;
                }
                MarkCleanAsChecked(dal, null);
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                HolyMessageBox.ShowError("These QSOs could not be marked.\n\n" + ex.Message,
                                         "Log Fixer", this);
                return;
            }

            HolyMessageBox.ShowSuccess(
                approving.Count.ToString("N0") + " " + one + " approved. Nothing in your log was changed.",
                "Log Fixer", this);

            // What he has just answered is answered. See _hideAlreadySettled.
            _hideAlreadySettled = true;
            await RunCheck();
        }

        // THE CONTACTS THIS RUN HAD NOTHING TO SAY ABOUT. Written wherever a run is answered, so that
        // "never checked" keeps meaning "logged since the last time I answered this window" whether the
        // answer was a correction, an approval, or both.
        private void MarkCleanAsChecked(DataAccess dal, HashSet<QSO> removed)
        {
            List<QSO> clean = _qsos.Where(q => q != null && q.id > 0 && q.ReviewState == 0
                                               && !_withFindings.Contains(q)
                                               && (removed == null || !removed.Contains(q)))
                                   .ToList();
            if (clean.Count == 0) return;
            dal.SetReviewState(clean.Select(q => q.id), 3);
            foreach (QSO q in clean) q.ReviewState = 3;
        }

        // THE ROWS THIS PRESS WOULD APPROVE: on screen, could have been ticked, and not ticked. Read
        // from the grid's own view, so a kind filter's hidden rows are not swept into a decision he was
        // never shown. Nothing already settled is counted - it is not his to answer twice.
        // WHAT HE TICKED IN THE "DO NOT CHANGE" COLUMN, and nothing else.
        //
        // This used to be worked out - the rows on screen he had NOT ticked for fixing - and every
        // attempt at drawing the line was wrong in a different way: with all kinds listed it offered to
        // approve 136 contacts he had never scrolled to, and narrowed to one kind it still counted rows
        // he had merely walked past. A tick in a box he pressed himself needs no rule and no reading of
        // his mind, so the whole of that guesswork is one property now.
        //
        // ONE KIND AT A TIME. Read from the grid's view, and only while a kind is being shown: a wrong
        // continent and a locator that is not a locator are two different jobs, judged differently, and
        // a window that writes all of them on one press invites a press that was meant for one of them.
        // With every kind listed the table is a summary of the work, not the work.
        private List<FixRow> RowsToApprove()
        {
            return RowsOnShow().Where(CanApprove).ToList();
        }

        // The rows the green button acts on: the ones listed, which is one kind's worth. Empty while
        // all kinds are on show, which is what makes the button inert there.
        // ── THE SECOND SCROLLBAR ────────────────────────────────────────────────────────────────────
        // The one on the left drives the same ScrollViewer as the one on the right, and follows it:
        // drag either, or turn the wheel, and both show the same place in the list. It is hidden while
        // the whole list fits, so it never stands there as a bar that cannot move.
        private void KindsLeftBar_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
        {
            if (KindsScroller != null) KindsScroller.ScrollToVerticalOffset(e.NewValue);
        }

        private void KindsScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (KindsLeftBar == null || KindsScroller == null) return;
            KindsLeftBar.Visibility = KindsScroller.ScrollableHeight > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private List<FixRow> RowsOnShow()
        {
            if (FindingsGrid == null || _filterKind == null) return new List<FixRow>();
            return FindingsGrid.Items.OfType<FixRow>().ToList();
        }

        private static bool CanApprove(FixRow r)
        {
            return r != null && r.Keep && r.Qso != null && r.Qso.ReviewState == 0;
        }

        // THE BUTTON SAYS BOTH HALVES OF WHAT IT DOES. It wrote corrections and, in the same press,
        // marked the rows he had read and left as approved - and said only the first, so the second
        // happened to four contacts without ever being on screen. "Fix 2, approve 4" is the press.
        //
        // AND IT IS ALIVE WITH NOTHING TICKED, which it never used to be. A man who reads six rows and
        // agrees with the log about all six has made a decision, and refusing to record it unless he
        // first ticks something he does not want ticked is the program arguing with him.
        private void UpdateFixButton()
        {
            if (Btn_Fix == null) return;
            if (_syncingKind) return;          // one update at the end of the sweep, not one per row
            int n = RowsOnShow().Count(r => r.Apply);
            List<FixRow> toApprove = RowsToApprove();
            int approving = toApprove.Count;
            int byAi = toApprove.Count(r => r.AiBacksLog);

            // WHO IS DOING THE APPROVING, ON THE BUTTON ITSELF. An approval an AI has backed is a
            // different piece of evidence from one given on his own judgement - it is stored as one -
            // so the button that writes it says which it is about to write.
            string approve = byAi == 0         ? "approve " + approving.ToString("N0")
                           : byAi == approving ? "AI approve " + approving.ToString("N0")
                           : "approve " + approving.ToString("N0") + " (" + byAi.ToString("N0")
                             + " with AI)";

            Btn_Fix.IsEnabled = n > 0 || approving > 0;

            // NO KIND, NO WORDS. "Fix selected" over a button that cannot act on anything is a promise
            // the window will not keep; blank, it is plainly waiting for him to choose a kind.
            Btn_Fix.Content =
                  _filterKind == null     ? ""
                : n > 0 && approving > 0 ? "Fix " + n.ToString("N0") + ", " + approve
                : n > 0                  ? "Fix " + n.ToString("N0") + " selected"
                : approving > 0          ? approve
                                         : "Fix selected";
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

        // The country NAME in the log is already right and only its code is not, so the QSO counts for
        // a country it does not say. Named for what is wrong, like "Wrong country name" beside it.
        private const string WrongCountryCode = "Wrong country code";

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
            // WHAT HE WAS ACTUALLY LOOKING AT WHEN HE PRESSED IT, read now, before anything is written
            // or awaited. A kind filter hides rows, and a row he never saw is not a row he decided
            // about - that is the mistake the old on-close question made, 4,522 contacts at a time.
            // ONE KIND'S WORTH, and empty unless a kind is being shown - the same rows the button
            // counted when it wrote its own label, so what he was promised is what gets written.
            var onScreen = RowsOnShow();
            var approvable = RowsToApprove();

            // THE ROWS DECIDE, NOT THE FINDINGS. A row can be worth writing for either of two reasons:
            // the program proposed something, or the operator typed something. Gating on findings alone
            // threw away every hand-typed answer - a checked row whose only finding was report-only
            // (Club Log's "did not count", say) fell straight through this and Fix appeared to do
            // nothing at all, which is exactly what it did.
            // AND WHEN THERE IS NOTHING TO WRITE, SAY SO. Both of these used to return in silence, so
            // pressing Fix with nothing ticked - or with rows ticked that carry no answer yet - looked
            // exactly like a Fix that had run and reported nothing.
            List<FixRow> rows = onScreen.Where(r => r.Apply && r.Fixable && r.Qso != null).ToList();
            if (rows.Count == 0)
            {
                // NOTHING TICKED IS STILL AN ANSWER when there are rows on screen: he read them and the
                // log was right about all of them. Nothing is written to a single contact, so there is
                // no safety copy to take and no report to file - only the note that these have been
                // settled, which is what stops them being raised at him again next month.
                await ApproveOnly();
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
                HolyMessageBox.ShowWarning(
                    "The log database is not open.\n\n"
                    + "Close HolyLogger and open it again.",
                    "Log Fixer", this);
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

            // AND THE OTHER HALF OF THE SAME PRESS: the rows ticked in Do not change. That is an answer
            // too - the log was right - and it is written down as one, so the Fixer stops asking about
            // them. Worth knowing BEFORE the press, not discovered afterwards. Counted the same way the
            // write counts them, so the number he is shown is the number that will be marked.
            List<FixRow> approvingRows = approvable.Where(r => !held.Contains(r)).ToList();
            int approving = approvingRows.Count;
            int byAi = approvingRows.Count(r => r.AiBacksLog);
            if (approving > 0)
            {
                what += ".\n\n" + approving.ToString("N0") + " QSO"
                        + (approving == 1 ? "" : "s") + " ticked in Do not change "
                        + (approving == 1 ? "stays" : "stay") + " exactly as "
                        + (approving == 1 ? "it is" : "they are") + " and "
                        + (approving == 1 ? "is" : "are") + " marked ";
                // Who approved it is written into the log, so it is named here too - and only the part
                // that is true. Saying "with the AI's backing" over a run he never asked an AI about
                // would be the message describing somebody else's evening.
                what += byAi == 0        ? "APPROVED BY YOU"
                      : byAi == approving ? "APPROVED, WITH THE AI BACKING YOU"
                      : "APPROVED — " + byAi.ToString("N0") + " of them with the AI backing you";
                what += ", so the Log Fixer stops raising " + (approving == 1 ? "it" : "them");
            }

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

            // ── THE SILENCE BETWEEN THE PRESS AND THE SPINNER ────────────────────────────────────
            //
            // The safety copy is the whole database - tens of megabytes - copied on the UI thread with
            // nothing on screen to say so, and it happened BEFORE the overlay appeared. Ten QSOs or one,
            // the wait was the same, and the window looked dead for the whole of it. Now the overlay is
            // up first and the copy runs off the thread, so the ring turns while the file is written.
            ShowFixOverlay(0, "Copying your database…", "");
            var backupClock = System.Diagnostics.Stopwatch.StartNew();
            string backup = await Task.Run(() => SaveBackup(dal));
            Log.Warn("Log Fixer: the safety copy took " + backupClock.ElapsedMilliseconds + " ms");

            if (backup == null)
            {
                HideFixOverlay();
                if (!HolyMessageBox.ShowConfirm("The safety copy of the log could not be written.\n\nFix "
                        + "them anyway?", "Log Fixer", HolyMsgType.Warning, this))
                    return;
            }

            Btn_Fix.IsEnabled = false;

            // Driven by the CHECKED ROWS. Grouping the findings instead skipped any row that had no
            // fixable finding on it - which is precisely the row somebody typed an answer into.
            int total = rows.Count;
            ShowFixOverlay(total);

            int written = 0;
            var gone = new List<QSO>();      // the contacts actually removed, for the windows showing them

            // WHAT EACH CONTACT SAID BEFORE THIS PRESS. Written into the log beside the correction, in
            // the same transaction, so that "Already checked and fixed" can show him the old value
            // months later instead of a contact he has to take on trust. Until this existed the only
            // surviving copy of the old value was inside a whole-database backup file.
            // ONE STAMP FOR THE WHOLE PRESS, not one per contact: this was one act, and four thousand
            // rows differing by a second would read as four thousand acts.
            var history = new List<FixNote>();
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd HHmm");
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

                        // Read before a single field moves. Compared again below, once everything
                        // this row is going to write has been written.
                        var wasBefore = Snapshot(qso);

                        foreach (Finding f in r.Findings) if (f.Fixable) ApplyTo(qso, f);

                        // Anything the operator typed is written AFTER the suggestions, so their word
                        // is the last one. A suggestion is applied by the finding that made it, which
                        // knows to carry the entity number and the zones along with a country name; a
                        // typed value goes to that one field alone.
                        foreach (var kv in r.Cells)
                            if (kv.Value.UserEdited && kv.Value.Proposed.Trim().Length > 0)
                                WriteField(qso, kv.Key, kv.Value.Proposed.Trim());

                        dal.Update(qso);
                        NoteChanges(qso, wasBefore, stamp, history);
                        written++;

                        // A count that moves, so a long job is visibly a long job and not a dead one.
                        if (written % 50 == 0 || written == total)
                        {
                            int done = written;
                            Dispatcher.BeginInvoke(new Action(() => UpdateFixOverlay(done, total)));
                        }
                    }

                    // Inside the transaction with the corrections themselves: a run that falls over
                    // halfway leaves neither the change nor the record of it.
                    dal.SaveFixHistory(history);
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

            // Down before the duplicate-comment question, which is a window he has to answer - but NOT
            // down for good: the marks and the log reload still to come are the slowest part of the
            // whole press, and they used to happen with the spinner already gone.
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

            // ── WRITTEN, AND REMEMBERED AS WRITTEN ───────────────────────────────────────────────
            //
            // These are settled: he was shown the problem, he ticked it, and the correction is in the
            // log. The scan should never put them to him again - and if a later country file disagrees
            // with the value he has just chosen, that is a fresh argument he can ask for by choosing
            // "Already checked and fixed" when the window opens, not one to be reopened behind his back.
            //
            // The contacts that were REMOVED are left out: there is no row left to mark. So are the ones
            // held back for the comment question - a group he chose to skip there was not written at
            // all, and calling that "corrected" would bury a duplicate pair he has not decided about.
            //
            // ── AND THE TWO OTHER ANSWERS THE SAME PRESS GAVE ────────────────────────────────────
            //
            // Pressing Fix answers the whole table, not only the ticked lines. A row he read and did not
            // tick is "leave this one" - state 2 - and a contact the scan found nothing to say about is
            // "nothing wrong here" - state 3. Without 3 a clean contact stayed unchecked for ever and
            // the never-checked count could never fall below the size of the log, which is the one
            // number he wants to watch: what have I logged since the last time I did this.
            //
            // 2 is written only for rows that were ON SCREEN and could have been ticked. A row hidden by
            // a kind filter, a report-only row with nothing to tick, a row held for the comment question
            // - none of those is a decision, and calling them one is how the old build silenced 4,522
            // contacts. And nothing here ever overwrites a state that is already set: the run he is
            // answering is about the rows in front of him, not about what he settled last month.
            // Up again for the marks and the reload. Everything from here to the message is the log
            // itself being brought up to date, and on a 28,000-QSO log it is the longest part of the
            // press - which is exactly why it must not happen behind a window that looks finished.
            ShowFixOverlay(0, "Updating the log…", "");
            var marksClock = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var goneNow = new HashSet<QSO>(gone);
                List<QSO> settledRows = rows.Where(r => r.Qso != null && !goneNow.Contains(r.Qso)
                                                      && !held.Contains(r))
                                          .Select(r => r.Qso).Distinct().ToList();
                if (settledRows.Count > 0)
                {
                    dal.SetReviewState(settledRows.Select(q => q.id), 1);
                    foreach (QSO q in settledRows) q.ReviewState = 1;
                }

                var putRight = new HashSet<QSO>(settledRows);
                List<FixRow> approvedRows = approvable
                    .Where(r => !putRight.Contains(r.Qso) && !goneNow.Contains(r.Qso)
                                && !held.Contains(r))
                    .ToList();

                // WHO APPROVED IT, kept apart. An approval an AI read the contact and gave - the log is
                // right - is not the same piece of evidence as one he gave on his own judgement, and
                // the day a country file changes its mind about a prefix, the two are worth telling
                // apart. 4 is the AI-backed one, 2 is his own.
                //
                // THE AI HAVING BEEN ASKED IS NOT THE SAME AS THE AI AGREEING. Asked covers the rows
                // where it said the log is WRONG, and calling those AI-approved because he left them
                // unticked would put words in its mouth: leaving that row is him overruling it, which
                // is his own approval and nobody else's.
                foreach (var whoApproved in approvedRows.GroupBy(r => r.AiBacksLog))
                {
                    int state = whoApproved.Key ? 4 : 2;
                    List<QSO> qsos = whoApproved.Select(r => r.Qso).Distinct().ToList();
                    if (qsos.Count == 0) continue;
                    dal.SetReviewState(qsos.Select(q => q.id), state);
                    foreach (QSO q in qsos) q.ReviewState = state;
                }

                MarkCleanAsChecked(dal, goneNow);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            Log.Warn("Log Fixer: writing what was settled took " + marksClock.ElapsedMilliseconds + " ms");

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
            }

            // AND THE LOG TABLE MUST SHOW WHAT WAS JUST WRITTEN - after a CORRECTION as well as after a
            // removal. It was reloaded only when contacts had been deleted, so a country put right here
            // appeared in the Log Workshop and stayed wrong in the main window's table behind it: the
            // same contact, two countries, on one screen. QSO.Country is a plain property that tells
            // nobody it has changed, so nothing on that grid could ever have noticed by itself.
            //
            // ReloadActiveLogQsos re-reads the log table AND re-points the Log Workshop at the fresh
            // collection, so one call covers both windows.
            if (written > 0)
            {
                var reloadClock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var main = Application.Current == null ? null
                             : Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                    if (main != null) main.ReloadActiveLogQsos();
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                Log.Warn("Log Fixer: reloading the log took " + reloadClock.ElapsedMilliseconds + " ms");
            }

            // Only now, with the log itself up to date, is the job actually finished.
            HideFixOverlay();

            int removed = gone.Count;
            int corrected = written - removed;
            string report =
                  (removed > 0 ? removed.ToString("N0") + " duplicate contact"
                                 + (removed == 1 ? "" : "s") + " removed from the log." : "")
                + (removed > 0 && corrected > 0 ? "\n" : "")
                + (corrected > 0 ? corrected.ToString("N0") + " QSO" + (corrected == 1 ? "" : "s")
                                   + " fixed." : "")
                // The other half of the press, said plainly afterwards as well as before it: these were
                // not changed, they were settled, and that is why they will not appear again.
                + (approving > 0 ? (removed > 0 || corrected > 0 ? "\n" : "")
                                   + approving.ToString("N0") + " QSO" + (approving == 1 ? "" : "s")
                                   + " approved unchanged"
                                   + (byAi == 0 ? " by you"
                                    : byAi == approving ? ", with the AI backing you"
                                    : ", " + byAi.ToString("N0") + " of them with the AI backing you")
                                   + "." : "")
                // It used to say "Close and reopen the log window to see the new values", which was true
                // and is not any more: the log table is reloaded above.
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

            // Re-check, so what is left on screen is what is still wrong - and what he has just
            // settled is no longer wrong, whatever he asked the chooser to open on. See
            // _hideAlreadySettled.
            _hideAlreadySettled = true;
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

        // ── PUTTING A CONTACT BACK THE WAY IT WAS ───────────────────────────────────────────
        //
        // The reverse of WriteField, and deliberately NOT the same method: WriteField is for a value
        // somebody has just typed, so it tidies what it is given - a callsign to capitals, a mode to
        // capitals. Nothing may be tidied here. What is written back is what the log itself held,
        // character for character, and a revert that returns a contact in a different case from the
        // one it left in has not put it back.
        //
        // The three fields with no column of their own are here too. They travel behind a country
        // correction, so a revert that did not carry them would leave the contact half returned:
        // Spain again, with Puerto Rico's zones.
        private static void RestoreField(QSO q, string key, string v)
        {
            if (q == null) return;
            v = v ?? "";
            switch (key)
            {
                case "Callsign": q.DXCall = v; break;
                case "Date": { string d = UnformatDate(v); if (d != null) q.Date = d; break; }
                case "Time": { string t = UnformatTime(v); if (t != null) q.Time = t; break; }
                case "Band": q.Band = v; break;
                case "Mode": q.Mode = v; break;
                case "Country": q.Country = v; break;
                // An empty one goes back to 0, which is what "no entity" is in this column. Guarding
                // on "only if it parses" would leave the new number standing in a contact that never
                // had one.
                case "Country Code": { int c; q.DxccCode = int.TryParse(v, out c) ? c : 0; break; }
                case "DXCC": q.DXCC = v; break;
                case "Continent": q.Continent = v; break;
                case "CQ Zone": q.CQZone = v; break;
                case "ITU Zone": q.ITUZone = v; break;
                case "DX Locator": q.DXLocator = v; break;
                case "Comment": q.Comment = v; break;
                case "IOTA": q.Iota = v; break;
                case "SOTA": q.SotaRef = v; break;
                case "POTA": q.PotaRef = v; break;
                case "WWFF": q.WwffRef = v; break;
            }
        }

        // The headline counts the contacts on the list, and the ones just put back have left it.
        private void RetitleList()
        {
            if (_listHeadline == null) return;
            var m = System.Text.RegularExpressions.Regex.Match(_listHeadline, @"^[\d,]+ QSOs? (?<rest>.*)$");
            if (!m.Success) return;
            int n = _qsos.Count;
            _listHeadline = n.ToString("N0") + " QSO" + (n == 1 ? "" : "s") + " " + m.Groups["rest"].Value;
        }

        private async void Btn_Revert_Click(object sender, RoutedEventArgs e)
        {
            // WHAT IS TICKED ON SCREEN, read before anything is written or awaited - the same rule the
            // Fix button obeys, and for the same reason.
            List<FixRow> rows = FindingsGrid.Items.OfType<FixRow>()
                .Where(r => r.Revert && r.CanRevert && r.Qso != null).ToList();
            if (rows.Count == 0) return;

            var dal = DataAccess.GetInstance();
            if (dal == null)
            {
                HolyMessageBox.ShowWarning(
                    "The log database is not open.\n\nClose HolyLogger and open it again.",
                    "Log Fixer", this);
                return;
            }

            // Fields, not contacts: five contacts can be twenty values going back, and the number that
            // matters to a man about to press this is how much of his log is about to move.
            int fields = rows.Sum(r => r.History
                                        .Select(n => n.Field)
                                        .Distinct(StringComparer.OrdinalIgnoreCase).Count());

            if (!HolyMessageBox.ShowConfirm(
                    rows.Count.ToString("N0") + " QSO" + (rows.Count == 1 ? "" : "s")
                    + " will be put back to the value" + (fields == 1 ? "" : "s")
                    + " held before the Log Fixer changed "
                    + (rows.Count == 1 ? "it" : "them") + " — "
                    + fields.ToString("N0") + " field" + (fields == 1 ? "" : "s") + " in all."
                    + "\n\nThey go back to NEVER CHECKED, so the next run of the Log Fixer will look at "
                    + (rows.Count == 1 ? "it" : "them") + " again and will very likely propose the same "
                    + "correction. The record of what was changed is dropped with "
                    + (rows.Count == 1 ? "it" : "them") + ", so "
                    + (rows.Count == 1 ? "this contact leaves" : "these contacts leave") + " this list."
                    + "\n\nYour WHOLE DATABASE — every log in it, not only this one — is copied first, "
                    + "into\n" + (string.IsNullOrEmpty(dal.BackupsFolder) ? "your Backups folder"
                                                                         : dal.BackupsFolder)
                    + "\nwhere Tools > Backups & Restore lists it and can put it back for you."
                    + "\n\nPut " + (rows.Count == 1 ? "it" : "them") + " back now?",
                    "Log Fixer", HolyMsgType.Warning, this))
                return;

            ShowFixOverlay(0, "Copying your database…", "");
            string backup = await Task.Run(() => SaveBackup(dal));
            if (backup == null)
            {
                HideFixOverlay();
                if (!HolyMessageBox.ShowConfirm("The safety copy of the log could not be written.\n\n"
                        + "Put them back anyway?", "Log Fixer", HolyMsgType.Warning, this))
                    return;
            }

            Btn_Revert.IsEnabled = false;
            ShowFixOverlay(rows.Count);

            int put = 0;
            try
            {
                await Task.Run(() => dal.RunInTransaction(() =>
                {
                    foreach (FixRow r in rows)
                    {
                        QSO qso = r.Qso;

                        // THE FIRST VALUE RECORDED FOR EACH FIELD, not the last. A contact that has
                        // been through the Fixer twice has two rows for its country, and the one the
                        // operator wants back is the one it started with - so each field is taken from
                        // the earliest press that touched it and every later one is passed over.
                        var already = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (FixNote n in r.History)
                            if (!string.IsNullOrEmpty(n.Field) && already.Add(n.Field))
                                RestoreField(qso, n.Field, n.Was);

                        dal.Update(qso);
                        // The record described a correction that is no longer in the log. Kept, it
                        // would show this contact among the corrected ones for ever, holding up a
                        // change that has been undone.
                        dal.ForgetFixHistory(qso.id);

                        put++;
                        if (put % 50 == 0 || put == rows.Count)
                        {
                            int at = put;
                            Dispatcher.BeginInvoke(new Action(() => UpdateFixOverlay(at, rows.Count)));
                        }
                    }
                }));
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                HideFixOverlay();
                HolyMessageBox.ShowError("Something went wrong while putting them back:\n\n" + ex.Message
                    + "\n\nNothing was written — the whole job is one transaction, so the log is exactly "
                    + "as it was."
                    + (backup != null ? "\n\nThe copy taken before it started is in:\n" + backup : ""),
                    "Log Fixer", this);
                UpdateRevertButton();
                return;
            }

            ShowFixOverlay(0, "Updating the log…", "");

            // NEVER CHECKED AGAIN, and written OUTSIDE the transaction above: SetReviewState opens one
            // of its own, and SQLite has no nested transactions.
            var back = rows.Select(r => r.Qso).Where(q => q != null).Distinct().ToList();
            try
            {
                dal.SetReviewState(back.Select(q => q.id), 0);
                foreach (QSO q in back) q.ReviewState = 0;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // The log table and the Log Workshop are holding these same QSO objects, read before this
            // press; nothing on those grids can notice a property that has quietly changed underneath.
            try
            {
                var main = Application.Current == null ? null
                         : Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                if (main != null) main.ReloadActiveLogQsos();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // And they are no longer contacts the Fixer has corrected, so they leave this list.
            var goneBack = new HashSet<int>(back.Select(q => q.id));
            _qsos.RemoveAll(q => q != null && goneBack.Contains(q.id));
            RetitleList();
            ShowList(_listHeadline);

            HideFixOverlay();

            HolyMessageBox.Show(
                put.ToString("N0") + " QSO" + (put == 1 ? " is" : "s are") + " back to the value"
                + (fields == 1 ? "" : "s") + " held before the Log Fixer changed "
                + (put == 1 ? "it" : "them") + "."
                + (backup != null ? "\n\nYour database as it was a moment ago is in:\n" + backup : ""),
                "Log Fixer", HolyMsgType.Info, this);
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
                HolyMessageBox.ShowWarning(
                    "This QSO could not be opened for editing:\n\n" + ex.Message + "\n\n"
                    + HolyMessageBox.WhatToDo(ex.Message, null),
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
            // CTRL+Z PUTS THE TICKS BACK.
            //
            // Ticking Do not change clears the Fix beside it - they are two answers to one question -
            // and a hand that slips has just thrown away a decision with nothing to say what it was.
            // He pressed Ctrl+Z, because that is what anybody presses, and the window did nothing.
            // Now it does: every tick, every clear, and every whole-column sweep goes on a stack first.
            if (e.Key == System.Windows.Input.Key.Z
                && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control)
                   == System.Windows.Input.ModifierKeys.Control)
            {
                UndoTicks();
                e.Handled = true;
                return;
            }

            if (e.Key != System.Windows.Input.Key.Escape) return;
            if (FindingsGrid != null)
            {
                FindingsGrid.UnselectAll();
                FindingsGrid.UnselectAllCells();
            }
            e.Handled = true;
        }

        // A WORD THAT TAKES ITSELF BACK OFF THE SCREEN.
        //
        // The line above the table belongs to the kinds - "Showing only: Wrong continent" - and is
        // rewritten only when the filter changes. A message written over it therefore STAYS, so
        // "Nothing to undo." sat there through every tick that followed and went on saying something
        // that had stopped being true. It gets three seconds, then the line goes back to its own words.
        private System.Windows.Threading.DispatcherTimer _summaryFlash;

        private void FlashSummary(string text)
        {
            if (TB_KindsSummary == null) return;
            TB_KindsSummary.Text = text;

            if (_summaryFlash == null)
            {
                _summaryFlash = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                _summaryFlash.Tick += (s, e) =>
                {
                    _summaryFlash.Stop();
                    if (TB_KindsSummary == null) return;
                    TB_KindsSummary.Text = _filterKind == null
                        ? "One kind at a time: click a kind above to work through its QSOs."
                        : "Showing only: " + _filterKind + ".  Click it again, or Show all kinds, to "
                          + "see the rest.";
                };
            }
            _summaryFlash.Stop();
            _summaryFlash.Start();
        }

        // WHAT THE BOXES SAID BEFORE THE LAST PRESS. One entry per press - a single box or a whole
        // column's worth - so one Ctrl+Z undoes one action rather than one row of it.
        private readonly Stack<List<Tuple<FixRow, bool, bool>>> _tickUndo
            = new Stack<List<Tuple<FixRow, bool, bool>>>();

        private void PushTickUndo(IEnumerable<FixRow> rows)
        {
            if (rows == null) return;
            var before = rows.Where(r => r != null)
                             .Select(r => Tuple.Create(r, r.Apply, r.Keep))
                             .ToList();
            if (before.Count == 0) return;

            // Twenty presses back is further than anybody reaches, and it keeps a window that has been
            // open all evening from holding a list as long as the log.
            if (_tickUndo.Count >= 20)
            {
                var keepThese = _tickUndo.ToList();
                keepThese.RemoveAt(keepThese.Count - 1);      // the oldest
                _tickUndo.Clear();
                for (int i = keepThese.Count - 1; i >= 0; i--) _tickUndo.Push(keepThese[i]);
            }
            _tickUndo.Push(before);
        }

        private void UndoTicks()
        {
            if (_tickUndo.Count == 0)
            {
                FlashSummary("Nothing to undo.");
                return;
            }

            List<Tuple<FixRow, bool, bool>> before = _tickUndo.Pop();
            _syncingKind = true;                              // one update at the end, not one per row
            try
            {
                foreach (var was in before)
                {
                    // Keep first: setting Apply clears Keep, so putting them back the other way round
                    // would undo half of what it had just restored.
                    was.Item1.Keep = was.Item3;
                    was.Item1.Apply = was.Item2;
                }
            }
            finally { _syncingKind = false; }

            UpdateFixButton();
            UpdateFixAllBox();
            UpdateKeepAllBox();
            UpdateKindBoxes();

            // And say that it happened. One tick going back on its own is easy to miss on a table of
            // ninety-six rows, and a key that appears to have done nothing gets pressed again.
            FlashSummary(before.Count == 1
                ? "Undone. That row is back as it was."
                : "Undone. " + before.Count.ToString("N0") + " rows are back as they were.");
        }

        // The right-click menu: the station's QRZ page, the whole QSO, and then the two copy commands.
        // Built fresh on every click because the row and the cell under the mouse are part of what it
        // offers.
        //
        // BUILT ON BUTTON-DOWN, not in ContextMenuOpening. A menu attached while the opening event is
        // already running arrives too late for that click: WPF had nothing to open, so the first
        // right-click only highlighted the row and the menu appeared on the second. Attaching it on
        // the way down means it is in place when the button comes up, which is when WPF opens it.
        private void FindingsGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var menu = new ContextMenu { FontSize = 16 };
            var cell = GridCopy.CellFrom(e.OriginalSource);

            // THE ROW IS SELECTED FIRST, or "Copy this row" has no row to copy. A DataGrid does not
            // select on a right-click, and this menu is built before the click is over, so without
            // this the copy line came up greyed out. A row already inside a selection is left alone,
            // so right-clicking one of several still copies all of them.
            var clicked = cell == null ? null : ItemsControl.ContainerFromElement(FindingsGrid, cell) as DataGridRow;
            if (clicked != null && !clicked.IsSelected)
            {
                FindingsGrid.SelectedItem = clicked.Item;
                clicked.IsSelected = true;
            }

            // The callsign comes from the row the mouse is over, whichever of its cells was clicked -
            // the same commands the log table offers, so a call whose country looks wrong here can be
            // looked at without leaving the window.
            FixRow row = cell == null ? null : cell.DataContext as FixRow;
            string call = row == null || row.Qso == null
                        ? null
                        : (row.Qso.DXCall ?? string.Empty).Trim().ToUpperInvariant();

            if (!string.IsNullOrWhiteSpace(call))
            {
                // Greyed while there is no QRZ session, because then there is nothing to open the
                // page with.
                bool qrzUp = MainWindow.QrzIsConnected();
                var qrzItem = new MenuItem
                {
                    Header = "Open " + call + " at QRZ.com",
                    IsEnabled = qrzUp,
                    ToolTip = qrzUp ? null : "No connection to QRZ.com"
                };
                // The QRZ globe beside it, so the line is recognisable at a glance. Dimmed with the
                // item when there is no connection - WPF greys a disabled header but leaves the icon
                // in full colour, which would read as though the line were live.
                try
                {
                    qrzItem.Icon = new Image
                    {
                        Source = new System.Windows.Media.Imaging.BitmapImage(
                                     new Uri("Images/qrz_mini_icon.png", UriKind.Relative)),
                        Width = 20,
                        Height = 20,
                        Stretch = Stretch.Uniform,
                        Opacity = qrzUp ? 1.0 : 0.4
                    };
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                ToolTipService.SetShowOnDisabled(qrzItem, true);
                qrzItem.Click += (s, a) => MainWindow.OpenQrzPage(call);
                menu.Items.Add(qrzItem);

                // The QSO itself, in the editing window but with nothing to press: this table is for
                // deciding, and a decision often needs the fields the table has no column for.
                var showItem = new MenuItem { Header = "Show full QSO" };
                // A DRAWN EYE, not a font glyph: the icon font's "view" marks are boxes and cards
                // with an eye buried in them, and this line wants the eye itself - the lid curving
                // over the pupil, the shape everything else uses for "look at this".
                var eyeBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
                var eye = new Canvas { Width = 20, Height = 20 };
                eye.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M1.5,10 C5,4.5 15,4.5 18.5,10 C15,15.5 5,15.5 1.5,10 Z"),
                    Stroke = eyeBrush,
                    StrokeThickness = 1.6,
                    Fill = Brushes.Transparent
                });
                var pupil = new System.Windows.Shapes.Ellipse
                {
                    Width = 6.4,
                    Height = 6.4,
                    Fill = eyeBrush
                };
                Canvas.SetLeft(pupil, 6.8);
                Canvas.SetTop(pupil, 6.8);
                eye.Children.Add(pupil);
                showItem.Icon = eye;
                showItem.Click += (s, a) => ShowQsoReadOnly(row);
                menu.Items.Add(showItem);

                // AND WHAT AN AI MAKES OF IT. This window says what the program's own rules found;
                // this line asks a second opinion about the same contact, on the things rules cannot
                // settle - an expedition callsign, a grid that belongs to another country, a comment
                // that contradicts the fields. It reports and never writes, exactly as this window does.
                if (row != null && row.Qso != null)
                {
                    var aiItem = new MenuItem { Header = RowMenuParts.MakeAiHeader() };
                    aiItem.Icon = new TextBlock
                    {
                        Text = "",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 16,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    QSO forAi = row.Qso;
                    aiItem.Click += (s, a) => OpenAiQsoCheck(forAi);
                    menu.Items.Add(aiItem);
                }

                menu.Items.Add(new Separator());
            }

            menu.Items.Add(GridCopy.CopyCellItem(GridCopy.TextOf(cell)));
            menu.Items.Add(GridCopy.CopyRowsItem(FindingsGrid));

            FindingsGrid.ContextMenu = menu;
        }

        // ONE AI WINDOW AT A TIME, and it belongs to this one - the same rule the log's and the
        // Workshop's menus follow.
        private AiQsoCheckWindow _aiCheckWindow;

        private void OpenAiQsoCheck(QSO qso)
        {
            if (qso == null) return;
            try
            {
                if (_aiCheckWindow != null)
                {
                    _aiCheckWindow.Close();
                    _aiCheckWindow = null;
                }

                var window = new AiQsoCheckWindow(qso, this);
                window.Closed += (s, e) => { if (ReferenceEquals(_aiCheckWindow, window)) _aiCheckWindow = null; };
                _aiCheckWindow = window;
                window.Show();
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError(
                    "Could not open the AI check.\n\n"
                    + ex.Message + "\n\n"
                    + HolyMessageBox.WhatToDo(ex.Message, null),
                    "AI check", this);
            }
        }

        // The QSO on screen exactly as the editor shows it, frozen. Nothing is written back, so unlike
        // the double-click there is no re-scan afterwards.
        private void ShowQsoReadOnly(FixRow row)
        {
            if (row == null || row.Qso == null) return;
            try
            {
                var viewer = new QsoEditWindow(row.Qso, default(Rect), true) { Owner = this };
                viewer.ShowDialog();
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                HolyMessageBox.ShowWarning(
                    "This QSO could not be opened:\n\n" + ex.Message + "\n\n"
                    + HolyMessageBox.WhatToDo(ex.Message, null),
                    "Log Fixer", this);
            }
        }

        private void Btn_Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ── REMEMBERING WHAT HE DECIDED ─────────────────────────────────────────────────────────
        //
        // The Fixer used to start every run from nothing. A man who had been through four hundred rows,
        // corrected forty and deliberately left the rest, opened it a month later and was shown the same
        // three hundred and sixty all over again - and the ones he had already thought about buried the
        // ones he had not. That is how a check people trust turns into a list they stop opening.
        //
        // So each QSO carries what he settled about it: 1 corrected, 2 looked at and left as it was,
        // 3 checked and clean. None of the three is offered again unless he asks for it by name in the
        // window that opens first and asks which contacts to check.
        //
        // NOTHING IS DECIDED BY CLOSING A WINDOW.
        //
        // Closing the Fixer used to ask whether the rows he had not ticked should stop being shown,
        // and that was wrong twice over. It is not a decision - a man opens this window to look, or is
        // called away from it - and a question nobody asked for, standing between him and the door,
        // gets answered by whichever button the Enter key happens to be on. It silenced 4,522 contacts
        // that way on its first outing.
        //
        // So Close closes, and decides nothing. THE FIX BUTTON is what settles a run: it writes the
        // ticked rows, and in the same breath records that the rows he left were left and the contacts
        // that raised nothing were clean. That is a press he chose to make, on a table he was looking
        // at - not a question ambushing him on his way out of the door.
    }

    // ── WHICH CONTACTS TO PUT IN FRONT OF HIM THIS TIME ─────────────────────────────────────────
    //
    // The Log Fixer used to open on everything the Log Workshop was showing - 28,580 contacts on this
    // machine, a scan that takes a while, and a table whose first four hundred rows he settled last
    // month. The second run is almost never the same job as the first, so he is asked before the scan
    // starts rather than being handed the whole log again.
    //
    // Four answers, and each one carries its own count, because the count is what makes the choice
    // obvious: "never checked - 12" on a Sunday evening says the week's contacts are clean in a way no
    // wording could. An answer worth nothing is not offered at all - a greyed line saying 0 is an
    // honest way to show that there is nothing there.
    //
    // ALL MEANS ALL, deliberately: every contact the Workshop is showing, whatever the Fixer has been
    // told about it before. The other three are what the database remembers; the difference between
    // All and the three of them together is the clean ones, which have nothing to show him.
    internal enum FixerScope { All, NeverChecked, Fixed, Approved, ApprovedByUser, ApprovedByAi }

    internal class LogFixerScopeWindow : Window
    {
        // THE WORDING, WRITTEN ONCE. This window asks the question and the Log Workshop's "Fixed" box
        // filters on the same answers; two lists that mean the same thing but read differently is two
        // things to learn, and the day one of them is reworded they stop agreeing altogether.
        // APPROVED, NOT "NOT FIXED". These are contacts the Fixer questioned, the operator looked at,
        // and the log turned out to be right about - a finished piece of work, not an unanswered one.
        // "Not fixed" made a done job read as a job still waiting, on the very list he opens to see
        // what is left to do.
        public const string AllLabel        = "All";
        public const string NeverLabel      = "Never checked";
        public const string FixedLabel      = "Already checked and fixed";
        public const string ApprovedLabel   = "Already checked and approved";

        // The Log Workshop can ask WHO approved a contact, which the chooser deliberately does not:
        // choosing what to check again is a question about work still to do, and there the two are one
        // answer. Looking back over what was decided is a different question, and there they are not.
        public const string ApprovedByUserLabel = "Approved by me";
        public const string ApprovedByAiLabel   = "Approved with AI";

        private FixerScope _choice = FixerScope.All;
        private bool _accepted;

        // Which contacts each answer means. The one place that decides it, so the Log Workshop's
        // "Fixed" filter and this window can never drift apart on what "never checked" is.
        public static bool Matches(QSO q, FixerScope scope)
        {
            if (q == null) return false;
            switch (scope)
            {
                case FixerScope.NeverChecked:   return q.ReviewState == 0;
                case FixerScope.Fixed:          return q.ReviewState == 1;
                case FixerScope.Approved:       return q.ReviewState == 2 || q.ReviewState == 4;
                case FixerScope.ApprovedByUser: return q.ReviewState == 2;
                case FixerScope.ApprovedByAi:   return q.ReviewState == 4;
                default:                        return true;
            }
        }

        public static List<QSO> Pick(IEnumerable<QSO> qsos, FixerScope scope)
        {
            return (qsos ?? Enumerable.Empty<QSO>()).Where(q => Matches(q, scope)).ToList();
        }

        // Null means he closed the window or pressed Cancel, and nothing should open.
        public static FixerScope? Ask(IList<QSO> qsos, Window owner)
        {
            var win = new LogFixerScopeWindow(qsos) { Owner = owner };
            win.ShowDialog();
            return win._accepted ? win._choice : (FixerScope?)null;
        }

        private LogFixerScopeWindow(IList<QSO> qsos)
        {
            var all = qsos ?? new List<QSO>();
            Title = "Log Fixer";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = ThemeManager.Brush("WindowBg");
            MinWidth = 400;

            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text = "Which contacts should the Log Fixer check?",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = ThemeManager.Brush("TextBrush"),
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock
            {
                Text = "These are the QSOs the Log Workshop is showing.",
                FontSize = 16,
                Foreground = ThemeManager.Brush("TextBrush"),
                Margin = new Thickness(0, 0, 0, 14),
            });

            // A COLOUR PER ANSWER, so the one he wants is found before the words are read. All is the
            // whole log and wears the strongest blue; the three that report on what the Fixer has done
            // take the colours the program already uses for the same ideas elsewhere - untouched blue,
            // put-right green, left-alone red. Dark text on all but the deep blue, which takes white.
            AddChoice(root, all.Count, FixerScope.All, AllLabel, "#2F6FB5", "#FFFFFF");
            AddChoice(root, all.Count(q => Matches(q, FixerScope.NeverChecked)), FixerScope.NeverChecked,
                      NeverLabel, "#CDE4F7", "#10243A");
            AddChoice(root, all.Count(q => Matches(q, FixerScope.Fixed)), FixerScope.Fixed,
                      FixedLabel, "#BFE5C2", "#12331A");
            AddChoice(root, all.Count(q => Matches(q, FixerScope.Approved)), FixerScope.Approved,
                      ApprovedLabel, "#F7CFCF", "#3A1414");

            // CANCEL IN THE MIDDLE, under all four, where it belongs to none of them. Against one edge
            // it reads as the partner of the button above it; centred, it is plainly the way out of the
            // whole question. Nothing here is the default key either - Enter must not pick one of four
            // answers on his behalf.
            var cancel = new Button
            {
                Content = "Cancel",
                FontSize = 16,
                Padding = new Thickness(28, 5, 28, 5),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0),
                IsCancel = true,
            };
            root.Children.Add(cancel);

            Content = root;
        }

        // ONE PRESS, NOT TWO. Choosing an answer and then confirming it is a second decision about a
        // question already settled; the button IS the answer, and pressing it starts the check.
        //
        // "Never checked" over "12 QSOs", each on its own line: the count is what he is really reading,
        // and at the end of a sentence it gets skipped. An answer worth nothing is greyed out rather
        // than hidden - "already checked and fixed - 0" is itself worth knowing.
        private void AddChoice(Panel host, int count, FixerScope scope, string label,
                               string background, string foreground)
        {
            bool live = count > 0;

            // BOTH LINES CENTRED, on the line above and on the button. A stack sizes itself to its
            // widest child, so the shorter line sits under the start of the longer one unless it is
            // told otherwise - which reads as a mistake on a button whose block is centred.
            var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            text.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
            });
            text.Children.Add(new TextBlock
            {
                Text = count.ToString("N0") + (count == 1 ? " QSO" : " QSOs"),
                FontSize = 16,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            });

            var button = new Button
            {
                Content = text,
                Template = ChoiceTemplate,
                Width = 300,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 0, 7),
                HorizontalAlignment = HorizontalAlignment.Center,
                IsEnabled = live,
                Background = Paint(live ? background : "#DDDDDD"),
                Foreground = Paint(live ? foreground : "#666666"),
                BorderBrush = Paint("#33000000"),   // AARRGGBB: a faint dark edge, not a black frame
            };
            button.Click += (s, e) => { _choice = scope; _accepted = true; Close(); };
            host.Children.Add(button);
        }

        private static SolidColorBrush Paint(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        // THE WINDOWS BUTTON PAINTS ITS OWN GREY OVER ANY BACKGROUND IT IS GIVEN the moment the mouse
        // touches it, which would take the colour off whichever button he is reaching for - the one
        // moment the colour is doing its job. So the chrome is a plain rounded border that shows the
        // colour it was handed, and the hover and the press are a change of weight rather than a
        // change of colour.
        private static readonly ControlTemplate ChoiceTemplate = (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate TargetType=\"Button\" "
            + "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">"
            + "  <Border x:Name=\"Chrome\" CornerRadius=\"5\" BorderThickness=\"1\""
            + "          Background=\"{TemplateBinding Background}\""
            + "          BorderBrush=\"{TemplateBinding BorderBrush}\""
            + "          Padding=\"{TemplateBinding Padding}\">"
            + "    <ContentPresenter HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Center\"/>"
            + "  </Border>"
            + "  <ControlTemplate.Triggers>"
            + "    <Trigger Property=\"IsMouseOver\" Value=\"True\">"
            + "      <Setter TargetName=\"Chrome\" Property=\"Opacity\" Value=\"0.85\"/></Trigger>"
            + "    <Trigger Property=\"IsPressed\" Value=\"True\">"
            + "      <Setter TargetName=\"Chrome\" Property=\"Opacity\" Value=\"0.65\"/></Trigger>"
            + "  </ControlTemplate.Triggers>"
            + "</ControlTemplate>");
    }
}

