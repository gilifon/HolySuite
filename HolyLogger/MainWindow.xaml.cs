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
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }
        
        private void Lock_Btn_MouseUp(object sender, MouseButtonEventArgs e)
        {
            TB_MyCallsign.IsEnabled = !(TB_MyCallsign.IsEnabled);
            TB_MyGrid.IsEnabled = !(TB_MyGrid.IsEnabled);
            if (TB_MyGrid.IsEnabled) ((Image)sender).Opacity = 1;
            else ((Image)sender).Opacity = 0.5;
        }

        private void RefreshDateTime_Btn_MouseUp(object sender, MouseButtonEventArgs e)
        {
            DatePicker_QsoDate.SelectedDate = DateTime.Now.Date;
            TB_QsoTime.Text = DateTime.Now.ToLongTimeString();
        }
    }
}
