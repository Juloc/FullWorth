# FullWorth Cloud integration (optional)

FullWorth Cloud is an **optional** service that lets independent self-hosted
instances share anonymized, reviewed knowledge (merchant→category mappings,
product/GTIN data, contract providers, price statistics) as signed knowledge
packs. **Self-hosting works fully without it.** With the cloud disabled the app
falls back to local rules, local learning and the bundled catalog.

## What ships in the public core (client side)

The self-hosted instance contains the complete **client**:

- `FullWorthCloudClient` — instance registration, credential rotation and
  batched contribution submission (`v1/submissions/batch`, machine-to-machine
  Bearer credential, idempotency key, gzip).
- Cloud consent + connection state (`CloudIntelligenceStateService`,
  `CloudConnectionState`, `CloudIntelligenceConsent`, `CloudInstanceCredential`)
  and the `/cloud/enable` · `/cloud/disable` endpoints + consent UI.
- The client **contribution outbox** (`CloudSubmissionProjector`,
  `CloudOutboxUploader` + worker, `CloudSubmissionOutbox`). Only already
  pseudonymized, derived envelopes are queued — never raw transactions,
  free-text descriptions, receipt images, account/user ids or credentials.
- The knowledge-pack **consumer** (`FullWorthKnowledgePackClient`,
  `KnowledgePackService`, `KnowledgePackSyncService` + worker) and its
  self-host admin endpoints.
- **Signature verification** (`KnowledgePackVerifier`): packs are accepted only
  if the downloaded bytes match the manifest SHA-256 and the `RSA-PSS-SHA256`
  signature verifies against the bundled **public** key. The private signing
  key is never in this repo.

Reception and contribution are reciprocal: there is no productive
"download-only" mode. A user opts in explicitly, per current policy version.

## What is NOT here (server side)

The central official platform is a separate private product (`fullworth-cloud`):
aggregation of contributions across instances, proprietary trust/confidence,
the AI-review worker, the central merchant/product/provider registry, the
price-statistics engine, the knowledge-pack **builder** and its **private
signing key**, cloud admin and the central audit database. The cloud only ever
sees data an instance explicitly sent over the documented API, and it never
touches a self-hosted instance's database.

## Enabling it

1. An operator points the instance at a cloud base URL (config) and enables it
   from the Intelligence → Cloud panel, accepting the current policy version.
2. The instance registers and receives a machine credential (stored hashed).
3. The outbox worker uploads reviewed contributions; the sync worker downloads,
   verifies and installs the latest knowledge pack.

If the cloud is unavailable or disabled, all of the above no-ops and the app
keeps working from local data.

## Wire contract

The stable contract the client speaks is documented, versioned and negotiated
(`ProtocolVersion`, `MinClientVersion`, `SchemaVersion`). The private server
implements the matching side. Verification uses `RSA-PSS-SHA256` over the raw
payload with a SHA-256 payload hash in the manifest.
