# FullWorth FinTS implementation plan

## Product decision
- ING uses FinTS as the default connection path.
- Enable Banking remains an explicit `Giro only (PSD2)` alternative.
- The implementation is read-only. No FinTS payment/order segments are exposed by FullWorth.
- FinTS is implemented as an owned `FullWorth.FinTs` .NET library without a runtime dependency on third-party FinTS clients.

## ING target
Official ING FinTS endpoint: `https://fints.ing.de/fints/`.
ING supports read access for Girokonto, Extra-Konto and Direkt-Depot. Account transaction history is available for 90 days. FinTS 3.0 is supported.

## Library architecture
`FullWorth.FinTs` contains:
1. wire codec for FinTS 3.0 delimiters, escaping and binary data;
2. typed segments and a message builder;
3. PIN/TAN security envelope;
4. synchronization/dialog state and BPD/UPD capability discovery;
5. HKSAL balances;
6. HKKAZ MT940 statements;
7. HKWPD depot holdings;
8. HKTAN process 4/2/S for two-step and decoupled TAN;
9. bank profiles/capabilities; ING is the first built-in profile.

## FullWorth integration
- Add provider `fints` beside `enable-banking`.
- Store FinTS credentials/state in the existing encrypted provider authorization field of `BankConnection`; never expose it on public endpoints.
- On ING connect, synchronize FinTS, discover accounts, and import all accessible Giro/Extra/Depot products.
- Reuse existing account ingest for cash accounts.
- Map HKWPD current holdings into the existing investment model as provider snapshot positions, securities and prices.
- A Giro already imported by another provider must not be silently duplicated; provider reconciliation remains explicit until a stable cross-provider identity is available.
- Background sync is provider-dispatched: Enable Banking keeps its current flow; FinTS opens a fresh read-only dialog using encrypted credentials.

## UX
When selecting ING:
- Default: `FinTS – Giro + Extra-Konten + Depot`.
- Alternative: `Enable Banking – nur Giro über PSD2`.
FinTS asks for ING username/access number and internet-banking password/PIN. TAN/push approval is shown only if the bank requires it.

## Security
- HTTPS only, endpoint allowlist for built-in bank profiles.
- PIN/password fields encrypted at rest using the existing FullWorth data-encryption key.
- Never log FinTS request/response payloads because HNSHA contains PIN/TAN.
- No raw provider credentials in application logs, audit events or API responses.
- No payment-capable FinTS public API in this phase.

## Delivery order
1. Owned FinTS protocol library + unit tests.
2. FinTS provider service and ING connect/TAN endpoints.
3. Cash-account sync.
4. Depot snapshot ingest.
5. Provider-dispatched background/manual sync.
6. ING-first UI provider selector.
7. Live ING test checklist and regression tests.

## References
- ING FinTS/HBCI customer information: https://www.ing.de/hilfe/psd2/kundenservice/
- FinTS specifications: https://www.fints.org/de/spezifikation
