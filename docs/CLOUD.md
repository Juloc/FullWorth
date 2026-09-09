# FullWorth Cloud — the instance side

This describes what a FullWorth instance does. The Cloud server lives in the private
`fullworth-cloud` repository; nothing here depends on having access to it.

Everything is opt-in per instance and off until an administrator makes the setup decision. With
Cloud disabled, FullWorth is fully functional: merchant/category/contract/product resolution falls
back to whatever the last verified knowledge pack installed, and benchmark and price panels report
"unavailable" instead of failing.

## Where the state lives

All of it is in `IntelligenceDbContext` (same database, own migration history — see
[ARCHITECTURE.md](ARCHITECTURE.md)):

| Table | Holds |
| --- | --- |
| `CloudConnectionStates` | one row, `ScopeKey = "instance"`: the instance id, mode, setup decision, entitlement status, last registration/submission/pack check, last error code |
| `CloudIntelligenceConsents` | one row per accepted policy version, with `AcceptedByUserId`, locale, client version, `RevokedAt` |
| `CloudInstanceCredentials` | the bearer credential, encrypted via `FieldCipher`, plus a `sha256:…` 16-hex fingerprint |
| `CloudSubmissionOutbox` | the observation queue |
| `KnowledgePackInstallations` / `KnowledgePackArchives` | the installed pack and the last 3 verified payloads |
| `Official*` (merchants, ontology, brands, contract providers, products) | the projection of the installed pack |
| `BrandAssetBlobs` | SVG bytes addressed by SHA-256, shared by official and custom packs |

The instance id is a `Guid` generated locally when the state row is first created. It is not tied to
a domain, an account or an email.

## Consent gate

`CloudIntelligencePolicy.CurrentVersion` is a compiled constant (currently `"2026-09-06.6"`).
Bumping it is what forces a fresh decision: `HasCurrentActiveConsentAsync` requires
`Mode == "enabled"` **and** a non-revoked consent row whose `PolicyVersion` equals the current
constant. Every uploader, worker and benchmark endpoint calls it first, so a policy bump silently
stops all outbound traffic until an admin re-accepts.

`EnableAsync` rejects a stale `policyVersion` in the request body (`cloud_policy_stale`), revokes
consents for older versions, and — when accepting a *new* version — deletes every outbox row that is
not already `sent` or `dead_letter`. A new disclosure must not retroactively authorize payloads
minimized under the old one; the workers regenerate whatever is still eligible.

`DisableAsync` revokes all consents, deletes the `CloudInstanceCredentials` row and drops all
untransmitted outbox rows. Re-enabling starts from a fresh registration and a fresh queue.

The consent UI is the "FullWorth Cloud Intelligence" panel in Einstellungen
(`wwwroot/features/access-setup.js`, `openCloudWizard`). The standalone admin page
`wwwroot/intelligence/index.html` has a fuller version of the same panel plus the brand-pack import,
but nothing in the app links to it — it is only reachable by typing
`/intelligence/index.html` (there is no `UseDefaultFiles`, so `/intelligence` hits the SPA
fallback).

## The endpoint, and why `BaseUrl` does not help you

`FullWorthCloudClient.OfficialBaseUrl` is the compiled constant `https://api.fullworth.de/`.

```csharp
var configured = configuration["FullWorthCloud:BaseUrl"]?.Trim();
var allowOverride = environment.IsDevelopment() || environment.IsEnvironment("Testing");
var value = allowOverride && !string.IsNullOrWhiteSpace(configured) ? configured : OfficialBaseUrl;
```

**`FullWorthCloud:BaseUrl` is ignored in Production.** A self-hoster who runs their own Cloud cannot
point their instance at it with configuration; the setting is honoured only under
`ASPNETCORE_ENVIRONMENT=Development` or `Testing`. In Production a non-HTTPS URL would also be
rejected outright. This is a hard block on self-hosting the Cloud half, and it is not documented
anywhere in the operator-facing files.

Timeout is 45 s. Failures are normalized to a `FullWorthCloudException` with a stable code:

