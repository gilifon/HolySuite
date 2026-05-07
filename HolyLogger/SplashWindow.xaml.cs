using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace HolyLogger
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();

            // Load the splash image from resources
            var uri = new System.Uri("pack://application:,,,/HolyLogger;component/Images/splash.png");
            SplashImage.Source = new BitmapImage(uri);

            // Set version text from assembly — single source of truth, never stale
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        }
    }
}
