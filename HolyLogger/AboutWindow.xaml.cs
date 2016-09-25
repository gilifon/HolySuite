using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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
    /// Interaction logic for AboutWindow.xaml
    /// </summary>
    public partial class AboutWindow : Window
    {
        Mutex myMutex;
        public AboutWindow()
        {
            

            InitializeComponent();
        }

        private void Window_About_Loaded(object sender, RoutedEventArgs e)
        {
            bool aIsNewInstance = false;
            myMutex = new Mutex(true, "AboutWindow", out aIsNewInstance);
            if (!aIsNewInstance)
            {
                this.Close();
            }
        }
    }
}
