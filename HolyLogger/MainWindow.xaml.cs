using System;
using System.Collections.Generic;
using System.IO;
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
using System.Data.SQLite;

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
            TB_MyCallsign.Focus();

            Left = (System.Windows.SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (System.Windows.SystemParameters.PrimaryScreenHeight - Height) / 2; 

            //RefreshDateTime_Btn_MouseUp(null, null);
            try
            {
                //string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
                //string path = (System.IO.Path.GetDirectoryName(executable));
                //AppDomain.CurrentDomain.SetData("DataDirectory", path);

                SQLiteConnection con = new SQLiteConnection(@"Data Source = C:\Users\gill\Source\Repos\HolySuite\HolyLogger\Data\logDB.db;Version=3");
                con.Open();
            }
            catch (Exception e)
            {

                MessageBox.Show("Failed to open DB");
            }
            

        }

        private void Lock_Btn_MouseUp(object sender, MouseButtonEventArgs e)
        {
            TB_MyCallsign.IsEnabled = !(TB_MyCallsign.IsEnabled);
            TB_MyGrid.IsEnabled = !(TB_MyGrid.IsEnabled);
            TB_Frequency.IsEnabled = !(TB_Frequency.IsEnabled);
            if (TB_MyGrid.IsEnabled) ((Image)sender).Opacity = 1;
            else ((Image)sender).Opacity = 0.5;
        }

        private void RefreshDateTime_Btn_MouseUp(object sender, MouseButtonEventArgs e)
        {
            DatePicker_QsoDate.SelectedDate = DateTime.Today;
            TB_QsoTime.Text = DateTime.Now.ToLongTimeString();
        }

        private void AddBtn_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("saved");
            ClearBtn_Click(null, null);
        }

        private void QRZBtn_Click(object sender, RoutedEventArgs e)
        {
            string url = "http://www.qrz.com";
            if (!string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
                url += "/db/" + TB_DXCallsign.Text;

            System.Diagnostics.Process.Start(@"chrome.exe", url);
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            //TB_Frequency.Text = string.Empty;
            TB_DXCallsign.Text = string.Empty;
            TB_Exchange.Text = string.Empty;
            TB_RSTSent.Text = "59";
            TB_RSTRcvd.Text = "59";
            RefreshDateTime_Btn_MouseUp(null, null);
            TB_DXCallsign.Focus();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F1)
            {
                AddBtn_Click(null, null);
            }
            else if (e.Key == Key.F5)
            {
                ClearBtn_Click(null, null);
            }
        }

        private void RST_GotFocus(object sender, RoutedEventArgs e)
        {
            if (((TextBox)sender).Text.Length > 0)
            {
                ((TextBox)sender).CaretIndex = 1;
                ((TextBox)sender).SelectionLength = 1;
            }
        }
      
    }
}
