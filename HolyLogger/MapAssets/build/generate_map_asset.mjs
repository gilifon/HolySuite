// Build the offline map asset that exactly reproduces HolyCluster's vivid map as it looked
// at commit 08329f38 ("Hardcode countries colors") — the version HolyLogger should mimic so
// both apps feel like one product.
//
//   - per-country color INDEX: the hardcoded COUNTRY_COLOR_BY_DXCC_NAME / _BY_FEATURE_INDEX
//     from that commit (./hardcoded_colors.mjs), applied to the identical dxcc_map.json.
//   - palette + ocean: the exact theme values from useColors.jsx @ 08329f38.
//
// Output: dxcc_colored.json  { palette:[9 hex], features:[ {p, ci, geometry}, ... ] }
import { readFileSync, writeFileSync } from "node:fs";
import { country_color_indices } from "./hardcoded_colors.mjs";

const dxcc_map = JSON.parse(readFileSync(new URL("./dxcc_map.json", import.meta.url)));

// Exact map_countries palette from HolyCluster useColors.jsx @ 08329f38 (country_0..country_8).
const PALETTE = [
    "#f6e36d", // 0 yellow
    "#8fca6b", // 1 green
    "#f3a15f", // 2 orange
    "#e97972", // 3 coral / red
    "#a884cc", // 4 purple
    "#98d4c1", // 5 mint
    "#e7c276", // 6 gold
    "#ee9bbb", // 7 pink
    "#f6faf9", // 8 near-white
];

// Round coordinates to 3 decimals (~110 m) to shrink the embedded file.
function round_coords(geometry) {
    const r = n => Math.round(n * 1e3) / 1e3;
    const map_ring = ring => ring.map(([lon, lat]) => [r(lon), r(lat)]);
    if (geometry.type === "Polygon")
        return { type: "Polygon", coordinates: geometry.coordinates.map(map_ring) };
    return { type: "MultiPolygon", coordinates: geometry.coordinates.map(p => p.map(map_ring)) };
}

const out_features = dxcc_map.features.map((f, i) => ({
    p: f.properties.dxcc_prefix,
    ci: country_color_indices[i],
    geometry: round_coords(f.geometry),
}));

const asset = { palette: PALETTE, features: out_features };
const json = JSON.stringify(asset);
writeFileSync(new URL("./dxcc_colored.json", import.meta.url), json);
console.log(`Wrote dxcc_colored.json (${(json.length / 1024).toFixed(0)} KB), ` +
    `${out_features.length} features, ${Math.max(...country_color_indices) + 1} colors.`);
