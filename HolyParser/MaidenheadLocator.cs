using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HolyParser
{
    public static class MaidenheadLocator
    {
        /// <summary>
        /// The shape a locator is allowed to have, in ONE place, because the program used to hold
        /// the same rule in four: this class, the Log Verifier, the cluster's comment scraper and
        /// the sentences shown to the operator - and the sentences had drifted away from the test.
        ///
        /// Pairs, always. Field (2 letters A-R) and square (2 digits) are required; subsquare
        /// (2 letters A-X), extended square (2 digits) and extended subsquare (2 letters A-X) are
        /// each optional but only in that order. So 4, 6, 8 or 10 characters - never 5, 7 or 9,
        /// and never fewer than 4. The four branches of LocatorToLatLng below are those same four
        /// lengths; anything asking "is this a locator?" should ask here rather than write the
        /// pattern out again.
        /// </summary>
        public static readonly Regex Legal =
            new Regex("^[A-R]{2}[0-9]{2}(?:[A-X]{2}(?:[0-9]{2}(?:[A-X]{2})?)?)?$",
                      RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Shortest and longest a locator may be. Useful for a text box's MaxLength.</summary>
        public const int MinLength = 4;
        public const int MaxLength = 10;

        /// <summary>
        /// What to tell the operator when what they typed is not a locator - the long form, for a
        /// dialog that has room for it. Kept next to Legal on purpose: a message that describes the
        /// format has to change in the same edit as the format itself.
        /// </summary>
        public const string FormatHint =
            "Use 2 letters + 2 digits (e.g. KM72). Two more letters (KM72OR), then two more digits " +
            "(KM72OR12), then two more letters (KM72OR12AB) can be added for extra precision - so a " +
            "locator is 4, 6, 8 or 10 characters, always added in pairs, never 5, 7 or 9. " +
            "The 1st/2nd characters are letters A-R, the 5th/6th and 9th/10th are letters A-X " +
            "(e.g. O), not zeros (0).";

        /// <summary>
        /// The same rule in one line, for a table cell or a placeholder with no room for the long form.
        /// </summary>
        public const string ShortFormatHint =
            "2 letters and 2 digits, then optional pairs - KM72, KM72OR, KM72OR12 or KM72OR12AB";

        /// <summary>
        /// Convert a locator to latitude and longitude in degrees
        /// </summary>
        /// <param name="locator">Locator string to convert</param>
        /// <returns>LatLng structure</returns>
        public static LatLng LocatorToLatLng(string locator)
        {
            locator = locator.Trim().ToUpper();
            if (Regex.IsMatch(locator, "^[A-R]{2}[0-9]{2}$"))
            {
                LatLng ll = new LatLng();
                ll.Long = (locator[0] - 'A') * 20 + (locator[2] - '0' + 0.5) * 2 - 180;
                ll.Lat = (locator[1] - 'A') * 10 + (locator[3] - '0' + 0.5) - 90;
                return ll;
            }
            else if (Regex.IsMatch(locator, "^[A-R]{2}[0-9]{2}[A-X]{2}$"))
            {
                LatLng ll = new LatLng();
                ll.Long = (locator[0] - 'A') * 20 + (locator[2] - '0') * 2 + (locator[4] - 'A' + 0.5) / 12 - 180;
                ll.Lat = (locator[1] - 'A') * 10 + (locator[3] - '0') + (locator[5] - 'A' + 0.5) / 24 - 90;
                return ll;
            }
            else if (Regex.IsMatch(locator, "^[A-R]{2}[0-9]{2}[A-X]{2}[0-9]{2}$"))
            {
                LatLng ll = new LatLng();
                ll.Long = (locator[0] - 'A') * 20 + (locator[2] - '0') * 2 + (locator[4] - 'A' + 0.0) / 12 + (locator[6] - '0' + 0.5) / 120 - 180;
                ll.Lat = (locator[1] - 'A') * 10 + (locator[3] - '0') + (locator[5] - 'A' + 0.0) / 24 + (locator[7] - '0' + 0.5) / 240 - 90;
                return ll;
            }
            else if (Regex.IsMatch(locator, "^[A-R]{2}[0-9]{2}[A-X]{2}[0-9]{2}[A-X]{2}$"))
            {
                LatLng ll = new LatLng();
                ll.Long = (locator[0] - 'A') * 20 + (locator[2] - '0') * 2 + (locator[4] - 'A' + 0.0) / 12 + (locator[6] - '0' + 0.0) / 120 + (locator[8] - 'A' + 0.5) / 120 / 24 - 180;
                ll.Lat = (locator[1] - 'A') * 10 + (locator[3] - '0') + (locator[5] - 'A' + 0.0) / 24 + (locator[7] - '0' + 0.0) / 240 + (locator[9] - 'A' + 0.5) / 240 / 24 - 90;
                return ll;
            }
            else
            {
                throw new FormatException("Invalid locator format");
            }
        }

        /// <summary>
        /// Returns true if the supplied string is a valid Maidenhead locator
        /// (i.e. LocatorToLatLng would parse it without throwing).
        /// </summary>
        /// <param name="locator">Locator string to validate</param>
        /// <returns>True if the locator is well-formed, otherwise false</returns>
        public static bool IsValidLocator(string locator)
        {
            if (string.IsNullOrWhiteSpace(locator))
                return false;

            // Asked of Legal rather than by calling LocatorToLatLng and catching: the question is
            // "is this the right shape", which is what Legal answers, and a wrong locator is then a
            // false rather than a thrown exception - this is called once per QSO over a whole log.
            // Trimmed and case-insensitive, exactly as LocatorToLatLng treats its argument.
            return Legal.IsMatch(locator.Trim());
        }

        /// <summary>
        /// Convert latitude and longitude in degrees to a locator
        /// </summary>
        /// <param name="ll">LatLng structure to convert</param>
        /// <returns>Locator string</returns>
        public static string LatLngToLocator(LatLng ll)
        {
            return LatLngToLocator(ll.Lat, ll.Long, 0);
        }

        /// <summary>
        /// Convert latitude and longitude in degrees to a locator
        /// </summary>
        /// <param name="ll">LatLng structure to convert</param>
        /// <param name="Ext">Extra precision (0, 1, 2)</param>
        /// <returns>Locator string</returns>
        public static string LatLngToLocator(LatLng ll, int Ext)
        {
            return LatLngToLocator(ll.Lat, ll.Long, Ext);
        }

        /// <summary>
        /// Convert latitude and longitude in degrees to a locator
        /// </summary>
        /// <param name="Lat">Latitude to convert</param>
        /// <param name="Long">Longitude to convert</param>
        /// <returns>Locator string</returns>
        public static string LatLngToLocator(double Lat, double Long)
        {
            return LatLngToLocator(Lat, Long, 0);
        }

        /// <summary>
        /// Convert latitude and longitude in degrees to a locator
        /// </summary>
        /// <param name="Lat">Latitude to convert</param>
        /// <param name="Long">Longitude to convert</param>
        /// <param name="Ext">Extra precision (0, 1, 2)</param>
        /// <returns>Locator string</returns>
        public static string LatLngToLocator(double Lat, double Long, int Ext)
        {
            string locator = "";

            Lat += 90;
            Long += 180;

            locator += (char)('A' + Math.Floor(Long / 20));
            locator += (char)('A' + Math.Floor(Lat / 10));
            Long = Math.IEEERemainder(Long, 20);
            if (Long < 0) Long += 20;
            Lat = Math.IEEERemainder(Lat, 10);
            if (Lat < 0) Lat += 10;

            locator += (char)('0' + Math.Floor(Long / 2));
            locator += (char)('0' + Math.Floor(Lat / 1));
            Long = Math.IEEERemainder(Long, 2);
            if (Long < 0) Long += 2;
            Lat = Math.IEEERemainder(Lat, 1);
            if (Lat < 0) Lat += 1;

            locator += (char)('A' + Math.Floor(Long * 12));
            locator += (char)('A' + Math.Floor(Lat * 24));
            Long = Math.IEEERemainder(Long, (double)1 / 12);
            if (Long < 0) Long += (double)1 / 12;
            Lat = Math.IEEERemainder(Lat, (double)1 / 24);
            if (Lat < 0) Lat += (double)1 / 24;

            if (Ext >= 1)
            {
                locator += (char)('0' + Math.Floor(Long * 120));
                locator += (char)('0' + Math.Floor(Lat * 240));
                Long = Math.IEEERemainder(Long, (double)1 / 120);
                if (Long < 0) Long += (double)1 / 120;
                Lat = Math.IEEERemainder(Lat, (double)1 / 240);
                if (Lat < 0) Lat += (double)1 / 240;
            }

            if (Ext >= 2)
            {
                locator += (char)('A' + Math.Floor(Long * 120 * 24));
                locator += (char)('A' + Math.Floor(Lat * 240 * 24));
                Long = Math.IEEERemainder(Long, (double)1 / 120 / 24);
                if (Long < 0) Long += (double)1 / 120 / 24;
                Lat = Math.IEEERemainder(Lat, (double)1 / 240 / 24);
                if (Lat < 0) Lat += (double)1 / 240 / 24;
            }

            return locator;

            //Lat += 90;
            //Long += 180;
            //v = (int) (Long / 20);
            //Long -= v * 20;
            //locator += (char) ('A' + v);
            //v = (int) (Lat / 10);
            //Lat -= v * 10;
            //locator += (char) ('A' + v);
            //locator += ((int) (Long / 2)).ToString();
            //locator += ((int) Lat).ToString();
            //Long -= (int) (Long / 2) * 2;
            //Lat -= (int) Lat;
            //locator += (char) ('A' + Long * 12);
            //locator += (char) ('A' + Lat * 24);
            //return locator;
        }

        /// <summary>
        /// Convert radians to degrees
        /// </summary>
        /// <param name="rad"></param>
        /// <returns></returns>
        public static double RadToDeg(double rad)
        {
            return rad / Math.PI * 180;
        }

        /// <summary>
        /// Convert degrees to radians
        /// </summary>
        /// <param name="deg"></param>
        /// <returns></returns>
        public static double DegToRad(double deg)
        {
            return deg / 180 * Math.PI;
        }

        /// <summary>
        /// Calculate the distance in km between two locators
        /// </summary>
        /// <param name="A">Start locator string</param>
        /// <param name="B">End locator string</param>
        /// <returns>Distance in km</returns>
        public static double Distance(string A, string B)
        {
            return Distance(LocatorToLatLng(A), LocatorToLatLng(B));
        }

        /// <summary>
        /// Calculate the distance in km between two locators
        /// </summary>
        /// <param name="A">Start LatLng structure</param>
        /// <param name="B">End LatLng structure</param>
        /// <returns>Distance in km</returns>
        public static double Distance(LatLng A, LatLng B)
        {
            if (A.CompareTo(B) == 0) return 0;

            double hn = DegToRad(A.Lat);
            double he = DegToRad(A.Long);
            double n = DegToRad(B.Lat);
            double e = DegToRad(B.Long);

            double co = Math.Cos(he - e) * Math.Cos(hn) * Math.Cos(n) + Math.Sin(hn) * Math.Sin(n);
            double ca = Math.Atan(Math.Abs(Math.Sqrt(1 - co * co) / co));
            if (co < 0) ca = Math.PI - ca;
            double dx = 6367 * ca;

            return dx;
        }

        /// <summary>
        /// Calculate the azimuth in degrees between two locators
        /// </summary>
        /// <param name="A">Start locator string</param>
        /// <param name="B">End locator string</param>
        /// <returns>Azimuth in degrees</returns>
        public static double Azimuth(string A, string B)
        {
            return Azimuth(LocatorToLatLng(A), LocatorToLatLng(B));
        }

        /// <summary>
        /// Calculate the azimuth in degrees between two locators
        /// </summary>
        /// <param name="A">Start LatLng structure</param>
        /// <param name="B">End LatLng structure</param>
        /// <returns>Azimuth in degrees</returns>
        public static double Azimuth(LatLng A, LatLng B)
        {
            if (A.CompareTo(B) == 0) return 0;

            double hn = DegToRad(A.Lat);
            double he = DegToRad(A.Long);
            double n = DegToRad(B.Lat);
            double e = DegToRad(B.Long);

            double co = Math.Cos(he - e) * Math.Cos(hn) * Math.Cos(n) + Math.Sin(hn) * Math.Sin(n);
            double ca = Math.Atan(Math.Abs(Math.Sqrt(1 - co * co) / co));
            if (co < 0) ca = Math.PI - ca;

            double si = Math.Sin(e - he) * Math.Cos(n) * Math.Cos(hn);
            co = Math.Sin(n) - Math.Sin(hn) * Math.Cos(ca);
            double az = Math.Atan(Math.Abs(si / co));
            if (co < 0) az = Math.PI - az;
            if (si < 0) az = -az;
            if (az < 0) az = az + 2 * Math.PI;

            return RadToDeg(az);
        }
    }

    /// <summary>
    /// Simple structure to store a position in latitude and longitude
    /// </summary>
    public struct LatLng : IComparable
    {
        /// <summary>
        /// Latitude, -90 to +90 (N/S direction)
        /// </summary>
        public double Lat;
        /// <summary>
        /// Longitude, -180 to +180 (W/E direction)
        /// </summary>
        public double Long;

        public override string ToString()
        {
            // Latitude is N/S, longitude is E/W (the labels were swapped for years; display-only).
            return Lat.ToString("#.###") + (Lat >= 0 ? "N" : "S") + " " + Long.ToString("#.###") + (Long >= 0 ? "E" : "W");
        }

        public int CompareTo(object to)
        {
            if (to is LatLng)
            {
                if (Lat == ((LatLng)to).Lat && Long == ((LatLng)to).Long) return 0;
                return -1;
            }
            return -1;
        }
    }
}
