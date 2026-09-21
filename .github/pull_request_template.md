<!--
Keep this concise. Write "Not applicable" for a section that does not apply, but do not delete the section.
The "Documentation impact" section is the exception: validation rejects "Not applicable", "None", "N/A", generic answers and unreplaced placeholders there.
Never paste secrets, connection strings, production identifiers, customer data, or imported business files.
Lifecycle and authority: docs/automation.md. Engineering rules: AGENTS.md.
-->

## Summary and reason

<!-- What changed and why, in a few sentences. -->

## Linked issue

Closes #

## Acceptance criteria addressed

<!-- Copy each acceptance criterion from the issue and state how it is met. -->

- [ ]

## Scope and explicit exclusions

<!-- What this PR deliberately does not do, including anything the issue marked out of scope. -->

## Implementation overview

<!-- Key design decisions, affected files/features, and any invariant from AGENTS.md or docs/architecture.md that this change touches. -->

## Validation commands and actual results

<!-- Paste the commands you ran and their real outcome. If complete validation could not run, state the exact command that failed or was unavailable. -->

```text
bash scripts/validate.sh   # or: powershell -ExecutionPolicy Bypass -File scripts/validate.ps1
```

Result:

## Tests added or changed

<!-- List new/changed tests and what they prove (success, edge cases, regression). -->

## Impact

| Area | Impact |
| --- | --- |
| Financial and inventory | Not applicable |
| Database and migration | Not applicable |
| API contract | Not applicable |
| Authentication/security | Not applicable |
| Secrets/configuration | Not applicable |
| Nayax/external integration | Not applicable |
| Deployment | Not applicable |

<!-- Replace "Not applicable" with a short description wherever the change touches that area, including which invariants were preserved or intentionally changed. -->

## UI evidence

<!-- Screenshots or a short description for UI changes, without production data. "Not applicable" otherwise. -->

## Documentation impact

<!--
Exactly one "Decision:" line and exactly one "Evidence:" line. CI validates this section (scripts/validate-documentation-impact.mjs) and the independent review compares it with the issue's documentation impact decision and the actual diff.
- Decision: UPDATED      -> Evidence lists every documentation file changed in this PR and what changed in it.
- Decision: NOT REQUIRED -> Evidence explains, for this specific change, why behaviour, contracts, architecture, configuration, automation, deployment, operations and user workflows are unaffected.
Any other decision, a missing/duplicate line, "None", "N/A", "Not applicable", a generic sentence, or the placeholders below fail validation.
-->

Decision: <UPDATED or NOT REQUIRED>

Evidence: <meaningful evidence>

## Known limitations and follow-up work

## Final safety checklist

- [ ] The change stayed within the linked issue's acceptance criteria and exclusions.
- [ ] Relevant tests were added or updated.
- [ ] Complete repository validation ran successfully, or the exact blocker is documented above.
- [ ] No secrets, credentials, local databases, or production data are included.
- [ ] No accidental migration, generated build output, or unrelated file is included.
- [ ] Financial and inventory invariants remain intact, or an explicit approved change is documented in the linked issue and this PR.
- [ ] The author (human or agent) did not merge or deploy; merge and deployment remain human-controlled.
