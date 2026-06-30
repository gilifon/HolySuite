# Map asset generator (offline colored polar map)

The polar map in HolyLogger renders **fully offline** by embedding two assets
(see `HolyLogger.csproj` -> EmbeddedResource):

- `../d3.v5.min.js`     — the d3 v5 library (no CDN)
- `../dxcc_colored.json` — country shapes + precomputed "4-color map" coloring

`dxcc_colored.json` reproduces HolyCluster's vivid map **exactly** as it looked at commit
`08329f38` ("Hardcode countries colors") — the version HolyLogger mimics so both apps look
like one product. It uses that commit's hardcoded per-country color assignment
(`hardcoded_colors.mjs`, copied from `ui/src/data/map_colors.js` @ 08329f38) on the identical
`dxcc_map.json`, plus that commit's exact 9-color palette + ocean (`#b8e8ee`) / graticule
(`#6bb7c4`) from `useColors.jsx`.

`hardcoded_colors.mjs` is `map_colors.js` @ 08329f38 with its `@/maps/...` import rewritten to
a local JSON import. The full clone used for archaeology lives in `HolyCluster_src/` (gitignored).

## Regenerate

```bash
cd MapAssets/build
npm install            # installs d3-geo (only build-time dependency)
node generate_map_asset.mjs
cp dxcc_colored.json ../dxcc_colored.json
```

Source data (`dxcc_map.json`, `lakes.json`) was pulled from
https://github.com/iarc-il/HolyCluster (`ui/src/maps/`). To refresh it, re-download
those files into this folder, then regenerate.

Coordinates are rounded to 3 decimals (~110 m) to keep the embedded file small.
