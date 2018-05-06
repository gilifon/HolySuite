using Apitron.PDF.Rasterizer;
using Apitron.PDF.Rasterizer.Configuration;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using System;
using System.Collections.Generic;
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

                //using (FileStream fs = new FileStream(FILE_PATH, FileMode.Open))
                //{
                //Document document = new Document(fs);
                //Page currentPage = document.Pages[0];
                //RenderingSettings settings = new RenderingSettings();

                //// we use original page's width and height for image as well as default rendering settings
                //using (Bitmap bitmap = currentPage.Render((int)currentPage.Width, (int)currentPage.Height, settings))
                //{
                //    bitmap.Save(Path.GetTempPath() + "map.png", ImageFormat.Png);
                //    MapPanel.Stretch = System.Windows.Media.Stretch.Uniform;
                //    MapPanel.Source = new BitmapImage(new System.Uri(Path.GetTempPath() + "map.png"));
                //}


                using (PdfDocument document = PdfReader.Open(FILE_PATH))
                {

                    int imageCount = 0;
                    // Iterate pages
                    foreach (PdfPage page in document.Pages)
                    {
                        // Get resources dictionary
                        PdfDictionary resources = page.Elements.GetDictionary("/Resources");
                        if (resources != null)
                        {
                            // Get external objects dictionary
                            PdfDictionary xObjects = resources.Elements.GetDictionary("/XObject");
                            if (xObjects != null)
                            {
                                ICollection<PdfItem> items = xObjects.Elements.Values;
                                // Iterate references to external objects
                                foreach (PdfItem item in items)
                                {
                                    PdfReference reference = item as PdfReference;
                                    if (reference != null)
                                    {
                                        PdfDictionary xObject = reference.Value as PdfDictionary;
                                        // Is external object an image?
                                        if (xObject != null && xObject.Elements.GetString("/Subtype") == "/Image")
                                        {
                                            ExportImage(xObject, ref imageCount);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    //fs.Close();
                    document.Close();
                }
            }
        }
        

        static void ExportImage(PdfDictionary image, ref int count)
        {

            string filter = image.Elements.GetName("/Filter");

            switch (filter)
            {
                case "/DCTDecode":
                    ExportJpegImage(image, ref count);
                    break;
            }
        }
        static void ExportJpegImage(PdfDictionary image, ref int count)
        {
            // Fortunately JPEG has native support in PDF and exporting an image is just writing the stream to a file.
            byte[] stream = image.Stream.Value;
            FileStream fs = new FileStream(string.Format("Image{0}.jpeg", count++), FileMode.Create, FileAccess.Write);
            BinaryWriter bw = new BinaryWriter(fs);
            bw.Write(stream);
            bw.Close();
        }
    }
}