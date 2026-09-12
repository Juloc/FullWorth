# Open items — to clarify & decide

This file lists only work that is still materially open. Shipped items should not remain here as speculative gaps.

## Structural frontend work

Shipped:

- shared core API/state/router/navigation/event bus/i18n
- feature activate/unmount lifecycle foundation
- centralized route writes
- permanent contract at `docs/FRONTEND_ARCHITECTURE.md`
- Accounts/banking owner extracted to `features/accounts.js`
- Settings/security owner extracted to `features/settings.js`
- Accounts MutationObserver/polling/synthetic-navigation integration removed
- Accounts no longer patches Dashboard/Wealth DOM
- Purchase advanced helpers no longer construct direct BFF URLs or use native confirms
- semantic money variants shipped in `ui/money.js`

Still open:

- complete CSS split into tokens/shell/components/responsive + feature-owned styles
- reduce remaining same-domain post-render Accounts UX decoration by merging it into the Accounts owner/shared identity components
- consolidate the remaining layered Purchase/Wealth helper modules where this reduces ownership ambiguity
- continue shrinking legacy architecture-test allow-lists

## Finance / behavior

Shipped:

- mixed-cycle budgets are not summed into a misleading headline
- canonical merchantId analytics -> transaction drill-down
- root-only category overview + child drill-down
- completed prior-period category Average3/Average6/Average12 for range analytics
- active period separated from preview history and trailing average
- normal debit/spending/contract values default to neutral rather than danger red
- Dashboard/Analytics share active-period semantics

Still open / worth tightening:

- move more existing callers from legacy sign classes to the semantic money variants where intent is known
- add broader backend numerical coverage for week/quarter/year completed-period averages and comparisons
- converge remaining period controls onto one explicit reusable PeriodState/PeriodPicker API rather than only shared semantics
- verify every Dashboard drill-down preserves its configured period/scope where applicable
- ensure all transfer/refund/pending/ignored drill-down lists reconcile exactly with their KPI scope

## UI_UX_SPEC items not yet fully implemented

- strict share/screenshot mode (§5)
- Alerts & actions widget (§8.6)
- portfolio-trend widget (§8.9)
- widget height presets and richer scope/visualization/forecast config (§6.2/§7)
- Dashboard/navigation customization: layouts, bottom-nav slots, merchant-logo preference (§6.3/§21)
- “Available until next income” needs a real forecast calculation rather than simply echoing account total

## Product/security decisions

- Decided (shipped): Amazon import intentionally uses server-side browser automation with an encrypted stored session, running automatic ~24h syncs. Credentials/OTP are submitted only to Amazon server-side, never persisted, never returned to the browser — consistent with the bank-credential rule. Documented in `PRODUCT_DECISIONS.md` and `SECURITY_ARCHITECTURE.md`. Manual/e-mail/export stay as complementary adapters.
- external-tool least-privilege permissions and stricter share/screenshot mode still need explicit product scope.

## Compliance / account deletion

- Extend the `PersonalDataPurgeManifest` CI guard to `AuthDbContext`. **`IntelligenceDbContext` is done** — all 23 entities are classified and `PurgeManifest_ClassifiesEveryIntelligenceEntity` fails on a new unclassified one. `AuthDbContext` lives in `FullWorth.Web`, so its guard belongs in `FullWorth.Web.Tests` (the Backend manifest cannot reference the Web `AuthUser` identity type).
