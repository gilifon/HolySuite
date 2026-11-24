using System.Windows;
using System.Windows.Controls;

namespace HolyLogger
{
    /// <summary>
    /// The Split button's settings on their own, in a small window: opened by right-clicking SPLIT on
    /// the Radio Control Panel, so the settings are one click away without the whole Options window
    /// (which is slow to open). It holds the same control as the Radio Control Panel page of Options,
    /// and both save straight to the settings on every edit, so they always agree.
    /// </summary>
    public sealed class SplitSettingsWindow : Window
    {
        private static SplitSettingsWindow _open;

        /// <summary>Shows the window, or brings forward the one already open.</summary>
        public static void ShowFor(Window owner)
        {
            if (_open != null)
            {
                if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
                _open.Activate();
                return;
            }
            _open = new SplitSettingsWindow { Owner = owner };
            _open.Closed += (s, e) => _open = null;
            _open.Show();
        }

        protected override void OnLocationChanged(System.EventArgs e)
        {
            base.OnLocationChanged(e);
            if (WindowState != WindowState.Normal || double.IsNaN(Left) || double.IsNaN(Top)) return;
            Properties.Settings.Default.SplitWindowLeft = Left;
            Properties.Settings.Default.SplitWindowTop = Top;
            SettingsFlush.RequestSave();   // the debounced save every other window uses
        }

        private SplitSettingsWindow()
        {
            Title = "Split button configuration";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBg");
            SetResourceReference(ForegroundProperty, "TextBrush");

            // Back where it was left - checked against every screen, so a monitor that is gone does not
            // strand it (the same care the Radio Control Panel takes).
            double left = Properties.Settings.Default.SplitWindowLeft;
            double top = Properties.Settings.Default.SplitWindowTop;
            if (left > -50000 && top > -50000 && RadioControlPanelWindow.IsOnAScreen(left, top))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }

            var host = new Border { Padding = new Thickness(12) };
            host.Child = new OptionsUserControls.SplitSettingsControl();
            Content = host;
        }
    }
}
