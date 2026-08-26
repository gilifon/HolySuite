using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HolyLogger
{
    // THE AI SERVICE CHOOSER, IN ONE PLACE SO IT CAN BE PUT IN FRONT OF THE OPERATOR WHEREVER HE IS.
    //
    // It began as part of the single-QSO check window, which was fine until the Log Fixer had to tell
    // somebody his allowance had run out. The message ended "or choose a different AI service in the
    // check window" - advice naming a window he would have had to go and find, which is the kind of
    // sentence that teaches an operator to stop reading messages.
    //
    // So the chooser moved here: the dropdown, the numbered steps for signing up to whichever service
    // is picked, what it costs, the link to its key page, and the box the key is pasted into. Both the
    // check window and the small window the Log Fixer opens show the same thing, and neither has to
    // know how any of it works.
    internal sealed class AiServicePanel : StackPanel
    {
        private readonly ComboBox _providerBox;
        private readonly StackPanel _serviceHelp;
        private readonly DockPanel _keyRow;
        private readonly TextBox _keyBox;

        // Optional hooks for whoever is hosting this. A window with a status line shows the trouble
        // there; one without simply does not pass a Say, and nothing is lost.
        internal Action<string> Say;
        internal Action KeySaved;
        internal Action ServiceChanged;

        // THE MODEL BOX IS FOR THE SETTINGS PAGE ONLY. In the check window and in the dialog that
        // confirms a run, the question is "which service, and has it a key" - a model name in either
        // of those is one more thing to read at a moment when nobody is choosing a model.
        private readonly bool _showModel;

        // Set when the model box is built; null when this panel has none.
        private Action _commitModel;

        /// <summary>
        /// Stores whatever stands in the model box, as though Save model had been pressed. For a
        /// host with an OK button of its own: a choice made and then not saved is a choice ignored.
        /// </summary>
        internal void CommitModel()
        {
            if (_commitModel != null) _commitModel();
        }

        internal AiServicePanel() : this(false) { }

        internal AiServicePanel(bool showModel)
        {
            _showModel = showModel;
            Margin = new Thickness(0, 0, 0, 10);

            // ── which service ───────────────────────────────────────────────────────────────────
            var chooser = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
            var label = new TextBlock
            {
                Text = "AI service:",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = ThemeManager.Brush("TextBrush"),
            };
            DockPanel.SetDock(label, Dock.Left);
            chooser.Children.Add(label);

            _providerBox = new ComboBox { FontSize = 16, Padding = new Thickness(6, 3, 6, 3) };
            foreach (AiService s in AiServices.All)
                _providerBox.Items.Add(new ComboBoxItem { Content = s.Label, Tag = s.Name, FontSize = 16 });
            SelectCurrent();
            _providerBox.SelectionChanged += Provider_Changed;
            chooser.Children.Add(_providerBox);
            Children.Add(chooser);

            // ── and everything that belongs to it ───────────────────────────────────────────────
            _serviceHelp = new StackPanel();
            Children.Add(_serviceHelp);

            _keyRow = new DockPanel { LastChildFill = true };
            var save = new Button
            {
                Content = "Save key",
                FontSize = 16,
                Padding = new Thickness(14, 4, 14, 4),
                Margin = new Thickness(8, 0, 0, 0),
            };
            save.Click += Save_Click;
            DockPanel.SetDock(save, Dock.Right);
            _keyRow.Children.Add(save);

            // A plain box, not a password box: an API key is pasted, and a paste that cannot be read
            // back is a paste nobody can check. It is his own machine and his own key.
            _keyBox = new TextBox { FontSize = 16, Padding = new Thickness(4) };
            _keyBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) Save_Click(s, null); };
            _keyRow.Children.Add(_keyBox);
            Children.Add(_keyRow);

            Refresh();
        }

        internal void FocusKey() { _keyBox.Focus(); }

        // WHAT THE CHOSEN SERVICE ASKS OF HIM, IN ORDER, IN HIS OWN WORDS.
        //
        // Not a paragraph. Signing up somewhere is a list of things done one after another, and a list
        // is what it is written as - he can hold his place in it while he is off in a browser doing
        // step two. The link is clickable, because an address that has to be retyped by hand is an
        // address that loses people.
        //
        // Rebuilt from scratch on every change rather than patched: four lines of controls are cheap,
        // and a panel half one service's instructions and half another's is the kind of thing nobody
        // notices until it has confused somebody.
        internal void Refresh()
        {
            _serviceHelp.Children.Clear();

            AiService service = AiServices.Current;
            bool haveKey = service.Key.Length > 0;

            // The key box goes away once there is a key, and so do the instructions for getting one.
            // What stays is the chooser above them, which is the part still worth having.
            _keyRow.Visibility = haveKey ? Visibility.Collapsed : Visibility.Visible;

            if (haveKey)
            {
                _serviceHelp.Children.Add(Line("A key for this service is saved on this computer.",
                                               italic: false, dim: true));
                ShowAllowance(service);
                ShowTopUp(service);
                ShowModelBox(service);
                return;
            }

            foreach (string step in AiServices.Guidance(service))
                _serviceHelp.Children.Add(Line(step, italic: false, dim: false));

            // WHAT IT COSTS, said plainly and last, because for most operators the price is the whole
            // decision and burying it inside step four would be a way of not saying it.
            var price = Line(service.Price, italic: true, dim: true);
            price.Margin = new Thickness(0, 6, 0, 4);
            _serviceHelp.Children.Add(price);

            var help = new TextBlock
            {
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeManager.Brush("TextBrush"),
                Margin = new Thickness(0, 0, 0, 6),
            };
            help.Inlines.Add(new System.Windows.Documents.Run("The key page: "));

            string address = service.KeyPageUrl;
            var link = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(service.KeyPageText))
            {
                NavigateUri = new Uri(address),
                ToolTip = address,
            };
            link.RequestNavigate += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(address); }
                catch (Exception swallowed)
                {
                    Log.Swallow(swallowed);
                    if (Say != null) Say("Could not open the browser. The address is " + address);
                }
                e.Handled = true;
            };
            help.Inlines.Add(link);
            help.Inlines.Add(new System.Windows.Documents.Run(
                "  -  the key is kept on this computer only."));
            _serviceHelp.Children.Add(help);
        }

        // WHAT IS LEFT TO SPEND, PUT UNDER THE NAME OF THE SERVICE.
        //
        // A paid service is a running total of somebody's money, and the operator should not have to
        // go to a website to find out where it stands. So it is shown here, beside the key it belongs
        // to - and shown for the free service not at all, which has nothing to spend.
        //
        // FETCHED AFTER THE PANEL IS ALREADY UP. It is a web call, and a web call on the way to
        // drawing a window is a window that does not appear. The line is added empty and hidden, and
        // it stays hidden unless an answer arrives: no answer means no claim about his money.
        //
        // THE SERVICE IS HELD, NOT LOOKED UP AGAIN ON THE WAY BACK. He can work the dropdown while
        // the request is still in the air, and an answer about the service he just left, written
        // under the name of the one he just picked, would be a lie about what he is about to spend.
        private async void ShowAllowance(AiService service)
        {
            if (service == null || string.IsNullOrEmpty(service.AllowanceUrl)) return;

            TextBlock line = Line(string.Empty, italic: false, dim: true);
            line.Visibility = Visibility.Collapsed;
            _serviceHelp.Children.Add(line);

            string said = await AiAllowance.DescribeAsync(service);
            if (said.Length == 0) return;

            // He moved on, or the panel was rebuilt under us while we waited. Either way this line
            // is no longer the one on the screen, and writing into it would be writing into nothing.
            if (!ReferenceEquals(service, AiServices.Current)) return;
            if (!_serviceHelp.Children.Contains(line)) return;

            line.Text = said;
            line.Visibility = Visibility.Visible;
        }

        // THE WAY TO THE ACCOUNT, ONCE THERE IS A KEY.
        //
        // Every link in this panel is part of the signup instructions, and the instructions go away
        // the moment a key is saved - which is right, but it left a man who wanted to put credit on
        // his account with nowhere to go. He had a key, so the program had stopped telling him where
        // the service lives. The one link he still needs is this one.
        private void ShowTopUp(AiService service)
        {
            if (service == null || string.IsNullOrEmpty(service.TopUpUrl)) return;

            var line = new TextBlock
            {
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeManager.Brush("TextBrush"),
                Margin = new Thickness(0, 6, 0, 0),
            };
            // WORDED FOR THE SERVICE IT BELONGS TO. One of them keeps a balance this window can show
            // and count down; the other counts days and reports no figure at all. "Add credit, or
            // see what you have spent" over a service that has no such page would be a promise the
            // page does not keep.
            line.Inlines.Add(new System.Windows.Documents.Run(
                string.IsNullOrEmpty(service.AllowanceUrl)
                    ? "The free allowance is a daily one. To go past it, turn on billing for the "
                      + "key's project: "
                    : "Add credit, or see what you have spent: "));

            string where = service.TopUpUrl;
            var link = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(service.TopUpText ?? where))
            {
                NavigateUri = new Uri(where),
                ToolTip = where,
            };
            link.RequestNavigate += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(where); }
                catch (Exception swallowed)
                {
                    Log.Swallow(swallowed);
                    if (Say != null) Say("Could not open the browser. The address is " + where);
                }
                e.Handled = true;
            };
            line.Inlines.Add(link);

            _serviceHelp.Children.Add(line);
        }

        // WHICH MODEL, WHEN THE OPERATOR WANTS TO SAY.
        //
        // Every service has a default written into the program, and it is the right answer for almost
        // everybody. Two people need this box: the one who has put credit on a paid account and wants
        // the better model he is now paying for, and the one whose default has been retired by the
        // company that made it - a name in a settings file is a fault he can mend himself, without
        // waiting for a new version of HolyLogger.
        //
        // Empty means the built-in default, and it says so: a blank box that silently means something
        // is a blank box people fill in with guesses.
        // ONE PLACE THAT TURNS "id|what it is" INTO ROWS, used for the built-in list and for the
        // fetched one, so the two can never come to look different from each other.
        private static void FillModels(ComboBox box, AiService service, string[] choices)
        {
            if (box == null || choices == null) return;

            box.Items.Clear();

            foreach (string choice in choices)
            {
                if (string.IsNullOrWhiteSpace(choice)) continue;

                int bar = choice.IndexOf('|');
                string id = (bar < 0 ? choice : choice.Substring(0, bar)).Trim();
                string what = bar < 0 ? string.Empty : choice.Substring(bar + 1).Trim();

                if (id.Length == 0) continue;

                bool isDefault = service != null
                    && string.Equals(id, service.DefaultModel, StringComparison.OrdinalIgnoreCase);

                box.Items.Add(new ComboBoxItem
                {
                    Content = id + (what.Length > 0 ? "   -   " + what : string.Empty)
                                 + (isDefault ? "   (used unless you change it)" : string.Empty),
                    Tag = id,
                    FontSize = 16,
                });
            }
        }

        // TODAY'S LIST, FETCHED AFTER THE PAGE IS ALREADY UP.
        //
        // The same rule as the credit line: a web call on the way to drawing a window is a window
        // that does not appear. The box is usable from the first moment with what the program was
        // built with, and is quietly refilled when the service answers.
        //
        // WHATEVER HE HAS TYPED SURVIVES. Refilling the list must not take the name out from under
        // an operator in the middle of choosing, so the text is put back exactly as it was.
        private async void RefreshModels(ComboBox box, AiService service)
        {
            if (box == null || service == null || string.IsNullOrEmpty(service.ModelsUrl)) return;

            string[] live = await AiModelList.ChoicesAsync(service);
            if (live == null || live.Length == 0) return;

            // He may have moved to another service, or closed the window, while it was in the air.
            if (!ReferenceEquals(service, AiServices.Current)) return;
            if (!_serviceHelp.Children.Contains(box.Parent as UIElement)) return;

            // Clearing the items wipes the text of an editable box, so what he had is put back -
            // and put back LAST, after the box has done its own tidying up.
            string typed = box.Text;
            FillModels(box, service, live);
            box.Dispatcher.BeginInvoke(new Action(() => box.Text = typed),
                                       System.Windows.Threading.DispatcherPriority.Input);
        }

        private void ShowModelBox(AiService service)
        {
            if (!_showModel || service == null || service.WriteModel == null) return;

            var label = Line("Which model answers:", italic: false, dim: false);
            label.Margin = new Thickness(0, 10, 0, 2);
            _serviceHelp.Children.Add(label);

            var row = new DockPanel { LastChildFill = true };

            var save = new Button
            {
                Content = "Save model",
                FontSize = 16,
                Padding = new Thickness(14, 4, 14, 4),
                Margin = new Thickness(8, 0, 0, 0),
            };
            DockPanel.SetDock(save, Dock.Right);
            row.Children.Add(save);

            // THE NAMES ARE OFFERED, NOT DEMANDED.
            //
            // This was an empty box under the words "leave empty for nvidia/nemotron-3-ultra-...".
            // To use a better model an operator had to already know that anthropic/claude-opus-5
            // exists, that it goes in that box, and how it is spelled - none of which the window
            // said. He was being asked to guess, and a program should never ask that.
            //
            // Editable, so a name that is not on the list can still be typed: the list is what the
            // program knows today, not a fence.
            var box = new ComboBox
            {
                IsEditable = true,
                FontSize = 16,
                Padding = new Thickness(4),
            };

            string current = (service.ReadModel() ?? string.Empty).Trim();

            FillModels(box, service, service.ModelChoices ?? new string[0]);

            // AND THEN TODAY'S LIST, WHEN IT ARRIVES. The box opens filled from what the program was
            // built with so it is never empty, and is refilled the moment the service says what it
            // really has - fetched at most once a day and kept on disk, the same as cty.dat. A model
            // retired last year stops being offered; one released last month starts being.
            RefreshModels(box, service);

            // SET WHEN THE BOX IS ON SCREEN, NOT BEFORE IT.
            //
            // An editable ComboBox has no text box to write into until its template is applied, so a
            // Text set here is written into nothing and the box comes up empty - which is what it
            // did: a model picked and saved was invisible when the page was opened again, and the
            // operator could not tell whether it had been saved at all.
            string showing = current.Length > 0 ? current : service.DefaultModel;
            box.Loaded += (s2, e2) => box.Text = showing;

            // Picking from the list puts the NAME in the box, not the sentence beside it - the
            // sentence is there to choose by, and would be refused as a model name.
            // AFTER THE BOX HAS FINISHED ITS OWN UPDATE, NOT DURING IT.
            //
            // An editable ComboBox writes the chosen item Content into its text box itself, and it
            // does so AFTER SelectionChanged returns - so a Text set inside the handler was being
            // overwritten a moment later, and picking a model left the box empty. Posting it back to
            // the dispatcher puts this line last, which is the only way it wins.
            //
            // It has to be the name alone: the words beside it are there to choose by, and would be
            // sent to the service as a model name and refused.
            box.SelectionChanged += (s2, e2) =>
            {
                var picked = box.SelectedItem as ComboBoxItem;
                if (picked == null) return;

                string id = (string)picked.Tag;
                box.Dispatcher.BeginInvoke(new Action(() => box.Text = id),
                                           System.Windows.Threading.DispatcherPriority.Input);
            };

            row.Children.Add(box);

            Action store = () =>
            {
                string typed = (box.Text ?? string.Empty).Trim();

                // The default typed back in means "the default" - stored empty, so a changed
                // default in a later version reaches him instead of being frozen here.
                if (string.Equals(typed, service.DefaultModel, StringComparison.OrdinalIgnoreCase))
                    typed = string.Empty;

                service.WriteModel(typed);
                try { Properties.Settings.Default.Save(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                if (Say != null)
                    Say(service.Model + " will be used from the next question onwards.");
            };

            // PRESSING OK MUST NOT THROW THE CHOICE AWAY.
            //
            // The box was only written to settings by the Save model button, so in the run dialog an
            // operator could pick a model, press OK, and be answered by the previous one - four runs
            // in a row reported opus-5 while he was choosing something else each time. Whoever hosts
            // this panel can now commit the box themselves, and the dialog does it on OK.
            _commitModel = store;

            save.Click += (s2, e2) => store();
            box.KeyDown += (s2, e2) => { if (e2.Key == Key.Enter) store(); };

            _serviceHelp.Children.Add(row);

            var note = Line("Pick one and press Save model. Any other name from the service's own "
                          + "website can be typed in as well; a name it does not know is refused "
                          + "with a message saying so, and nothing is spent on it.",
                            italic: true, dim: true);
            note.Margin = new Thickness(0, 4, 0, 0);
            _serviceHelp.Children.Add(note);
        }

        private TextBlock Line(string text, bool italic, bool dim)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
                Opacity = dim ? 0.85 : 1.0,
                Foreground = ThemeManager.Brush("TextBrush"),
                Margin = new Thickness(0, 0, 0, 2),
            };
        }

        private void SelectCurrent()
        {
            string now = AiServices.Current.Name;
            foreach (ComboBoxItem item in _providerBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals((string)item.Tag, now, StringComparison.OrdinalIgnoreCase))
                {
                    _providerBox.SelectedItem = item;
                    return;
                }
            }
            if (_providerBox.Items.Count > 0) _providerBox.SelectedIndex = 0;
        }

        private void Provider_Changed(object sender, SelectionChangedEventArgs e)
        {
            var item = _providerBox.SelectedItem as ComboBoxItem;
            if (item == null) return;

            AiServices.Choose((string)item.Tag);
            Refresh();

            // The box belongs to whichever service is showing, and a key half-pasted for the last one
            // must not be saved against this one.
            _keyBox.Text = string.Empty;
            if (ServiceChanged != null) ServiceChanged();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string key = (_keyBox.Text ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                if (Say != null) Say("Paste the key first.");
                return;
            }

            // TO THE SERVICE SHOWING IN THE BOX ABOVE, not to one shared setting. Each keeps its own
            // key, so going back to one used last month does not mean fetching it again.
            AiServices.Current.WriteKey(key);
            try { Properties.Settings.Default.Save(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            Refresh();
            if (KeySaved != null) KeySaved();
        }

        // The key this service had has just been refused, so it is cleared rather than left to be
        // believed in. Otherwise the program goes on thinking it has a working key and the operator
        // gets the same refusal every time he presses the button.
        internal void ForgetKey()
        {
            AiServices.Current.WriteKey(string.Empty);
            try { Properties.Settings.Default.Save(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            Refresh();
        }
    }
}
