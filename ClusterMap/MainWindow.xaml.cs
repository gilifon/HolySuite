using ClusterMap.Utils;
using HolyParser;
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
            HamQTH country = Services.getHamQth("dl2nd");
        }

        private void LoadDefaultMap()
        {
            string FILE_PATH_PDF = Path.GetTempPath() + "map.pdf";
            string FILE_PATH_TIFF = Path.GetTempPath() + "map.tif";
            string FILE_PATH_PNG = Path.GetTempPath() + "map.png";
            const string DOWNLOADER_URI = "https://ns6t.net/azimuth/code/azimuth.fcgi?title=&location=32.0917%2C+34.885&distance=15000&paper=LETTER&bluefill=on&view=on&submit=&iplocationused=false";
            if (!File.Exists(FILE_PATH_PNG))
            {
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

                    MapPanel.Stretch = System.Windows.Media.Stretch.Fill;

                    Rectangle cropRect = new Rectangle(0, 145, 815, 760);
                    Bitmap src = Image.FromFile(FILE_PATH_TIFF) as Bitmap;
                    Bitmap target = new Bitmap(cropRect.Width, cropRect.Height);

                    using (Graphics g = Graphics.FromImage(target))
                    {
                        g.DrawImage(src, new Rectangle(0, 0, target.Width, target.Height),
                                         cropRect,
                                         GraphicsUnit.Pixel);
                        target.Save(FILE_PATH_PNG, ImageFormat.Png);
                    }
                }
            }
            MapPanel.Source = new BitmapImage(new System.Uri(FILE_PATH_PNG));
        }
    }
}