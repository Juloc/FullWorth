# Brand Packs

FullWorth merchant/company logos are pack-based. The public web bundle contains no merchant SVG catalog.

## Resolution order

1. enabled custom brand packs (higher `priority` first)
2. official FullWorth Cloud brand pack
3. transaction category icon
4. monogram

Matching is local. Transaction/counterparty text is never sent to a logo provider.

## Official pack transport

Knowledge-pack schema v2 signs only brand metadata and content descriptors. A descriptor contains the SHA-256
and byte length of an SVG. The instance keeps a shared `BrandAssetBlobs` cache keyed by SHA-256 and downloads
`/v1/knowledge-packs/assets/{sha256}` only when that hash is missing.

A normal knowledge-pack update therefore transfers no logo bytes when the referenced logos are unchanged.
Legacy schema-v1 packs with embedded SVG bytes are still accepted and migrated into the same cache.

The browser receives only brand metadata/aliases from the catalog endpoint. SVG bytes are requested lazily
from the authenticated local `/api/intelligence/brand-assets/{sha256}` route when an icon is actually rendered.
Those local responses use a private one-year immutable cache, so a large installed pack is not serialized into
every page-load response.

Unreferenced blobs are retained for 30 days before garbage collection so temporary pack changes do not
immediately force a later re-download.

## Custom pack format

Instance administrators can import JSON from **AI & Intelligence → Eigene Brand-Packs**.

```json
{
  "name": "Meine Firmenlogos",
  "version": "1.0",
  "priority": 2000,
  "enabled": true,
  "assets": [
    {
      "brandKey": "meine-firma",
      "canonicalName": "Meine Firma",
      "logoKey": "meine-firma",
      "mediaType": "image/svg+xml",
      "contentBase64": "PHN2ZyB4bWxucz0i...",
      "contentSha256": null,
      "sourceName": "internal",
      "sourceUrl": null,
      "licenseNote": "Owned by my organization"
    }
  ],
  "aliases": [
    {
      "aliasKey": "MEINE FIRMA GMBH",
      "brandKey": "meine-firma",
      "country": "DE"
    }
  ]
}
```

`contentSha256` is optional for custom packs. If supplied, FullWorth verifies it. Otherwise FullWorth
computes the hash while importing.

Importing a pack again with the same `name` replaces that pack's metadata/assets atomically, so custom
packs can be updated without changing their precedence slot or touching the official FullWorth pack.

## Safety limits

- SVG only (`image/svg+xml`)
- maximum 256 KiB per logo
- maximum 5,000 assets and 100,000 aliases per pack
- active SVG content, scripts, external HTTP references and event handlers are rejected
- source URLs, when present, must use HTTPS
- identical SVG bytes across official/custom packs are stored only once

Custom packs are independent of Cloud Intelligence consent and are never uploaded to FullWorth Cloud.
