# FullWorth frontend architecture

Reference for `src/FullWorth.Web/wwwroot`. It describes what the frontend *is* and which rules hold,
including the places where the code knowingly breaks a rule. Rules without a guard are conventions;
rules with a guard are in `tests/FullWorth.Web.Tests/FrontendArchitectureGuardTests.cs`.

Still-open frontend work lives in `docs/OPEN_ITEMS.md`, not here.

## Shape

Vanilla ES modules, no build step, no bundler, no framework, no `node_modules`. The browser loads the
same files that sit in the repo. The dev stack next to this repo bind-mounts `wwwroot`, so an edit is
live on reload.

**One page is one folder** under `wwwroot/pages/`, holding `page.html`, `page.css` and `page.js`. The
folder path is the address: `pages/settings/security/passkeys` answers `/settings/security/passkeys`.
`ops/generate-shell.mjs` writes the menu and every `page.html` into `index.html` between markers;
`--check` fails when the file and the folder tree have drifted apart. There is one document, and it is
generated, not maintained by hand.

The only frontend HTML that is *not* a page in `wwwroot/pages/`:

| Route | Source | Why |
| --- | --- | --- |
| `/auth`, `/auth/login`, … | `auth/index.html` | there is no session yet, so there is no shell |
| `/account/deletion` | `account-deletion/index.html` | the account is switched off; a menu there would lead nowhere |
| `/share/receipt/{token}` | `Modules/Purchases/ShareReceiptEndpoints.cs` | a public link, opened without an account |

The three import pages used to belong on that list: they kept their `<head>` and body in C# raw string
literals, repeated the whole stylesheet chain by hand, and `ops/ui-harness/server.mjs` parsed those
literals so an edited page arrived edited. They are ordinary pages under `pages/settings/import/` now,
and both the guard that pinned their stylesheet chain and the harness's parser are gone.

