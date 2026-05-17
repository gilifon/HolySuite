using System.Reflection;
using System.Windows;
using System.Windows.Media;
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

            var timerUri = new System.Uri("pack://application:,,,/HolyLogger;component/Images/timer.png");
            var timerSource = new BitmapImage(timerUri);
            TimerIcon.Source = CreateWhiteIcon(timerSource);

            // Set version text from assembly — single source of truth, never stale
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        }

        private static BitmapSource CreateWhiteIcon(BitmapSource source)
        {
            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var width = formatted.PixelWidth;
            var height = formatted.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];

            formatted.CopyPixels(pixels, stride, 0);

            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] == 0)
                    continue;

                pixels[i + 0] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
            }

            return BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        }
    }
}
