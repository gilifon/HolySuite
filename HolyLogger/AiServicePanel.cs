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
            line.Inlines.Add(new System.Windows.Documents.Run("Add credit, or see what you have spent: "));

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
        private void ShowModelBox(AiService service)
        {
            if (!_showModel || service == null || service.WriteModel == null) return;

            var label = Line("Model (leave empty for " + service.DefaultModel + "):",
                             italic: false, dim: false);
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

            var box = new TextBox
            {
                FontSize = 16,
                Padding = new Thickness(4),
                Text = (service.ReadModel() ?? string.Empty).Trim(),
            };
            row.Children.Add(box);

            Action store = () =>
            {
                service.WriteModel((box.Text ?? string.Empty).Trim());
                try { Properties.Settings.Default.Save(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                if (Say != null)
                    Say(service.Model + " will be used from the next question onwards.");
            };

            save.Click += (s, e) => store();
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) store(); };

            _serviceHelp.Children.Add(row);

            var note = Line("The names are on the service's own website. A name it does not know is "
                          + "refused with a message saying so - nothing is spent on it.",
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
