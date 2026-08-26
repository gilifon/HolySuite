using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HolyLogger
{
    // THE CHOOSER ON ITS OWN, FOR WHOEVER NEEDS TO OFFER IT.
    //
    // The Log Fixer's refusal used to end "or choose a different AI service in the check window",
    // which told the operator to go and find a window rather than giving him the thing. This is the
    // thing: a small window with the same chooser, the same signup steps and the same key box, opened
    // from wherever the suggestion was made.
    //
    // Deliberately nothing else in it. It is not a settings page and it is not a place to explain the
    // feature - it is the two minutes between "I have run out" and "I am using the other one".
    internal sealed class AiServiceWindow : Window
    {
        private readonly TextBlock _status;

        internal AiServiceWindow(Window owner)
        {
            Owner = owner;
            Title = "Which AI service";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            // SIZED TO ITS CONTENT rather than to a number: the steps are four lines for one service
            // and five for another, and a fixed height would clip whichever is longer the day one of
            // them gains a line.
            SizeToContent = SizeToContent.Height;
            Width = 620;
            MinWidth = 460;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = ThemeManager.Brush("WindowBg");
            FontSize = 16;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = "Choose the AI service, and get a key for it.",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeManager.Brush("TextBrush"),
                Margin = new Thickness(0, 0, 0, 10),
            });

            _status = new TextBlock
            {
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeManager.Brush("TextBrush"),
                Margin = new Thickness(0, 0, 0, 8),
            };

            var panel = new AiServicePanel
            {
                Say = text => _status.Text = text,
                KeySaved = () => _status.Text = "Saved. Close this window and it will go on.",
            };
            // The line below the panel belongs to whichever service is showing, and he can change
            // that with the dropdown while the window is open.
            panel.ServiceChanged = () => _status.Text = WhyNoKey();
            root.Children.Add(panel);
            root.Children.Add(_status);

            _status.Text = WhyNoKey();

            var close = new Button
            {
                Content = "Close",
                FontSize = 16,
                Height = 38,
                MinWidth = 120,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            close.Click += (s, e) => Close();
            root.Children.Add(close);

            Content = root;
        }

        // WHY THIS WINDOW HAS OPENED AT ALL, TO SOMEBODY WHO KNOWS HE HAS A KEY.
        //
        // The keys are kept one per service, so choosing a different service in the list is enough to
        // leave the program with none - the old key is still there, still saved, simply not the one
        // in use. Being told "no key has been entered" after pasting one last week reads as a program
        // that has thrown it away, and the next thing he does is go and fetch another.
        //
        // So when a key does exist somewhere, it is named and the way back to it is one line long.
        // Empty when the chosen service already has its key, or when there genuinely is none.
        private static string WhyNoKey()
        {
            if (AiQsoCheck.HasKey) return string.Empty;

            string elsewhere = AiServices.WithKeysExcept(AiServices.Current);
            if (elsewhere.Length == 0) return string.Empty;

            return "The service chosen here has no key yet - but one is already saved for "
                 + elsewhere + ". Pick it in the list above to use it.";
        }
    }
    // "6 QSOs WILL BE CHECKED BY AI" - BY WHICH AI?
    //
    // The run was confirmed with a plain message box, and it never said whose opinion was about to be
    // fetched. That matters more than it sounds: the same six QSOs put to two services came back 5-1
    // from one and 4-2 from the other, so the service IS part of the answer. And a man who reads the
    // name and wants the other one should not have to press Cancel, go and find Options, change it,
    // and come back - the chooser is small, so it stands in the dialog beside the question.
    //
    // Deliberately the whole AiServicePanel and not a bare dropdown: pick a service with no key yet
    // and the panel turns into the signup steps and a key box, right here. When a key is already
    // saved it collapses to one line and what is left is a name and a list to change it from.
    internal sealed class AiRunPrompt : Window
    {
        private readonly Button _ok;
        private readonly TextBlock _warning;

        private bool Confirmed;

        internal static bool Ask(Window owner, string lines)
        {
            var dlg = new AiRunPrompt(owner, lines);
            dlg.ShowDialog();
            return dlg.Confirmed;
        }

        private AiRunPrompt(Window owner, string lines)
        {
            Owner = owner;
            Title = "Check with AI";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.Height;
            Width = 620;
            MinWidth = 480;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = ThemeManager.Brush("WindowBg");
            FontSize = 16;

            var root = new StackPanel { Margin = new Thickness(16) };

            var what = new TextBlock
            {
                Text = lines,
                FontSize = 18,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeManager.Brush("TextBrush"),
                Margin = new Thickness(0, 0, 0, 14),
            };
            root.Children.Add(what);

            // THE MODEL IS PART OF THE ANSWER, so it is named here and can be changed here.
            //
            // This dialog said which SERVICE would be asked and never which model - and the model is
            // what actually answers: the same six QSOs came back 5-1 from one and 4-2 from another,
            // both through OpenRouter. Naming the service alone told him almost nothing, and made
            // him close the window and go to Options to change the one thing he wanted to change.
            var panel = new AiServicePanel(showModel: true);
            root.Children.Add(panel);

            _warning = new TextBlock
            {
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed,
            };
            root.Children.Add(_warning);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0),
            };

            _ok = new Button
            {
                Content = "OK",
                FontSize = 16,
                Height = 38,
                MinWidth = 120,
                IsDefault = true,
                Margin = new Thickness(0, 0, 10, 0),
            };
            // OK MEANS "ASK WITH WHAT THE WINDOW IS SHOWING", model included. Without this the box
            // was decoration: he picked a model, pressed OK, and the run went out with whatever had
            // been saved before.
            _ok.Click += (s2, e2) => { panel.CommitModel(); Confirmed = true; Close(); };
            buttons.Children.Add(_ok);

            var cancel = new Button
            {
                Content = "Cancel",
                FontSize = 16,
                Height = 38,
                MinWidth = 120,
                IsCancel = true,
            };
            cancel.Click += (s2, e2) => Close();
            buttons.Children.Add(cancel);

            root.Children.Add(buttons);
            Content = root;

            // A SERVICE WITH NO KEY CANNOT ANSWER, so OK does not pretend it can. Rechecked whenever
            // he changes the service or saves a key, both of which he can do without leaving here.
            panel.ServiceChanged = Recheck;
            panel.KeySaved = Recheck;
            Recheck();

            KeyDown += (s2, e2) => { if (e2.Key == System.Windows.Input.Key.Escape) Close(); };
        }

        private void Recheck()
        {
            bool ready = AiQsoCheck.HasKey;

            _ok.IsEnabled = ready;
            _warning.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;

            if (!ready)
                _warning.Text = "This service has no key yet. Paste one above, or pick a service that "
                              + "already has one.";
        }
    }
}
