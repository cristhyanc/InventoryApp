# Third-party notices

InventoryApp includes or is derived from the third-party work listed below. Each entry
reproduces the upstream licence notice. Add an entry here whenever a change bundles third-party
code, fonts or other assets, or recreates a third-party design.

## Material Dashboard 3 (design reference)

The InventoryApp frontend's visual language — its colour palette, gradients, shadows, radii and
typography — was **recreated** from Material Dashboard 3 v3.2.0 by Creative Tim, using the
values published in its `assets/scss/material-dashboard/_variables.scss`. The look was rebuilt
as Tailwind design tokens and shared component classes in
`frontend/inventory-app/tailwind.config.js` and `frontend/inventory-app/src/styles.scss`, with
darker text variants added so all text meets WCAG 2.2 AA.

No Material Dashboard source file, stylesheet, script, image or build artefact is copied into
or distributed with this repository, and neither the Bootstrap framework nor any Material
Dashboard CSS or JavaScript is a dependency of this application.

Upstream project: <https://github.com/creativetimofficial/material-dashboard-free>

```text
MIT License

Copyright (c) 2017 Creative Tim (https://www.creative-tim.com)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALING IN THE
SOFTWARE.
```

## Inter (bundled font)

The Inter typeface is bundled and self-hosted through the `@fontsource/inter` npm package
(weights 400/500/600/700), loaded from `frontend/inventory-app/angular.json`. The font files are
served from this application; no Google Fonts or other content-delivery-network request is made
for them.

Upstream project: <https://github.com/rsms/inter>. The full licence text ships with the package
at `node_modules/@fontsource/inter/LICENSE`.

```text
Copyright 2016 The Inter Project Authors (https://github.com/rsms/inter)

This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is available with a FAQ at: https://openfontlicense.org
```
