# Issue #1191 browser evidence

The screenshots in this directory are issue #1191's required validation evidence: the **signed-out**
public Restockroo homepage after the public beta copy refresh, at both widths the issue names.

| File | Shows |
|---|---|
| `homepage-signed-out-1440px.png` | the whole page at 1440px: the hero ("Know what to buy. Pack what you need."), the beta notice and the export notice directly under it, **Start free** and **Sign in** in the header, the example dashboard, the features, the reworded Nayax section, the three "How it works" steps, the FAQs without the three paid-plan ones, and the footer's contact line - with no Pricing section and no Pricing link in the header or the footer navigation |
| `homepage-signed-out-390px.png` | the same page at 390px, where the header collapses to the logo, **Start free** and the menu button, and nothing overflows the viewport |

## How they were produced

```bash
cd frontend/inventory-app
npm run e2e:install                                      # once: Playwright and Chromium
npm run e2e:visual -- visual/landing-page-public-beta.visual.ts
```

They are captured from the real landing page rendered by the real application shell, by
`frontend/inventory-app/e2e/visual/landing-page-public-beta.visual.ts`. Name the file when running
`e2e:visual`, as above: the configuration rewrites every screenshot of every visual run, so an
unscoped run also re-renders the other issues' evidence.

The run starts the Angular dev server alone — no API process, no database, no Nayax call and no
sign-in — which is enough because the landing page injects no API service and makes no request at
all. Everything on these captures is static marketing copy and the clearly labelled "Example
dashboard · Sample data" of invented figures; no business data, site, machine, account name,
credential or imported document appears in them, and none can.

**Signed out** is the point of this evidence, and the run asks the `e2e` build's MSAL stub for it:
that stub reports the synthetic signed-in account by default (what the workflow suite needs) and no
account at all when `sessionStorage` holds `e2e-signed-out` before the bundle loads, which is the
state `signedOutVisitorGuard` shows the page at `/` on. See
[docs/architecture.md § End-to-end testing authentication](../../architecture.md#end-to-end-testing-authentication-issue-46).

The same run asserts, in the same browser, what jsdom cannot: that the page has no page-level
horizontal scrolling at **every** width from 320px to 390px, and that the viewport meta tag still
allows pinch zoom — the issue's last acceptance criterion. The copy itself is asserted by
`landing-page.component.spec.ts` and `landing-header.component.spec.ts` in the Jest suite, which
`scripts/validate.sh` runs. These files are evidence of a specific head, not a baseline the suite
compares against; regenerate them in the same commit as any change to the landing page.
