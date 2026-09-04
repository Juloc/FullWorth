# Repository Separation — Migration Report (Phase A Audit)

FullWorth is an **Open-Core** product split across three repositories:

| Repository | Visibility | Contents |
|---|---|---|
| `Juloc/FullWorth` | **public** | the complete, self-hostable app (this repo) |
| `Juloc/fullworth-cloud` | private | the central official FullWorth Cloud **server** |
| `Juloc/fullworth-demo` | private | the public-visitor **demo overlay** (images + synthetic seed) |

The public core **must build and self-host fully without** the two private
repos. Private repos may depend on the public core (images / wire contracts);
the public core never depends on them.

## The cloud client/server boundary

The single most important rule (spec §16): **local Intelligence and the whole
client side of FullWorth Cloud are part of the Open-Core product and stay
public.** Only the *central official platform* is private.

```
many self-hosted FullWorth instances
        │  (client: contribute + consume — PUBLIC)
        ▼
FullWorth Cloud  ── aggregation · trust · AI review · central registry ──┐
        │           knowledge-pack generation + private signing key       │  PRIVATE
        ▼                                                                  │
signed, versioned Knowledge Packs ─────────────────────────────────────────┘
        │  (client: verify + install — PUBLIC)
        ▼
self-hosted FullWorth instances
```

### Intelligence module classification (`src/FullWorth.Backend/Modules/Intelligence`)

Every file was classified into one of four categories. **Categories 1 and 2
stay public**; only genuine central-server code (category 3) would move — and
none exists in the app today.

| File | Category | Disposition |
|---|---|---|
| `AiBudgetGuard.cs`, `AiCostEstimator.cs` | 1 local | public |
| `IntelligenceModels/Store/Provider/Digests/*`, `Scheduled*`, `LearnedMerchantMapping.cs` | 1 local | public |
| `IntelligenceFeedbackRecorder.cs`, `IntelligenceSuggestion*`, `IntelligenceAdmin*`, `IntelligenceAudit.cs` | 1 local | public |
| `TransactionClassificationFeedbackMiddleware.cs`, `IntelligenceManualJobService.cs` | 1 local | public |
| `CloudIntelligenceModels.cs`, `CloudIntelligenceStateService.cs` | 2 cloud client | public |
| `FullWorthCloudClient.cs` (register/rotate/**submit batch**) | 2 cloud client | public |
| `CloudSubmissionProjector.cs`, `CloudOutboxUploader.cs` (+ worker) | 2 client outbox | public |
| `CloudSyncAdminEndpoints.cs` | 2 self-host admin | public |
| `FullWorthKnowledgePackClient.cs` | 2 pack consumer | public |
| `KnowledgePackModels.cs`, `KnowledgePackService.cs`, `KnowledgePackSyncService.cs` (+ worker) | 2 pack consumer | public |
| `KnowledgePackVerifier.cs` (RSA-PSS/SHA-256, **public key only**) | 2 signature verification | public |
| `KnowledgePackAdminEndpoints.cs` | 2 self-host admin | public |
| Central aggregation / trust / AI-review / registry / **pack builder + private signing key** / cloud admin | 3 proprietary server | **does not exist here → build in `fullworth-cloud`** |

**Result: the entire Intelligence module stays public.** The self-hosted app
contains no central-server code, so nothing moves out of this repo.

## Correction applied on this branch

An earlier, over-eager first pass had stripped the client-side contribution +
knowledge-pack machinery out of the public core (and added a
`RemoveContributionAndKnowledgePacks` drop migration). That violated the
Open-Core boundary above. This branch **restores it verbatim** (with the
`Finance.*` → `FullWorth.*` rebrand applied):

- restored 8 files: `CloudOutboxUploader`, `CloudSubmissionProjector`,
  `CloudSyncAdminEndpoints`, `FullWorthKnowledgePackClient`,
  `KnowledgePackAdminEndpoints`, `KnowledgePackService`,
  `KnowledgePackSyncService`, `KnowledgePackVerifier`;
- restored the stripped members of `CloudIntelligenceModels`,
  `FullWorthCloudClient`, `IntelligenceDbContext`, `IntelligenceFeedbackRecorder`,
  `KnowledgePackModels`, `CloudIntelligenceStateService`;
- re-wired `Program.cs` (DI + cloud/knowledge-pack endpoints),
  `IngestionModule.cs` (official-mapping load) and the integration-test worker
  stop-list;
- removed the drop migration and the throwaway `GtinKey` helper
  (`CloudSubmissionProjector` owns `TryCreateGtinSubjectKey`);
- restored the client cloud tests and the full cloud-UI baseline test.

The public core builds standalone and self-hosts fully without cloud or demo.

## What is private (never in this repo)

Server implementation of FullWorth Cloud, central aggregation of contributions,
proprietary trust/confidence, the cloud AI-review worker, the central
merchant/product/provider registry, cloud admin backend/frontend, the central
price-statistics engine, the knowledge-pack **builder** and its **private
signing key**, the cloud job scheduler, the central cloud audit DB, demo
seeding, the demo reset worker and private demo config. See `fullworth-cloud`
and `fullworth-demo`.
