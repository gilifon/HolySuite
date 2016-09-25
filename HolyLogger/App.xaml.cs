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
        public Mutex Mutex;
        public App()
        {
            bool isOnlyInstance = false;
            Mutex = new Mutex(true, @"MarkdownMonster", out isOnlyInstance);
            if (!isOnlyInstance)
            {
                Environment.Exit(0);
            }
        }
    }
}
