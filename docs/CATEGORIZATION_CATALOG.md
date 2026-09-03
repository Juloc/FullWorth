# Automatic categorization catalog

FullWorth ships a deterministic built-in transaction catalog for Germany. The goal is useful categorization immediately after the first bank sync without requiring every user to create rules first.

## Precedence

Automatic categorization follows this order:

1. **Personal categorization rule** — always wins when it matches.
2. **Existing imported/external category** — for example a category imported from Finanzguru is preserved when no personal rule overrides it.
3. **Built-in Germany catalog** — merchant aliases, then transaction-description signals, then MCC fallback.
4. **Uncategorized** — ambiguous transactions are deliberately left unresolved instead of guessed.

Transactions with `CategorizationSource == "manual"` are never overwritten by ingestion or retroactive reapply.

## Semantic category keys

The catalog returns stable semantic keys such as:

- `food.groceries`
- `vehicle.fuel`
- `transport.public`
- `subscriptions.streaming`
- `income.salary`

The key is resolved against the active categories of the current FullWorth Space. Users may rename and reorganize default categories without breaking catalog matches because category `Key` is stable. If a detailed default category is archived, resolution walks up the dotted key and uses an active parent when available.

Custom top-level and child categories are fully supported. A personal rule can map any merchant or transaction pattern to any custom category and takes precedence over the catalog.

## Germany catalog

Source: `src/FullWorth.Backend/Modules/Categories/GermanyCategorizationCatalog.cs`

The current catalog covers common German and Germany-relevant merchants/providers across:

- supermarkets, grocery delivery and bakeries
- restaurants, delivery services and cafés
- drugstores and beauty
- fuel, EV charging, workshops, car washes and parking
- Deutsche Bahn, regional public transport, buses, taxis and rideshare
- internet, mobile phone and utilities
- insurance and statutory health insurance
- pharmacies, doctors, dental and optical
- electronics, clothing, furniture, DIY, books and general shopping
- streaming, software and cloud subscriptions
- sports, gaming, cinemas and events
- airlines, hotels, booking services, package travel and cruises
- education
- pet stores and veterinary services
- brokers/investment platforms

It also contains narrow description signals for salary, benefits, refunds, interest, rent, mortgage, utilities, childcare, cash withdrawals, bank fees, taxes and donations.

MCC is used as a fallback for supported merchant-category codes and ranges, including airlines, hotels, transport, fuel/charging, retail, food, health, insurance, education, leisure, ATM withdrawals and taxes.

## Matching rules

Merchant aliases are normalized with the same `MerchantNormalization` implementation used by banking ingestion. Matching uses normalized token phrases so short aliases do not accidentally match inside unrelated words.

Merchant matching is expense-direction aware. This prevents a positive card refund from being classified as new spending merely because its counterparty is a known merchant. Income description signals are separately direction-aware.

Payment intermediaries such as PayPal and Klarna are intentionally not assigned a spending category on their name alone. They can represent purchases from many unrelated merchants. If richer provider data contains the underlying merchant or MCC, that information can still classify the transaction.

Specific aliases must appear before broader aliases where both can match. Example: `ARAL PULSE` must remain before `ARAL`, and `AMAZON PRIME` before `AMAZON`.

## Adding catalog entries

When extending the catalog:

1. Prefer a stable merchant/brand name over legal-entity suffixes or location-specific text.
2. Add only mappings that are reasonably unambiguous.
3. Put a specific alias before a broader overlapping alias.
4. Use the most specific existing semantic category key that is appropriate.
5. Add a new default semantic category in `FullWorthSeeder` only when the distinction is useful to users generally.
6. Add or update tests in `GermanyCategorizationCatalogTests`.
7. Do not add payment intermediaries as ordinary merchants.
8. Do not overwrite manual classifications.

A static catalog can never enumerate every local business in Germany. Unknown local merchants are primarily covered by MCC where the bank/provider supplies it; otherwise they remain uncategorized until a user rule or future catalog entry resolves them.

## Retroactive application

`POST /api/categorization-rules/reapply?apply=true` applies personal rules and the built-in catalog to historical non-manual transactions. The same evaluation method is used during live banking ingestion so historical and newly synchronized transactions follow the same precedence.
