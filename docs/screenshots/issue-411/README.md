# Issue #411 visual evidence

The screenshots in this directory are the visual evidence the #409 series rules require for
issue #411: the four restyled shared widgets and the complete bundled icon set, at both widths
the series asks for.

| File | Shows |
|---|---|
| `icons-1440px.png`, `icons-390px.png` | every entry of `ICON_PATHS`, so the Rounded geometry can be checked by eye |
| `confirmation-dialog-1440px.png`, `confirmation-dialog-390px.png` | `ConfirmationDialogComponent` |
| `loading-indicator-1440px.png`, `loading-indicator-390px.png` | `LoadingIndicatorComponent` |
| `toast-success-*.png`, `toast-warning-*.png`, `toast-error-*.png`, `toast-info-*.png` | each `ToastContainerComponent` variant |
| `multi-select-dropdown-open-1440px.png`, `multi-select-dropdown-open-390px.png` | `MultiSelectDropdownComponent` with its panel open |

## How they were produced

They are captured from the real components rendered by the real application shell, on the
development-only fixture route `__design-system/widgets`
(`frontend/inventory-app/src/app/design-system/widget-gallery.component.ts`).

```bash
cd frontend/inventory-app
npm run e2e:install   # once: installs Playwright and downloads Chromium
npm run e2e:visual    # rewrites every PNG in this directory
```

The run starts the Angular dev server and nothing else — no API process, no database, no Nayax
call, no sign-in. Everything on screen is neutral synthetic sample text ("Sample product A",
"Delete sample item?"); no business data, site, machine, account name, credential or imported
document appears in any of these files, and none can.

They are committed rather than left as a workflow artefact so the pull request can show them
inline. Regenerate them in the same commit as any change to the shared widgets or the icon set;
they are evidence of a specific head, not a baseline the suite compares against.
