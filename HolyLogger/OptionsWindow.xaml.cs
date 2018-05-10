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
        protected override void OnLocationChanged(EventArgs e)
        {
            if (this.Left >= 0)
                Properties.Settings.Default.OptionsWindowLeft = this.Left;
            if (this.Top >= 0)
                Properties.Settings.Default.OptionsWindowTop = this.Top;
            base.OnLocationChanged(e);
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

        private void HideAllControls()
        {
            QRZServiceControlInstance.Visibility = Visibility.Hidden;
            UserInterfaceControlInstance.Visibility = Visibility.Hidden;
            GeneralSettingsControlControlInstance.Visibility = Visibility.Hidden;
        }
    }
}
