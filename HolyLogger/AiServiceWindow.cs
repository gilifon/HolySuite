using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HolyLogger
{
    // AiServiceWindow WAS HERE, and is gone. It was a small window carrying the service chooser,
    // the signup steps and the key box, opened wherever a key turned out to be missing - useful
    // while there were two services and the setup was a couple of lines. Setting up is now one page
    // in Options, with pictures, and a second window doing half the job is a second place to keep
    // right. Nothing opened it any more.
    //
    // HOW TO PUT CREDIT ON THE ACCOUNT, IN PICTURES.
    //
    // The steps beside the key box say what to do in words, and words about somebody else's website
    // are the kind of instruction an operator gives up on halfway through - the page has changed
    // shape, the button is a different colour, and he cannot tell whether he is even in the right
    // place. Three screenshots with an arrow on the thing to press settle it in a glance.
    //
    // The shots carry no account of ours: the address, the balance and the transactions were painted
    // out of the images before they went into the program.
    internal sealed class AiPayHelpWindow : Window
    {
        internal static void Show(Window owner)
        {
            new AiPayHelpWindow(owner).ShowDialog();
        }

        private AiPayHelpWindow(Window owner)
        {
            Owner = owner;
            Title = "Credit and an API key on openrouter.ai";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Width = 900;
            Height = 780;
            MinWidth = 640;
            MinHeight = 420;
            ShowInTaskbar = false;
            Background = ThemeManager.Brush("WindowBg");

            var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };

            stack.Children.Add(Heading("Credit and an API key, on openrouter.ai"));

            stack.Children.Add(Caption(
                "1.  Open the Credits page - the link is under this line - and press **Add Credits**, "
                + "where the arrow points."));
            stack.Children.Add(Link("The page is here:", "openrouter.ai/credits"));
            stack.Children.Add(Picture("openrouter_credits.png"));

            stack.Children.Add(Caption(
                "2.  Type the amount. Five or ten dollars is plenty: a question about six QSOs "
                + "costs a few cents."));
            stack.Children.Add(Picture("openrouter_amount.png"));

            stack.Children.Add(Caption(
                "3.  At the bottom of the payment box, turn ON the switch that says Use one-time payment methods. "
                + "Then the card is used for this payment only and is not kept on the account."));
            stack.Children.Add(Picture("openrouter_onetime.png"));

            // THE KEY IS THE OTHER HALF, and this window said nothing about it. Credit on the
            // account buys nothing until the program has a key to spend it with, and an operator who
            // has just paid and still cannot ask a question has been left in the middle of the job.
            stack.Children.Add(Caption(
                "4.  Now the API key. Open API Keys, press **+ New Key**, and give it any name you "
                + "want - HolyLogger, for example."));
            stack.Children.Add(Link("The API key page is here:", "openrouter.ai/keys"));
            stack.Children.Add(Picture("openrouter_newkey.png"));
            stack.Children.Add(Caption(
                "5.  Type the API key name. A **credit limit** can be set here too - that is the most "
                + "this key can ever spend."));
            stack.Children.Add(Picture("openrouter_keylimit.png"));

            stack.Children.Add(Caption(
                "6.  The key is shown once and never again. Copy it, and paste it into the box on "
                + "the [[AI Service]] page - it is kept on this computer only."));
            stack.Children.Add(Picture("openrouter_copykey.png"));

            var scroll = new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            var close = new Button
            {
                Content = "Close",
                FontSize = 16,
                Height = 38,
                MinWidth = 120,
                Margin = new Thickness(18, 0, 18, 14),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsCancel = true,
                IsDefault = true,
            };
            close.Click += (s, e) => Close();

            var root = new DockPanel();
            DockPanel.SetDock(close, Dock.Bottom);
            root.Children.Add(close);
            root.Children.Add(scroll);

            Content = root;
            KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        }

        // A LINK INSIDE THIS WINDOW, because the one on the page behind cannot be reached: this
        // window is modal, so while it is open every link under it is dead. An operator reading
        // "click the link above" and finding it will not click was being sent in a circle.
        private static UIElement Link(string before, string address)
        {
            var line = new TextBlock
            {
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(26, 0, 0, 8),
                Foreground = ThemeManager.Brush("TextBrush"),
            };

            if (!string.IsNullOrEmpty(before))
                line.Inlines.Add(new System.Windows.Documents.Run(before + " "));

            var link = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(address))
            {
                NavigateUri = new Uri(address.StartsWith("http") ? address : "https://" + address),
                ToolTip = address,
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

        private static TextBlock Heading(string text)
        {
            var t = new TextBlock
            {
                Text = text,
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = ThemeManager.Brush("TextBrush"),
            };
            return t;
        }

        // **LIKE THIS** IS BOLD. The one thing to press is the only thing worth finding at a
        // glance, and a caption that names it in the same weight as the rest of the sentence makes
        // the reader hunt for it twice - once here and once on the picture.
        // THE NUMBER SITS IN ITS OWN COLUMN, so every line of the step lines up under the first
        // word and not under the number. A second line starting at the far left reads as a new
        // thought rather than the rest of this one.
        private static UIElement Caption(string text)
        {
            string number = string.Empty;
            string words = text ?? string.Empty;

            int stop = words.IndexOf(".", StringComparison.Ordinal);
            if (stop > 0 && stop <= 2)
            {
                number = words.Substring(0, stop + 1);
                words = words.Substring(stop + 1).TrimStart();
            }

            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 6) };

            if (number.Length > 0)
            {
                var head = new TextBlock
                {
                    Text = number,
                    FontSize = 16,
                    Width = 26,
                    VerticalAlignment = VerticalAlignment.Top,
                    Foreground = ThemeManager.Brush("TextBrush"),
                };
                DockPanel.SetDock(head, Dock.Left);
                row.Children.Add(head);
            }

            var block = new TextBlock
            {
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeManager.Brush("TextBrush"),
            };
            row.Children.Add(block);
            text = words;

            // TWO MARKERS. **like this** is bold, for the thing to press on the picture; [[like
            // this]] wears a grey ground, for a button of OUR OWN named in the sentence - so a
            // reader can see at a glance which words are a button in this program and which are
            // words on somebody else's website.
            AddMarked(block, text ?? string.Empty);
            return row;
        }

        internal static void AddMarked(TextBlock block, string text)
        {
            string rest = text;
            bool bold = false;
            bool shaded = false;

            while (rest.Length > 0)
            {
                int b = rest.IndexOf("**", StringComparison.Ordinal);
                int g = rest.IndexOf(shaded ? "]]" : "[[", StringComparison.Ordinal);

                int mark = b < 0 ? g : (g < 0 ? b : Math.Min(b, g));
                string piece = mark < 0 ? rest : rest.Substring(0, mark);

                if (piece.Length > 0)
                {
                    var run = new System.Windows.Documents.Run(piece);
                    if (bold) run.FontWeight = FontWeights.Bold;
                    // The grey ground is enough to mark it as a button; bold as well made it shout
                    // louder than the thing he is actually being told to press.
                    if (shaded) run.Background = new SolidColorBrush(Color.FromRgb(0xC6, 0xC6, 0xCE));
                    block.Inlines.Add(run);
                }

                if (mark < 0) break;

                rest = rest.Substring(mark + 2);
                if (mark == b) bold = !bold; else shaded = !shaded;
            }
        }

        // SCALED DOWN, NEVER UP. The shots are wider than this window and are let down to fit; a
        // picture blown up past its own size is a blurred picture, and these are being read.
        private static UIElement Picture(string file)
        {
            var box = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeManager.Brush("GridLine"),
                Margin = new Thickness(0, 0, 0, 6),
            };

            try
            {
                var image = new Image
                {
                    // THE PACK ADDRESS, NOT A RELATIVE PATH. These pictures are compiled INTO the
                    // program, and a relative Uri looks for a file on disk beside the exe - which
                    // is not there, so the picture came up empty and silently: no file, no error,
                    // three empty frames with captions above them.
                    Source = new BitmapImage(new Uri("pack://application:,,,/Images/" + file, UriKind.Absolute)),
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    HorizontalAlignment = HorizontalAlignment.Left,

                    // A CEILING, SO ONE TALL SHOT DOES NOT FILL THE WINDOW. The payment box is
                    // nearly 800 pixels tall; at full size it pushed the other two off the bottom
                    // and the reader had no idea there were three.
                    MaxHeight = 340,
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                box.Child = image;
            }
            catch (Exception swallowed)
            {
                // A missing picture is a gap, not a crash: the words above it still say what to do.
                Log.Swallow(swallowed);
                box.Child = Caption("(the picture could not be shown)");
            }

            return box;
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
                FontSize = 18,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeManager.Brush("TextBrush"),
                Margin = new Thickness(0, 0, 0, 14),
            };
            // THE COUNT IN BOLD. It is the one number in the sentence and the whole reason he is
            // being asked - six QSOs is a different decision from four hundred.
            AiPayHelpWindow.AddMarked(what, lines);
            root.Children.Add(what);

            // THE MODEL IS PART OF THE ANSWER, so it is named here and can be changed here.
            //
            // This dialog said which SERVICE would be asked and never which model - and the model is
            // what actually answers: the same six QSOs came back 5-1 from one and 4-2 from another,
            // both through OpenRouter. Naming the service alone told him almost nothing, and made
            // him close the window and go to Options to change the one thing he wanted to change.
            var panel = new AiServicePanel(showModel: true, compact: true);
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

            // CENTRED, NOT PUSHED INTO THE CORNER. This dialog is one short question with two
            // answers; a pair of buttons hard against the right edge of a nearly empty window reads
            // as an afterthought rather than the point of it.
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0),
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
                // "or pick a service that already has one" was true while there were two services.
                // There is one, so that half of the sentence sent him looking for a dropdown that is
                // no longer on the screen.
                _warning.Text = "Press Show me how to set this up, or set the key in "
                              + "Options > AI Service.";
        }
    }
}
