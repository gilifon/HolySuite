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
        private const string D3_RESOURCE = "HolyLogger.MapAssets.d3.v5.min.js";
        private const string DATA_RESOURCE = "HolyLogger.MapAssets.dxcc_colored.json";

        private static string _d3Js;
        private static string _countryJson;

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
    }
}
