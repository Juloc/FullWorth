// Baut den mitgelieferten Markenkatalog.
//
//   node ops/brand-icons/build.mjs                 # gegen die gepinnte Fassung auffrischen
//   node ops/brand-icons/build.mjs --check         # nur melden, was fehlt oder neu waere
//   node ops/brand-icons/build.mjs --version 17.0.0
//
// Woher die Marken kommen: aus GermanyCategorizationCatalog.cs. Wer dort einen Haendler ergaenzt,
// bekommt sein Logo hier ohne zweite Liste - das ist der Grund, diese Datei nicht von Hand zu
// pflegen. Dazu kommt extra-brands.json fuer die Gegenparteien, die der Katalog bewusst NICHT als
// Haendler fuehrt (PayPal, Klarna, Banken): ein Logo ist keine Kategorisierung.
//
// Was dieses Skript NICHT tut: SVGs pruefen. Das waere eine zweite, driftende Kopie von
// BrandSvgValidator in JavaScript. Es schreibt Dateien; geprueft wird jede einzelne offline in
// BundledBrandCatalogTests mit den echten C#-Validatoren.
//
// Etikette gegenueber jsDelivr: vier gleichzeitig, ~100 ms Abstand, und misses.json verhindert,
// dass die sicheren 404er bei jedem Lauf erneut abgefragt werden.
import { createHash } from 'node:crypto';
import { mkdir, readFile, readdir, unlink, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';

const HERE = import.meta.dirname;
const REPO_ROOT = resolve(HERE, '..', '..');
const CATALOG_CS = join(REPO_ROOT, 'src', 'FullWorth.Backend', 'Modules', 'Categories', 'GermanyCategorizationCatalog.cs');
const OUT_DIR = join(REPO_ROOT, 'src', 'FullWorth.Backend', 'Modules', 'Intelligence', 'BrandAssets');
const MISSES = join(HERE, 'misses.json');

const args = process.argv.slice(2);
const CHECK_ONLY = args.includes('--check');
const versionArg = args.indexOf('--version');
const DEFAULT_VERSION = '16.29.0';
const VERSION = versionArg >= 0 ? args[versionArg + 1] : DEFAULT_VERSION;
if (!/^\d+\.\d+\.\d+$/.test(VERSION)) {
  console.error(`Keine gueltige Simple-Icons-Fassung: ${VERSION}`);
  process.exit(2);
}

const LICENSE = 'Simple Icons CC0-1.0; trademarks remain property of their owners.';
const SOURCE_NAME = 'simple-icons/simple-icons';
const url = slug => `https://cdn.jsdelivr.net/npm/simple-icons@${VERSION}/icons/${slug}.svg`;

// ---- dieselben Regeln wie BrandSlugDerivation.NormalizeSlug / BrandAliasKey.Of ------------------

function normalizeSlug(value) {
  return (value ?? '')
    .trim()
    .replaceAll('ß', 'ss')
    .replaceAll('ẞ', 'ss')
    .replaceAll('&', ' and ')
    .normalize('NFD')
    .replace(/\p{Mn}/gu, '')
    .toLowerCase()
    .replace(/[^a-z0-9]/g, '');
}

function aliasKey(value) {
  const key = (value ?? '')
    .trim()
    .replaceAll('ß', 'SS')
    .replaceAll('ẞ', 'SS')
    .toUpperCase()
    .normalize('NFD')
    .replace(/\p{Mn}/gu, '')
    .replace(/[^A-Z0-9]/g, ' ')
    .split(' ')
    .filter(Boolean)
    .join(' ');
  return key.length === 0 || key.length > 300 ? null : key;
}

// ---- Quellen -----------------------------------------------------------------------------------

async function aliasesFromCategorizationCatalog() {
  const source = await readFile(CATALOG_CS, 'utf8');
  const block = source.slice(
    source.indexOf('MerchantEntries ='),
    source.indexOf('TextEntries ='));
  const aliases = [];
  for (const entry of block.matchAll(/new\("[^"]+",\s*"[^"]+",\s*\[([^\]]*)\]\)/g)) {
    for (const quoted of entry[1].matchAll(/"([^"]+)"/g)) aliases.push(quoted[1]);
  }
  if (aliases.length === 0) throw new Error('MerchantEntries nicht gefunden - hat sich die Form geaendert?');
  return aliases;
}

async function readJson(name, fallback) {
  try { return JSON.parse(await readFile(join(HERE, name), 'utf8')); }
  catch { return fallback; }
}

// ---- Abholen -----------------------------------------------------------------------------------

async function fetchSlug(slug) {
  const response = await fetch(url(slug), {
    headers: { accept: 'image/svg+xml', 'user-agent': 'FullWorth-brand-icons/1.0' },
    redirect: 'error'
  });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`${slug}: HTTP ${response.status}`);
  const text = await response.text();
  if (text.length > 262_144) throw new Error(`${slug}: ${text.length} Bytes ueber der Grenze`);
  return text;
}

/// Feste Groessen raus, damit das Logo den Kreis fuellt statt darin zu schrumpfen. Sonst unveraendert.
function polish(svg) {
  return svg
    .replace(/\s(width|height)="[^"]*"/g, '')
    .replace(/<svg\b/, '<svg preserveAspectRatio="xMidYMid meet"');
}

