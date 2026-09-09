# UI harness

A local development server that renders the **real** frontend against canned data, so any page or
dialog can be opened and measured without a login and without a database.

```bash
node ops/ui-harness/server.mjs          # http://127.0.0.1:8095
node ops/ui-harness/server.mjs 8096     # different port
```

It serves `src/FullWorth.Web/wwwroot` unchanged and injects `fixtures.js` before `app.js`, which
stubs the BFF. Nothing here is served by the app; it is a tool, not a feature.

## Why it exists

Verifying a layout should not require credentials. Checking "is this control 44px on a phone" against
the running stack meant logging in first, which is exactly the step that kept blocking mobile audits.

## What it deliberately does

- **Reads the C#-inlined pages out of the source.** `src/FullWorth.Web/Modules/Import/*Page.cs` keep
  their HTML in a raw string literal and map it onto routes there. The harness parses both the literal
  and the `MapGet` routes at startup, so an edited page is served edited and a new route appears by
  itself. A hand-copied HTML file once made a change look like it had no effect.
- **Announces its SPA fallback** with an `X-Harness-Fallback` response header. A silent fallback once
  made a reachable page look broken, and the harness was then treated as evidence about the product.
  If that header is present, the harness had no page and you are looking at the shell.
- **Answers the antiforgery endpoint and logs write bodies**, so commit and save paths can be walked
  end to end. `secure-fetch.js` captures `nativeFetch` at module load, so a page-side `fetch`
  override never sees the writes - the server log is the only place the real payload shows up.

## Fixtures

`fixtures.js` (browser side) answers `/bff/*` and `/api/*`; unknown endpoints return `[]`, which every
view tolerates. It is injected in front of `/app.js`, so it applies to the SPA only - the C#-inlined
pages load their own feature module instead and are answered by `server.mjs`. That split is on
purpose: the import flow is verified through the server, because the commit payload can only be read
from the server log (`secure-fetch.js` captures `nativeFetch` at module load, so a page-side override
never sees the request). `server.mjs` also answers `/auth/admin/*`, which the browser stub never sees
because the admin page talks to it directly rather than through the BFF. Keep fixture data deliberately awkward - a long counterparty name, a
row that fails validation, a duplicate - because a layout only breaks on the awkward cases.

Note the ordering rule in `server.mjs`: fixture keys are matched as substrings, so a key that is a
substring of another path must come first (`rollback` before `import-jobs`), and the map only applies
to `/bff/` and `/api/` paths - without that guard it also answered `/features/accounts.js` with JSON.