`ShareReceiptEndpoints.Page()` is a different animal: a self-contained mini page with an inline
`<style>` block. That block is blocked by the CSP (see [CSP](#csp)), so those pages render unstyled.

## Shells

| Shell | Route | Own scripts |
| --- | --- | --- |
| `index.html` | `/` + every view path (`MapFallbackToFile`) | `app.js`, `app/motion.js` |
| `auth/index.html` | `/auth`, `/auth/login`, `/auth/register`, … (`Program.cs`) | `auth/auth.js` |
| `account-deletion/index.html` | `/account/deletion` | `account-deletion/deletion.js` |

Admin, Passkeys, Gehalt, Import and Intelligence were shells of their own once. Each had no side menu,
and the old `compensation.html` carried a hand-copied nav bar that had already fallen behind the real one.
They are pages of this shell now.

`index.html` carries every view container and toggles `.active` on one of them. The rule that makes
that hold is `.view:not(.active){display:none}` in `styles/shell.css`: two classes of specificity, so
a page stylesheet loaded later cannot win by accident — which is exactly what happened when
`.import-center-view{display:grid}` showed the import page on top of every other screen.

`Program.cs` does not call `UseDefaultFiles()`, so a directory shell has to be requested by its full
path.

Three small scripts are shared across shells rather than owned by one:

- `app/boot.js` — classic script in `<head>`, deliberately not a module. It writes everything that has
  to be true before the first paint onto `<html>`: theme, brand colours, font, typography, the
  collapsed state and width of the sidebar, the privacy flag. Whatever this file does not do is a
  layout shift.
- `security/browser-fetch.js` — the `window.fetch` antiforgery patch (see [Core services](#core-services--wwwrootcore)).
- `pwa/register-sw.js` — registers `sw.js`, nothing else. It used to pull the Coach in with an
  `import()` after the first paint; the Coach is a page now and `app.js` loads it with everything else.

`account-deletion/deletion.css` is deliberately self-contained: hardcoded hex colours, Inter, its own
dark-mode block, no token layer loaded. It is the one page that does not use design tokens.

## Bootstrap — `app.js`

447 lines. It owns bootstrap, shell and composition, and nothing else:

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
`fetch` — `pages/insights/page.js` uses that. Note the interaction with GET de-duplication: aborting a
deduped GET rejects the shared promise for every caller inside the 2 s window.

Two near-identical antiforgery layers ship side by side. `security/secure-fetch.js` is the ES module
`core/api.js` uses; it captures `nativeFetch` at module load and never patches anything.
`security/browser-fetch.js` is a classic script loaded before `app.js` on every shell and *does*
replace `window.fetch`. Because the classic script runs first, `secure-fetch.js` captures the already
patched function, so a BFF write attaches the CSRF header twice from two independent token caches.
`browser-fetch.js` is the single entry on the `NoNewGlobalFetchMonkeyPatches` allow-list.

## Routing

Views (`wwwroot/app/menu.js`, the single menu source): `dashboard`, `insights`, `coach`, `accounts`,
`transactions`, `purchases`, `merchants`, `budgets`, `contracts`, `compensation`, `pension`, `tax`,
`analytics`, `networth`, `categories`, `rules`, `notifications`, `audit`, `settings`, `admin`.
`dashboard` is `/`; every other view is `/<view>`.

Below some of them sit subpages with an address but no menu entry, because a menu with everything in
it is no menu: `/settings/security/passkeys`, `/settings/import`,
`/settings/import/finanzguru/xlsx`, `/settings/import/broker-pdf`, `/settings/intelligence`. They mark
their parent in the menu.

`MapFallbackToFile("index.html")` serves the shell for all of them and the app resolves the view from
`location.pathname` on boot, so reload, back/forward and deep links work.

Rules:

- `core/router.js` performs the URL write; `showView()` calls it.
- Pages navigate through `core/navigation.js` (`navigate`, or `ctx.navScope(view, query)`), never by
  triggering another control's `.click()`.
- No `window.fw*` navigation bridges. `NoGlobalFeatureNavigationBridgeReturns` guards this.
- Account/group/category/merchant scope belongs in URL-backed route state.
- Nothing is added to the menu at runtime. Coach and the accounts subtree used to be inserted after
  the first paint; that made the delivered order different from the markup and was a shift source.
  `MenuParityTests` compares sidebar, phone bar and the "Mehr" sheet against the one definition.

**Exceptions, unguarded:** three pages write history themselves instead of going through the router —
`pages/contracts/page.js` (`replaceState` for its filter query), `pages/tax/page.js` and
`pages/pension/page.js` (`pushState` for their own detail path).

The synthetic-click refresh is gone. Ten call sites did
`document.querySelector('#refresh')?.click()` after a save; there is no `#refresh` element in this
document, so the optional chaining swallowed it and the reload never happened. They emit
`surface:reload` now and `app.js` answers with `loadCurrent()`.

## Page ownership

One page is one folder under `pages/`. A page may bring more modules than the three files — Käufe and
Vermögen bring a dozen each — but they live in that folder and no other page imports them.
`features/` is what is left over: seven modules that genuinely belong to no single page
(`ux-kit`, `category-picker`, `global-search`, `access-setup`, `data-completeness`, `sharing`,
`wealth-portability`).

The shipped convention is a `renderX(ctx)` / `bindX(ctx)` pair: `bindX` is called once from `app.js`
`bind()` and wires static listeners; `renderX` is registered with the feature registry and runs on every
activation and every refresh.

`createFeatureRegistry().activate(name, ctx)` is also the refresh path. Cleanup only runs when the
active page *name* changes; re-activating the same page runs the handler first and disposes the
previous cleanup afterwards. Only `pages/insights/page.js::mountInsights` returns a cleanup callback
(it aborts its in-flight request and drops its click handler). Everything else returns nothing, so the
"explicit lifecycle" is available but almost unused.

Rules, and `FrontendStructureGuardTests` is the version that argues back:

- a page owns its own DOM and does not patch another page after render
- a page never imports another page; `components/` knows neither a page nor the server
- a module never re-exports a name it calls itself — `export { x } from …` does not bind `x` here,
  and that shipped as `esc is not defined` on five pages
- no `<link>` from JavaScript and no `import()`; everything is there at the first paint
- no retry/poll loop waiting for another renderer or a freshly created entity
- no new `*-installer.js`, `*-final-ui.js`, `*-parity-ui.js`, `*-completion-ui.js` module names
  (`NoNewPatchLayerFileNames`)

**Exception:** `pages/accounts/presentation.js` decorates the account rows after the bundle arrives.
That decoration is why the accounts list was the worst shift in the app (a row grew from 73 to 125
pixels); it now happens on a list that is still detached, and the finished list is inserted once.
`bindAccountsPresentation()` registers a `fullworth:view-change` listener with no cleanup path.

## Global observers — the documented exception

Three `MutationObserver`s remain, and they are the entire allow-list of
`NoNewGlobalDomPatchObservers`:

- `app/appearance.js` (`initAppearance`) — re-injects and re-syncs the colour/typography controls into
  `#view-settings .settings-grid` on any body mutation (rAF-coalesced), plus a second observer on
  `documentElement[lang]` that rebuilds them on a language switch.
- `components/accessibility-release.js` — sets `aria-label` on the Transactions filters, `scope="col"`
  on its table headers, and an accessible name on dialog close buttons.
- `app/motion.js` — animates `characterData` changes of `.metric strong`, `.widget-metric strong` and
  `.budget-detail .kv strong` so numbers count up instead of snapping.

The rule is narrower than "no global observers": **no new** ones, and none from a page module. Fixing
one of the three means moving its work into the owning renderer, not adding a fourth. Four more were
removed rather than allowed — on the transactions list, the tax panels, the primary action's label and
the category circles — and each one's replacement is written down where it used to sit.

## Shared UI — `wwwroot/components/`

`dialog.js`, `confirm.js`, `buttons.js`, `money.js`, `toast.js`, `privacy.js`, `empty.js`,
`form-dialog.js`, `combobox.js`, `password-toggle.js`, `balance-meaning.js`, `chart-scrubber.js`,
`topbar-metrics.js`, `accessibility-release.js`, `mobile-interactions.js`.

- **Dialogs.** Only `components/dialog.js` may call `createElement('dialog')`
  (`OnlySharedDialogModuleMayIntroduceNewNativeDialogs`, single-entry allow-list).
  `createDialog(html, {mobileMode:'sheet'})` adds `.fw-dialog--sheet`; `app.js` wraps it as
  `ctx.dialog` with the localized close label.
- **Confirm.** `components/confirm.js` / `ctx.confirm`. Native `confirm()` is banned with an **empty**
  allow-list (`NoNewNativeConfirmCalls`) — there are no legacy exceptions left.
- **Buttons.** `components/buttons.js` exports `ButtonRole` (Primary / Secondary / Danger),
  `buttonClass()` and `applyButtonRole()`, mapping to `.btn` plus `.btn-primary` / `.btn-secondary` /
  `.btn-danger` in `styles/components.css`. Adoption is partial: most call sites write the class
  strings literally or use the older `.primary-action` from `styles/shell.css`. There is no guard.
- **`features/ux-kit.js`** is the render kit the finance pages share: `identityIcon()`,
  `categoryIconInner()`, `sectionCard()`, `trendBadge()`, `monogramHue()`, the official brand-logo
  catalog (`ensureOfficialBrandCatalog`) and `cycleWindow()`. It sits in `features/`, not
  `components/`, because it knows what a merchant and a category are.
- **`pages/dashboard/page.js`** owns the widget grid, the dashboard edit mode and the compact net-worth
  preview (current value plus `miniSparkline` over `api/net-worth/history` and the change, tapping
  through to Wealth). Wealth management controls are not duplicated into it.

## Money semantics

`components/money.js` owns formatting, privacy masking **and** the semantic variants:

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

`moneyClass` is used by the Budgets, Verträge, Vermögen and Buchungen pages.
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

`features/ux-kit.js::cycleWindow(cycle, offset, lang)` is the implementation. It returns `from`/`to` (the
preview window: 12 weeks, 12 months, 8 quarters, 5 years), `activeFrom`/`activeTo` (the one selected
bucket), `averageFrom`/`averageTo` (the N completed buckets immediately *before* the active one),
`granularity`, `buckets`, `label` and `isCurrent`. `offset` moves the active bucket — and with it the
trailing window — by exactly one bucket, so prev/next is one month, not one 12-month window.

Consequences that must not regress:

- `Monat` means the selected month. A 12-month chart does not turn the headline into a 12-month sum.
  `pages/analytics/page.js::fillSpending()` publishes `Ø Ausgaben / Monat` via `avgPerBucket()`;
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

Category overview is root-only and disjoint: `pages/analytics/page.js::fillCategory` filters
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

**Exception:** sixteen modules ship their own inline `{de:{…}, en:{…}}` table instead — Vermögen,
Altersvorsorge, Steuern, Käufe, Benachrichtigungen and the Übersicht, plus `app/appearance.js` and
`components/accessibility-release.js`; `pages/coach/page.js` and `pages/insights/page.js` use inline
`t('de','en')` pairs. The two shell modules have a reason: they also run before `core/i18n.js` has
loaded anything. The pages do not. New user-visible strings belong in the locale files — Coach moved
its markup strings there when it became a page, and the rest of its module is the remaining work.

## CSS

`src/FullWorth.Web/Security/Headers/SecurityHeadersPolicy.cs` forbids `<style>` blocks, so every rule is
a `.css` file. The chain `index.html` loads, in order:

| # | File | Owns |
| --- | --- | --- |
| 1 | `styles/tokens.css` | colours, spacing, radii, shadows, typography vars; light plus `[data-theme=dark]` |
| 2 | `styles/reset.css` | element normalization, base font/number features, PWA touch rules |
| 3 | `appearance.css` | the two user-chosen brand colours. Loads third *on purpose* so it can never win against `app.css`; it only sets what the user picked |
| 4 | `styles/shell.css` | shell grid, sidebar, topbar, bottom nav, `.primary-action`, `.view:not(.active)` |
| 5 | `styles/components.css` | metrics, panels, rows, `.btn` roles, money variants, `.amount` defaults, the one shimmer and the one `.sr-only` |
| 6 | `app.css` | what is still shared across pages and has not found its layer yet |
| 7 | `styles/responsive.css` | the central breakpoints |
| 8 | `design-depth.css` | shadows, depth, easing, motion — visual only |
| 9 | `dialogs.css` | `dialog`, `::backdrop`, `.dialog-card`, `.dialog-actions` |
| 10 | `pages/*/page.css` | one per page, written into the document by `ops/generate-shell.mjs` |
| 11 | `styles/mobile-polish.css` | the phone layer, last on purpose |

`SharedCssLayersAreExplicitAndOrdered` pins positions 1–6 and the existence of the `styles/` files.
`FrontendStructureGuardTests.The_root_collects_no_new_stylesheets` names the four that still sit at the
`wwwroot` root — `app.css`, `appearance.css`, `design-depth.css`, `dialogs.css`. That list may get
shorter, never longer.

`styles/features/` is gone. It held 23 sheets, 19 of which were appended to the `<head>` by their owning
module at first render, so every screen drew once unstyled and then rebuilt itself — the second-largest
shift source in the application. A stylesheet belongs to its page now, and
`FrontendStructureGuardTests.No_module_appends_a_stylesheet` keeps it that way.

Rules: semantic tokens only, no hardcoded colours, no per-feature radii/shadows/breakpoints. Do not fix
a component by adding a more specific override elsewhere — correct the owning layer. `design-depth.css`
and `appearance.css` may change presentation, never structure or behaviour. `auth/` carries a shortened
chain and `account-deletion/` carries none.

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
  `script-src-attr`, so every script is a file — this is why `pwa/register-sw.js` exists as a file.
- Cross-origin images are limited to the Enable Banking hosts, which is why
  `pages/accounts/presentation.js::logo()` validates bank logo URLs against them.

Two violations are in the tree today. `features/ux-kit.js` puts
`onerror="this.closest('.fw-ident').classList.add('fw-ident-failed');this.remove()"` on the brand-logo
`<img>`, so the `.fw-ident-failed` fallback in `app.css:133` never fires. `ShareReceiptEndpoints.Page()`
emits an inline `<style>` block, so `/share/receipt/*` renders unstyled. Neither is caught by
`SecurityHeadersSourceAuditTests`, which only scans files under `wwwroot`.

## Service worker

`sw.js` caches **only** the static shell. `/api`, `/bff`, `/auth`, `/share` and `/connect` are always
network and never cached, so no financial data lands in the cache. Strategy is network-first with a
cache fallback: serving cached JS first once combined a fresh `index.html` with stale modules and crashed
the installed PWA. Bump `VERSION` to ship a new shell; old caches are purged on activate.

`APP_SHELL` is a hand-maintained list, and `Pwa/PwaOfflineShellCoverageTests` walks the real import
graph from `index.html` to insist that everything the shell reaches is in it — both directions, so a
moved file fails the test instead of quietly breaking a cold offline start. It was 52 entries short
when that test was written.

## Window globals

Feature-integration globals are gone. Three assignments remain and have **zero** consumers anywhere in
the repo — `window.FullWorthAppearance` (`app/appearance.js`), `window.financeAntiforgery` and
`window.financeFileUpload` (`security/browser-fetch.js`). The antiforgery/upload replacements are the
module exports `refreshAntiforgeryToken()` and `snapshotUploadFile()` in `security/secure-fetch.js`.

## Verification

There is **no linter and no formatter** in this repo, and until the layout-stability test there was no
automated browser test either. What exists:

- syntax check: `cp file.js /tmp/c.mjs && node --check /tmp/c.mjs`
- `node ops/generate-shell.mjs` after adding a page; `--check` fails when `index.html` and the folder
  tree have drifted apart
- `ops/ui-harness/server.mjs` (`node ops/ui-harness/server.mjs`, port 8095) renders the real `wwwroot`
  against canned fixtures with no login and no database. See `ops/ui-harness/README.md` for its
  fixture rules and the `X-Harness-Fallback` header.
- live check against the running app: the dev stack next to this repo bind-mounts `wwwroot`; reload and
  read the browser console after any refactor.
- the C# guards in `tests/FullWorth.Web.Tests`: `FrontendArchitectureGuardTests` (this contract),
  `Frontend/FrontendStructureGuardTests` (the six structure rules),
  `Frontend/MenuParityTests` (phone and desktop against the one definition),
  `Frontend/LayoutStabilityTests` (Playwright, a real shift measurement per page and size),
  `Frontend/AdminPageDesignSystemGuardTests`, `Frontend/SingleElementQueryGuardTests`,
  `Frontend/ToastVisibilityTests`, `Frontend/TouchRevealGuardTests`,
  `Accessibility/AccessibilityGuardTests`, `Responsive/ResponsiveLayoutTests`, `Theme/ThemeParityTests`,
  `Theme/TypographyAppearanceTests`, `Security/Headers/SecurityHeadersSourceAuditTests`, plus the
  per-page `*UiBaselineTests`. Baseline tests assert module *contents*, so they pass happily while the
  page around the module is broken — that is what the structure and layout guards are for, and the
  `esc is not defined` bug that shipped to `main` is the proof: every string test was green.

Architecture-guard allow-lists may only shrink. CI (`.github/workflows/ci.yml`) is `workflow_dispatch`
only by repository policy and is not a tag gate; do not add push or pull_request triggers to get a run.
