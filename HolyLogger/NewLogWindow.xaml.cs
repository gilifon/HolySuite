using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace HolyLogger
{
    // Reusable dialog to name a log (Create New Log, contest log, Rename). Rejects a name that is
    // already used by another log. For rename, pass excludeId so the log can keep its own name.
    // When showCopyOptions is true it also collects the log's station callsign and an optional
    // copy-target log; those extras are hidden for the plain name / rename uses.
    //
    // The callsign box is TYPEABLE. It is filled from the main window when there is something there,
    // but a log must be able to get its callsign here: with the main window's box empty there was
    // nowhere else to enter one, and the log was created with no callsign at all - which is a log you
    // cannot log into. The operator is NOT part of a log's identity (a club station has many
    // operators through one callsign), so it is not asked for here.
    public partial class NewLogWindow : Window
    {
        private readonly DataAccess _dal;
        private readonly long _excludeId;
        public string LogName { get; private set; }


        // Set only when showCopyOptions is true. CopyTargetLogId is null when "(don't copy)" is chosen.
        public string LogCallsign { get; private set; }
        public long? CopyTargetLogId { get; private set; }

        // False when the operator chose to leave out the records that name no callsign. True otherwise,
        // which is also the answer when the question was never asked.
        public bool FillMissingCallsign { get; private set; } = true;

        // callsignChoices: the callsigns to pick from when they are already known - the ones an ADIF
        // being imported was made under, most-used first. More than one turns the callsign box into a
        // drop-down; one or none leaves it a box to type in.
        //
        // showCopyTarget: the "copy new QSOs into" chooser. Off for an import, where naming the log and
        // saying whose it is are enough to be answering at once; it can be set later in the Log Manager.
        public NewLogWindow(DataAccess dal, string prompt = "Enter a name for the new log:", string initial = "",
                            long excludeId = 0, bool showCopyOptions = false,
                            string defaultCallsign = "",
                            List<string> callsignChoices = null, bool showCopyTarget = true,
                            string callsignHint = null,
                            string introLabel = null, string introValue = null,
                            int noCallsignCount = 0)
        {
            InitializeComponent();
            _dal = dal;
            _excludeId = excludeId;
            Prompt.Text = prompt;
            TB_Name.Text = initial ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(introValue))
            {
                IntroPanel.Visibility = Visibility.Visible;
                IntroLabel.Text = introLabel ?? string.Empty;
                IntroValue.Text = introValue;
            }

            if (showCopyOptions)
            {
                CopyOptionsPanel.Visibility = Visibility.Visible;
                TB_Callsign.Text = (defaultCallsign ?? string.Empty).Trim();

                if (callsignChoices != null && callsignChoices.Count > 1)
                {
                    // A LIST ONLY WHEN THERE IS SOMETHING TO CHOOSE FROM. One callsign is not a choice,
                    // and a drop-down holding a single item asks the reader to look for one.
                    TB_Callsign.Visibility = Visibility.Collapsed;
                    CB_Callsign.Visibility = Visibility.Visible;
                    CB_Callsign.ItemsSource = callsignChoices;
                    CB_Callsign.SelectedIndex = 0;   // most-used first
                    CallsignSectionTitle.Text = "This log contains these station callsigns:";
                }
                else if (callsignChoices != null && callsignChoices.Count == 1)
                {
                    CallsignSectionTitle.Text = "This log contains the following station callsign:";
                }

                // The sentence names the callsign that is actually chosen, and follows it when it is
                // changed. _fixedHint is the one case where there is no callsign to name: a file that
                // does not say who made its QSOs.
                _fixedHint = callsignHint;
                CB_Callsign.SelectionChanged += (s, e) => UpdateCallsignHint();
                // The list is editable, so the sentence has to follow TYPING in it as well as picking.
                CB_Callsign.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                                       new System.Windows.Controls.TextChangedEventHandler((s, e) => UpdateCallsignHint()));
                TB_Callsign.TextChanged += (s, e) => UpdateCallsignHint();
                UpdateCallsignHint();

                if (noCallsignCount > 0)
                {
                    NoCallPanel.Visibility = Visibility.Visible;
                    NoCallText.Text = noCallsignCount.ToString("N0")
                                    + (noCallsignCount == 1 ? " QSO in this file does" : " QSOs in this file do")
                                    + " not say which callsign made them.";
                }

                if (!showCopyTarget) CopyTargetSection.Visibility = Visibility.Collapsed;
                RefreshCopyTargets();
            }

            Loaded += (s, e) => { WidenForTheName(); TB_Name.Focus(); TB_Name.SelectAll(); };
        }

        // THE WHOLE NAME, VISIBLY WHOLE. Imported logs are named after the file, and those names run
        // long - "Log4OM_ADIF_20260816102715_all_20260819_114144". At a fixed 460 the end of such a name
        // sits outside the box, and a name you cannot see the end of is one you cannot check. The window
        // is widened to the name it is actually showing, with room to spare after it so it is plain that
        // nothing is hidden. Never narrower than the 460 every dialog here has always been.
        private void WidenForTheName()
        {
            try
            {
                string name = TB_Name.Text ?? string.Empty;
                if (name.Length == 0) return;

                double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;

                double Measure(string s, System.Windows.Controls.Control like, FontWeight weight)
                {
                    if (string.IsNullOrEmpty(s)) return 0;
                    return new System.Windows.Media.FormattedText(
                        s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new System.Windows.Media.Typeface(like.FontFamily, like.FontStyle, weight, like.FontStretch),
                        like.FontSize, System.Windows.Media.Brushes.Black, dpi).Width;
                }

                // The file name shown above can be the longest line in the window; it wraps if it will
                // not fit even at the cap.
                double nameWidth   = Measure(name, TB_Name, TB_Name.FontWeight);
                double promptWidth = Measure(Prompt.Text, TB_Name, FontWeights.Bold);
                double introWidth  = IntroPanel.Visibility == Visibility.Visible
                                   ? Measure(IntroValue.Text, TB_Name, FontWeights.Normal) : 0;

                // The widest line, then the box's padding and frame, the 22 of margin each side of the
                // grid, the window's frame - and 90 of empty box after the name, so it is plain that
                // nothing is hidden past the right-hand edge.
                double wanted = Math.Max(Math.Max(nameWidth + 90, promptWidth + 20), introWidth + 20)
                              + 22 + 22 + 22 + 16;
                Width = Math.Min(Math.Max(460, wanted), 1000);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The hint to show when there is no callsign to name (a file that carries none). Empty for
        // every other case, where the sentence is built from the callsign itself.
        private string _fixedHint;

        // The callsign currently chosen, from whichever of the two controls is on screen.
        // Text, not SelectedItem: the list is editable, and a callsign typed into it belongs to no item.
        // Picking from the list sets Text too, so this reads both the same way.
        private string ChosenCallsign() =>
            CB_Callsign.Visibility == Visibility.Visible
                ? (CB_Callsign.Text ?? string.Empty).Trim()
                : (TB_Callsign.Text ?? string.Empty).Trim();

        private void UpdateCallsignHint()
        {
            string call = ChosenCallsign().ToUpperInvariant();
            CallsignHint.Inlines.Clear();

            // The choice about records naming no callsign names the callsign chosen above, so it has to
            // follow it when it changes.
            if (RB_FillMissingText != null)
                RB_FillMissingText.Text = call.Length > 0 ? "Give them " + call
                                                          : "Give them this log's callsign";

            if (call.Length == 0)
            {
                CallsignHint.Text = string.IsNullOrWhiteSpace(_fixedHint)
                    ? "The log is opened for this callsign. Opening it later on will put this callsign as the station callsign in the main window."
                    : _fixedHint;
                return;
            }

            // Two sentences, two lines. The second is the one that changes something outside this
            // window, so it is the one written in the accent colour - with the callsign itself in black
            // inside it, the same word standing out of both lines.
            var accent = ThemeManager.Brush("AccentBrush");

            CallsignHint.Text = string.Empty;
            CallsignHint.Inlines.Add(new System.Windows.Documents.Run("The log will use "));
            CallsignHint.Inlines.Add(new System.Windows.Documents.Run(call) { FontWeight = FontWeights.Bold });
            CallsignHint.Inlines.Add(new System.Windows.Documents.Run(" as the Station callsign."));
            CallsignHint.Inlines.Add(new System.Windows.Documents.LineBreak());
            CallsignHint.Inlines.Add(new System.Windows.Documents.Run("Opening it later on will put ")
                { FontWeight = FontWeights.Bold, Foreground = accent });
            CallsignHint.Inlines.Add(new System.Windows.Documents.Run(call)
                { FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.Black });
            CallsignHint.Inlines.Add(new System.Windows.Documents.Run(" as the station callsign in the main window.")
                { FontWeight = FontWeights.Bold, Foreground = accent });
        }

        // First item = "(don't copy)" sentinel (Id 0); then EVERY regular log. A copy may go to any log
        // the operator chooses - the callsign of this log does not limit where its QSOs are mirrored,
        // and a log that receives copies made under another callsign simply holds that callsign too
        // from then on. Contest logs are the one exception: they must never receive copies, because a
        // contest log's QSOs may only come from contest operation.
        private void RefreshCopyTargets()
        {
            if (_dal == null || CB_CopyTarget == null) return;
            if (CopyOptionsPanel == null || CopyOptionsPanel.Visibility != Visibility.Visible) return;

            var items = new List<LogInfo> { new LogInfo { Id = 0, Name = "(don't copy)" } };
            try
            {
                items.AddRange(_dal.GetLogs().Where(l => string.IsNullOrEmpty(l.EventType)));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            CB_CopyTarget.ItemsSource = items;
            CB_CopyTarget.SelectedIndex = 0;
            CB_CopyTarget.IsEnabled = true;
        }

        private void Btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            string name = (TB_Name.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
            {
                HolyMessageBox.ShowWarning("Please enter a name.", "Log name", this);
                return;
            }
            if (!_dal.LogNameAvailable(name, _excludeId))
            {
                HolyMessageBox.ShowWarning("A log named \"" + name + "\" already exists. Please choose a different name.", "Name already used", this);
                return;
            }

            if (CopyOptionsPanel.Visibility == Visibility.Visible)
            {
                // A log without a callsign cannot be logged into, and now that the box is typeable
                // there is somewhere to put one - so ask for it here instead of creating such a log.
                string call = ChosenCallsign();
                if (call.Length == 0)
                {
                    HolyMessageBox.ShowWarning("Enter the station callsign this log is for.", "Station callsign", this);
                    TB_Callsign.Focus();
                    return;
                }

                // IS IT A CALLSIGN AT ALL? "ABC" was accepted and became a log's callsign. Asked with
                // the same test the rest of the program uses, and asked rather than refused: a few real
                // calls end in a digit and fail the shape rule, and a log must never become unusable
                // because its owner's callsign is an unusual one.
                if (!CallsignIdentity.LooksLikeCallsign(call) &&
                    !HolyMessageBox.ShowConfirm(
                        "\"" + call.ToUpperInvariant() + "\" does not look like a callsign.\n\n" +
                        "Use it as this log's station callsign anyway?",
                        "Station callsign", HolyMsgType.Warning, this))
                {
                    if (CB_Callsign.Visibility == Visibility.Visible) CB_Callsign.Focus();
                    else TB_Callsign.Focus();
                    return;
                }

                LogCallsign = call.ToUpperInvariant();
                FillMissingCallsign = RB_LeaveMissing.IsChecked != true;
                long tid = 0;
                try { if (CB_CopyTarget.SelectedValue != null) tid = Convert.ToInt64(CB_CopyTarget.SelectedValue); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                CopyTargetLogId = tid > 0 ? (long?)tid : null;
            }

            LogName = name;
            DialogResult = true;
            Close();
        }
    }
}
