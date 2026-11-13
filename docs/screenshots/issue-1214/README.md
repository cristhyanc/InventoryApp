# Issue #1214 browser evidence

Screenshots of the **production build** (`ng build`, served with a fallback to `index.html` like
`src/staticwebapp.config.json`), signed out, after issue #1214 published the legal pages.

| File | Shows |
|---|---|
| `privacy-policy-390px.png`, `privacy-policy-1440px.png` | the whole `/privacy-policy` page: the owner-approved copy in eleven sections, "Effective date: 13 November 2026", the table of contents and business details, with no placeholder and no horizontal overflow |
| `terms-of-use-390px.png`, `terms-of-use-1440px.png` | the whole `/terms-of-use` page, likewise |
| `homepage-footer-390px.png`, `homepage-footer-1440px.png` | the public homepage footer with its new **Legal** list: Privacy Policy and Terms of Use |

Checked at the same time, at both widths: opening each page directly and reloading it stays on the
page with no sign-in redirect; `scrollWidth - clientWidth` is 0; the footer's Privacy Policy link
opens `/privacy-policy` by keyboard (focus and Enter); a table-of-contents entry chosen by keyboard
moves focus to its section.
