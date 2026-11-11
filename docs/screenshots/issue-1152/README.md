# Issue #1152 browser evidence

The screenshots in this directory are issue #1152's required browser check of the Reimbursement
Report page (`/admin/imports/reimbursement-report`): desktop and 390px, captured from the real
application shell in a real browser.

| File | Shows |
|---|---|
| `reimbursement-report-desktop-1440.png` | the whole page at 1440px: the sidebar entry **Reimbursement Report** directly under **Imports**, the breadcrumb Admin / Imports / Reimbursement Report, the ZIP picker and **Upload** button, every job state the API reports, and one **Completed with failures** upload expanded to its failed file names and reasons |
| `reimbursement-report-mobile-390.png` | the same page at 390px: the 16px gutter, every upload stacked (file name above its state and counts), a long file name wrapped inside its card, and no sideways scroll |
| `reimbursement-report-mobile-empty-390.png` | the first phone viewport with nothing uploaded yet: the empty state, the disabled **Upload** button, the "upload the ZIP exactly as Nayax emails it" note, and the shell's bottom navigation where it really sits |

The long-list 390px capture is taken with the fixed bottom navigation hidden: a fixed bar is painted
at the first viewport's foot in a full-page capture, so it would otherwise sit across an upload in
the middle of the list. The empty-state capture is a viewport capture taken with the bar in place.

## What the browser was asked, not just shown

`frontend/inventory-app/e2e/visual/reimbursement-report.visual.ts` asserts before each capture, in
the browser, the part of the acceptance criterion jsdom cannot answer because it computes no layout:

- `document.documentElement.scrollWidth - clientWidth <= 0` at both widths — no horizontal overflow.
- the computed `padding-left` and `padding-right` of `.page` are `16px` at 390px — the mobile gutter.
- each upload's summary stacks at 390px (its state badge starts below its file name) and is laid out
  as a row at 1440px.
- the longest file name's box is no wider than its own upload row.

## How they were produced

```bash
cd frontend/inventory-app
npm run e2e:install                       # once: Playwright and Chromium
npm run e2e:visual -- reimbursement-report
```

Name the file when running `e2e:visual`, as above: the configuration rewrites every screenshot of
every visual run, so an unscoped run also re-renders the other issues' evidence.

The run starts the Angular dev server alone — no API process, no database, no Nayax call, no
sign-in. Every `/api/` request is answered in the browser with neutral synthetic uploads
(`sample-reimbursements-*.zip`, invented XML file names, a "Sample Vending" business); no business
data, site, machine, account name, credential, figure or imported document appears in any of these
files, and none can. Nothing is uploaded and no import is started.

These files are evidence of a specific head, not a baseline the suite compares against; regenerate
them in the same commit as any change to this page.
