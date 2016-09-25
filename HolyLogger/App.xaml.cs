using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        Mutex myMutex;
        public App()
        {
     
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            bool aIsNewInstance = false;
            myMutex = new Mutex(true, "HolyLoggerApplication", out aIsNewInstance);
            if (!aIsNewInstance)
            {
                MessageBox.Show("Holyland logger is already open...");
                App.Current.Shutdown();
            }
        }
    }
}
