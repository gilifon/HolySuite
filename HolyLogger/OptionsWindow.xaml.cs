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
            GeneralItem.IsSelected = true;
        }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Make sure any eQSL account edit still in progress (typed but not yet committed) is saved.
            if (EqslServiceControlInstance != null) EqslServiceControlInstance.SaveAll();
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
        private void QRZServiceItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            QRZServiceControlInstance.Visibility = Visibility.Visible;
        }
        private void QRZLogbookItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            QRZLogbookControlInstance.Visibility = Visibility.Visible;
        }
        private void EqslServiceItem_Selected(object sender, RoutedEventArgs e)
        {
            HideAllControls();
            // Reload so any callsign logged since the window opened shows up in the accounts table.
            EqslServiceControlInstance.LoadAccounts();
            EqslServiceControlInstance.Visibility = Visibility.Visible;
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

        //ImportItem_Selected

        private void HideAllControls()
        {
            QRZServiceControlInstance.Visibility = Visibility.Hidden;
            QRZLogbookControlInstance.Visibility = Visibility.Hidden;
            EqslServiceControlInstance.Visibility = Visibility.Hidden;
            UserInterfaceControlInstance.Visibility = Visibility.Hidden;
            GeneralSettingsControlControlInstance.Visibility = Visibility.Hidden;
            ImportControlInstance.Visibility = Visibility.Hidden;
            SatelliteControlInstance.Visibility = Visibility.Hidden;
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
