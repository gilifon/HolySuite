using ClusterMap.Utils;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Media.Imaging;
using WebSupergoo.ABCpdf11;

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
            string FILE_PATH_PDF = Path.GetTempPath() + "map.pdf";
            string FILE_PATH_TIFF = Path.GetTempPath() + "map.tif";
            const string DOWNLOADER_URI = "https://ns6t.net/azimuth/code/azimuth.fcgi?title=&location=32.0917%2C+34.885&distance=15000&paper=LETTER&bluefill=on&view=on&submit=&iplocationused=false";

            using (var writeStream = File.OpenWrite(FILE_PATH_PDF))
            {
                var httpRequest = WebRequest.Create(DOWNLOADER_URI) as HttpWebRequest;
                var httpResponse = httpRequest.GetResponse();
                httpResponse.GetResponseStream().CopyTo(writeStream);
                writeStream.Close();
                Doc theDoc = new Doc();
                theDoc.Read(FILE_PATH_PDF);
                // set up the rendering parameters
                theDoc.Rendering.ColorSpace = XRendering.ColorSpaceType.Rgb;
                theDoc.Rendering.BitsPerChannel = 8;
                theDoc.Rendering.DotsPerInchX = 96;
                theDoc.Rendering.DotsPerInchY = 96;
                // loop through the pages
                int n = theDoc.PageCount;
                for (int i = 1; i <= n; i++)
                {
                    theDoc.PageNumber = i;
                    theDoc.Rect.String = theDoc.CropBox.String;
                    theDoc.Rendering.SaveAppend = (i != 1);
                    //theDoc.Rendering.SaveCompression = XRendering.Compression.G4;
                    theDoc.SetInfo(0, "ImageCompression", "4");
                    theDoc.Rendering.Save(FILE_PATH_TIFF);
                }
                theDoc.Clear();

                //TiffImage myTiff = new TiffImage(FILE_PATH_TIFF);
                //imageBox is a PictureBox control, and the [] operators pass back
                //the Bitmap stored at that position in the myImages ArrayList in the TiffImage

                MapPanel.Stretch = System.Windows.Media.Stretch.Uniform;
                MapPanel.Source = new BitmapImage(new System.Uri(FILE_PATH_TIFF));

                //using (Bitmap bitmap = (Bitmap)myTiff.myImages[0])
                //{
                //    bitmap.Save(Path.GetTempPath() + "map.png", ImageFormat.Png);
                //    MapPanel.Stretch = System.Windows.Media.Stretch.Uniform;
                //    MapPanel.Source = new BitmapImage(new System.Uri(Path.GetTempPath() + "map.png"));
                //}
            }
        }
    }
}