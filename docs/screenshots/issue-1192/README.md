# Issue #1192 browser evidence

The screenshots in this directory are issue #1192's required validation evidence: the **signed-out**
public Restockroo homepage with its refill and supplier comparison examples, at both widths the
issue names, and the open mobile comparison dialog the issue's last acceptance criterion is about.

| File | Shows |
|---|---|
| `homepage-examples-signed-out-1440px.png` | the whole page at 1440px: the refill example ("Banksia House · Banksia House VM", the four-product Pick List with its "To pack" and "Loaded" columns and the note that Loaded is not a separate record) and, below it, the full supplier comparison inline - the seven rows of the issue's table, the "Lowest unit price" badge on each of the three tied 30-pack rows, "Example comparison — prices and availability change." and the confirm-before-buying notice |
| `homepage-examples-signed-out-390px.png` | the same page at 390px, where the comparison collapses to its readable preview ("From $1.07 inc GST / unit" and "3 of 7 suppliers tie for the lowest unit price") with the **View full comparison** button, and nothing overflows the viewport |
| `supplier-comparison-dialog-390px.png` | the full comparison open in its dialog at 390px, opened from the keyboard - the heading, the example notice, the seven rows with their badges, the confirm notice and the Close button |

## How they were produced

```bash
cd frontend/inventory-app
npm run e2e:install                                           # once: Playwright and Chromium
npm run e2e:visual -- visual/landing-refill-supplier-examples.visual.ts
```

They are captured from the real landing page rendered by the real application shell, by
`frontend/inventory-app/e2e/visual/landing-refill-supplier-examples.visual.ts`. Name the file when
running `e2e:visual`, as above: the configuration rewrites every screenshot of every visual run, so
an unscoped run also re-renders the other issues' evidence.

The run starts the Angular dev server alone — no API process, no database, no Nayax call and no
sign-in — which is enough because the landing page injects no API service and makes no request at
all. Everything on these captures is static marketing copy and the two clearly labelled examples,
"Example refill · Fictional data" and "Example comparison · Fictional data", whose figures are
invented and live in `landing-content.ts`; no business data, site, machine, account name, credential
or imported document appears in them, and none can.

**Signed out** is the point of this evidence, and the run asks the `e2e` build's MSAL stub for it
through the shared `e2e/visual/support/landing-page.ts` helper, exactly as issue #1191's run does;
see [docs/screenshots/issue-1191/README.md](../issue-1191/README.md) and
[docs/architecture.md § End-to-end testing authentication](../../architecture.md#end-to-end-testing-authentication-issue-46).

## What the same run measures, beyond taking a picture

These are the parts of the issue's last acceptance criterion that jsdom cannot answer, because it
applies no user-agent stylesheet and computes no layout:

- **No page-level horizontal scrolling** at 1440px and 390px with the dialog closed, with the whole
  page in view, and — the state the issue names — at 390px with the dialog **open**. A modal in the
  top layer is laid out against the initial containing block, so it can widen the document even
  though the page behind it is inert; the run measures the document, and separately checks that the
  dialog's own panel stays inside the viewport. The comparison table scrolls sideways **inside** the
  dialog, which is its own scroller and not the page.
- **Pinch zoom stays enabled**: the viewport meta tag is still `width=device-width, initial-scale=1`
  with no `maximum-scale` and no `user-scalable`, with and without the dialog.
- **The dialog is opened from the keyboard** (focus **View full comparison**, press Enter), and is
  dismissed both by **Escape** and by its **Close** button reached with Tab — each dismissal
  returning focus to **View full comparison**.
- **A closed dialog is painted nowhere.** The element is always in the DOM, so the run asserts that
  before it is opened it is hidden, carries no `open` attribute, and contributes neither a dialog
  role nor its Close control to the accessibility tree. This caught a real defect: a bare Tailwind
  `flex` on the element is an author declaration and beat the user agent's own
  `dialog:not([open]) { display: none }`, leaving the comparison painted over the page at every
  width. The template now carries `hidden open:flex`.

The rows, the copy and the component's own Tab cycle are asserted by `landing-page.component.spec.ts`
and `landing-supplier-comparison-example.component.spec.ts` in the Jest suite, which
`scripts/validate.sh` runs. These files are evidence of a specific head, not a baseline the suite
compares against; regenerate them in the same commit as any change to the landing page.
