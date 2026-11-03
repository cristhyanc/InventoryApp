# Issue #1052 browser evidence

The screenshots in this directory are the visual half of issue #1052's required browser check: the
New Product **Name** field suggesting the shared master product catalogue, at both widths the issue
names, with **two size variants of one product** in the list.

| File | Shows |
|---|---|
| `suggestions-open-1440px.png`, `suggestions-open-390px.png` | the open suggestion panel with both variants, each labelled `Sample Brand Tasty Cheese Corn Chips · 45 g` / `· 175 g`, so two sizes of one product are told apart at a glance and the panel stays inside the viewport |
| `selection-tracking-1440px.png`, `selection-tracking-390px.png` | the whole page after the 175 g row is taken: **Name** and **Unit Label** filled in from the catalogue's own values, and the supplier-price-tracking note with its on-by-default opt-out checkbox |

## How they were produced

```bash
cd frontend/inventory-app
npm run e2e:install                                                  # once: Playwright and Chromium
npm run e2e:visual -- visual/new-product-master-product-suggestions.visual.ts
```

They are captured from the real New Product page rendered by the real application shell, by
`frontend/inventory-app/e2e/visual/new-product-master-product-suggestions.visual.ts`. Name the file
when running `e2e:visual`, as above: the configuration rewrites every screenshot of every visual
run, so an unscoped run also re-renders the other issues' evidence.

The run starts the Angular dev server alone — no API process, no database, no Nayax call, no
sign-in. Every `/api/` request is answered in the browser with neutral synthetic data (a
"Sample Vending" business and two "Sample Brand Tasty Cheese Corn Chips" catalogue rows); no
business data, site, machine, account name, credential or imported document appears in any of these
files, and none can. The page is never submitted, so no product is created and no master product is
linked.

The behaviour these pictures show is asserted, in the same browser against the real API, by
`frontend/inventory-app/e2e/tests/new-product-master-product-suggestions.e2e.ts`. These files are
evidence of a specific head, not a baseline the suite compares against; regenerate them in the same
commit as any change to this field.