| Condition | Code | `Transient` |
| --- | --- | --- |
| socket/DNS failure | `cloud_unreachable` | yes |
| client-side timeout | `cloud_timeout` | yes |
| 401 | `cloud_unauthorized` | no |
| 403 | `cloud_entitlement_denied` | no |
| 413 | `cloud_batch_too_large` | no |
| 429 | `cloud_rate_limited` | yes (honours `Retry-After`) |
| 5xx | `cloud_server_error` | yes |
| other | `cloud_http_<status>` | no |
| unparsable body | `cloud_invalid_json` | no |

The most recent code is written to `CloudConnectionStates.LastErrorCode` and shown in the settings
row, so an operator sees `cloud_unreachable` or `knowledge_pack_public_key_missing` in the UI.

## Enrollment

`POST v1/instances/register` with `{ instanceId, policyVersion, clientVersion }`. `clientVersion` is
the assembly version.

The shared enrollment token is **optional**:

```csharp
var enrollment = configuration["FullWorthCloud:EnrollmentToken"]?.Trim();
if (!string.IsNullOrWhiteSpace(enrollment))
    request.Headers.TryAddWithoutValidation("X-FullWorth-Enrollment-Token", enrollment);
```

Compose passes `FullWorthCloud__EnrollmentToken: ${FULLWORTH_CLOUD_ENROLLMENT_TOKEN:-}`, i.e. empty
by default. An external self-hoster therefore enrolls with no header at all, and today that works
because the Cloud deployment has public registration enabled. Whether it succeeds is entirely the
server's decision: a Cloud with a configured token rejects a tokenless attempt with
`enrollment_missing`, and a Cloud with neither a token nor public registration fails closed the same
way.

The response carries `{ instanceId, credential, credentialExpiresAt, entitlementStatus }`. The client
rejects a response whose `instanceId` does not echo back, or whose credential is empty
(`cloud_registration_invalid_response`), then stores the credential encrypted.

Registration is lazy and happens wherever a credential is first needed — the outbox uploader, the
knowledge-pack sync, the benchmark endpoints and the admin `cloud/enable` handler all call
`RegisterAsync` when `CloudInstanceCredentials` has no row. On a `401` the uploader deletes the
stored credential so the next pass re-registers. `POST v1/instances/rotate-credential` is
implemented (`RotateCredentialAsync`, `Bearer` current credential) but nothing in the product calls
it — only the two test doubles implement the interface member. `credentialExpiresAt` is stored and
never acted on, so credential rotation does not happen.

## Observation outbox

Contributions are queued locally first and uploaded by a background worker, so no user request ever
waits on the Cloud and nothing is lost if it is unreachable.

### What gets queued

| Producer | `EventType` | Cadence | Payload |
| --- | --- | --- | --- |
| `IntelligenceFeedbackRecorder` | `product_category_corrected`, `contract_candidate_accepted` / `_rejected`, and the recorded action | on user feedback, same `SaveChanges` as the feedback row | the minimized projection only, and only when `feedback.CloudEligible` |
| `IntelligenceSuggestionReviewService` | `ai_suggestion_accepted` / `_rejected` | on review | — |
| `CloudMerchantBenchmarkContributionService` | `benchmark_observation` (`spending.merchant.monthly`) | 24 h | one previous-month net-spend sum per canonical merchant key + currency |
| `CloudContractBenchmarkContributionService` | `benchmark_observation` (`contract.energy.monthly_cost`, `contract.internet.monthly_cost`, `contract.insurance.health.monthly_cost`, `contract.insurance.monthly_cost`) | 24 h | one monthly cost per metric/currency/entity |
| `CloudSavingsBenchmarkContributionService` | `benchmark_observation` (`savings.rate`) | 24 h | one rate per instance/month |
| `CloudProductPriceContributionService` | `price_observation` | 6 h | GTIN subject key + effective unit price |

Minimization is done at queue time, in the producer:

- Merchant observations only ever carry a **canonical merchant key resolved from the installed signed
  knowledge pack** (`CloudOperationalRegistryResolver`). A counterparty the pack does not know is
  simply not contributed. Local merchant ids, raw counterparty strings and per-transaction amounts
  never leave the instance.
- Price observations require a real GTIN barcode (`GtinKey.TryCreateGtinSubjectKey`) on a
  `confirmed` purchase item, and only for items touched after `AcceptedAt`. There is no historical
  backfill.
