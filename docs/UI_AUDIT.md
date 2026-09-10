# UI audit — desktop, mobile and installed PWA

Measured on 2026-09-10 against `main`. Everything here was either measured in a browser at a real
viewport or read out of the code with a script; nothing in this document is an impression. The scripts
are named so a later pass can repeat the measurement instead of arguing about it.

Two of the three complaints that started this had a single cause each and are already fixed (see
[What this pass fixed](#what-this-pass-fixed)). The third — "the dialogs are over-complex, hard to use
and look bad" — is not a bug, it is a missing abstraction, and that is what the plan below is about.

---

## How to reproduce the measurements

```bash
node ops/ui-harness/server.mjs        # http://127.0.0.1:8095, the real wwwroot against fixtures
```

- **Layout probe** — `probe(route)` in the page: walks every visible element and reports only findings
  (page scrolling sideways, content wider than a non-scrolling box, anything past the right edge, a
  clipped number, a tap target under 40 px, a fixed element off-screen). Repeats are collapsed, so an
  empty result means the route is clean at that viewport. Measure, never eyeball: a `position:fixed`
  element has `offsetParent === null`, so "it looks fine" is not a test.
- **Dialog census** — counts the controls, buttons, tabs and sections each `dialog(...)` call site
  renders, by walking its template literal.
- **Hardcoded colours** — counts hex literals per stylesheet (`styles/tokens.css` is the legitimate
  home; everywhere else is a violation of the tokens-only rule).
- **Offline shell coverage** — `PwaOfflineShellCoverageTests` walks the real import graph from
  `index.html` and fails when a module the shell needs is not precached.

Viewports: desktop `1440×900`, mobile `375×812`, plus `display-mode: standalone` for the PWA rules.

---

## The dialogs

### The evidence

| Measure | Value |
| --- | --- |
| `dialog(...)` call sites building markup as a template literal | **76** |
| Form controls rendered inside dialogs | **188** |
| Dialogs with more than 6 controls | **12** |
| Dialogs with more than 4 buttons | **5** |
| Hardcoded hex colours outside `tokens.css` | **~220** across 13 stylesheets |

The worst individual cases, by controls / buttons / bytes of markup:

| Call site | Controls | Buttons | What it is |
| --- | --- | --- | --- |
| `features/transactions.js:145` | 14 | 3 | the booking filter drawer |
| `features/rules.js:122` | 13 | 3 | the rule editor |
| `features/contracts.js:1548` | 12 | 3 | the contract editor |
| `features/loans.js:142` | 11 | 4 | the loan editor |
| `features/investment-performance-ui.js:225` | 12 | 2 | portfolio settings |
| `features/networth.js:1236` | 11 | 3 | the asset editor |
| `features/transactions.js:606` | 5 | 9 | the booking detail drawer |
| `features/contracts.js:888` | 0 | 11 | a dialog that is really a menu |

### The cause

**There is no form primitive.** `ui/dialog.js` gives exactly one thing: `createDialog(html)`, a shell
with a header, a close button and a mobile swipe-to-dismiss. Everything *inside* is hand-written HTML at
each of the 76 call sites, so every one of them independently re-decides

- field order and grouping,
- whether anything is optional or behind disclosure,
- the label/input markup and therefore the spacing,
- validation and the error position,
- what the actions row looks like and where the destructive action goes,
- and the mobile treatment.

That is why they diverge and why they read as over-complex: nothing shared exists to be consistent
*with*. The three symptoms follow from it:

1. **No progressive disclosure.** The booking filter puts 14 controls in one flat list of equal weight —
   account, account group, type, status, from, to, category, merchant, minimum amount, maximum amount and
   four checkboxes. On a 375 px screen the two people actually use (date range, category) are in the
   middle of a long scroll.
2. **Dialogs used as menus.** `features/contracts.js:888` has 11 buttons and no inputs. There is already
   a `more-sheet` / `mobileMode: 'sheet'` pattern for exactly that, and the owner's own rule is to
   collapse crowded row actions into a `⋯` menu.
3. **Per-feature dialog CSS.** Dialog rules live in `ui/dialog.js`'s `.fw-dialog*`, in `dialogs.css`, in
   `app.css` and in eight feature stylesheets — several of them single-line minified files carrying their
   own hex palette (`receipt-imports.css` 53 literals, `compensation-history.css` 48, `compensation.css`
   38, `styles/features/accounts.css` 21). Per-feature maximum heights are all different: `92vh`, `96vh`,
   `86vh`, `min(88vh, 920px)`, `100dvh`.

### The plan

Ordered so that each step is shippable on its own and nothing is a rewrite. The existing
`createDialog` stays; this adds the layer that was missing above it.

**Step 1 — a declarative form dialog (one new module, no call sites changed yet).**
`ui/form-dialog.js`: takes a title, a field spec and an actions spec, returns the dialog plus a values
getter. It owns the label/input markup, the required marker, the inline error position, the actions row
(primary right, destructive separated), `Enter` to submit, `Escape` to cancel, and focus on the first
field. A field entry names its kind (`text` / `number` / `money` / `date` / `select` / `check` /
`textarea`), its label, and — the important one — a `group` and an `advanced: true`. `advanced` fields
render inside a `<details>`, which is a pattern this codebase already uses (the custom range on the
wealth page). Nothing else changes yet, so this step cannot break a screen.

**Step 2 — convert the four editors.** `rules.js:122`, `contracts.js:1548`, `loans.js:142`,
`networth.js:1236`. These are plain "edit an entity" forms and are the cheapest conversions; each one
should lose code, not gain it. Convert one, look at it, then do the rest.

**Step 3 — the booking filter, which needs a decision, not just a conversion.** Keep the four filters
that carry their weight visible (account/group, date range, category, direction) and move the other ten
behind "Mehr Filter". A filter that is set must stay visible even when collapsed — otherwise a hidden
filter silently changes what the list shows, which is worse than a long form. The existing filter badge
already counts active filters, so the disclosure summary can say how many are set inside it.

**Step 4 — the dialogs that are menus.** `contracts.js:888` and `transactions.js:606` become
`mobileMode: 'sheet'` menus of row actions, not forms. This is the step the owner's "no clutter, collapse
into `⋯`" rule asks for directly.

**Step 5 — one dialog stylesheet.** Fold the per-feature dialog rules into the shared layer, replace the
~220 hardcoded hex literals with tokens, and settle on **one** maximum height and **one** mobile
treatment (full-height sheet under 768 px, centred card above). The minified single-line feature
stylesheets should be unminified in the same pass — there is no build step, so minified source buys
nothing and costs every future reader.

**Step 6 — a guard.** A test in `tests/FullWorth.Web.Tests` that fails when a dialog call site renders
more than N controls without a `<details>`, and when a hex literal appears outside `tokens.css`. Without
it this grows straight back: the census went from 73 to 76 call sites during this audit alone.

---

## What this pass fixed

- **The installed PWA could not cold-start offline.** The service worker precached 95 of 137 assets, and
  the missing ones included `security/secure-fetch.js` (the whole network layer), `ui/money.js`,
  `ui/lock.js`, `ui/dashboard.js` and seven feature modules the shell imports statically. Online this is
  invisible, because the fetch handler is network-first and caches what it fetches; offline the first
  import fails. The existing PWA tests only checked the opposite direction — that everything listed
  exists on disk — so nothing caught it. Now the shell's whole import graph is precached and
  `PwaOfflineShellCoverageTests` walks it so the list cannot drift again.
- **Safe-area insets that silently did nothing.** `env(safe-area-inset-*)` was used in fourteen places
  with **no fallback**. In a browser without safe-area support the entire declaration is invalid and gets
  dropped, so `padding-bottom: calc(84px + env(...))` became *no* bottom padding and the fixed bottom nav
  covered the last row of the list. All fourteen now carry the `0px` fallback the newer lines already
  used, and the responsive test asserts it.
- **`100vh` in an installed app.** `.shell` used `min-height: 100vh` with no `dvh` fallback while the
  sidebar right below it already had one. Installed, `100vh` is the *largest* viewport rather than the
  visible one, so the shell could extend past the bottom of the screen.
- **Nothing added a top inset.** Installed there is no browser chrome above the page, so the sticky
  topbar sat under the status bar and a notch ate the page title. The horizontal insets are handled too,
  which is what landscape needs. In a browser tab every inset is `0` and nothing moves.

---

## Measured layout findings

### Page bodies: clean

The probe reported **no findings at all** on `/`, `/accounts`, `/transactions`, `/contracts`,
`/budgets`, `/analytics`, `/pension` and `/settings` at 1440×900, and none on `/`, `/accounts`,
`/transactions`, `/contracts` and `/pension` at 375×812. No sideways page scroll, nothing past the
right edge, no clipped number, no tap target under 40 px, no fixed element off-screen. The page
layouts are not the problem.

### Dialogs: every single one overflowed, and it was one declaration

This is the "overflow design error" — found and fixed in this pass, and worth recording because of how
it hid.

`.dialog-card` is `display: grid` with no `grid-template-columns`, so its single implicit column is
`auto`, which resolves to **max-content**. A `<select>`'s max-content width is its longest `<option>`.
So one long category or merchant name sized the whole dialog. Measured on the booking filter at
375 px:

| | before | after |
| --- | --- | --- |
| card width | 375 px | 375 px |
| card content width | **467 px** | 375 px |
| grid column | **435 px** | 343 px |
| elements past the right edge | **14** (every label, every select, the close button) | 0 |

All 76 dialog call sites use `.dialog-card`, so all of them had it. It depends on the data, which is
exactly why it read as random and was never pinned down. `minmax(0, 1fr)` plus `min-width: 0` on the
items fixes it — plain `1fr` would **not** have, because a grid item's default `min-width: auto`
refuses to shrink below min-content. `ResponsiveLayoutTests` pins it.

Re-measured after the fix, at 375 px: the booking filter, the manual-booking dialog and the account
`⋯` sheet all report zero horizontal overflow and zero elements past the edge.

### What is left is the design, not the layout

The booking filter still scrolls **364 px vertically** at 375 px — 14 controls in a flat list. That is
not an overflow bug, it is the disclosure problem in step 3 of the plan above, and it is the reason the
dialog reads as over-complex even now that it fits.
