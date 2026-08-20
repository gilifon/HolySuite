using System;
using System.IO;
using System.Reflection;

namespace HolyLogger.ToolsUserControls
{
    // Supplies the offline map assets (d3 library + precomputed colored country data) that the
    // polar map embeds directly into its HTML so the map renders with NO internet connection.
    // The assets are shipped as EmbeddedResource (see HolyLogger.csproj):
    //   HolyLogger.MapAssets.d3.v5.min.js     - the d3 v5 library (no CDN)
    //   HolyLogger.MapAssets.dxcc_colored.json - { palette:[8 hex], features:[{p, ci, geometry}] }
    //                                            from HolyCluster's dxcc_map, pre-4-colored.
    internal static class MapAssetProvider
    {
        // LEAFLET TOO, for the same reason d3 is here.
        //
        // The polar map has rendered without the internet since it was written; the Leaflet map never
        // did. It fetched its library and its stylesheet from unpkg.com every time it opened - and it
        // opens at startup. Inside an embedded Internet Explorer that fetch happens on the thread that
        // draws the window, so the whole program stood still while it waited: measured at seven to
        // eight seconds, inside one WM_WINDOWPOSCHANGED, with no memory allocated the whole time (which
        // is what said the work was native rather than ours).
        //
        // The map tiles still come from the internet - a map of the world cannot be shipped in a
        // program this size - but Leaflet fetches those itself, in the background, without holding the
        // window. It is the library and the stylesheet, loaded as part of the page, that blocked.
        private const string LEAFLET_JS_RESOURCE = "HolyLogger.MapAssets.leaflet.min.js";
        private const string LEAFLET_CSS_RESOURCE = "HolyLogger.MapAssets.leaflet.min.css";
        private const string LEAFLET_ICON_RESOURCE = "HolyLogger.MapAssets.leaflet-marker-icon.png";
        private const string LEAFLET_ICON2X_RESOURCE = "HolyLogger.MapAssets.leaflet-marker-icon-2x.png";
        private const string LEAFLET_SHADOW_RESOURCE = "HolyLogger.MapAssets.leaflet-marker-shadow.png";

        private const string D3_RESOURCE = "HolyLogger.MapAssets.d3.v5.min.js";
        private const string DATA_RESOURCE = "HolyLogger.MapAssets.dxcc_colored.json";

        private static string _d3Js;
        private static string _countryJson;
        private static string _leafletJs;
        private static string _leafletCss;

        private static string ReadResource(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(name))
            {
                if (s == null)
                    throw new InvalidOperationException("Embedded map asset not found: " + name);
                using (var reader = new StreamReader(s))
                    return reader.ReadToEnd();
            }
        }

        // The raw d3 library source (cached).
        public static string D3Js => _d3Js ?? (_d3Js = ReadResource(D3_RESOURCE));

        // The colored-country GeoJSON-ish data as a JSON string (cached).
        public static string CountryJson => _countryJson ?? (_countryJson = ReadResource(DATA_RESOURCE));

        // A <script> tag with d3 inlined, to drop into the map HTML in place of the CDN <script src>.
        public static string D3ScriptTag => "<script>" + D3Js + "</script>";

        // A <script> tag that defines window.DXCC_DATA for the map to render countries offline.
        public static string CountryDataScriptTag =>
            "<script>window.DXCC_DATA=" + CountryJson + ";</script>";

        public static string LeafletJs => _leafletJs ?? (_leafletJs = ReadResource(LEAFLET_JS_RESOURCE));
        public static string LeafletCss => _leafletCss ?? (_leafletCss = ReadResource(LEAFLET_CSS_RESOURCE));

        // Leaflet's own script and stylesheet, inlined - in place of the two lines that used to fetch
        // them from unpkg.com. Cached after the first read: 158 KB, read once per run of the program.
        public static string LeafletScriptTag => "<script>" + LeafletJs + "</script>";

        // LEAFLET'S OWN MARKER PICTURES, CARRIED IN THE PAGE.
        //
        // Leaflet works out where its marker images are from the address its script was loaded from.
        // Inlined, there is no address to work from - so the markers that do not carry an icon of their
        // own (the DX pin, on the spot map) would come out broken. This hands the three images to the
        // page as data, and tells Leaflet's default icon to use them, which is exactly what the CDN
        // used to provide. Four kilobytes, and the map is then whole with no internet at all.
        public static string LeafletDefaultIconScriptTag
        {
            get
            {
                string icon = DataUri(LEAFLET_ICON_RESOURCE);
                string icon2x = DataUri(LEAFLET_ICON2X_RESOURCE);
                string shadow = DataUri(LEAFLET_SHADOW_RESOURCE);
                return "<script>"
                     + "L.Icon.Default.prototype.options.iconUrl='" + icon + "';"
                     + "L.Icon.Default.prototype.options.iconRetinaUrl='" + icon2x + "';"
                     + "L.Icon.Default.prototype.options.shadowUrl='" + shadow + "';"
                     + "L.Icon.Default.prototype.options.imagePath='';"
                     + "</script>";
            }
        }

        private static string DataUri(string resource)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(resource))
            {
                if (s == null) return string.Empty;
                using (var mem = new MemoryStream())
                {
                    s.CopyTo(mem);
                    return "data:image/png;base64," + Convert.ToBase64String(mem.ToArray());
                }
            }
        }
        public static string LeafletStyleTag => "<style>" + LeafletCss + "</style>";
    }
}
