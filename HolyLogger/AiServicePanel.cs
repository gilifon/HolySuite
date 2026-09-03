using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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

        // COMPACT: everything about SETTING IT UP is left out while there is already a key.
        //
        // The run dialog is not the place to be taught how to open an account. Once a key is saved,
        // the only thing there worth a decision is which model answers - so the how-to button, the
        // line saying a key exists, and the box to replace it all go, and what is left is one row.
        // With no key it shows the lot, because then setting it up IS the decision.
        private readonly bool _compact;

        internal AiServicePanel() : this(false, false) { }

        internal AiServicePanel(bool showModel) : this(showModel, false) { }

        internal AiServicePanel(bool showModel, bool compact)
        {
            _showModel = showModel;
            _compact = compact;
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

            // A LIST OF ONE IS NOT A CHOICE, AND NEITHER IS A LABEL ABOVE IT. While OpenRouter was the
            // only service the dropdown asked him to pick something he had no say in, so it was left
            // out of the panel - wired, but not shown. Google Gemini is back beside it, so there is
            // a choice again and the box appears on its own, without this panel being touched.
            if (AiServices.All.Length > 1) Children.Add(chooser);

            // ── and everything that belongs to it ───────────────────────────────────────────────
            _serviceHelp = new StackPanel();
            Children.Add(_serviceHelp);

            _keyRow = new DockPanel { LastChildFill = true };
            var save = new Button
            {
                Content = "Save API key",
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

            // NOT ADDED HERE. The panel used to end with this row, which put the key box below
            // everything including the model list - so an operator following the steps went past
            // "copy the key" and had to look under the model box to find where it goes. It is
            // placed by Refresh instead, straight after the how-to, where the eye already is.

            Refresh();

            // THE LAST LINE, WHEREVER THIS PANEL IS SHOWN. An answer from an AI reads like an answer
            // from a book, and it is not one: it can be wrong about a callsign, a country or a date
            // with exactly the same confidence it is right. Said once, here, under every place the
            // service is chosen - Options, the check window and the Log Fixer all host this panel -
            // so nobody meets an AI answer in this program without having been told.
            //
            // Added AFTER Refresh, and outside _serviceHelp, so it stays at the bottom: Refresh empties
            // that panel and fills it again on every change of service.
            var caution = new TextBlock
            {
                Text = "Please note that AI can make mistakes.",
                FontSize = 16,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            };
            // The theme's own red, by reference rather than by a brush taken once: this panel is open
            // across colour-scheme changes, and a copied colour would keep the old scheme's red.
            caution.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            Children.Add(caution);
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

            // THREE THINGS, IN THE ORDER HE DOES THEM: how to set it up, where the key goes, which
            // model answers.
            //
            // The page used to open with a wall - the service named, a line saying a key was saved,
            // what had been spent, a link, then seven numbered steps, then the box. Every line of it
            // was true and most of it was in the way. The words above this panel now say what the
            // thing is and what it costs; the steps live behind one button, with pictures; and what
            // is left is the two boxes he has to fill in.
            //
            // The key box shows whether or not there is a key, because a key gets replaced for
            // ordinary reasons and hiding the only way to do it is hiding the fix.
            _keyRow.Visibility = Visibility.Visible;

            // COMPACT IS THE RUN DIALOG, AND IT SETS NOTHING UP.
            //
            // With a key it shows the model and nothing else. Without one it says what is missing
            // and offers the page where that is fixed - but it does NOT carry a paste box of its
            // own: a key is pasted in one place, Options > AI Service, and one place is one place to
            // keep right. A second box on a dialog he opened to ask a question is a second thing to
            // maintain and a second way to end up with a key saved somewhere he did not expect.
            if (_compact)
            {
                if (!haveKey)
                {
                    ShowKeyState(false);
                    ShowTopUp(service);
                }

                ShowModelBox(service);
                return;
            }

            // AND THE FULL PAGE: the state of things first, then the offer to put it right, then the
            // box. The button used to sit above the line saying there was no key, which is an answer
            // printed above its question. A man reads what is wrong, then what to do about it.
            ShowKeyState(haveKey);
            ShowTopUp(service);
            ShowKeyBox(haveKey);
            ShowModelBox(service);
        }

        // WHAT IS LEFT TO SPEND, FETCHED AFTER THE PAGE IS ALREADY UP.
        //
        // A paid service is a running total of somebody's money, and he should not have to go to a
        // website to see where it stands. It is a web call, though, and a web call on the way to
        // drawing a window is a window that does not appear - so the line is added empty and hidden,
        // and it stays hidden unless an answer arrives. No answer means no claim about his money.
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

        // THE WAY TO THE ACCOUNT, AND THE PICTURES THAT SHOW WHAT TO DO THERE.
        //
        // Everything that happens on somebody else's website is behind one button: adding credit,
        // making the API key, setting a limit on it. Words alone about a page an operator has never
        // seen are words he abandons halfway through.
        private void ShowTopUp(AiService service)
        {
            if (service == null) return;

            // ── A SERVICE WITH NO PICTURES STILL HAS TO BE EXPLAINED ────────────────────────────
            //
            // The whole setup story moved behind that one button, and the button is built from
            // TopUpUrl - which only a PAID service has. So when Google Gemini came back, the page it
            // showed a man with no key was a line saying he had none and a model box: nowhere to get
            // one, no address, not a word about what to do. The steps were in the service definition
            // the whole time, unread.
            //
            // Written out here in the same order he does them, with the key page as a link he can
            // press. Four short lines are not a wall, and they are all this service needs.
            if (string.IsNullOrEmpty(service.TopUpUrl))
            {
                if (service.Steps != null)
                {
                    int step = 1;
                    foreach (string s in service.Steps)
                        _serviceHelp.Children.Add(Line(step++ + ". " + s, italic: false, dim: true));
                }

                if (!string.IsNullOrEmpty(service.KeyPageUrl))
                    _serviceHelp.Children.Add(KeyPageLink(service));

                if (!string.IsNullOrEmpty(service.Price))
                    _serviceHelp.Children.Add(Line(service.Price, italic: true, dim: true));

                return;
            }

            var how = new Button
            {
                Content = "Show me how to set this up",
                FontSize = 16,
                Padding = new Thickness(14, 4, 14, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 4),
            };
            how.Click += (s, e) => AiPayHelpWindow.Show(Window.GetWindow(this));
            _serviceHelp.Children.Add(how);

            // NO SPENT LINE. It was his own account rather than an instruction, which is why I
            // kept it - and he cut it: this page is for setting the thing up, and what has been
            // spent is on the account page the link goes to. Two places saying it is one too many.
            // ShowAllowance is left in the file; nothing calls it while the line is not wanted.
        }

        // What the box is FOR when a key is already saved - otherwise an empty box under "a key is
        // saved on this computer" reads as a fault rather than an offer.
        // WHETHER THERE IS A KEY AT ALL, SAID BEFORE THE BOX.
        //
        // An empty box tells him nothing about the state of things: he cannot see whether a key is
        // saved and he is about to replace it, or whether there is none and nothing will work until
        // he pastes one. The second of those is the one that stops the feature dead, so it is said
        // in red - the only red on this page, and it goes the moment a key is saved.
        private void ShowKeyState(bool haveKey)
        {
            var line = Line(haveKey ? "An API key is already saved on this computer."
                                    : "No API key is saved on this computer yet.",
                            italic: false, dim: false);

            line.FontWeight = FontWeights.Bold;
            line.Foreground = new SolidColorBrush(haveKey
                ? Color.FromRgb(0x1A, 0x4F, 0xA8)     // blue: settled
                : Color.FromRgb(0xC6, 0x28, 0x28));   // red: nothing will work until this is done
            line.Margin = new Thickness(0, 0, 0, 8);

            _serviceHelp.Children.Add(line);
        }

        private void ShowKeyBox(bool haveKey)
        {
            var line = Line(haveKey ? "Paste a new API key here to replace it:"
                                    : "Paste the API key here:",
                            italic: false, dim: false);
            line.Margin = new Thickness(0, 10, 0, 2);
            _serviceHelp.Children.Add(line);

            // The row is re-parented on every Refresh, so it is taken off its old parent first -
            // WPF refuses an element that already has one, and the whole panel would come up blank.
            var was = _keyRow.Parent as Panel;
            if (was != null) was.Children.Remove(_keyRow);

            _serviceHelp.Children.Add(_keyRow);
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

                box.Items.Add(new ComboBoxItem
                {
                    Content = id + (what.Length > 0 ? "   -   " + what : string.Empty),
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
            _ = box.Dispatcher.BeginInvoke(new Action(() => box.Text = typed),
                                           System.Windows.Threading.DispatcherPriority.Input);
        }

        private void ShowModelBox(AiService service)
        {
            if (!_showModel || service == null || service.WriteModel == null) return;

            var label = Line("Which AI model to use?", italic: false, dim: false);
            label.Margin = new Thickness(0, 10, 0, 2);
            _serviceHelp.Children.Add(label);

            // NO SAVE BUTTON. It existed because nothing else wrote the box down, and that is no longer
            // true: the run dialog stores it when OK is pressed and the Options window stores it as it
            // closes. A button that only does what leaving the page already does is one more thing to
            // press and one more way to think you have lost your choice by not pressing it.
            var row = new DockPanel { LastChildFill = true };

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
            // this panel commits the box itself now: the run dialog on OK, Options as it closes.
            _commitModel = store;

            // Enter still stores it, for the man who types a model name and expects Enter to mean it.
            box.KeyDown += (s2, e2) => { if (e2.Key == Key.Enter) store(); };

            _serviceHelp.Children.Add(row);

            // NO NOTE UNDER THE BOX. It explained that a name can be typed by hand and that a
            // wrong one is refused without costing anything - both true, and both answers to
            // questions nobody had asked yet. The list is the instruction.
        }

        // The page where the key is made, as something to press. An address printed as words is an
        // address somebody has to retype, and half of them mistype it.
        private TextBlock KeyPageLink(AiService service)
        {
            var line = new TextBlock
            {
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 6),
                Foreground = ThemeManager.Brush("TextBrush"),
            };
            line.Inlines.Add(new System.Windows.Documents.Run("The key page is here: "));

            string address = string.IsNullOrEmpty(service.KeyPageText) ? service.KeyPageUrl
                                                                       : service.KeyPageText;
            var link = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(address))
            {
                NavigateUri = new Uri(service.KeyPageUrl),
                ToolTip = service.KeyPageUrl,
            };
            link.RequestNavigate += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(link.NavigateUri.ToString()); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                e.Handled = true;
            };
            line.Inlines.Add(link);
            return line;
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
                if (Say != null) Say("Paste the API key first.");
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
