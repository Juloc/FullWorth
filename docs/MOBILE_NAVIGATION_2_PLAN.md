# FullWorth Mobile Navigation 2.0 — UX + Implementation Plan

Status: planned  
Target: FullWorth mobile/PWA first, shared navigation model for desktop/tablet  
Principle: one navigation system, multiple layouts, fully user-configurable within clear UX limits.

---

# 1. Problem

The current mobile navigation uses a fixed bottom bar plus a `More` sheet for overflow.

This creates several problems:

- important features are hidden behind `More`
- all users are forced into the same information architecture
- users who mainly use contracts, budgets, wealth, purchases or Coach cannot prioritize them
- adding another feature makes the overflow problem worse
- the current icon language is inconsistent in meaning and visual weight
- mobile navigation feels like a compressed desktop sidebar instead of a purpose-built mobile app
- the existing `More` sheet solves space, but not orientation

Navigation 2.0 must remove the idea that one fixed ordering is correct for everybody.

---

# 2. Core model

FullWorth gets one central navigation registry and several presentation layouts.

The registry knows what exists.

The layout decides how it is presented.

The user preferences decide what is pinned.

Conceptually:

```text
NavigationRegistry
├─ dashboard
├─ accounts
├─ transactions
├─ analytics
├─ wealth
├─ contracts
├─ budgets
├─ purchases
├─ goals
├─ coach
├─ rules
└─ other eligible feature areas

NavigationPreferences
├─ style
├─ pinnedItems[]
├─ centerActionEnabled
├─ centerActionItems[]
└─ optional layout-specific preferences
```

No layout may maintain its own duplicate feature list.

---

# 3. UX rules

## 3.1 No permanent `More` tab

The current mobile `More` overflow tab/sheet is removed as a primary navigation concept.

A feature that is not pinned remains reachable through:

- navigation editor
- global search / command surface
- contextual links
- dashboard modules
- burger navigator in Minimal layout

No feature is deleted merely because it is not pinned.

## 3.2 Maximum bottom navigation size

Bottom navigation supports:

- minimum 2 pinned destinations
- maximum 5 visible slots
- recommended default: 4 destinations

If the center action button is enabled, it occupies one visible slot.

Example:

```text
Übersicht   Buchungen      +      Vermögen   Analyse
```

## 3.3 Dashboard is the default anchor

For standard presets, `Übersicht` stays the first destination.

Custom mode may allow a user to remove it from the bottom bar, but it must remain globally reachable.

## 3.4 Navigation must never reorder itself automatically

FullWorth may suggest:

> Verträge wird häufig verwendet. Zur Navigation hinzufügen?

But it must never silently reorder or replace user navigation.

---

# 4. Navigation layouts

These are presets/configurations of the same navigation system.

They are not separate implementations.

---

# 5. Layout A — Action

Purpose: users who frequently add/import/record financial objects.

Default mobile bar:

```text
Übersicht   Buchungen      +      Vermögen   Analyse
```

The center `+` is an action, not a destination.

Tapping it opens a compact action sheet.

Initial actions:

- Buchung hinzufügen
- Kauf/Beleg erfassen
- Vertrag hinzufügen
- Vermögenswert hinzufügen
- Konto verbinden
- Import starten

Rules:

- the `+` remains centered
- primary actions may be context-aware
- maximum number of immediately visible actions should stay small
- destructive actions never belong here
- the action sheet must be usable with one hand

The user can optionally configure which supported actions appear.

---

# 6. Layout B — Classic

Purpose: predictable default for new users.

Suggested default:

```text
Übersicht   Konten   Buchungen   Analyse   Vermögen
```

Characteristics:

- no center action button
- explicit finance destinations
- lowest learning cost
- suitable as first-run default
- still editable by long press

Contracts, budgets, purchases, goals and Coach remain accessible via other surfaces unless pinned by the user.

---

# 7. Layout C — Finance Hub

Purpose: reduce top-level destinations by grouping around user tasks instead of individual features.

Default:

```text
Übersicht   Geldfluss   Vermögen   Planung
```

Suggested composition:

## Geldfluss

- Buchungen
- Einnahmen/Ausgaben
- Kategorien
- Händler
- related transaction views

## Vermögen

- Konten
- Investments
- Vermögenswerte
- Schulden
- allocation/performance views

## Planung

- Budgets
- Verträge
- Ziele
- recurring planning surfaces

Important:

The hub model should not duplicate existing data or create artificial domain entities.

It is only a navigation/information architecture layer.

---

# 8. Layout D — Minimal

Purpose: maximum content area and minimum persistent chrome.

No bottom navigation.

Top-left:

