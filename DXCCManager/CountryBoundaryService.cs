using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace DXCCManager
{
    // Answers "does this lat/lon look like it's really in this DXCC entity's territory?" - the
    // question behind the grid-locator-vs-callsign-country check in the Log Fixer.
    //
    // Backed by DxccBoundaries.txt, a curated subset of Natural Earth's 1:50m Admin 0 - Map
    // Subunits (public domain, naturalearthdata.com), keyed by the same entity name cty.dat
    // uses (QSO.Country). Only entities where that entity's own official reference point lands
    // on or close to its matched polygon were kept - about 250 of DXCC's ~340 entities. The rest
    // (mostly small remote islands and country-internal splits like Asiatic/European Russia that
    // no general-purpose boundary data captures) are simply ABSENT here on purpose: TryCheck
    // returns false for them, and the caller shows nothing rather than risk a wrong verdict.
    //
    // Not a bare "point in polygon" test - a 50m-scale coastline is a simplification, and a
    // country's own reference point can legitimately sit offshore of it. But the tolerance is
    // PER ENTITY, not one flat number for everyone: it comes from the data file, sized to what
    // that specific entity actually needed when it was curated (how far ITS OWN official cty.dat
    // point sits from ITS OWN polygon), plus a 30% safety margin, floored at 50km for ordinary
    // coastline slop. So Israel or Monaco - a compact country whose own point lands exactly
    // inside its border - gets the tight 50km floor, while a scattered-atoll nation like the
    // Marshall Islands, whose own point legitimately sits 235km from any single island, keeps the
    // slack it actually needs. A single flat tolerance loose enough for the Marshall Islands
    // would let a wrong-country locator for a small, compact country pass unnoticed - the exact
    // mistake this check exists to catch.
    public static class CountryBoundaryService
    {
        private class Entity
        {
            public string Name;
            public string Prefix;
            public double ToleranceKm;
            public List<double[][]> Rings; // each ring: [ [lon,lat], [lon,lat], ... ]
        }

        private static readonly object _lock = new object();
        private static Dictionary<string, Entity> _byName;

        private static Dictionary<string, Entity> ByName
        {
            get
            {
                if (_byName == null)
                {
                    lock (_lock)
                    {
                        if (_byName == null)
                            _byName = Load();
                    }
                }
                return _byName;
            }
        }

        /// <summary>
        /// True if this DXCC entity name is one we hold a boundary for (i.e. TryCheck can give a
        /// real answer for it). Lets a caller decide up front whether to even attempt the check.
        /// </summary>
        public static bool HasBoundary(string dxccEntityName)
        {
            if (string.IsNullOrWhiteSpace(dxccEntityName)) return false;
            return ByName.ContainsKey(dxccEntityName.Trim());
        }

        /// <summary>
        /// Checks (lat, lon) against the named DXCC entity's territory and its OWN tolerance (see
        /// the class remarks for why the tolerance is per-entity rather than a shared constant).
        /// Returns false when the entity is not in the curated set - there is nothing to say
        /// either way, and the caller must treat that as "don't check", not "invalid". A caller
        /// flags a mismatch when it returns true and distanceKm &gt; toleranceKm.
        /// </summary>
        public static bool TryCheck(string dxccEntityName, double lat, double lon,
                                     out double distanceKm, out double toleranceKm)
        {
            distanceKm = 0;
            toleranceKm = 0;
            if (string.IsNullOrWhiteSpace(dxccEntityName)) return false;

            Entity e;
            if (!ByName.TryGetValue(dxccEntityName.Trim(), out e)) return false;

            toleranceKm = e.ToleranceKm;

            foreach (double[][] ring in e.Rings)
            {
                if (PointInRing(lon, lat, ring))
                {
                    distanceKm = 0;
                    return true;
                }
            }

            double best = double.MaxValue;
            foreach (double[][] ring in e.Rings)
            {
                double d = MinEdgeDistanceKm(lat, lon, ring);
                if (d < best) best = d;
            }
            distanceKm = best;
            return true;
        }

        private static Dictionary<string, Entity> Load()
        {
            var result = new Dictionary<string, Entity>(StringComparer.Ordinal);
            string text = ReadEmbeddedBoundaries();
            if (string.IsNullOrEmpty(text)) return result;

            string[] lines = text.Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i].TrimEnd('\r');
                i++;
                if (line.Length == 0 || line[0] == '#') continue;

                string[] header = line.Split('\t');
                if (header.Length < 3) continue;

                double toleranceKm;
                double.TryParse(header[2], NumberStyles.Float, CultureInfo.InvariantCulture, out toleranceKm);

                var entity = new Entity
                {
                    Name = header[0],
                    Prefix = header[1],
                    ToleranceKm = toleranceKm,
                    Rings = new List<double[][]>()
                };

                if (i >= lines.Length) break;
                int ringCount;
                if (!int.TryParse(lines[i].TrimEnd('\r'), NumberStyles.Integer, CultureInfo.InvariantCulture, out ringCount))
                    break;
                i++;

                for (int r = 0; r < ringCount; r++)
                {
                    if (i >= lines.Length) break;
                    string[] parts = lines[i].TrimEnd('\r').Split(' ');
                    i++;

                    int pointCount = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    var ring = new double[pointCount][];
                    for (int p = 0; p < pointCount; p++)
                    {
                        double lon = double.Parse(parts[1 + p * 2], CultureInfo.InvariantCulture);
                        double lat = double.Parse(parts[2 + p * 2], CultureInfo.InvariantCulture);
                        ring[p] = new[] { lon, lat };
                    }
                    entity.Rings.Add(ring);
                }

                result[entity.Name] = entity;
            }

            return result;
        }

        private static string ReadEmbeddedBoundaries()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("DxccBoundaries.txt", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) return string.Empty;
            using (Stream s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null) return string.Empty;
                using (var reader = new StreamReader(s))
                    return reader.ReadToEnd();
            }
        }

        // Ray casting. Rings here are always the OUTER boundary only (holes were dropped when the
        // data was curated) - a point inside a hole (a lake, an enclave like Lesotho) reads as
        // "inside", which only ever makes the check more lenient, never a false "not in country".
        private static bool PointInRing(double x, double y, double[][] ring)
        {
            bool inside = false;
            int n = ring.Length;
            int j = n - 1;
            for (int k = 0; k < n; k++)
            {
                double xi = ring[k][0], yi = ring[k][1];
                double xj = ring[j][0], yj = ring[j][1];
                if (((yi > y) != (yj > y)) &&
                    (x < (xj - xi) * (y - yi) / (yj - yi + 1e-15) + xi))
                    inside = !inside;
                j = k;
            }
            return inside;
        }

        private const double EarthRadiusKm = 6371.0;

        private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180.0, p2 = lat2 * Math.PI / 180.0;
            double dPhi = (lat2 - lat1) * Math.PI / 180.0;
            double dL = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
                     + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dL / 2) * Math.Sin(dL / 2);
            return 2 * EarthRadiusKm * Math.Asin(Math.Sqrt(a));
        }

        // Distance from (lat, lon) to the nearest point on segment (lat1,lon1)-(lat2,lon2), via a
        // flat-earth projection scaled by cos(lat) - fine at the segment lengths a 50m coastline
        // has, and far cheaper than a true great-circle nearest-point solve.
        private static double PointToSegmentKm(double lat, double lon, double lat1, double lon1, double lat2, double lon2)
        {
            double cosLat = Math.Cos(lat * Math.PI / 180.0);
            double x = lon * cosLat, y = lat;
            double x1 = lon1 * cosLat, y1 = lat1;
            double x2 = lon2 * cosLat, y2 = lat2;
            double dx = x2 - x1, dy = y2 - y1;
            double t;
            if (dx == 0 && dy == 0) t = 0;
            else t = Math.Max(0.0, Math.Min(1.0, ((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy)));
            double px = x1 + t * dx, py = y1 + t * dy;
            double plon = px / cosLat, plat = py;
            return HaversineKm(lat, lon, plat, plon);
        }

        private static double MinEdgeDistanceKm(double lat, double lon, double[][] ring)
        {
            double best = double.MaxValue;
            for (int k = 0; k < ring.Length - 1; k++)
            {
                double d = PointToSegmentKm(lat, lon, ring[k][1], ring[k][0], ring[k + 1][1], ring[k + 1][0]);
                if (d < best) best = d;
            }
            return best;
        }
    }
}
