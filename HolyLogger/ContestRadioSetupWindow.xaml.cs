using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using HolyLogger.Contests;

namespace HolyLogger
{
    // Options > General > Contest Radio Setup: one row per radio, holding the commands that put it in VFO mode,
    // simplex, no tone and no TSQL, and the one contest they are for (picked at the top). Opening a
    // log of that contest sends the row of the radio on CAT - see MainWindow.ApplyContestRadioSetup.
    // Saved when the window closes.
    public partial class ContestRadioSetupWindow : Window
    {
        private readonly ObservableCollection<RadioCommandSet> _sets = new ObservableCollection<RadioCommandSet>();
        private readonly Func<RadioCommandSet, string> _sendNow;

        // Offered in the Radio box: radios that work on 2m and/or 70cm (ContestRadioCommands.VhfUhfRadios)
        // AND have a file in OmniRig right now, read fresh each time the window opens. The ones set up as
        // RIG1/RIG2 come first. The box is editable, so a file renamed in OmniRig can still be typed.
        public List<string> RigNames { get; }

        public ContestRadioSetupWindow(Window owner, IEnumerable<string> omniRigRigs, Func<RadioCommandSet, string> sendNow)
        {
            var omniRigFiles = ContestRadioCommands.OmniRigRigFiles();
            NotInOmniRigBrushConverter.OmniRigFiles = omniRigFiles;
            RigNames = BuildRigNames(omniRigRigs, omniRigFiles);
            InitializeComponent();
            Owner = owner;
            _sendNow = sendNow;

            var setup = ContestRadioCommands.Load();

            // A contest picked before that no longer has 2m/70cm still shows, so it is not lost silently.
            var contests = ContestRadioCommands.VhfUhfContests();
            var picked = ContestService.FindById(setup.ContestId);
            if (picked != null && !contests.Contains(picked)) contests.Insert(0, picked);
            ContestBox.ItemsSource = contests;
            ContestBox.SelectedItem = picked ?? contests.FirstOrDefault();

            foreach (var s in setup.Radios) _sets.Add(s);

            // The radios already set up come first in the box, so the operator's own are at hand.
            FillRadioBox();
            string first = _sets.Select(s => (s.Radio ?? "").Trim()).FirstOrDefault(n => n.Length > 0)
                           ?? RigNames.FirstOrDefault() ?? string.Empty;
            ShowRadio(first);
            SelectInBox(first);
            // The full list is for the one pick that follows Add Radio; DropDownClosed below puts the
            // box back on the 2m/70cm list once it shuts, however that happens. The radio just added
            // stays in that list, since every radio set up here is listed whatever its bands.
            RadioBox.SelectionChanged += (s, e) => ShowRadio(RadioBox.SelectedItem as string);
            // The list is built again each time it drops, so a radio that has just been given its
            // commands (or had them deleted) shows the right mark.
            //
            // AND OPENED AT THE TOP. The starred (has-commands) radios sort first, but WPF scrolls a
            // freshly-opened ComboBox to keep the SELECTED item in view - so once the selected radio
            // was not near the top, opening the list hid the very stars it exists to show at a glance.
            // Overridden here, after that automatic scroll has happened (Background priority; Loaded
            // is too early - the scroll into view has not run yet).
            RadioBox.DropDownOpened += (s, e) =>
            {
                FillRadioBox();
                Dispatcher.BeginInvoke(new Action(() => ScrollToTop(RadioBox)),
                                       System.Windows.Threading.DispatcherPriority.Background);
            };
            // The list resets to the 2m/70cm one however it was closed - picking a radio, clicking away,
            // or Escape - not only when a pick fired SelectionChanged. Left as it was, closing the full
            // list any other way kept it showing on the very next drop. The Add Radio line's job is
            // done either way too.
            RadioBox.DropDownClosed += (s, e) => { _showingAllFiles = false; ShowAddHint(false); };

            // ── ONLY WHAT A COMMAND CAN BE MADE OF ──────────────────────────────────────────────
            //
            // A command is either hex bytes for Icom ("FE FE 88 E0 07 FD") or plain text for the
            // others ("MD04;", "EX0840;"), so letters, digits, spaces and ';' are all it can hold. A
            // radio name is an OmniRig file name: letters, digits, space and - _ . ( ) /.
            // Anything else is refused as it is typed or pasted, rather than found later by the radio.
            foreach (var box in CommandsPanel.Children.OfType<TextBox>())
                LimitTyping(box, IsCommandChar);

            Closing += Window_Closing;
        }

        // The commands shown at the moment; created for a radio that has none yet. An untouched one is
        // dropped on save (Save leaves out the empty sets), so picking a radio costs nothing.
        private RadioCommandSet _shown;

        // A radio that already has commands - kept in the file, or shipped with HolyLogger - is marked
        // with a star in the list, so it can be seen at a glance which ones are ready.
        private const string Star = "★ ";

