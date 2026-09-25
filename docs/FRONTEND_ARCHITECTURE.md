# FullWorth frontend architecture

Reference for `src/FullWorth.Web/wwwroot` and the Razor pages in `src/FullWorth.Web/Pages`. It
describes what the frontend *is* and which rules hold, including the places where the code knowingly
breaks a rule. Rules without a guard are conventions; rules with a guard name it.

Still-open frontend work lives in GitHub Issues, not here.

## Shape

Vanilla ES modules, no build step, no bundler, no framework, no `node_modules`. The browser loads the
same files that sit in the repo.

**Every address is a Razor page** (#154). There is no client-side router and no fallback: an address
without a page answers 404. A page is two halves that belong together:

| Half | Where | Holds |
| --- | --- | --- |
| markup | `Pages/<Area>/Index.cshtml` | `@page "/address"`, the page's static markup, its `@section Styles` (`page.css`) and `@section Scripts` (`entry.js`) |
| behaviour | `wwwroot/pages/<area>/` | `entry.js` (the page's one module script), `page.js` and whatever else the page brings, `page.css` |

`Pages/Shared/_Layout.cshtml` is the frame every page shares: the `<head>` with the stylesheet chain,
the sidebar (`_Navigation.cshtml`) and the bottom bar (`_BottomNavigation.cshtml`), both rendered on
the server so nothing is inserted after the first paint. Tabs with their own address (`/pension/*`,
`/tax/review`) answer with their page through extra routes in `Program.cs`;
`SubpageAddressTests` reads those addresses out of the page modules and requests every one.

The only frontend HTML that is *not* a Razor page:

| Route | Source | Why |
| --- | --- | --- |
| `/auth`, `/auth/login`, … | `auth/index.html` | there is no session yet, so there is no frame |
| `/account/deletion` | `account-deletion/index.html` | the account is switched off; a menu there would lead nowhere |
| — | `offline/index.html` | shown by the service worker when a page cannot be loaded at all (see [Service worker](#service-worker)) |
| `/share/receipt/{token}` | `Modules/Purchases/ShareReceiptEndpoints.cs` + `share-receipt/share-receipt.css` | a public link, opened without an account |

`account-deletion/deletion.css` is deliberately self-contained: hardcoded hex colours, its own
dark-mode block, no token layer loaded. It is the one page that does not use design tokens.

## Static files

`Program.cs` serves `wwwroot` through **`MapStaticAssets()`**, and the Razor pages through
`MapRazorPages().WithStaticAssets()`:

- In markup, `href="~/styles/tokens.css"` and `src="~/pages/pension/entry.js"` resolve to the
  fingerprinted address (`/styles/tokens.892ywcc37e.css`), served `immutable`. A release names new
  addresses, so a stale file in any cache matches none of them. The plain name still answers, with
  `no-cache` and an ETag. Everything is precompressed (br/gzip).
- Modules import each other by relative path (`../../app/shell.js`), i.e. by plain name.
- Kept plain on purpose: the font preload (the stylesheet loads the font by its plain name; a
  fingerprinted preload would download it twice), `manifest.json?v=` and the `/pwa/*` icons.
- `MapStaticAssets()` carries `.AllowAnonymous()`: static assets are endpoints now, and the fallback
  authorization policy would otherwise put the login page's own stylesheet behind a login.
  `StaticAssetsTests` requests them anonymously.
- **`FullWorthWeb:LiveStaticFiles=true`** switches back to plain `UseStaticFiles` for the dev stack in
  `../local`, which bind-mounts `wwwroot`: MapStaticAssets reads a build-time manifest and would not
  see an edit. `LiveStaticFilesTests` covers that mode.

**Icons** come from one SVG sprite, `wwwroot/icons/sprite.svg`, referenced with
`<use href="…sprite.<hash>.svg#id">`. `IconSprite` (C#) builds the fingerprinted address for the
server-rendered navigation; the layout puts it on `<body data-sprite>`, and `components/sprite.js`
(`spriteHref(symbol)`) reads it for everything the browser draws. Symbol ids are `nav-*` (navigation),
`cat-*` (category icons, mapped from category keys and their German aliases in `components/icons.js`)
and `ui-*` (everything else: toggles, actions, account kinds, empty states; one drawing per meaning).
The static documents (`auth/`) point at the plain name and carry `data-sprite="/icons/sprite.svg"`, so
`spriteHref` has one rule everywhere. A `<use>` on a missing id draws nothing without any error, so
`IconSpriteTests` checks every id named anywhere — and `No_icon_is_drawn_inline` keeps a second
catalogue from growing back: an `<svg>` whose content has no placeholder is a fixed picture and belongs
in the sprite. Charts build their geometry from data and stay inline; the one illustration that is not
an icon (the chart preview in Einstellungen) is named there.

## Boot order of a page

1. **Classic scripts in `<head>`**, deliberately not modules: `pwa/standalone-init.js`, `app/theme.js`
   (the one theme engine, `window.FullWorthTheme`), `app/boot.js` (theme, brand colours, sidebar width
   and collapsed state, privacy flag — everything that has to be true before the first paint; whatever
   it does not do is a layout shift), `pwa/register-sw.js`. `_Navigation.cshtml` adds
   `app/nav-state.js` right after the sidebar markup, so the group state is restored while parsing.
2. **The page's `entry.js`**, the only module script:

   ```js
   import { startShellPage } from '../../app/shell.js';
   import { renderPension, bindPension } from './page.js';
   await startShellPage(async context => { … });
   ```

3. **`app/shell.js::startShellPage(render)`** — the same for every page: installs navigation, loads
   the locale and applies it, renders the page header, binds topbar, theme and privacy toggles, the
   overflow menus, global search and the "Mehr" sheet, loads the session
   (`/auth/capabilities`, spaces), then calls `render(ctx)` and starts the inactivity lock.

`ctx` (built in `app/page-context.js::createPageContext`) is what a page receives: `api`, `bankApi`,
`get` (i18n), `esc`, `date`, `dateTime`, `toast`, `dialog`, `money`, `isPrivate`, `categoryOptions`,
`jsonBody`, `empty`, `skeleton`, `reload`, `confirm`, `bffUrl`, `apiText`, `navScope`, `showView`.

## Core services — `wwwroot/core/`

| Module | Owns |
| --- | --- |
| `api.js` | the only BFF client. `/bff/backend/…` and `/bff/banking/…` URL construction, `fullWorthSpaceId` injection, JSON parsing, `error.status`/`error.detail`, in-flight GET de-duplication (2 s TTL, 200-entry cap), cache flush on any non-GET. Antiforgery and upload snapshots come from `security/secure-fetch.js` |
| `services.js` | the singletons: `apiClient`, `api`, `bankApi`, `i18n` |
| `navigation.js` | `installNavigation(handler)` / `navigate(view, options)`. `startShellPage` installs a handler that turns a view into an address (`app/routes.js`) and assigns it |
| `state.js` | global state only: `lang`, `theme`, `messages`, `view`, `spaces`, `space`, `capabilities` |
| `event-bus.js` | `onAppEvent` / `emitAppEvent` over a private `EventTarget`; the unsubscribe function is the return value |
| `i18n.js` | `/locales/{de,en}.json` loading, dotted-path `get()`, `apply()` over `data-i18n`, `data-i18n-placeholder`, `data-i18n-title` |
| `html.js` | `esc` and the other markup helpers |

`api.js` has no dedicated AbortController support, but `signal` passes through `requestOptions` to
`fetch` — `pages/insights/page.js` uses that. Note the interaction with GET de-duplication: aborting a
deduped GET rejects the shared promise for every caller inside the 2 s window.

`core/router.js`, `core/feature-registry.js` and the `window.fetch` patch `security/browser-fetch.js`
belonged to the one-document shell and are gone. With the patch went the old oddity that a BFF write
attached its CSRF header twice from two independent token caches.

## Navigation

**Menu source.** The server renders the navigation from `Navigation/NavigationCatalog.cs`.
`wwwroot/app/menu.js` is its mirror for the two places that cannot run C#: the "Mehr" sheet the
browser builds, and `ops/ui-harness`. `NavigationCatalogParityTests` keeps the two identical, and
`MenuParityTests` compares sidebar, bottom bar and "Mehr" against the one definition. Nothing is added
to the menu at runtime.

Subpages with an address but no menu entry — `/settings/security/passkeys`, `/settings/import`,
`/settings/import/finanzguru/xlsx`, `/settings/import/broker-pdf`, `/settings/intelligence`,
`/settings/bank-connections`, the account detail — mark their parent in the menu. `app/routes.js`
lists them for the browser, `NavigationCatalog` for the server.

Rules:

- Pages navigate through `core/navigation.js` (`navigate`, or `ctx.navScope(view, query)`), never by
  triggering another control's `.click()`. A navigation is a real page load.
- No `window.fw*` navigation bridges. `NoGlobalFeatureNavigationBridgeReturns` guards this.
- Account/group/category/merchant scope belongs in the query string.
- After a save, a page emits `surface:reload`; the shell answers with `ctx.reload`.

**Exceptions, unguarded:** a few pages write history themselves — `pages/contracts/page.js`
(`replaceState` for its filter query), `pages/tax/page.js` and `pages/pension/page.js` (`pushState`
for their tabs), and several `entry.js` files that drop a one-shot query parameter after reading it.

## Page ownership

One page is one folder under `wwwroot/pages/`. A page may bring more modules than `page.js` — Käufe
and Vermögen bring a dozen each — but they live in that folder and no other page imports them.
`features/` holds what more than one area needs and what knows the server or the domain, so
`components/` may not have it: `ux-kit`, `data-completeness`, `wealth-portability`, and — since the
page split made it visible — `bank-connections` (the bank flows, opened from Konten, Übersicht,
Einstellungen and their own page), `access-setup` (Einstellungen and the first start), `insights`
(the page and the dashboard block) and `investment-performance` (Vermögen and a depot account).

**A shared module takes its stylesheet with it**, as `styles/<name>.css`, and every page that uses
the module links it in its `@section Styles`, before its own `page.css`. Those four used to style
themselves from one page's `page.css`, so on the other page they drew unstyled: the "Wichtig für dich"
block on the dashboard matched not a single rule. `A_page_does_not_reach_into_another_page` resolves
every import (sibling paths and side-effect imports too — its first version saw neither) with the
area under `pages/` as the boundary, so a subpage may still use its area's modules.

The convention is a `renderX(ctx)` / `bindX(ctx)` pair: `entry.js` calls `bindX` once and `renderX`
inside `startShellPage`.

Rules, and `FrontendStructureGuardTests` is the version that argues back:

- a page owns its own DOM and does not patch another page after render
- a page never imports another page; `components/` knows neither a page nor the server
- a module never re-exports a name it calls itself — `export { x } from …` does not bind `x` here,
  and that shipped as `esc is not defined` on five pages
- no `<link>` from JavaScript and no `import()` in a page; everything is there at the first paint
  (`app/boot.js`, a classic script, is the one place that uses `import()`)
- no retry/poll loop waiting for another renderer or a freshly created entity
- no new `*-installer.js`, `*-final-ui.js`, `*-parity-ui.js`, `*-completion-ui.js` module names
  (`NoNewPatchLayerFileNames`)

**Exception:** `pages/accounts/presentation.js` decorates the account rows after the bundle arrives.
That decoration is why the accounts list was the worst shift in the app (a row grew from 73 to 125
pixels); it now happens on a list that is still detached, and the finished list is inserted once.

## Global observers — the documented exception

One `MutationObserver` remains, and it is the entire allow-list of `NoNewGlobalDomPatchObservers`:
`components/accessibility-release.js` sets `aria-label` on the Transactions filters, `scope="col"` on
its table headers, and an accessible name on dialog close buttons.

The rule is narrower than "no global observers": **no new** ones, and none from a page module. Fixing
the one that is left means moving its work into the owning renderer, not adding a second. Two went
recently: `app/appearance.js` (the colour panel is static markup now) and `app/motion.js`, which made
figures count up by watching their text — figures stand at once, one paint and no change after it.

## Shared UI — `wwwroot/components/`

`dialog.js`, `confirm.js`, `buttons.js`, `money.js`, `toast.js`, `privacy.js`, `empty.js`,
`form-dialog.js`, `combobox.js`, `password-toggle.js`, `balance-meaning.js`, `chart-scrubber.js`,
`topbar-metrics.js`, `accessibility-release.js`, `mobile-interactions.js`, `icons.js`, `sprite.js`.

- **Dialogs.** Only `components/dialog.js` may call `createElement('dialog')`
  (`OnlySharedDialogModuleMayIntroduceNewNativeDialogs`, single-entry allow-list).
  `createDialog(html, {mobileMode:'sheet'})` adds `.fw-dialog--sheet`; `app/page-context.js` wraps it
  as `ctx.dialog` with the localized close label.
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
`logoAssetPath`, else the official brand-alias catalog) → category icon (emoji or a `cat-*` symbol of the sprite,
with German key aliases) → category-tinted monogram. Transactions, contracts and recent
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
a `.css` file. The chain `Pages/Shared/_Layout.cshtml` loads, in order:

| # | File | Owns |
| --- | --- | --- |
| 1 | `styles/tokens.css` | colours, spacing, radii, shadows, typography vars; light plus `[data-theme=dark]` |
| 2 | `styles/reset.css` | element normalization, base font/number features, PWA touch rules |
| 3 | `styles/appearance.css` | the two user-chosen brand colours. Loads third *on purpose* so it can never win against `app.css`; it only sets what the user picked |
| 4 | `styles/shell.css` | shell grid, sidebar, topbar, bottom nav, `.primary-action` |
| 5 | `styles/components.css` | metrics, panels, rows, `.btn` roles, money variants, `.amount` defaults, the one shimmer and the one `.sr-only` |
| 6 | `styles/app.css` | what is still shared across pages and has not found its layer yet |
| 7 | `styles/responsive.css` | the central breakpoints |
| 8 | `styles/design-depth.css` | shadows, depth, easing, motion — visual only |
| 9 | `styles/dialogs.css` | `dialog`, `::backdrop`, `.dialog-card`, `.dialog-actions` |
| 10 | `styles/coach.css` | the Coach dock, which floats over every page — so the frame loads it, at the place it used to have as page CSS |
| 11 | `pages/<area>/page.css` | the page's own sheet, through its `@section Styles` |

`SharedCssLayersAreExplicitAndOrdered` pins positions 1–6 and the existence of the `styles/` files.
`FrontendStructureGuardTests.The_root_collects_no_stylesheets` insists the `wwwroot` root holds no
`.css` at all. Four used to sit there — `app.css`, `appearance.css`, `design-depth.css`,
`dialogs.css` — and the guard used to name them as a shrinking exception list. The list is empty now.

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

Two violations sat in the tree for months, and both broke silently — no build failed, no request
failed, only the browser refused and said so in a console nobody reads.

`features/ux-kit.js` put `onerror="this.remove()"` on the brand-logo `<img>`. `script-src 'self'`
covers `script-src-attr`, so the handler never ran and a failed logo stayed put — and because a brand
logo carries its own plate (`.fw-ident-brand-logo` has a background), that empty frame covered the
monogram stacked underneath it. It is one `bindIdentityIcons()` listener now: an image error does not
bubble, but it can be caught in the capture phase, so one listener replaces an attribute per image.
`ShareReceiptEndpoints.Page()` emitted an inline `<style>` block, which `style-src 'self'` discards, so
`/share/receipt/*` rendered unstyled; that sheet is a file under `share-receipt/` now and uses tokens,
which also gave the page the dark mode it never had.

Neither was caught, because `SecurityHeadersSourceAuditTests` only read `*.html` — and markup is also
born in JavaScript template strings and C# raw strings. Its
`Nothing_generates_markup_the_policy_refuses_to_run` reads those too.

## Service worker

`sw.js` caches **one thing**: the page shown without a connection, `offline/index.html`, and the
files it loads (`OFFLINE_ASSETS`). When loading a page fails at the network, the worker answers with
it instead of the browser's error page; "Erneut versuchen" reloads, and so does the `online` event.
Every other request goes past the worker, and nothing it receives is ever stored — no `cache.put`
anywhere, which `PwaAssetsTests` pins. Push notifications are the worker's other job.

Why no more: a page is HTML, and the application's HTML is never cached because it carries personal
data. Without its HTML no page opens offline, so a precache of modules saves nothing. Until #154 the
worker held more than a hundred files for an offline cold start that could never succeed — and since
MapStaticAssets the pages request their files by fingerprint, so the precache would not even have
matched. Gehalt, long listed as the exception, calculates on the server (`api/compensation/calculate`).
Speed comes from the browser cache instead: fingerprinted files are `immutable`.

`Pwa/PwaOfflinePageTests` holds the list and the page together: the list is exactly what the page
loads (including the font its stylesheet names), the page asks no server, and every listed file comes
without signing in — `cache.addAll` fails on a single redirect to the login, and then the worker never
installs. Bump `VERSION` when the offline page changes; `activate` purges older caches.

## Window globals

Feature-integration globals are gone. `window.FullWorthTheme` (`app/theme.js`) is the one that is
meant: classic scripts that run before any module need the theme engine. `window.FullWorthAppearance`
(`app/appearance.js`) remains and has **zero** consumers. Antiforgery and upload snapshots are the
module exports `refreshAntiforgeryToken()` and `snapshotUploadFile()` in `security/secure-fetch.js`.

## Verification

There is **no linter and no formatter** in this repo, and until the layout-stability test there was no
automated browser test either. What exists:

- syntax check: `cp file.js /tmp/c.mjs && node --check /tmp/c.mjs`
- `ops/ui-harness/server.mjs` (`node ops/ui-harness/server.mjs`, port 8095) renders the real `wwwroot`
  and the Razor pages (`ops/ui-harness/razor.mjs`, which knows exactly the constructs `_Layout` uses
  and throws on anything else) against canned fixtures with no login and no database. It serves plain
  names, not fingerprints, and caches modules — restart it after an edit. See `ops/ui-harness/README.md` for its
  fixture rules and the `X-Harness-Fallback` header.
- live check against the running app: the dev stack next to this repo bind-mounts `wwwroot`; reload and
  read the browser console after any refactor.
- the C# guards in `tests/FullWorth.Web.Tests`: `FrontendArchitectureGuardTests` (this contract),
  `Frontend/FrontendStructureGuardTests` (the six structure rules),
  `Frontend/MenuParityTests` (phone and desktop against the one definition),
  `Frontend/LayoutStabilityTests` (Playwright, a real shift measurement per page and size),
  `Frontend/IconAlignmentTests` (Playwright, every icon-only container measured against its icon —
  the string guards cannot see that a 18px icon sits 12px from the left and 6px from the right),
  `Frontend/AdminPageDesignSystemGuardTests`, `Frontend/SingleElementQueryGuardTests`,
  `Frontend/ToastVisibilityTests`, `Frontend/TouchRevealGuardTests`,
  `Accessibility/AccessibilityGuardTests`, `Responsive/ResponsiveLayoutTests`, `Theme/ThemeParityTests`,
  `Theme/TypographyAppearanceTests`, `Security/Headers/SecurityHeadersSourceAuditTests`, plus the
  per-page `*UiBaselineTests`. Baseline tests assert module *contents*, so they pass happily while the
  page around the module is broken — that is what the structure and layout guards are for, and the
  `esc is not defined` bug that shipped to `main` is the proof: every string test was green.

Architecture-guard allow-lists may only shrink. CI (`.github/workflows/ci.yml`) is `workflow_dispatch`
only by repository policy and is not a tag gate; do not add push or pull_request triggers to get a run.
