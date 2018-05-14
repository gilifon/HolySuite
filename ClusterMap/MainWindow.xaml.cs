using HolyParser;
using System;
using System.Collections.Generic;
using System.Device.Location;
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
        public const bool IS_DEBUG = false;
        public const double RADIUS = 500;
        public const double PIXLES_PER_DISTANCE = 0.03;// RADIUS / 15000f;
        public double lat { get; set; }
        public double lng { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            HamQTH country = Services.getHamQth("PP5DZ");
            lat = 32.270068;
            lng = 35.080606;
            LoadDefaultMap();
        }

        private void LoadDefaultMap()
        {
            string FILE_PATH_PDF = Path.GetTempPath() + "map.pdf";
            string FILE_PATH_TIFF = Path.GetTempPath() + "map.tif";
            string FILE_PATH_PNG = Path.GetTempPath() + "map.png";
            string DOWNLOADER_URI = "https://ns6t.net/azimuth/code/azimuth.fcgi?title=&location=" + lat + "%2C" + lng + "&distance=15000&paper=LETTER&bluefill=on&view=on&submit=&iplocationused=false";
            //const string DOWNLOADER_URI = "https://ns6t.net/azimuth/code/azimuth.fcgi?title=&location=0%2C+0&distance=15000&paper=LETTER&bluefill=on&view=on&submit=&iplocationused=false";
            if (IS_DEBUG || !File.Exists(FILE_PATH_PNG))
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

                    MapPanel.Stretch = System.Windows.Media.Stretch.Uniform;

                    //Rectangle cropRect = new Rectangle(0, 145, 815, 760);
                    Rectangle cropRect = new Rectangle(30, 150, 755, 755);
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

            double destination_Lat = -42.438317;
            double destination_Lng = 147.261328;

            double distance = Utils.DistanceBetweenPlaces(lat, lng, destination_Lat, destination_Lng);
            Tuple<double,double> bearing = Utils.BearingVectors(lat, lng, destination_Lat, destination_Lng);
            double lat_vect = distance * bearing.Item1;
            double lng_vect = distance * bearing.Item2;
            PaintPoint(lat_vect, lng_vect);
        }

        private void PaintPoint(double lat, double lng)
        {
            System.Windows.Shapes.Ellipse point = new System.Windows.Shapes.Ellipse();
            point.Stroke = System.Windows.Media.Brushes.Red;
            point.Fill = System.Windows.Media.Brushes.Red;
            point.StrokeThickness = 1;
            point.Width = 4;
            point.Height = 4;
            System.Windows.Controls.Canvas.SetLeft(point, (MapPanel.Source.Width / 2) + lng2pix(lng) - point.Width / 2);
            System.Windows.Controls.Canvas.SetTop(point, (MapPanel.Source.Height / 2) - lat2pix(lat) - point.Height / 2);

            MapContainer.Children.Add(point);
        }

        private double lng2pix(double value)
        {
            return value * PIXLES_PER_DISTANCE;
        }
        private double lat2pix(double value)
        {
            return value * PIXLES_PER_DISTANCE;
        }
    }
   
}