async function pool(items, size, worker) {
  const results = [];
  let index = 0;
  await Promise.all(Array.from({ length: size }, async () => {
    while (index < items.length) {
      const mine = index++;
      results[mine] = await worker(items[mine]);
      await new Promise(done => setTimeout(done, 100));
    }
  }));
  return results;
}

// ---- Lauf --------------------------------------------------------------------------------------

const overrides = await readJson('slug-overrides.json', { map: {}, deny: [] });
const extra = await readJson('extra-brands.json', []);
const knownMisses = new Set(CHECK_ONLY ? [] : await readJson('misses.json', []));
const deny = new Set(overrides.deny ?? []);

/** slug -> { canonicalName, aliases:Set } */
const wanted = new Map();
function want(rawName, slugHint) {
  const slug = overrides.map?.[slugHint ?? normalizeSlug(rawName)] ?? slugHint ?? normalizeSlug(rawName);
  if (!slug || deny.has(slug)) return;
  const key = aliasKey(rawName);
  if (!key) return;
  const existing = wanted.get(slug) ?? { canonicalName: rawName, aliases: new Set() };
  existing.aliases.add(key);
  if (rawName.length < existing.canonicalName.length) existing.canonicalName = rawName;
  wanted.set(slug, existing);
}

for (const alias of await aliasesFromCategorizationCatalog()) want(alias);
for (const brand of extra) {
  for (const alias of brand.aliases ?? [brand.name]) want(alias, brand.slug);
  // Der Anzeigename kommt aus der Datei, nicht aus dem laengsten Alias.
  const target = wanted.get(brand.slug);
  if (target) target.canonicalName = brand.name;
}

const slugs = [...wanted.keys()].sort();
const toProbe = slugs.filter(slug => !knownMisses.has(slug));
console.log(`${slugs.length} Kurznamen, ${toProbe.length} werden abgefragt (${slugs.length - toProbe.length} bekannte Fehlschlaege uebersprungen).`);

const fetched = await pool(toProbe, 4, async slug => {
  try { return { slug, svg: await fetchSlug(slug) }; }
  catch (error) { return { slug, svg: null, error: String(error.message) }; }
});

const hits = fetched.filter(x => x.svg);
const misses = fetched.filter(x => !x.svg && !x.error).map(x => x.slug);
const errors = fetched.filter(x => x.error);
for (const failure of errors) console.warn(`  ! ${failure.error}`);

if (CHECK_ONLY) {
  const current = new Set((await readdir(OUT_DIR).catch(() => [])).filter(f => f.endsWith('.svg')).map(f => f.slice(0, -4)));
  const added = hits.map(x => x.slug).filter(slug => !current.has(slug));
  const gone = [...current].filter(slug => !hits.some(x => x.slug === slug));
  console.log(added.length ? `NEU:  ${added.join(', ')}` : 'Nichts Neues.');
  console.log(gone.length ? `WEG:  ${gone.join(', ')}` : 'Nichts entfallen.');
  process.exit(added.length || gone.length ? 1 : 0);
}

await mkdir(OUT_DIR, { recursive: true });
for (const stale of (await readdir(OUT_DIR).catch(() => [])).filter(f => f.endsWith('.svg'))) {
  if (!hits.some(x => `${x.slug}.svg` === stale)) await unlink(join(OUT_DIR, stale));
}

const brands = [];
for (const { slug, svg } of hits.sort((a, b) => a.slug.localeCompare(b.slug))) {
  const payload = Buffer.from(polish(svg), 'utf8');
  await writeFile(join(OUT_DIR, `${slug}.svg`), payload);
  const entry = wanted.get(slug);
  brands.push({
    brandKey: slug,
    canonicalName: entry.canonicalName,
    file: `${slug}.svg`,
    contentSha256: createHash('sha256').update(payload).digest('hex'),
    byteLength: payload.length,
    aliases: [...entry.aliases].sort(),
    sourceName: SOURCE_NAME,
    sourceUrl: url(slug),
    licenseNote: LICENSE
  });
}

// Ein Alias darf nur einer Marke gehoeren. Sonst entscheidet die Reihenfolge im Katalog, welches
// Logo eine Buchung bekommt - und das ist keine Entscheidung, das ist ein Zufall.
const owner = new Map();
for (const brand of brands) {
  brand.aliases = brand.aliases.filter(alias => {
    if (owner.has(alias)) {
      console.warn(`  ! Alias "${alias}" beansprucht von ${owner.get(alias)} und ${brand.brandKey}; bleibt bei ${owner.get(alias)}.`);
      return false;
    }
    owner.set(alias, brand.brandKey);
    return true;
  });
}

await writeFile(
  join(OUT_DIR, 'catalog.json'),
  JSON.stringify({ simpleIconsVersion: VERSION, brands: brands.filter(b => b.aliases.length > 0) }, null, 2) + '\n');
await writeFile(MISSES, JSON.stringify([...new Set([...knownMisses, ...misses])].sort(), null, 2) + '\n');

console.log(`${brands.length} Marken geschrieben, ${misses.length} ohne Eintrag im Katalog.`);
console.log(`Gesamtgroesse: ${Math.round(brands.reduce((sum, b) => sum + b.byteLength, 0) / 1024)} kB`);
if (dirname(OUT_DIR)) console.log(`Ziel: ${OUT_DIR}`);