- Savings observations reduce every local space to one median value first, so a many-space instance
  cannot carry more weight than a single-space one.
- Country is only attached when every contributing row agrees on it; otherwise it is `null`.
- Values outside `(0, 1_000_000]` are dropped.

Every row has an idempotency key: a SHA-256 over metric + entity + currency + month + value for
merchant observations, `benchmark:{metricKey}:{currency}:{observedMonth}:{revisionDate}` (or a hash
including the entity key) for contract observations,
`benchmark:savings.rate:{observedMonth}:{revisionDate}` for savings, a stable per-purchase-item key
for prices, and `feedback:{id}:schema:{n}` for feedback. Duplicates are skipped at queue time. The
price producer additionally
*replaces* the payload of a still-`queued`/`failed` row when the purchase item is corrected before
transmission — once `sent`, that item is never uploaded again.

### Upload loop

`CloudLearningOutboxWorker` runs `UploadOnceAsync` every 60 s.

1. Consent and enabled state are re-checked.
2. Up to 100 rows in `queued` or `failed` state, with `NextAttemptAt` due and no live lease, are
   claimed by a single `ExecuteUpdateAsync`: status → `sending`, `LeaseOwner` →
   `cloud-uploader:{machine}:{guid}`, `LeaseExpiresAt` → now + 2 min, `AttemptCount` incremented.
   The lease is what makes a crashed pass recoverable — the rows become claimable again when it
   expires.
3. `POST v1/submissions/batch` with `Bearer` credential, `Idempotency-Key: batch:{instance}:{batch}`,
   gzip-compressed JSON body. Hard limits: 500 events per batch and 2 MiB compressed
   (`cloud_batch_too_large`).
4. The response's per-event statuses decide each row:
   - `accepted` or `duplicate` → `sent`, error cleared;
   - `rejected` → `dead_letter` with the server's error code (the payload is wrong, retrying cannot
     fix it);
   - anything else, or a missing per-event result → retry.

### Retry, backoff, dead letter

```csharp
if (row.AttemptCount >= 12) { row.Status = DeadLetter; row.NextAttemptAt = null; }
else { row.Status = Failed;
       var seconds = Math.Min(3600, 15 * Math.Pow(2, Math.Min(8, row.AttemptCount)));
       row.NextAttemptAt = now.Add(retryAfter ?? TimeSpan.FromSeconds(seconds)); }
```

15 s doubling per attempt, exponent capped at 8 (so 15 s … 64 min) and the delay itself capped at
1 h. A `Retry-After` from the server overrides the computed delay. After 12 attempts the row becomes
`dead_letter` and is never retried.

A transport-level failure retries the **whole claimed batch**, not individual rows. There is no
requeue command and no admin view of the dead-letter queue: a `dead_letter` row can only be
inspected in the database, and `POST /api/intelligence/admin/cloud/disable` is the only way to clear
untransmitted rows.

## Benchmarks and prices

Reads are pass-through: the instance does not cache Cloud aggregates.

- `GET /api/intelligence/benchmarks/?metricKey=…` (plus optional `entityKey`, `currency`, `country`,
  `regionBucket`, `householdSizeBand`, `incomeBand`, `ageBand`, `observedMonth`) →
  `v1/benchmarks`. With `entityKey` it uses the entity-specific variant.
- `GET /api/intelligence/benchmarks/contracts`, `…/contracts/{contractId}`, `…/savings`,
  `/api/intelligence/benchmarks/merchants/{merchantId}` are the resolved, per-resource wrappers the
  UI calls.
- `GET /api/intelligence/prices/purchase-items/{purchaseItemId}` returns the Cloud aggregate plus a
  local price history. Without a GTIN or a valid currency it returns
  `{ available: false, reason: "public_product_id_missing" | "currency_invalid", local: … }` and
  makes no Cloud call.

A response carries `median`, `mean`, `p25`, `p75`, `min`, `max`, `observationCount` and
`distinctInstanceCount`. A `204 No Content` from the Cloud (no aggregate for that bucket) becomes
`Results.NoContent()`. Without current consent, and on a registration failure, the endpoints return
`503`. All of them run per-request with the instance credential, so every panel is a live round
trip.

