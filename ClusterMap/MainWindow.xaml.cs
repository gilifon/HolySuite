using Apitron.PDF.Rasterizer;
using Apitron.PDF.Rasterizer.Configuration;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ClusterMap
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            LoadDefaultMap();
        }

        private void LoadDefaultMap()
        {
            string FILE_PATH = Path.GetTempPath() + "map.pdf";
            const string DOWNLOADER_URI = "https://ns6t.net/azimuth/code/azimuth.fcgi?title=&location=32.0917%2C+34.885&distance=15000&paper=LETTER&bluefill=on&view=on&submit=&iplocationused=false";

            using (var writeStream = File.OpenWrite(FILE_PATH))
            {
                var httpRequest = WebRequest.Create(DOWNLOADER_URI) as HttpWebRequest;
                var httpResponse = httpRequest.GetResponse();
                httpResponse.GetResponseStream().CopyTo(writeStream);
                writeStream.Close();

                using (FileStream fs = new FileStream(FILE_PATH, FileMode.Open))
                {
                    Document document = new Document(fs);
                    Page currentPage = document.Pages[0];
                    RenderingSettings settings = new RenderingSettings();

                    // we use original page's width and height for image as well as default rendering settings
                    using (Bitmap bitmap = currentPage.Render((int)currentPage.Width, (int)currentPage.Height, settings))
                    {
                        bitmap.Save(Path.GetTempPath() + "map.png", ImageFormat.Png);
                        MapPanel.Stretch = System.Windows.Media.Stretch.Uniform;
                        MapPanel.Source = new BitmapImage(new System.Uri(Path.GetTempPath() + "map.png"));
                    }
                    fs.Close();
                }
            }
        }
    }
}