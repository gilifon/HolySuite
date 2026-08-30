using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for PropertiesWindow.xaml
    /// </summary>
    public partial class OptionsWindow : Window
    {
        public OptionsWindow()
        {
            InitializeComponent();

            // This window and its ~10 OptionsUserControls panels were never migrated to dark mode --
            // every color in them is hardcoded for a light background (confirmed: zero DynamicResource
            // usage in any of those files). Setting this window's own Background="White" (in XAML)
            // isn't enough on its own: every plain TextBox/Button/Label still resolves its Background/
            // Foreground from the app-wide implicit Style in Themes/Controls.xaml, which points at the
            // *current* theme's tokens, not this window's. Locking every token to its light-mode value
            // in THIS window's own Resources makes every DynamicResource lookup inside it find the
            // light value first (closer in the tree than Application.Resources), so the whole subtree
            // renders in light mode regardless of the app's current theme -- without editing 10 files.
            foreach (var kv in ThemePalette.Tokens)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value[ThemePalette.FindScheme("light").Column]));
                brush.Freeze();
                Resources[kv.Key] = brush;
            }
            // The TreeView's default background resolves through these SystemColors keys, which
            // ThemeManager repoints at the theme's (dark) menu surface app-wide for combo-box popups;
            // revert them locally so the left-hand navigation tree isn't dark-on-dark.
            Resources[SystemColors.WindowBrushKey] = Brushes.White;
            Resources[SystemColors.WindowFrameBrushKey] = SystemColors.ActiveBorderBrush;

            GeneralItem.IsSelected = true;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Never open taller than the screen's work area. On a low-resolution screen or with
            // Windows display scaling at 125/150%, the fixed 750px height exceeded the visible
            // area and the bottom controls were clipped off the screen edge. Shrink to fit; the
            // ScrollViewer then exposes a scrollbar so nothing is unreachable. Also nudge the
            // window up if it would hang off the bottom.
            var work = SystemParameters.WorkArea;
            if (Height > work.Height)
                Height = work.Height;
            if (!double.IsNaN(Top) && Top + Height > work.Bottom)
                Top = Math.Max(work.Top, work.Bottom - Height);
        }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Make sure any eQSL account edit still in progress (typed but not yet committed) is saved.
            if (EqslServiceControlInstance != null) EqslServiceControlInstance.SaveAll();
            // Same for a band frequency typed on the Radio Control Panel page and not tabbed away from.
            if (RadioControlPanelControlInstance != null) RadioControlPanelControlInstance.SaveAll();
            // And the General page, which holds three port numbers. An operator changed his UDP port,
            // closed this window with the X while the caret was still in the box, and the port was back
            // to what it had been - the box had never lost focus, so the value never left it.
            if (GeneralSettingsControlControlInstance != null) GeneralSettingsControlControlInstance.SaveAll();
            // And the AI model box, for the same reason: it had a Save button of its own because
            // nothing here committed it, and a model chosen and not saved was a model ignored. Saved on
            // the way out now, like everything else on these pages - so the button could go.
            if (_aiPanel != null) _aiPanel.CommitModel();
            base.OnClosing(e);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            if (this.Left >= 0)
                Properties.Settings.Default.OptionsWindowLeft = this.Left;
            if (this.Top >= 0)
                Properties.Settings.Default.OptionsWindowTop = this.Top;
            base.OnLocationChanged(e);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) this.Close();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (this.Width >= 0)
                Properties.Settings.Default.OptionsWindowWidth = this.Width;
            if (this.Height >= 0)
                Properties.Settings.Default.OptionsWindowHeight = this.Height;
        }

        private void GeneralItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            GeneralSettingsControlControlInstance.Visibility = Visibility.Visible;
        }
        private void UserInterfaceItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            UserInterfaceControlInstance.Visibility = Visibility.Visible;
        }
        private void QRZServicesItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            QRZServicesControlInstance.Visibility = Visibility.Visible;
        }
        private void EqslServiceItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            // Reload so any callsign logged since the window opened shows up in the accounts table.
            EqslServiceControlInstance.LoadAccounts();
            EqslServiceControlInstance.Visibility = Visibility.Visible;
        }
        private void ClublogServiceItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            ClublogServiceControlInstance.Visibility = Visibility.Visible;
        }
        private void LotwItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            LotwControlInstance.Visibility = Visibility.Visible;
        }
        private void ImportItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            ImportControlInstance.Visibility = Visibility.Visible;
        }
        private void SatelliteItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            SatelliteControlInstance.Visibility = Visibility.Visible;
        }
        private void PersonalInfoItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            PersonalInfoControlInstance.Reload();   // pick up a callsign changed since the window opened
            PersonalInfoControlInstance.Visibility = Visibility.Visible;
        }

        //ImportItem_Selected

        // WHERE THE AI IS CHOSEN WHEN NOBODY IS IN THE MIDDLE OF USING IT.
        //
        // Until now the only way to pick a service or paste a key was to be standing in front of a
        // question - the check window on one QSO, or the box the Log Fixer puts up when it finds no
        // key. An operator who simply wants to see which service he is on, or move from the free
        // allowance to the paid one before he starts, had nowhere to go. This is that place.
        //
        // Built the first time the page is opened, not when the window is: it costs a web call to
        // show what credit is left, and Options is opened for a dozen reasons that are not this one.
        private AiServicePanel _aiPanel;

        private void AiItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();

            if (_aiPanel == null)
            {
                var heading = new TextBlock
                {
                    Text = "Setting up an AI service",
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4),
                };
                heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                AiHostInstance.Children.Add(heading);

                var note = new TextBlock
                {
                    // HIS OWN WORDS, 2026-08-27. Written as an operator hears it: what the thing
                    // is, who provides it, what it costs, and the one fear worth answering before
                    // he types a card number - that nothing is kept and nothing is charged twice.
                    // TWO SERVICES AGAIN, so the paragraph names both and says which is which - a
                    // page that talks only about paying, above a box offering a free one, sends
                    // somebody for a credit card he does not need.
                    Text = "You can set an AI service that will help you to check your QSOs. There "
                         + "are two, and you choose below." + Environment.NewLine + Environment.NewLine
                         + "Google Gemini is free and asks for no credit card. Its allowance is small "
                         + "— around twenty checks a day — and it starts again the next day."
                         + Environment.NewLine + Environment.NewLine
                         + "OpenRouter is paid and reaches GPT, Claude and Gemini, and it answers "
                         + "better. You create a credit by paying with your credit card, which will "
                         + "not be saved and will not be charged again if you use the whole credit "
                         + "amount. Asking the AI to verify 10 QSOs in one question will cost a few "
                         + "cents." + Environment.NewLine + Environment.NewLine
                         + "An API key will be saved on this computer.",
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85,
                    Margin = new Thickness(0, 0, 0, 12),
                };
                note.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                AiHostInstance.Children.Add(note);

                _aiPanel = new AiServicePanel(showModel: true);
                AiHostInstance.Children.Add(_aiPanel);
            }
            else
            {
                // The key may have been pasted, or the service changed, in one of the other windows
                // since this page was last looked at.
                _aiPanel.Refresh();
            }

            AiHostInstance.Visibility = Visibility.Visible;
        }

        private void RadioPanelItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            RadioControlPanelControlInstance.Visibility = Visibility.Visible;
        }

        private void HideAllControls()
        {
            AiHostInstance.Visibility = Visibility.Hidden;
            QRZServicesControlInstance.Visibility = Visibility.Hidden;
            EqslServiceControlInstance.Visibility = Visibility.Hidden;
            ClublogServiceControlInstance.Visibility = Visibility.Hidden;
            LotwControlInstance.Visibility = Visibility.Hidden;
            UserInterfaceControlInstance.Visibility = Visibility.Hidden;
            GeneralSettingsControlControlInstance.Visibility = Visibility.Hidden;
            ImportControlInstance.Visibility = Visibility.Hidden;
            SatelliteControlInstance.Visibility = Visibility.Hidden;
            PersonalInfoControlInstance.Visibility = Visibility.Hidden;
            RadioControlPanelControlInstance.Visibility = Visibility.Hidden;
        }

        /// <summary>
        /// Refreshes the cluster settings display in the User Interface tab.
        /// Call this when cluster settings are changed externally.
        /// </summary>
        public void RefreshClusterSettings()
        {
            UserInterfaceControlInstance?.RefreshClusterSettings();
        }
    }
}