## Knowledge packs

A knowledge pack is one signed JSON payload that carries the reviewed, shared knowledge:
merchant alias → canonical merchant + category mappings, a category/provider/product ontology with
aliases and redirects, contract providers and signatures, products with GTINs and aliases, and brand
metadata. `KnowledgePackSyncWorker` checks every 6 h (5 min after a failure, 30 min while disabled).

### Fetch

1. `GET v1/knowledge-packs/latest?currentVersion=…&region=…` → manifest
   (`packId`, `version`, `schemaVersion`, `region`, `contentSha256`, `signatureAlgorithm`,
   `signatureBase64`, `minimumClientVersion`), or `null` when already current.
2. `ValidateManifest` rejects an unsupported schema version, a signature algorithm other than
   `RSA-PSS-SHA256`, a region other than `FullWorthCloud:KnowledgePackRegion` (default `GLOBAL`), a
   malformed hash, a `packId` different from `FullWorthCloud:KnowledgePackId`
   (`knowledge_pack_id_untrusted`; Compose defaults it to `fullworth-official`) and a
   `minimumClientVersion` above the running assembly (`knowledge_pack_client_too_old`).
3. A version that is not strictly newer than the installed one is refused
   (`knowledge_pack_downgrade_rejected`).
4. If the exact bytes of the installed version are still in `KnowledgePackArchives`, the client tries
   `GET v1/knowledge-packs/{id}/{version}/delta?baseVersion=…` and reconstructs
   `base[0..prefix] + middle + base[^suffix..]`. Otherwise it does a full
   `GET v1/knowledge-packs/{id}/{version}`. Either way the result is capped at 5 MiB.

### Verification

`VerifyPayloadBytes` compares SHA-256 against `manifest.ContentSha256` in fixed time, then verifies
`signatureBase64` with RSA-PSS/SHA-256 over the payload bytes. A delta carries no additional trust:
if reconstructed bytes fail this gate the client discards them, downloads the full pack and verifies
again. Payload fields must echo the manifest, and every collection has a hard row cap (100 000
merchants, 200 000 product GTINs, 5 000 brand assets, …).

Key resolution order (`ResolvePublicKeyPem`): `FullWorthCloud:KnowledgePackPublicKeyPem` →
`…Path` (unreadable file falls through) → `…Base64` (malformed value falls through) →
`KnowledgePackProtocol.OfficialPublicKeyPem`.

**The shipped key is empty.** `OfficialPublicKeyPem = ""`, so
`ResolveOfficialPublicKeyPem()` returns `null` and an instance with no explicit override fails closed
with `knowledge_pack_public_key_missing` on every sync. This is intentional fail-closed behaviour —
it never trusts an unverifiable pack — but it also means **no instance can install a pack today**
unless its operator supplies a key out of band. The constant has to be filled in at release time.

### Installation

One transaction: every `Official*` table is emptied and replaced, brand blobs are upserted by hash,
`KnowledgePackInstallations` is updated, and the verified payload is archived
(`KnowledgePackArchives`, newest 3 kept). Merchant mappings without a `categoryKey` are dropped;
duplicates on (alias, direction, country) collapse to the highest confidence; category keys are
rewritten through the pack's own `category` redirects; a mapping referencing a `logoKey` the pack
does not define aborts the whole install (`knowledge_pack_brand_reference_invalid`). Unreferenced
brand blobs are pruned afterwards.

`CloudOperationalRegistryResolver` and `CloudOntologyResolver` read only these installed tables and
never touch the network, so classification keeps working with the Cloud offline.

## Brand packs

Merchant and company logos are pack-based. The public web bundle contains **no** merchant SVG
catalog, and transaction or counterparty text is never sent to a logo provider — matching is local.

### Resolution order

`identityIcon` in `wwwroot/ui/ux-kit.js`:

1. transfer glyph (`⇄`, or `↑` for savings) when the row is a transfer — this overrides everything;
2. the resolved logo asset path;
3. the category icon (a literal emoji stored in the category, or a known category glyph);
4. a category-tinted monogram from the first letter.

