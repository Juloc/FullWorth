# FullWorth — Accessibility audit

This document records the code-level accessibility baseline for the current FullWorth web app. It is not a claim of WCAG certification; browser, keyboard, contrast-tool and screen-reader checks still belong to the runtime release gate.

## Current baseline

The current app already provides:

- semantic application landmarks (`aside`, `nav`, `main`, `header`, sections and native tables);
- a skip-to-content link targeting `#main`;
- native buttons, inputs, selects and `dialog` elements;
- visible `:focus-visible` treatment;
- `prefers-reduced-motion` handling;
- `document.documentElement.lang` updates when the selected language changes;
- `aria-current="page"` on active desktop and mobile navigation;
- `role="status"` for toast announcements;
- privacy controls with `aria-pressed`;
- chart image labels where charts are exposed as SVG images.

## Wealth release pass (PR 10)

`/ui/accessibility-release.js` closes the remaining repeated gaps without duplicating dialog implementations:

- `#tx-query` receives a localized accessible name;
- transaction direction/filter selects receive localized accessible names;
- transaction table headers receive explicit `scope="col"`;
- dynamically-created icon-only close/cancel buttons using `×`, `✕` or `✖` receive a localized `aria-label`;
- a mutation observer covers dialogs created after initial page load;
- language changes re-apply the German/English labels;
- the module is part of the PWA static shell and contains no financial data.

The wealth-specific web baseline tests verify that the module is loaded, localized and cached, and that the service worker continues to exclude `/api`, `/bff`, `/auth`, `/share` and `/connect` data from offline caching.

## Runtime release checks

Before a production release, run these checks against the built application on desktop and a narrow/mobile viewport:

1. Keyboard-only navigation through sidebar/bottom navigation, Wealth overview, asset type chooser and every specialized asset dialog.
2. Verify focus remains visible, dialog focus is contained, Escape closes dialogs and focus returns to a sensible trigger.
3. Run axe or Lighthouse accessibility checks on at least Dashboard, Buchungen, Vermögen, one real-estate detail, one specialized asset detail and investment security detail.
4. Check light and dark theme contrast, especially muted text, semantic status text and disabled controls.
5. Screen-reader smoke test with NVDA/VoiceOver: navigation, one transaction row, Wealth totals, one modal, one form validation message and one toast.
6. Verify the privacy toggle masks sensitive wealth labels/identifiers in all specialized asset and property views.

Any regression found in these runtime checks blocks release even when the static baseline tests pass.