```text
☰   Seitentitel
```

The burger opens a dedicated full-height mobile navigator.

Suggested structure:

```text
Finanzen
  Übersicht
  Buchungen
  Konten
  Vermögen
  Analyse

Planung
  Verträge
  Budgets
  Ziele

Weitere Bereiche
  Käufe
  Coach
  Regeln
  ...
```

Requirements:

- not a tiny desktop sidebar
- optimized for touch
- clear section hierarchy
- recent/pinned destinations may appear first
- current destination clearly marked
- opening/closing must preserve page state

This layout should feel intentionally minimal, not like a fallback.

---

# 9. Layout E — Custom

Purpose: fully user-controlled mobile navigation.

This is the long-term power-user model.

The bottom navigation contains only user-selected destinations.

Examples:

```text
Übersicht   Verträge   Buchungen   Coach
```

```text
Konten   Vermögen   Analyse
```

```text
Buchungen   Verträge      +      Vermögen   Coach
```

No fixed requirement that every user sees the same five items.

---

# 10. Long-press navigation editing

Primary interaction:

Long press any bottom navigation item.

Enter `Navigation bearbeiten` mode.

The interaction should borrow the familiar mental model of editing a phone home screen, without visually copying a specific platform.

Behavior:

- pinned items become draggable
- each removable item gets a small remove control
- available space is clearly visible
- dragging immediately previews the order
- a `+` / `Bereich hinzufügen` control opens the available destination list
- Save/Done exits edit mode
- tapping outside must not accidentally discard changes

Optional restrained motion:

- tiny movement/tilt while editing
- no excessive wobble
- respect reduced-motion preference

## Remove behavior

Removing an item means:

> remove from navigation

It does NOT mean:

> disable/delete feature

Use wording that makes this distinction clear.

## Add behavior

Available destinations are shown by logical groups and can be added with one tap.

Already pinned destinations are clearly marked.

---

# 11. Navigation settings

Add:

`Einstellungen → Darstellung → Navigation`

Controls:

- Navigationsstil
  - Classic
  - Action
  - Finance Hub
  - Minimal
  - Custom
- Angeheftete Bereiche
- Reihenfolge
- Center Action on/off where supported
- Center Action actions
- Reset to preset

Changing layout should preserve compatible user choices where possible.

Do not reset silently.

---

# 12. Global reachability

Removing `More` requires a better universal discovery path.

Global search must support navigation destinations as first-class results.

Examples:

Searching:

- `Verträge`
- `Budgets`
- `Vermögen`
- `DKB`
- `Netflix`
- `Amazon`
- `Analyse`

can produce:

- destination
- account
- contract
- transaction/merchant
- supported action

Navigation destinations should appear before fuzzy low-value results when the query exactly matches a section name.

Potential future extension:

command-style results such as:

- `Vertrag hinzufügen`
- `Konto verbinden`
- `CSV importieren`

---

# 13. Dashboard relationship

The dashboard should complement personalized navigation instead of duplicating it.

Dashboard modules may provide:

- frequently used accounts
- recent transactions
- wealth snapshot
- contract changes
- budgets
- important insights
- user-selected shortcuts

Dashboard modules and bottom navigation are separate preferences.

A user may remove `Verträge` from the bottom bar while keeping a contracts dashboard module.

---

# 14. Coach / AI placement

Coach must not be forced into the main navigation.

Preferred model:

- globally available contextual Coach launcher
- page context passed to Coach
- optional pinned Coach destination for users who want it

This supports both:

- users who rarely use AI
- users who use Coach as a primary interface

No special AI-only navigation architecture.

---

# 15. Icon system redesign

Navigation 2.0 includes a complete icon audit.

Current icons must not be kept just because they already exist.

## Rules

Use one consistent family/style:

- same nominal canvas
- same visual weight
- same stroke width
- same corner language
- outline-first
- no mixed filled/outline navigation set
- no emoji
- no arbitrary colored icons in primary navigation
- active state uses container/text emphasis, not an unrelated replacement glyph

Every icon must communicate the destination, not merely look finance-related.

## Proposed semantic directions

- Übersicht: dashboard/home overview
- Konten: account/card/bank-account semantic
- Buchungen: transaction/list movement semantic
- Analyse: analytical chart semantic
- Verträge: document/recurring agreement semantic
- Budgets: allocation/limit semantic
- Käufe: purchase/bag/receipt semantic
- Ziele: target semantic
- Coach: subtle assistant/spark/chat semantic

## Wealth icon

The current wealth icon should be replaced.

Do not use:

- piggy bank
- generic wallet
- plain bank building
- cash-only symbol

Preferred direction:

