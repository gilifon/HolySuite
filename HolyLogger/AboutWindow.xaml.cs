using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
            Left = (System.Windows.SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (System.Windows.SystemParameters.PrimaryScreenHeight - Height) / 2;
            L_Version.Text = "Version " + Assembly.GetExecutingAssembly().GetName().Version.ToString();
            L_LastUpdate.Text = "Last Update: " + GetLinkerDateTime(Assembly.GetExecutingAssembly()).ToShortDateString();
        }

        public int kuku()
        {
            return 1;
        }

        public static DateTime GetLinkerDateTime(Assembly assembly, TimeZoneInfo tzi = null)
        {
            // Constants related to the Windows PE file format.
            const int PE_HEADER_OFFSET = 60;
            const int LINKER_TIMESTAMP_OFFSET = 8;

            // Discover the base memory address where our assembly is loaded
            var entryModule = assembly.ManifestModule;
            var hMod = Marshal.GetHINSTANCE(entryModule);
            if (hMod == IntPtr.Zero - 1) throw new Exception("Failed to get HINSTANCE.");

            // Read the linker timestamp
            var offset = Marshal.ReadInt32(hMod, PE_HEADER_OFFSET);
            var secondsSince1970 = Marshal.ReadInt32(hMod, offset + LINKER_TIMESTAMP_OFFSET);

            // Convert the timestamp to a DateTime
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var linkTimeUtc = epoch.AddSeconds(secondsSince1970);
            var dt = TimeZoneInfo.ConvertTimeFromUtc(linkTimeUtc, tzi ?? TimeZoneInfo.Local);
            return dt;
        }
    }
}

