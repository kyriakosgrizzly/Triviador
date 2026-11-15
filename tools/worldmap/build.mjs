// Builds the Conquest world map from Natural Earth country borders (public domain, via world-atlas).
//
//   cd tools/worldmap && npm install && npm run build
//
// Outputs:
//   src/Triviador.Server/wwwroot/maps/world.json        SVG paths, label points, links (for the browser)
//   src/Triviador.Domain/Conquest/WorldMap.g.cs         ids, names, continents, borders (for the engine)
//
// Each region is a group of whole countries. Borders between regions come from the shared
// country borders; sea crossings are listed by hand in SEA_LINKS.

import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { feature, merge, neighbors } from "topojson-client";
import { geoNaturalEarth1, geoPath, geoCentroid, geoArea } from "d3-geo";

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, "../..");
const topology = JSON.parse(readFileSync(resolve(here, "node_modules/world-atlas/countries-110m.json"), "utf8"));

// ------------------------------------------------------------------ regions

const REGIONS = [
  // North America
  { key: "canada", name: "Canada", continent: "North America", countries: ["Canada"] },
  { key: "usa", name: "United States", continent: "North America", countries: ["United States of America"] },
  { key: "mexico", name: "Mexico & Caribbean", continent: "North America",
    countries: ["Mexico", "Guatemala", "Belize", "Honduras", "El Salvador", "Nicaragua", "Costa Rica", "Panama",
      "Cuba", "Haiti", "Dominican Rep.", "Jamaica", "Bahamas", "Puerto Rico", "Trinidad and Tobago"] },
  { key: "greenland", name: "Greenland & Iceland", continent: "North America", countries: ["Greenland", "Iceland"] },

  // South America
  { key: "gran-colombia", name: "Gran Colombia", continent: "South America",
    countries: ["Colombia", "Venezuela", "Guyana", "Suriname"] },
  { key: "andes", name: "Andes", continent: "South America", countries: ["Peru", "Bolivia", "Ecuador"] },
  { key: "brazil", name: "Brazil", continent: "South America", countries: ["Brazil"] },
  { key: "southern-cone", name: "Southern Cone", continent: "South America",
    countries: ["Argentina", "Chile", "Uruguay", "Paraguay", "Falkland Is."] },

  // Europe
  { key: "british-isles", name: "British Isles", continent: "Europe", countries: ["United Kingdom", "Ireland"] },
  { key: "scandinavia", name: "Scandinavia", continent: "Europe", countries: ["Norway", "Sweden", "Finland", "Denmark"] },
  { key: "western-europe", name: "Western Europe", continent: "Europe",
    countries: ["France", "Belgium", "Netherlands", "Luxembourg", "Spain", "Portugal"] },
  { key: "central-europe", name: "Central Europe", continent: "Europe",
    countries: ["Germany", "Poland", "Czechia", "Slovakia", "Austria", "Switzerland", "Hungary"] },
  { key: "balkans", name: "Italy & Balkans", continent: "Europe",
    countries: ["Italy", "Slovenia", "Croatia", "Bosnia and Herz.", "Serbia", "Montenegro", "Kosovo", "Albania",
      "Macedonia", "Greece", "Bulgaria", "Romania", "Moldova"] },
  { key: "eastern-europe", name: "Eastern Europe", continent: "Europe",
    countries: ["Ukraine", "Belarus", "Lithuania", "Latvia", "Estonia"] },
  { key: "russia", name: "Russia", continent: "Asia", countries: ["Russia"] },

  // Asia
  { key: "caucasus", name: "Turkey & Caucasus", continent: "Asia", countries: ["Turkey", "Georgia", "Armenia", "Azerbaijan"] },
  { key: "middle-east", name: "Middle East", continent: "Asia",
    countries: ["Syria", "Iraq", "Jordan", "Israel", "Lebanon", "Palestine", "Cyprus", "N. Cyprus", "Saudi Arabia",
      "Yemen", "Oman", "United Arab Emirates", "Qatar", "Kuwait"] },
  { key: "persia", name: "Persia", continent: "Asia", countries: ["Iran", "Afghanistan", "Pakistan"] },
  { key: "central-asia", name: "Central Asia", continent: "Asia",
    countries: ["Kazakhstan", "Uzbekistan", "Turkmenistan", "Kyrgyzstan", "Tajikistan"] },
  { key: "india", name: "India", continent: "Asia", countries: ["India", "Nepal", "Bhutan", "Bangladesh", "Sri Lanka"] },
  { key: "china", name: "China", continent: "Asia", countries: ["China", "Taiwan", "Mongolia"] },
  { key: "east-asia", name: "Japan & Korea", continent: "Asia", countries: ["Japan", "North Korea", "South Korea"] },
  { key: "southeast-asia", name: "Southeast Asia", continent: "Asia",
    countries: ["Myanmar", "Thailand", "Laos", "Vietnam", "Cambodia", "Malaysia", "Brunei"] },

  // Oceania
  { key: "archipelago", name: "Malay Archipelago", continent: "Oceania",
    countries: ["Indonesia", "Philippines", "Timor-Leste", "Papua New Guinea", "Solomon Is."] },
  { key: "australia", name: "Australia & NZ", continent: "Oceania",
    countries: ["Australia", "New Zealand", "New Caledonia", "Vanuatu", "Fiji"] },

  // Africa
  { key: "north-africa", name: "North Africa", continent: "Africa",
    countries: ["Morocco", "W. Sahara", "Algeria", "Tunisia", "Libya", "Egypt"] },
  { key: "west-africa", name: "West Africa", continent: "Africa",
    countries: ["Mauritania", "Mali", "Senegal", "Gambia", "Guinea-Bissau", "Guinea", "Sierra Leone", "Liberia",
      "Côte d'Ivoire", "Burkina Faso", "Ghana", "Togo", "Benin", "Niger", "Nigeria"] },
  { key: "central-africa", name: "Central Africa", continent: "Africa",
    countries: ["Chad", "Cameroon", "Central African Rep.", "Eq. Guinea", "Gabon", "Congo", "Dem. Rep. Congo"] },
  { key: "east-africa", name: "East Africa", continent: "Africa",
    countries: ["Sudan", "S. Sudan", "Ethiopia", "Eritrea", "Djibouti", "Somalia", "Somaliland", "Kenya", "Uganda",
      "Rwanda", "Burundi", "Tanzania"] },
  { key: "southern-africa", name: "Southern Africa", continent: "Africa",
    countries: ["Angola", "Zambia", "Malawi", "Mozambique", "Zimbabwe", "Namibia", "Botswana", "South Africa",
      "Lesotho", "eSwatini", "Madagascar"] },
];