A compact portfolio/asset symbol combining:

- stacked asset layers / holdings
- subtle upward value movement

It should mean `Gesamtvermögen / Bestand`, not merely `Sparen` or `Bargeld`.

Create 2–3 icon candidates in the same FullWorth icon grammar and select after testing at 20–24 px.

---

# 16. Mobile page shell redesign

Navigation alone is not enough.

Every mobile destination should follow one predictable shell.

Recommended structure:

```text
Top bar
  optional back/burger
  title
  page-specific action(s)

Context / filter row when needed

Primary content

Persistent bottom navigation
  or no bottom navigation in Minimal layout
```

Rules:

- avoid duplicating the page title in multiple places
- primary action must be reachable with one hand
- filters should not consume half the viewport
- secondary actions belong in contextual menus/sheets
- maintain safe-area spacing
- floating Coach must never cover the last important row or bottom nav
- dialogs on mobile remain dedicated full-screen/sheet surfaces as appropriate

---

# 17. Desktop relationship

Desktop uses the same registry and preferences.

Do not create a second incompatible navigation model.

Desktop can expose more destinations because space permits it.

Suggested structure:

## Pinned

User-prioritized finance areas.

## Other finance areas

Remaining eligible destinations.

## Account/system area

- Einstellungen
- Verbindungen
- Haushalt
- profile/account controls

These must remain visually separated from financial work areas.

Desktop sidebar collapse/width behavior should keep working independently of mobile layout choice.

---

# 18. Data model

Navigation preferences should be user-specific, not global-instance-specific.

Suggested model:

```text
UserNavigationPreferences
- UserId
- LayoutStyle
- PinnedDestinationIds[]
- CenterActionEnabled
- CenterActionIds[]
- Version
- UpdatedAt
```

If a JSON preference object is simpler for the current architecture, that is acceptable initially.

Requirements:

- schema version
- safe fallback to defaults
- unknown/deleted destination IDs ignored safely
- feature-disabled destinations ignored safely
- preferences survive app updates
- household members may have different navigation

Local storage may be used for immediate optimistic UX, but server persistence is the source of truth once implemented.

---

# 19. Navigation registry

Create one registry with stable IDs.

Example shape:

```js
{
  id: 'wealth',
  route: '/wealth',
  labelKey: 'nav.wealth',
  icon: 'wealth',
  group: 'finance',
  mobileEligible: true,
  pinnable: true,
  searchable: true,
  capability: null
}
```

The registry must be used by:

- mobile bottom navigation
- Minimal navigator
- navigation editor
- desktop sidebar
- global search destination results
- settings navigation picker

Do not hard-code separate lists in each surface.

---

# 20. Presets

Presets are only initial configurations.

Suggested preset definitions:

## Classic

```text
dashboard
accounts
transactions
analytics
wealth
```

## Action

```text
dashboard
transactions
__action__
wealth
analytics
```

## Finance Hub

```text
dashboard
cashflow-hub
wealth-hub
planning-hub
```

## Minimal

No bottom slots.

## Custom

Use current user pinned selection.

Changing from one preset to Custom should copy the current visible selection as the starting point.

---

# 21. Migration from current navigation

Current users must not suddenly lose orientation.

Initial migration:

1. detect no stored Navigation 2.0 preferences
2. assign Classic preset
3. preserve current default destinations as closely as possible
4. remove `More` only once global reachability and editor exist
5. show one lightweight onboarding hint:

`Navigation gedrückt halten, um sie anzupassen.`

Do not show a multi-page onboarding wizard solely for this feature.

---

# 22. Implementation phases

Each phase must leave `main` deployable.

## Phase 0 — Baseline

- document current mobile nav items
- document current `openMoreSheet()` behavior
- add navigation regression tests around current routes
- capture mobile screenshots/test states if current test tooling supports it

No user-visible behavior change.

## Phase 1 — NavigationRegistry

- central registry
- stable destination IDs
- labels/icons/capabilities/groups
- adapt desktop and existing mobile rendering to read registry where safe

No UX redesign yet.

## Phase 2 — Preference model

- user navigation preference API/storage
- defaults
- versioning
- validation
- fallback behavior

Initially preferences may not affect rendering.

## Phase 3 — New configurable bottom bar

- render from preferences
- 2–5 slots
- active state
- safe-area behavior
- current route handling
- no editor yet

Keep existing `More` temporarily as fallback during this deploy.

## Phase 4 — Navigation editor

- long press enters edit mode
- drag reorder
- remove
- add destination
- persist
- reset
- accessibility keyboard alternatives where applicable

## Phase 5 — Global destination search

- all pinnable/searchable registry destinations
- exact section-name matching
- route navigation
- optional action entries

