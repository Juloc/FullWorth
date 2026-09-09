# FullWorth frontend architecture

Reference for `src/FullWorth.Web/wwwroot`. It describes what the frontend *is* and which rules hold,
including the places where the code knowingly breaks a rule. Rules without a guard are conventions;
rules with a guard are in `tests/FullWorth.Web.Tests/FrontendArchitectureGuardTests.cs`.

Still-open frontend work lives in `docs/OPEN_ITEMS.md`, not here.

## Shape

Vanilla ES modules, no build step, no bundler, no framework, no `node_modules`. The browser loads the
same files that sit in the repo. `fullworth-test` on <http://localhost:8099> bind-mounts `wwwroot`, so
an edit is live on reload.

The only frontend HTML that is *not* a file in `wwwroot`:

| Route | Source |
| --- | --- |
| `/settings/import`, `/settings/import/finanzguru` | `src/FullWorth.Web/Modules/Import/ImportCenterPage.cs` |
| `/settings/import/finanzguru/xlsx` | `src/FullWorth.Web/Modules/Import/FinanzguruImportPage.cs` |
| `/settings/import/broker-pdf` | `src/FullWorth.Web/Modules/Import/BrokerPdfImportPage.cs` |
| `/share/receipt/{token}` | `src/FullWorth.Web/Modules/Purchases/ShareReceiptEndpoints.cs` |

Each import page keeps its `<head>` and body in a C# raw string literal and serves it with
`Results.Content(Html, "text/html")`; its behaviour lives in a normal module under
`wwwroot/features/*-import-page.js`. Because the page hand-writes its own `<head>`, it has to repeat
the whole stylesheet chain — loading only `app.css` once left every design token undefined and shipped
unstyled 20px form controls on phones. `tests/FullWorth.Web.Tests/Frontend/ImportPageStylesheetGuardTests.cs`
pins the chain and its order for all three. Registration goes through
`Modules/Import/ImportPageEndpoints.cs` so a new import source needs no provider-to-provider coupling.