        private bool HasCommands(string name)
        {
            var s = _sets.FirstOrDefault(x => string.Equals((x.Radio ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s.CommandsToSend().Count > 0;
            return ContestRadioCommands.DefaultFor(name) != null;
        }

        // Add Radio: the list becomes every radio OmniRig has a file for, so a radio HolyLogger does not
        // know by name - a new model, or a file renamed in OmniRig - can still be set up. It is the
        // operator's business then that the one he picks is a 2m/70cm radio.
        private bool _showingAllFiles;

        private void ShowAddHint(bool on)
        {
            TB_AddHint.Inlines.Clear();
            if (on)
            {
                // The real path when it can be found, so there is nowhere else to look - rather than
                // the generic "OmniRig's Rigs folder", which leaves the operator to go hunting for it.
                // On its own line and in black, so the path stands out from the sentence around it.
                string folder = ContestRadioCommands.OmniRigRigsFolder();
                TB_AddHint.Inlines.Add(new Run("Click your radio in the list. If it is not there, put "
                    + "its OmniRig file (.ini) in:"));
                TB_AddHint.Inlines.Add(new LineBreak());
                TB_AddHint.Inlines.Add(new Run(folder ?? "OmniRig's Rigs folder")
                    { Foreground = Brushes.Black });
                TB_AddHint.Inlines.Add(new Run(", then press Add Radio again."));
            }
            Border_AddHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            TB_Hint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            _showingAllFiles = true;
            FillRadioBox();

            // Said beside the open list, not in a message box: a box takes the focus, which shuts the
            // list, and the operator has to press OK before he can pick anyway. This goes as soon as
            // he picks a radio or clicks away from the list.
            //
            // AFTER FillRadioBox, never before: filling the list puts the box back on its radio, and
            // that fires SelectionChanged, which clears this line - so it vanished the instant it was
            // set. It stands in the place of the window's own hint, which it hides while it is up.
            ShowAddHint(true);
            RadioBox.IsDropDownOpen = true;
        }

        private void FillRadioBox()
        {
            string keep = (_shown?.Radio ?? "").Trim();
            // The radios already set up, and the 2m/70cm radios OmniRig has a file for (RigNames) -
            // or every file it has, once "Other radio..." has been picked.
            var names = _sets.Select(s => (s.Radio ?? "").Trim()).Where(n => n.Length > 0).ToList();
            names.AddRange(_showingAllFiles
                               ? (IEnumerable<string>)(ContestRadioCommands.OmniRigRigFiles() ?? new HashSet<string>())
                               : RigNames);
            // The radios that HAVE commands first, each group by name, so what is ready to use is at
            // the top of the list.
            var items = names.Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderByDescending(HasCommands)
                             .ThenBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                             .Select(n => HasCommands(n) ? Star + n : n).ToList();
            RadioBox.ItemsSource = items;
            SelectInBox(keep);
        }

        // Puts the box on a radio by its bare name, whether the item carries a star or not.
        private void SelectInBox(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { RadioBox.SelectedItem = null; return; }
            RadioBox.SelectedItem = (RadioBox.ItemsSource as IEnumerable<string>)?
                .FirstOrDefault(i => string.Equals(i.StartsWith(Star.Trim(), StringComparison.Ordinal)
                                                       ? i.Substring(1).Trim() : i,
                                                   name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private void ShowRadio(string radioName)
        {
            string name = (radioName ?? "").Trim();
            if (name.StartsWith(Star.Trim(), StringComparison.Ordinal))   // picked from the list
                name = name.Substring(1).Trim();
            if (_shown != null && string.Equals((_shown.Radio ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase))
                return;

            if (name.Length == 0) { _shown = null; CommandsPanel.DataContext = null; TB_RadioNote.Text = ""; return; }

            _shown = _sets.FirstOrDefault(s => string.Equals((s.Radio ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase));
            bool isNew = _shown == null;
            bool fromList = false;
            if (isNew)
            {
                // HolyLogger ships commands for some radios; a radio picked for the first time starts
                // from those rather than from six empty boxes.
                _shown = ContestRadioCommands.DefaultFor(name) ?? new RadioCommandSet { Radio = name };
                fromList = _shown.CommandsToSend().Count > 0;
                _sets.Add(_shown);
            }
            CommandsPanel.DataContext = _shown;
            TB_RadioNote.Text = !isNew ? ""
                : fromList ? "Filled in from HolyLogger's own list for this radio. Check them with Send now."
                : "No commands kept for this radio yet. Type them here; empty ones are not sent.";
        }

        // Hex bytes for Icom, plain text ending in ';' for the others.
        private static bool IsCommandChar(char c)
            => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == ' ' || c == ';';

        // An OmniRig file name: "IC-7100-DATA-FIL1", "IC- 820", "FT-100 D".
        private static bool IsRadioNameChar(char c)
            => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
               || c == ' ' || c == '-' || c == '_' || c == '.' || c == '(' || c == ')' || c == '/';

        // Refuses a character as it is typed, and a paste that carries one. Keeping the box clean is
        // kinder than letting a command through that the radio will never understand.
        private static void LimitTyping(UIElement box, Func<char, bool> allowed)
        {
            if (box == null) return;
            box.PreviewTextInput += (s, e) =>
            {
                if (!(e.Text ?? "").All(allowed)) { e.Handled = true; MainWindow.BeepRefusedKey(); }
            };
            DataObject.AddPastingHandler(box, (s, e) =>
            {
                string text = (e.DataObject.GetData(DataFormats.UnicodeText)
                               ?? e.DataObject.GetData(DataFormats.Text)) as string;
                if (text == null || !text.All(allowed)) { e.CancelCommand(); MainWindow.BeepRefusedKey(); }
            });
        }

        // Scrolls a ComboBox's open drop-down back to its first item, so the starred radios - sorted to
        // the top - are what the operator sees the instant it opens. Without this the list opens on the
        // SELECTED radio: with ID-5100A tenth in the list, the nine starred ones above it were off the
        // top of the list and nothing said they were there.
        //
        // THE LIST IS IN A POPUP, which has a visual tree of its own - it is NOT among the ComboBox's
        // own children, so looking for the ScrollViewer under the ComboBox found nothing every time
        // (measured in a test harness: null, both right after opening and after the queue drained).
        // It is reached through the template's PART_Popup instead.
        private static void ScrollToTop(ComboBox box)
        {
            var popup = box.Template == null ? null
                        : box.Template.FindName("PART_Popup", box) as System.Windows.Controls.Primitives.Popup;
            var sv = popup == null || popup.Child == null ? null : FindVisualChild<ScrollViewer>(popup.Child);
            if (sv != null) { sv.ScrollToHome(); return; }

            // No ScrollViewer yet (the popup may not have been built): ask the first row to show itself.
            var first = box.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            if (first != null) first.BringIntoView();
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private static List<string> BuildRigNames(IEnumerable<string> omniRigRigs, HashSet<string> omniRigFiles)
        {
            // No OmniRig folder found: nothing to check against, so the whole list is offered.
            var vhfUhf = ContestRadioCommands.VhfUhfRadios
                             .Where(n => omniRigFiles == null || omniRigFiles.Contains(n)).ToList();
            var names = new List<string>();
            foreach (string r in omniRigRigs ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(r) && vhfUhf.Contains(r.Trim(), StringComparer.OrdinalIgnoreCase))
                    names.Add(r.Trim());
            names.AddRange(vhfUhf);
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_shown == null) return;
            string name = string.IsNullOrWhiteSpace(_shown.Radio) ? "this radio" : "the " + _shown.Radio.Trim();
            if (!HolyMessageBox.ShowConfirm("Delete the commands for " + name + "?", "Contest Radio Setup",
                                            HolyMsgType.Warning, this))
                return;
            _sets.Remove(_shown);
            _shown = null;
            CommandsPanel.DataContext = null;
            TB_RadioNote.Text = string.Empty;
            FillRadioBox();          // the radio loses its star
            RadioBox.SelectedItem = null;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnSendNow_Click(object sender, RoutedEventArgs e)
        {
            var s = _shown;
            if (s == null) return;
            if (!CheckCommands(new[] { s })) return;
            string result = _sendNow?.Invoke(s);
            if (!string.IsNullOrEmpty(result))
                HolyMessageBox.Show(result, "Contest Radio Setup", HolyMsgType.Info, this);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!CheckCommands(_sets)) { e.Cancel = true; return; }
            ContestRadioCommands.Save((ContestBox.SelectedItem as Contest)?.Id, _sets);
        }

        // Stops a byte typed with one digit ("07 0 FD") from being sent as text.
        private bool CheckCommands(IEnumerable<RadioCommandSet> sets)
        {
            foreach (var s in sets)
            {
                var cells = new[] { ("VFO", s.Vfo), ("FM wide", s.FmWide), ("No Auto Repeater", s.NoAutoRepeater),
                                    ("Simplex", s.Simplex), ("No Tone", s.NoTone), ("No TSQL", s.NoTsql) };
                foreach (var (label, command) in cells)
                {
                    if (ContestRadioCommands.LooksValid(command)) continue;
                    string radio = string.IsNullOrWhiteSpace(s.Radio) ? "a radio" : "the " + s.Radio.Trim();
                    ShowRadio((s.Radio ?? "").Trim());
                    SelectInBox((s.Radio ?? "").Trim());
                    HolyMessageBox.ShowError("The " + label + " command for " + radio + " is not right.\n\n"
                                             + "Each byte needs two digits, with a space between: FE FE 8C E0 07 00 FD",
                                             "Contest Radio Setup", this);
                    return false;
                }
            }
            return true;
        }
    }

    // A radio name with no file in OmniRig - renamed or deleted there since the row was saved - is
    // shown in red, so the operator knows to fix or delete the row. Bound to the box's own text, so it
    // follows each keystroke.
    public class NotInOmniRigBrushConverter : IValueConverter
    {
        internal static HashSet<string> OmniRigFiles;
        private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string name = (value as string ?? "").Trim();
            if (name.StartsWith("★", StringComparison.Ordinal))   // the list's mark, not part of the name
                name = name.Substring(1).Trim();
            if (OmniRigFiles == null || name.Length == 0 || OmniRigFiles.Contains(name))
                return DependencyProperty.UnsetValue;
            return Red;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