/** Countries left off the board. */
const IGNORED = new Set(["Antarctica", "Fr. S. Antarctic Lands"]);

/** Single islands of a country that belong somewhere else (or nowhere). Return a region key, null to drop, or undefined. */
function overridePolygon(country, [lon, lat]) {
  if (country === "France" && lon < -30) return "gran-colombia";             // French Guiana
  if (country === "United States of America" && lon < -150 && lat < 30) return null; // Hawaii
  return undefined;
}

/** Crossings over water. Each pair becomes a border. */
const SEA_LINKS = [
  ["usa", "russia"],                  // Bering Strait (drawn across the map edge)
  ["canada", "greenland"],
  ["greenland", "british-isles"],
  ["greenland", "scandinavia"],
  ["british-isles", "western-europe"],
  ["british-isles", "scandinavia"],
  ["scandinavia", "central-europe"],  // Denmark–Germany is land, kept for clarity if data drops it
  ["western-europe", "north-africa"], // Gibraltar
  ["balkans", "north-africa"],        // Sicily–Tunisia
  ["middle-east", "east-africa"],     // Bab-el-Mandeb
  ["brazil", "west-africa"],          // Atlantic narrows
  ["east-asia", "china"],
  ["east-asia", "russia"],
  ["archipelago", "southeast-asia"],
  ["archipelago", "australia"],
  ["mexico", "gran-colombia"],
];

// ------------------------------------------------------------------ assign polygons to regions

const regionOfCountry = new Map();
for (const r of REGIONS) for (const c of r.countries) {
  if (regionOfCountry.has(c)) throw new Error(`${c} is in two regions`);
  regionOfCountry.set(c, r.key);
}

const pieces = [];        // one topojson Polygon geometry per island/part, with its region
const unassigned = [];
for (const g of topology.objects.countries.geometries) {
  const name = g.properties.name;
  if (IGNORED.has(name)) continue;
  const region = regionOfCountry.get(name);
  if (!region) { unassigned.push(name); continue; }

  const polys = g.type === "MultiPolygon" ? g.arcs.map((a) => ({ type: "Polygon", arcs: a }))
    : g.type === "Polygon" ? [{ type: "Polygon", arcs: g.arcs }] : [];
  for (const p of polys) {
    const centroid = geoCentroid(feature(topology, p));
    const o = overridePolygon(name, centroid);
    if (o === null) continue;
    pieces.push({ ...p, region: o ?? region, country: name });
  }
}
if (unassigned.length) throw new Error(`Countries without a region: ${unassigned.join(", ")}`);

const regionKeys = REGIONS.map((r) => r.key);
for (const k of regionKeys) if (!pieces.some((p) => p.region === k)) throw new Error(`Region ${k} has no land`);

// ------------------------------------------------------------------ borders

const edges = new Set();
const edgeKey = (a, b) => (a < b ? `${a}|${b}` : `${b}|${a}`);
const nb = neighbors(pieces);
nb.forEach((list, i) => {
  for (const j of list) {
    const a = pieces[i].region, b = pieces[j].region;
    if (a !== b) edges.add(edgeKey(a, b));
  }
});
const landEdges = new Set(edges);
for (const [a, b] of SEA_LINKS) {
  if (!regionKeys.includes(a) || !regionKeys.includes(b)) throw new Error(`Bad sea link ${a}-${b}`);
  edges.add(edgeKey(a, b));
}