At this point hidden features remain easy to reach.

## Phase 6 — Remove `More`

- delete primary `More` bottom tab
- remove `openMoreSheet()` usage
- delete obsolete CSS/JS
- update tests
- verify every former More destination remains reachable

## Phase 7 — Layout presets

Implement:

- Classic
- Action
- Minimal
- Custom

Finance Hub may land separately because it changes information architecture more substantially.

## Phase 8 — Center action

- central action slot
- action sheet
- supported actions registry
- optional personalization
- contextual ordering where safe

## Phase 9 — Finance Hub

- cashflow hub
- wealth hub
- planning hub
- reuse existing views/data
- no duplicate business logic

## Phase 10 — Icon overhaul

- audit all primary nav icons
- replace inconsistent glyphs
- implement wealth candidates
- standardize active/inactive treatment
- test at mobile sizes and dark mode

## Phase 11 — Mobile shell polish

Review every primary mobile screen for:

- title hierarchy
- back behavior
- primary action
- filter placement
- bottom spacing
- Coach collision
- sheets/dialogs
- one-hand use
- empty/error/loading states

## Phase 12 — Desktop unification

- same registry/preferences
- pinned vs additional finance areas
- preserve sidebar collapse/resizing
- keep system/account tools separate

---

# 23. Interaction details

## Long press

Recommended threshold around normal platform expectations.

Requirements:

- scrolling must not accidentally enter edit mode
- a normal tap still navigates immediately
- haptic feedback may be used where supported but must not be required
- edit mode entry has a clear visual state

## Drag

- reorder only along available slots
- placeholder shows destination
- edge scrolling is unnecessary for the bottom bar itself
- adding/removing should not cause large layout jumps

## Active state

Use:

- subtle background/container
- stronger text/icon

Do not use:

- bright finance-status colors
- red for navigation selection
- inconsistent filled alternate icon

---

# 24. Accessibility

Must support:

- at least 44×44 px effective touch targets
- visible focus states
- aria labels on icon-only controls
- reduced motion
- screen-reader understandable edit controls
- non-drag alternative for reordering if required
- contrast in light/dark themes
- safe-area inset handling

Do not make long press the only way to configure navigation.

Settings must expose the same functionality.

---

# 25. Tests

Add tests for:

## Registry

- every destination ID unique
- every pinnable destination has icon and labels
- capabilities correctly hide unavailable destinations
- unknown IDs do not crash rendering

## Preferences

- default preset
- save/reload
- invalid duplicates normalized
- >5 slots rejected/normalized
- removed feature IDs ignored
- layout switching preserves sensible state

## Mobile

- active destination
- 2/3/4/5 slot rendering
- center action rendering
- long-press editor entry
- reorder
- remove/add
- persistence
- safe-area class/layout
- no `More` after migration phase

## Search

Every unpinned destination remains globally discoverable.

## Routing

Current deep links continue to work even if destination is not pinned.

## Regression

- Coach launcher does not overlap bottom nav
- dialogs still work
- PWA standalone mode
- dark mode
- German/English labels

---

# 26. Release strategy

Do not ship the whole redesign as one large risky commit.

Recommended production-visible sequence:

1. registry/internal refactor
2. persisted preferences
3. configurable bottom bar while More remains
4. editor
5. global destination search
6. remove More
7. presets
8. center action
9. icon overhaul
10. Finance Hub
11. page-shell polish
12. desktop unification

After each visible step:

- build
- tests
- mobile smoke test
- PWA standalone smoke test
- light/dark check
- deployable main

---

# 27. Acceptance criteria

Navigation 2.0 is complete when:

- no mobile `More` navigation dump is needed
- the user can choose the destinations that matter to them
- editing feels comparable to familiar mobile home-screen organization
- every unpinned feature remains easy to find
- mobile supports Classic, Action, Minimal and Custom layouts
- Finance Hub can be enabled as a coherent alternate IA
- the center `+` is optional
- navigation preferences are per user
- icons use one consistent semantic visual system
- Wealth has a purpose-built, clearly understandable icon
- Coach can be pinned but is not forced
- desktop and mobile share the same underlying registry
- routes/deep links remain independent of pin state
- all important interactions work in PWA standalone mode
- `main` remains deployable throughout rollout

---

# 28. Design decision summary

FullWorth should not decide that one fixed navigation is correct for every user.

The app provides:

- strong defaults
- several purposeful layout presets
- a simple long-press editing model
- global reachability for everything
- consistent icons
- one shared technical navigation registry

The result should feel like a mobile finance app designed around the user's workflow, not a desktop sidebar squeezed into a phone.