`ShareReceiptEndpoints.Page()` is a different animal: a self-contained mini page with an inline
`<style>` block. That block is blocked by the CSP (see [CSP](#csp)), so those pages render unstyled.

## Shells

| Shell | Route | Own scripts |
| --- | --- | --- |
| `index.html` | `/` + every SPA view path (`MapFallbackToFile`) | `app.js`, `ui/motion.js` |
| `auth/index.html` | `/auth`, `/auth/login`, `/auth/register`, … (`Program.cs`) | `auth/auth.js` |
| `admin/index.html` | `/admin`, admin-gated | `admin/admin.js` |
| `passkeys/index.html` | `/settings/security/passkeys` | `passkeys/*.js` |
| `account-deletion/index.html` | `/account/deletion` | `account-deletion/deletion.js` |
| `compensation.html` | static file, linked from Settings and the More sheet | `features/compensation*.js` |
| `intelligence/index.html` | static file, no in-app link | `intelligence/*.js` |

`index.html` pre-renders all 16 view containers (`#view-dashboard` … `#view-settings`) and toggles
`.active`. Features render into their static container; there is no per-route DOM mount.

`Program.cs` does not call `UseDefaultFiles()`, so a directory shell has to be requested by its full
path. Nothing in the app links to `intelligence/index.html`; the AI/Cloud rows in Settings
(`#ai-access-settings`, `#cloud-intelligence-settings`, handled by `features/access-setup.js`) open
dialogs in the SPA instead, so that shell has no reachable entry point.

Three small scripts are shared across shells rather than owned by one:

- `theme-init.js` — classic script in `<head>`. Resolves the stored theme and typography and writes
  them onto `<html>` *before* first paint, so the page never restyles after load, then dynamically
  imports `ui/appearance.js` on `DOMContentLoaded`. Loaded by `index.html`, `admin/index.html`,
  `compensation.html`, `intelligence/index.html` and the three C#-inlined import pages.
- `security/browser-fetch.js` — the `window.fetch` antiforgery patch (see [Core services](#core-services--wwwrootcore)).
- `pwa/register-sw.js` — registers `sw.js`, and because it is present on every authenticated page and
  already CSP-allowed it also injects `styles/features/coach.css` and dynamically imports
  `features/coach-shell.js`. It skips both on `body.auth-body`.

`account-deletion/deletion.css` is deliberately self-contained: hardcoded hex colours, Inter, its own
dark-mode block, no token layer loaded. It is the one page that does not use design tokens.

## Bootstrap — `app.js`

394 lines. It owns bootstrap, shell and composition, and nothing else:

- session/capability boot (`/auth/capabilities`), space loading, locale/theme application
- the shell: sidebar collapse + drag-resize, responsive auto-collapse, topbar overflow menu, mobile
  More sheet, layout reset, global-search key binding
- `showView()` — view activation, nav active state, page header, `fullworth:view-change`
- the shared `ctx` object and the feature registry table

Feature rendering must not come back into it. `SettingsWorkflowsStayOutOfAppBootstrap` and
`BootstrapLivesInApp_NotInFeatureOwners` guard both directions.

`ctx` is what features receive: `api`, `bankApi`, `get` (i18n), `esc`, `date`, `dateTime`, `toast`,
`dialog`, `money`, `isPrivate`, `categoryOptions`, `jsonBody`, `empty`, `skeleton`, `reload`,
`confirm`, `bffUrl`, `navScope`, `showView`.

## Core services — `wwwroot/core/`

| Module | Owns |
| --- | --- |
| `api.js` | the only BFF client. `/bff/backend/…` and `/bff/banking/…` URL construction, `fullWorthSpaceId` injection, JSON parsing, `error.status`/`error.detail`, in-flight GET de-duplication (2 s TTL, 200-entry cap), cache flush on any non-GET |
| `services.js` | the singletons: `apiClient`, `api`, `bankApi`, `i18n` |
| `router.js` | URL writes. `pathForView`, `viewFromPath`, `write()` (`pushState`/`replaceState`). It does not mount or clean up anything |
| `navigation.js` | `installNavigation(handler)` / `navigate(view, options)` — the module-level navigation entry point features use |
| `feature-registry.js` | `register` / `activate` / `refresh` / `unmount` |
| `state.js` | global state only: `lang`, `theme`, `messages`, `view`, `spaces`, `space`, `capabilities` |
| `event-bus.js` | `onAppEvent` / `emitAppEvent` over a private `EventTarget`; the unsubscribe function is the return value |
| `i18n.js` | `/locales/{de,en}.json` loading, dotted-path `get()`, `apply()` over `data-i18n`, `data-i18n-placeholder`, `data-i18n-title` |

`api.js` has no dedicated AbortController support, but `signal` passes through `requestOptions` to
`fetch` — `features/insights.js` uses that. Note the interaction with GET de-duplication: aborting a
deduped GET rejects the shared promise for every caller inside the 2 s window.

Two near-identical antiforgery layers ship side by side. `security/secure-fetch.js` is the ES module
`core/api.js` uses; it captures `nativeFetch` at module load and never patches anything.
`security/browser-fetch.js` is a classic script loaded before `app.js` on every shell and *does*
replace `window.fetch`. Because the classic script runs first, `secure-fetch.js` captures the already
patched function, so a BFF write attaches the CSRF header twice from two independent token caches.
`browser-fetch.js` is the single entry on the `NoNewGlobalFetchMonkeyPatches` allow-list.

## Routing

Views: `dashboard`, `insights`, `transactions`, `accounts`, `budgets`, `contracts`, `networth`,
`analytics`, `purchases`, `tax`, `categories`, `rules`, `notifications`, `merchants`, `audit`,
`settings`. `dashboard` is `/`; every other view is `/<view>`. The server's
`MapFallbackToFile("index.html")` serves the shell for all of them and the app resolves the view from
`location.pathname` on boot, so reload, back/forward and deep links work.

Rules:

- `core/router.js` performs the URL write; `showView()` calls it.
- Features navigate through `core/navigation.js` (`navigate`, or `ctx.navScope(view, query)`), never by
  triggering another control's `.click()`.
- No `window.fw*` navigation bridges. `NoGlobalFeatureNavigationBridgeReturns` guards this and
  `window.fwNavScope`, `fwOpenBudget`, `fwSyncResponsiveSidebar` and `fwClampSidebarWidth` are gone.
- Account/group/category/merchant scope belongs in URL-backed route state.

**Exceptions, unguarded:**

- Three modules write history themselves instead of going through the router —
  `features/contracts.js:48` (`replaceState` for its filter query), `features/tax.js:204` and
  `features/coach-shell.js:557` (`/coach`, which is not a registered view at all: `coach-shell.js` is
  dynamically imported by `pwa/register-sw.js`, outside `app.js`'s module graph and outside the feature
  registry).
- Synthetic clicks still exist as a refresh mechanism. `features/wealth-specialized-assets.js`,
  `wealth-specialized-assets-extra.js` and `purchases-gpt-normal.js` call
  `document.querySelector('#refresh')?.click()` after a save — 11 sites in total. There is no
  `#refresh` element in `index.html` (only in `admin/index.html`), so with optional chaining every one
  of them is a silent no-op and the intended reload never happens. `features/compensation-extended.js:116`
  and `compensation-history.js:324` click `#calculate` on the standalone Gehalt page, and
  `features/wealth-investment-consolidation.js:189` clicks `[data-ip-tab="performance"]`; those targets
  do exist. Triggering a file picker, a download anchor or forwarding a keyboard activation is not this
  pattern and is fine.

## Feature ownership

53 flat modules in `features/`. Do not create `features/<name>/` subfolders — the files stay flat.

The shipped convention is a `renderX(ctx)` / `bindX(ctx)` pair: `bindX` is called once from `app.js`
`bind()` and wires static listeners; `renderX` is registered with the feature registry and runs on every
activation and every refresh.

`createFeatureRegistry().activate(name, ctx)` is also the refresh path. Cleanup only runs when the
active feature *name* changes; re-activating the same feature runs the handler first and disposes the
previous cleanup afterwards. Only `features/insights.js::mountInsights` currently returns a cleanup
callback (it aborts its in-flight request and drops its click handler). Everything else returns nothing,
so the "explicit lifecycle" is available but almost unused.

Rules:

- a feature owns its own DOM and does not patch another feature after render
- no retry/poll loop waiting for another renderer or a freshly created entity
- no new `*-installer.js`, `*-final-ui.js`, `*-parity-ui.js`, `*-completion-ui.js` module names
  (`NoNewPatchLayerFileNames`)
- no unbounded global listener without an owner

**Exception:** `features/accounts-presentation.js` (121 lines) restructures the app shell after render.
`ensureNav()` moves the sidebar's `[data-view="accounts"]` and `[data-view="transactions"]` buttons into
a generated `#accounts-sidebar-group`, and prepends a local sub-nav into both `#view-accounts` **and**
`#view-transactions`. `bindAccountsPresentation()` also registers a `fullworth:view-change` window
listener with no cleanup path. The guard
`AccountsPresentationUsesSharedCoreWithoutPatchObserverOrSyntheticNavigation` pins what was actually
removed there — no `/bff/` URLs, no `MutationObserver`, no `.click()`, no `fwNavScope`, drill-down by
`[data-account-id]` / `[data-group-id]` / `[data-connection-id]` — but it says nothing about shell
mutation. Accounts no longer decorates Dashboard or Wealth DOM; those own their account drill-down.

## Global observers — the documented exception

Three `MutationObserver`s watch `document.body` with `{childList, subtree}`. They are the entire
allow-list of `NoNewGlobalDomPatchObservers`, and all three do post-render decoration of DOM they do
not own:

- `ui/appearance.js` (`initAppearance`) — re-injects and re-syncs the colour/typography controls into
  `#view-settings .settings-grid` on any body mutation (rAF-coalesced), plus a second observer on
  `documentElement[lang]` that rebuilds them on a language switch. It is started by `theme-init.js`
  on `DOMContentLoaded`, so it also runs on `admin/index.html`, `compensation.html`,
  `intelligence/index.html` and the three C#-inlined import pages, where its Settings grid never exists.
- `ui/accessibility-release.js` — sets `aria-label` on `#tx-query` / `#tx-direction` / `#tx-flags`,
  `scope="col"` on the Transactions table headers, and an accessible name on `×` dialog close buttons.
- `ui/motion.js` — animates `characterData` changes of `.metric strong`, `.widget-metric strong` and
  `.budget-detail .kv strong` so numbers count up instead of snapping.

The rule that holds is narrower than "no global observers": **no new** ones, and none from a feature
module. Fixing one of the three means moving its work into the owning renderer, not adding a fourth.

## Shared UI — `wwwroot/ui/`

`dialog.js`, `confirm.js`, `buttons.js`, `money.js`, `toast.js`, `privacy.js`, `lock.js`,
`category-picker.js`, `chart-scrubber.js`, `global-search.js`, `topbar-metrics.js`, `dashboard.js`,
`ux-kit.js`, `appearance.js`, `accessibility-release.js`, `motion.js`.

- **Dialogs.** Only `ui/dialog.js` may call `createElement('dialog')`
  (`OnlySharedDialogModuleMayIntroduceNewNativeDialogs`, single-entry allow-list). `createDialog(html, {mobileMode:'sheet'})`
  adds `.fw-dialog--sheet`; `app.js` wraps it as `ctx.dialog` with the localized close label.
- **Confirm.** `ui/confirm.js` / `ctx.confirm`. Native `confirm()` is banned with an **empty**
  allow-list (`NoNewNativeConfirmCalls`) — there are no legacy exceptions left.
- **Buttons.** `ui/buttons.js` exports `ButtonRole` (Primary / Secondary / Danger), `buttonClass()` and
  `applyButtonRole()`, mapping to `.btn` + `.btn-primary`/`.btn-secondary`/`.btn-danger` in
  `styles/components.css`. Adoption is partial: only `features/budgets.js`, `features/categories.js`,
  `features/investment-performance-ui.js` and `ui/confirm.js` import it; the rest write the class strings
  literally (~35 sites) or use the older `.primary-action` from `styles/shell.css`. There is no guard.
- **`ux-kit.js`** is the render kit the finance pages share: `identityIcon()`, `categoryIconInner()`,
  `sectionCard()`, `trendBadge()`, `monogramHue()`, the official brand-logo catalog
  (`ensureOfficialBrandCatalog`) and `cycleWindow()`.
- **`ui/dashboard.js`** owns the widget grid, the dashboard edit mode and the compact net-worth preview
  (current value + `miniSparkline` over `api/net-worth/history` + change, tapping through to Wealth).
  Wealth management controls are not duplicated into it.

## Money semantics

`ui/money.js` owns formatting, privacy masking **and** the semantic variants:

```text
MoneyVariant: neutral | income | warning | danger | debt | muted
moneyClass(variant, extra) -> "amount money-<variant> <extra>"
```

`styles/components.css` maps them onto tokens. Every monetary value goes through `money()` /
`converted()` / `percent()` / `maskIdentifier()` so privacy mode stays consistent — never format or mask
per screen.

Ordinary debit, spending and contract cost are **neutral**. A negative sign is not a danger state. The
central default is `styles/components.css`: `.amount.negative{color:var(--text)}` and
`.amount.positive{color:var(--positive)}`. Red is for a real problem — overdraft, budget exceeded,
failed payment or sync, meaningful loss — or an intentionally distinct debt/loss presentation. An
income-vs-expense chart may still use two series colours; that is a legend, not a rule about every
negative amount.

`moneyClass` is used by `features/budgets.js`, `contracts.js`, `networth.js` and `transactions.js`.
Other callers still pick `.negative`/`.positive` by sign, which lands on the same neutral default but
carries no intent.

## Period semantics

The finance UI keeps four things apart:

```text
active bucket        the selected week/month/quarter/year — what the KPI means
preview window       the chart's history, ending at the active bucket
comparison           previous bucket, or an average over COMPLETED prior buckets
scope                account / group / category / merchant / direction
```

`ui/ux-kit.js::cycleWindow(cycle, offset, lang)` is the implementation. It returns `from`/`to` (the
preview window: 12 weeks, 12 months, 8 quarters, 5 years), `activeFrom`/`activeTo` (the one selected
bucket), `averageFrom`/`averageTo` (the N completed buckets immediately *before* the active one),
`granularity`, `buckets`, `label` and `isCurrent`. `offset` moves the active bucket — and with it the
trailing window — by exactly one bucket, so prev/next is one month, not one 12-month window.

Consequences that must not regress:

- `Monat` means the selected month. A 12-month chart does not turn the headline into a 12-month sum.
  `features/analytics.js::fillSpending()` publishes `Ø Ausgaben / Monat` via `avgPerBucket()`;
  `fillInout()` takes income/expense/net from the active bucket while the bars still show history.
- A running period is not a completed one: trailing averages exclude the active bucket by construction
  (`averageTo` = the day before `activeFrom`).
- Category and merchant cards request `activeBucketRange` for their totals and rows; drill-downs open
  that exact bucket.
- Dashboard previews and Analytics use the same active-period semantics. They are previews of the same
  domain queries, not a second calculation system.
- Net worth is point-in-time and is never summed across periods. Changing the range changes the history
  and the comparison, not the meaning of the headline.
- Budgets are evaluated in their own active budget period; mixed cadences are not summed into one
  headline.
- Contracts summarise on the backend's monthly equivalent; a yearly contract contributes a twelfth to
  the summary while its row stays yearly.

## Identity and drill-down

Stable identifiers beat display text: `accountId`, `accountGroupId`, `categoryId`, `merchantId`. Text
matching is a search fallback, not the aggregation identity. Merchant rows carry both
(`data-merchant-id` plus `data-merchant`) and prefer `merchantId=` for the transaction filter; aliases
resolve server-side.

`identityIcon()` is the single resolver: transfer glyph → installed brand logo (explicit
`logoAssetPath`, else the official brand-alias catalog) → category icon (emoji or the `CATEGORY_ICONS`
line-art, with German key aliases) → category-tinted monogram. Transactions, contracts and recent
bookings must not re-implement icon logic.

Category overview is root-only and disjoint: `features/analytics.js::fillCategory` filters
`!category.parentId` for the total, the donut **and** the top-6 list, then drills root → children →
matching transactions with `includeDescendants=true`. A parent includes its descendants exactly once.

## API boundary

- Only `core/api.js` constructs `/bff/backend` or `/bff/banking` URLs
  (`NoNewFeatureMayCallBffDirectly`, single-entry allow-list). Features use `ctx.api` / `ctx.bankApi` /
  `apiClient`, or `ctx.bffUrl(path)` when they need the URL itself (downloads, `<img src>`).
- The backend owns authorization, money and FX calculation, contract cadence and lifecycle, analytics
  totals, category descendants, merchant normalization, transfer/refund semantics and financial state
  transitions. Transfers and ignored bookings are excluded from income/expense analytics; pending
  bookings are not mixed into booked totals; a linked refund reduces the original expense allocation
  instead of becoming income.
- The frontend formats requests, renders DTOs, validates forms and applies purely visual sorting. It does
  not recompute finance truth.

## i18n

`locales/de.json` and `locales/en.json` (≈1080 leaf keys) are the shared source, reached through
`ctx.get('path.to.key')` and `data-i18n*` attributes.
`tests/FullWorth.Web.Tests/LocalizationTests.cs` pins that both files parse and carry the nav/page keys.

**Exception:** 17 modules ship their own inline `{de:{…}, en:{…}}` table instead —
`features/networth.js`, `tax.js`, `tax-review-extra.js`, `notifications.js`, `accounts-presentation.js`,
`purchase-articles-workspace.js`, `purchase-articles-advanced-actions.js`, the three
`wealth-real-estate-*`, both `wealth-specialized-assets*`, the three `*-import-page.js`,
`ui/appearance.js` and `ui/accessibility-release.js` — plus inline `t('de','en')` pairs in
`features/analytics.js`. The three import pages and the two `ui/` modules have a reason: they run on
shells that never boot `core/i18n.js`, so they read `finance.language` (or `documentElement.lang`)
directly. The feature modules do not. New user-visible strings belong in the locale files.

## CSS

`src/FullWorth.Web/Security/Headers/SecurityHeadersPolicy.cs` forbids `<style>` blocks, so every rule is
a `.css` file. The chain `index.html` loads, in order:

| # | File | Owns |
| --- | --- | --- |
| 1 | `styles/tokens.css` | colours, spacing, radii, shadows, typography vars; light + `[data-theme=dark]` |
| 2 | `styles/reset.css` | element normalization, base font/number features, PWA touch rules |
| 3 | `appearance.css` | the two user-chosen brand colours. Loads third *on purpose* so it can never win against `app.css`; it only sets what the user picked |
| 4 | `styles/shell.css` | shell grid, sidebar, topbar, bottom nav, `.primary-action` |
| 5 | `styles/components.css` | metrics, panels, rows, `.btn` roles, money variants, `.amount` defaults |
| 6 | `app.css` | feature styles (1233 lines) — dashboard grid, transactions, analytics, contracts, wealth, identity |
| 7 | `styles/responsive.css` | the central breakpoints |
| 8 | `styles/features/insights.css` | |
| 9 | `design-depth.css` | shadows, depth, easing, motion — visual only |
| 10 | `dialogs.css` | `dialog`, `::backdrop`, `.dialog-card`, `.dialog-actions` |
| 11–13 | `styles/features/tax.css`, `tax-review-extra.css`, `accounts.css` | eagerly loaded feature sheets |

`SharedCssLayersAreExplicitAndOrdered` pins positions 1–6, the existence of the four `styles/` files and
the absence of the retired `parity-completion.css`; `MainShellDoesNotLoadDeletedPatchModules` pins that
`index.html` no longer loads `features/accounts-ux.js`, `features/compensation-nav.js` or
`parity-completion.css` (`compensation-nav.js` still exists and is loaded by `compensation.html`, which
is its owner). `ImportPageStylesheetGuardTests` pins the equivalent chain inside the C#-inlined pages.
Positions 7–13 are convention only.

Six stylesheets sit at the `wwwroot` root: `app.css`, `appearance.css`, `design-depth.css`, `dialogs.css`
and — used only by `compensation.html` — `compensation.css` and `compensation-history.css`. They are part
of the layering, not leftovers; the "five layers under `styles/`" is the *shared* part of the chain, not
the whole of it.

23 sheets live in `styles/features/`. Most are injected by their owning module at first render
(`features/networth.js`, `receipt-imports.js`, the `purchase-*` and `wealth-*` modules, …);
`styles/features/coach.css` is injected by `pwa/register-sw.js` alongside the dynamic
`features/coach-shell.js` import.

Rules: semantic tokens only, no hardcoded colours, no per-feature radii/shadows/breakpoints. Do not fix
a component by adding a more specific override elsewhere — correct the owning layer. `design-depth.css`
and `appearance.css` may change presentation, never structure or behaviour. Standalone shells carry a
shortened chain (`auth`, `passkeys`, `intelligence` skip shell/responsive; `admin` loads the full one)
and `account-deletion` carries none.

## CSP

```text
default-src 'self'; script-src 'self'; style-src 'self'; style-src-attr 'unsafe-inline';
img-src 'self' data: https://enablebanking.com https://*.enablebanking.com;
font-src 'self'; connect-src 'self'; worker-src 'self'; manifest-src 'self'; media-src 'self';
object-src 'none'; frame-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'
```

Applied by `UseFinanceSecurityHeaders()` to every response. `Program.cs` calls
`AddFinanceSecurityHeaders()` without configuration, so `SecurityHeadersOptions.ReportOnly` stays
`false` and the policy is enforced, never report-only. What that means in practice:

- **Inline `style` attributes are allowed** — `style-src-attr 'unsafe-inline'` exists for exactly that.
  `element.style.setProperty(…)` and a template binding such as `style="--ident-h:${monogramHue(name)}"`
  are fine and used. `SecurityHeadersSourceAuditTests.NoSourceInlineStylesRemain` permits
  `style="${…}"` bindings and rejects *static* inline styles, which belong in a class.
- **`<style>` blocks and inline stylesheets are not.** `style-src` has no `'unsafe-inline'`.
- **Inline scripts and inline event handlers are not.** `script-src 'self'` covers
  `script-src-attr`, so every script is a file — this is why `pwa/register-sw.js` exists as a file and
  why it hosts the small shell extensions.
- Cross-origin images are limited to the Enable Banking hosts, which is why
  `features/accounts-presentation.js::logo()` validates bank logo URLs against them.

Two violations are in the tree today. `ui/ux-kit.js:107` puts
`onerror="this.closest('.fw-ident').classList.add('fw-ident-failed');this.remove()"` on the brand-logo
`<img>`, so the `.fw-ident-failed` fallback in `app.css:133` never fires. `ShareReceiptEndpoints.Page()`
emits an inline `<style>` block, so `/share/receipt/*` renders unstyled. Neither is caught by
`SecurityHeadersSourceAuditTests`, which only scans files under `wwwroot`.

## Service worker

`sw.js` caches **only** the static shell. `/api`, `/bff`, `/auth`, `/share` and `/connect` are always
network and never cached, so no financial data lands in the cache. Strategy is network-first with a
cache fallback: serving cached JS first once combined a fresh `index.html` with stale modules and crashed
the installed PWA. Bump `VERSION` to ship a new shell; old caches are purged on activate.

`APP_SHELL` is a hand-maintained list and is currently incomplete — 19 statically imported modules are
missing from it, including `ui/money.js`, `ui/privacy.js`, `ui/dashboard.js`, `ui/lock.js`,
`security/secure-fetch.js`, `features/categories.js`, `rules.js`, `merchants.js`, `audit.js`,
`notifications.js`, `loans.js` and `access-setup.js`. Because of network-first they are still cached
opportunistically on first successful fetch, so this degrades cold offline start rather than breaking
boot. Keep the list current when adding a module to the shell's static graph.

## Window globals

Feature-integration globals are gone. Three assignments remain and have **zero** consumers anywhere in
the repo — `window.FullWorthAppearance` (`ui/appearance.js:636`), `window.financeAntiforgery` and
`window.financeFileUpload` (`security/browser-fetch.js`). The antiforgery/upload replacements are the
module exports `refreshAntiforgeryToken()` and `snapshotUploadFile()` in `security/secure-fetch.js`.

## Verification

There is **no linter, no formatter and no automated browser or e2e test** in this repo. What exists:

- syntax check: `cp file.js /tmp/c.mjs && node --check /tmp/c.mjs`
- `ops/ui-harness/server.mjs` (`node ops/ui-harness/server.mjs`, port 8095) renders the real
  `wwwroot` against canned fixtures with no login and no database, and parses the C#-inlined import
  pages straight out of `Modules/Import/*Page.cs` so an edited page is served edited. See
  `ops/ui-harness/README.md` for its fixture rules and the `X-Harness-Fallback` header.
- live check against the running app: <http://localhost:8099> bind-mounts `wwwroot`; reload and read the
  browser console after any refactor.
- the C# guards in `tests/FullWorth.Web.Tests`: `FrontendArchitectureGuardTests` (this contract),
  `Frontend/ImportPageStylesheetGuardTests`, `Frontend/AdminPageDesignSystemGuardTests`,
  `Frontend/TouchRevealGuardTests`, `Accessibility/AccessibilityGuardTests`,
  `Responsive/ResponsiveLayoutTests`, `Theme/ThemeParityTests`,
  `Theme/TypographyAppearanceTests`, `Security/Headers/SecurityHeadersSourceAuditTests`, plus the
  per-feature `*UiBaselineTests`. Baseline tests assert module *contents*, so they pass happily while
  the page around the module is broken — that is what the stylesheet and header guards are for.

Architecture-guard allow-lists may only shrink. CI (`.github/workflows/ci.yml`) is `workflow_dispatch`
only by repository policy and is not a tag gate; do not add push or pull_request triggers to get a run.