// Connectivity check.
const adj = new Map(regionKeys.map((k) => [k, new Set()]));
for (const e of edges) { const [a, b] = e.split("|"); adj.get(a).add(b); adj.get(b).add(a); }
const seen = new Set([regionKeys[0]]); const queue = [regionKeys[0]];
while (queue.length) for (const n of adj.get(queue.shift())) if (!seen.has(n)) { seen.add(n); queue.push(n); }
if (seen.size !== regionKeys.length) throw new Error(`Map not connected; unreachable: ${regionKeys.filter((k) => !seen.has(k))}`);

// ------------------------------------------------------------------ geometry

const WIDTH = 1600;
const regionFeatures = REGIONS.map((r) => ({
  type: "Feature",
  properties: { key: r.key },
  geometry: merge(topology, pieces.filter((p) => p.region === r.key)),
}));

// Rotate so the map's edge falls in the Bering Strait.
const projection = geoNaturalEarth1().rotate([-11, 0]);
projection.fitWidth(WIDTH, { type: "FeatureCollection", features: regionFeatures });
const path = geoPath(projection).digits(1);
const [[, y0], [, y1]] = path.bounds({ type: "FeatureCollection", features: regionFeatures });
const HEIGHT = Math.ceil(y1 + 10);
projection.translate([projection.translate()[0], projection.translate()[1] - y0 + 10]);

function labelPoint(geometry) {
  // Centroid of the largest single landmass, so labels sit on land.
  const polys = geometry.type === "MultiPolygon" ? geometry.coordinates : [geometry.coordinates];
  let best = null, bestArea = -1;
  for (const p of polys) {
    const f = { type: "Polygon", coordinates: p };
    const a = geoArea(f);
    if (a > bestArea) { bestArea = a; best = f; }
  }
  return projection(geoCentroid(best)).map((v) => Math.round(v * 10) / 10);
}

// Hand-tuned label nudges where the largest landmass centroid looks odd.
const LABEL_NUDGE = {
  usa: [20, 20], canada: [-40, 40], russia: [40, 30], greenland: [10, 20], "east-asia": [10, -10],
  archipelago: [-40, -15], "british-isles": [-28, 2], scandinavia: [0, 0], australia: [0, 0],
  "southern-cone": [10, -40], mexico: [-10, 0], "middle-east": [0, 0],
  "central-europe": [-8, 10], "eastern-europe": [18, -2], "western-europe": [-40, 20], balkans: [20, 18],
};

const out = {
  name: "World",
  width: WIDTH,
  height: HEIGHT,
  source: "Natural Earth 1:110m via world-atlas (public domain)",
  regions: REGIONS.map((r, i) => {
    const f = regionFeatures[i];
    const [lx, ly] = labelPoint(f.geometry);
    const [dx, dy] = LABEL_NUDGE[r.key] ?? [0, 0];
    return {
      id: i + 1,
      key: r.key,
      name: r.name,
      continent: r.continent,
      path: path(f),
      label: [Math.round(lx + dx), Math.round(ly + dy)],
      neighbors: [...adj.get(r.key)].map((k) => regionKeys.indexOf(k) + 1).sort((a, b) => a - b),
    };
  }),
  seaLinks: SEA_LINKS.filter(([a, b]) => !landEdges.has(edgeKey(a, b)))
    .map(([a, b]) => ({ a: regionKeys.indexOf(a) + 1, b: regionKeys.indexOf(b) + 1, wrap: a === "usa" && b === "russia" })),
};

const jsonPath = resolve(root, "src/Triviador.Server/wwwroot/maps/world.json");
mkdirSync(dirname(jsonPath), { recursive: true });
writeFileSync(jsonPath, JSON.stringify(out));

// ------------------------------------------------------------------ C# for the engine

const cs = `// <auto-generated>
// Generated by tools/worldmap/build.mjs from Natural Earth data. Do not edit by hand: change the script and rebuild.
// </auto-generated>

namespace Triviador.Domain.Conquest;

public static partial class WorldMap
{
    private static readonly RegionInfo[] RegionData =
    [
${out.regions.map((r) => `        new(${r.id}, "${r.key}", "${r.name}", "${r.continent}"),`).join("\n")}
    ];

    private static readonly (int A, int B)[] BorderData =
    [
${[...edges].sort().map((e) => { const [a, b] = e.split("|"); return `        (${regionKeys.indexOf(a) + 1}, ${regionKeys.indexOf(b) + 1}), // ${a} – ${b}${landEdges.has(e) ? "" : " (sea)"}`; }).join("\n")}
    ];
}
`;
const csPath = resolve(root, "src/Triviador.Domain/Conquest/WorldMap.g.cs");
mkdirSync(dirname(csPath), { recursive: true });
writeFileSync(csPath, cs);

console.log(`${out.regions.length} regions, ${edges.size} borders (${edges.size - landEdges.size} sea), ${WIDTH}x${HEIGHT}, ${(JSON.stringify(out).length / 1024).toFixed(0)} KB`);
for (const r of out.regions) console.log(`${String(r.id).padStart(2)} ${r.key.padEnd(16)} ${r.neighbors.length} neighbours`);
