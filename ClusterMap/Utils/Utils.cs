using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ClusterMap
{
    static class Utils
    {
        public static Tuple<double,double> BearingVectors(double slat, double slng, double dlat, double dlng)
        {
            double y = Math.Sin(ToRad(dlng - slng)) * Math.Cos(ToRad(dlat));
            double x = Math.Cos(ToRad(slat)) * Math.Sin(ToRad(dlat)) - Math.Sin(ToRad(slat)) * Math.Cos(ToRad(dlat)) * Math.Cos(ToRad(dlng - slng));
            return new Tuple<double, double>(x, y);
        }

        public static double ToRad(double degrees)
        {
            return degrees * (Math.PI / 180);
        }

        public static double ToDegrees(double radians)
        {
            return radians * 180 / Math.PI;
        }

        public static double ToBearing(double radians)
        {
            // convert radians to degrees (as bearing: 0...360)
            return (ToDegrees(radians) + 360) % 360;
        }

        public static double DistanceBetweenPlaces(double slat, double slng, double dlat, double dlng)
        {
            double R = 6371; // km

            double sLat1 = Math.Sin(ToRad(slat));
            double sLat2 = Math.Sin(ToRad(dlat));
            double cLat1 = Math.Cos(ToRad(slat));
            double cLat2 = Math.Cos(ToRad(dlat));
            double cLon = Math.Cos(ToRad(slng) - ToRad(dlng));

            double cosD = sLat1 * sLat2 + cLat1 * cLat2 * cLon;

            double d = Math.Acos(cosD);

            double dist = R * d;

            return dist;
        }
        public static double HaversineDistanceBetweenPlaces(double slat, double slng, double dlat, double dlng)
        {
            double R = 6371; // km
            double a = Math.Pow(Math.Sin((ToRad(dlat) - ToRad(slat)) / 2), 2) + Math.Cos(ToRad(slat)) * Math.Cos(ToRad(dlat)) * Math.Pow(Math.Sin((ToRad(dlng) - ToRad(slng)) / 2), 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }
    }
}