The logo itself comes from `BrandPackService.GetEffectiveCatalogAsync`: enabled custom packs first
(highest `priority`, then most recently updated), then the official pack. First writer per
`brandKey` wins, and an asset whose blob is missing from `BrandAssetBlobs` is skipped entirely.
Aliases are returned longest-first so a more specific alias matches before a shorter one.

### Transport

Schema v2 signs brand *metadata plus content descriptors* — a descriptor is a SHA-256 and a byte
length, not the SVG. `ResolveBrandBlobsAsync` reuses a cached blob when hash, byte length and media
type all match, accepts an embedded blob when the pack carried one, and otherwise fetches
`GET v1/knowledge-packs/assets/{sha256}` and re-verifies it against the descriptor. A pack update
whose logos are unchanged therefore transfers no logo bytes. Legacy schema-v1 packs must embed
`contentBase64` for every asset (a v1 descriptor without bytes is rejected) and are migrated into
the same blob cache.

The browser only receives metadata and aliases from `GET /api/intelligence/brand-catalog`, each asset
carrying `assetPath = /api/intelligence/brand-assets/{sha256}`. Bytes are fetched lazily from that
authenticated route when an icon actually renders. That route re-runs
`BrandAssetVerifier.VerifySvg` against the stored blob before returning it (a blob that no longer
verifies becomes a 404) and answers with `Cache-Control: private, max-age=31536000, immutable` and
the hash as `ETag`. The BFF forwards those cache validators for this one path only. The catalog
itself is cached in the browser for 6 h. A large installed pack is therefore never serialized into a
page-load response.

Unreferenced blobs are kept for 30 days (`UnreferencedBlobRetention`) before garbage collection, so
temporarily disabling a pack does not force a re-download.

### Custom packs

Instance administrators import JSON at `POST /api/intelligence/admin/brand-packs/custom` — in the UI,
the "Eigene Brand-Packs" panel on `/intelligence/index.html`. Custom packs are independent of Cloud
Intelligence consent, are never uploaded, and work with the Cloud switched off.

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
    { "aliasKey": "MEINE FIRMA GMBH", "brandKey": "meine-firma", "country": "DE" }
  ]
}
```

`priority` defaults to 1000 and is clamped to 1–10 000. `logoKey` defaults to `brandKey`.
`contentSha256` is optional — supplied, it is verified; omitted, it is computed during import.
`country` normalizes to `GLOBAL` unless it is a two-letter code. Re-importing with the same `name`
replaces that pack's metadata, assets and aliases atomically, so a pack can be updated without
changing its precedence slot or touching the official pack.

Other routes: `GET` the same path lists packs with asset/alias counts,
`PUT /{id}/enabled` toggles one, `DELETE /{id}` removes it. All four require an Intelligence admin
and write an `IntelligenceAuditEvent`.

### Limits, for both official and custom packs

`BrandAssetVerifier.VerifySvg` enforces:

- media type exactly `image/svg+xml`, 1 byte to 256 KiB;
- valid UTF-8, containing `<svg`;
- rejected on `<script`, `<foreignobject`, `<iframe`, `<object`, `<embed`, `javascript:`,
  `onload=`, `onerror=`, `href="http…`, `url(http…` or any `xlink:href=`;
- `brandKey` ASCII alphanumeric plus `.`, `_`, `-`, max 120 chars; alias max 300 normalized chars;
- `sourceUrl`, when present, must be absolute HTTPS.

Per pack: 1–5 000 assets and at most 100 000 aliases, no duplicate `brandKey`. Identical SVG bytes
are stored once, keyed by hash, across official and custom packs.

## Admin surface

`/api/intelligence/admin/cloud*` — all Intelligence-admin only:

| Route | Effect |
| --- | --- |
| `GET /cloud` | the full `CloudIntelligenceStateView`, including `requiresSetupDecision` and `lastErrorCode` |
| `POST /cloud/enable` | store consent, then register immediately; audit `cloud.enabled` |
| `POST /cloud/disable` | revoke consent, delete the credential, drop untransmitted rows; audit `cloud.disabled` |
| `POST /cloud/sync` | queue contract + savings benchmarks, run one outbox upload, run one pack sync, and return the counts — the manual equivalent of all four workers |
