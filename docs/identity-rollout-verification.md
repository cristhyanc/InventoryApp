# Customer identity rollout: verification, staging smoke tests and rollback (issue #714)

**Status (18 October 2026).** The automated half of the verification is complete and passing: every
row of kind **A** and **B** in § 2 is `passed` and runs in `scripts/validate.sh`. Every row of kind
**C** — anything that needs a provisioned Microsoft Entra External ID tenant, a hosted user flow or a
real provider-issued token — is **`not verified`**, because no such tenant exists for this
repository's deployments and no agent may create one. **Production activation of customer identity
sign-in is therefore blocked** until a human completes § 3 in staging and signs off § 7.

This document is the consolidated validation of the approved rollout in
[docs/identity-provider-decision.md](identity-provider-decision.md) (Option C: coexistence, no
migration, no account linking). It replaces nothing in that design; it records how each of its claims
is checked, by whom, and what is still unchecked. Its companions are
[README.md § Authentication configuration](../README.md#authentication-configuration) for the
configuration and the per-journey staging checks, and
[docs/architecture.md § Authentication and authorization](architecture.md#authentication-and-authorization)
for the boundary itself.

## 1. Three kinds of evidence, never pooled

The one mistake this document exists to prevent is reading a green test run as "the new sign-in
works". A synthetic token is not a provider, and a configured provider is not a completable journey.
Each row of the matrix therefore declares which kind of evidence it rests on.

| Kind | Who runs it | What it can prove | What it cannot prove |
| --- | --- | --- | --- |
| **A — synthetic API/JWT** | `scripts/validate.sh`, on every pull request and every push | What the API accepts, refuses and resolves for a token with a given issuer, audience, tenant, scope and claims, and every membership decision after that | That any tenant exists, that a provider is enabled in a user flow, that a real token has the claims assumed, or that anybody can sign in |
| **B — synthetic browser journey** | the same script (Jest, jsdom) | What the SPA decides, stores, refuses and asks MSAL for across a whole journey — the authority choice, the cache partition, the invite hand-off, a cancellation and the retry | That a hosted page renders, that Google or an email address is offered on it, or that a redirect ever completes |
| **C — human hosted-provider staging** | a named human, in staging, with their own test identity | That the configured tenant actually offers and completes each supported journey, and that the token it issues is the one the API's rules assume | Nothing about production until it is repeated there (§ 4 phase 7) |

Row statuses are exactly three: **`passed`**, **`failed`**, **`not verified`**. "Not verified" is a
result, not an omission — § 7 treats it as a blocker, and nobody may record a kind-C row from a
kind-A or kind-B run.

**No external or paid identity call happens in routine CI.** Kinds A and B mint their own tokens with
their own signing keys against stubbed OpenID Connect metadata documents, and neither reaches
`login.microsoftonline.com`, `ciamlogin.com`, Google or any other provider. No secret, production
credential or real identifier is used by either: the `ExternalId` section and the SPA's
`externalIdSettings` stay empty in source control, and each test supplies its own test identifiers.

## 2. The verification matrix

Evidence is given as the test class and the test, so a reviewer can run exactly it. Backend tests are
in `backend/Inventory.IntegrationTests/`, frontend specs in `frontend/inventory-app/src/app/`.

**The dependent work is merged, and its own suites are part of this evidence.** Every launch
dependency is on `develop`: the design (#709, merged as PR #748), the API's second bearer scheme
(#710, PR #764), the SPA's authority choice and Google journey (#711, PR #765) and the local
email-and-password journey (#712, PR #767). #713 is **deferred and not a launch dependency** (design
§ 12), so this validation covers cross-issuer identity separation instead of a linking feature, and
nothing here requires one. Every suite named below runs in the same `scripts/validate.sh` pipeline, so
"row 3 passed" means it passed on the same commit as everything else — not in an earlier run of an
earlier branch.

### 2.1 Token validation and the trust boundary (issue #710's suite, re-read as #714 evidence)

| # | What is verified | Kind | Evidence | Status |
| --- | --- | --- | --- | --- |
| 1 | A workforce token from **two different** Entra tenants is accepted and resolves its own actor; the multi-tenant issuer-to-`tid` binding is intact | A | `TrustedIssuerTokenValidationTests.A_workforce_token_from_either_tenant_is_accepted_and_resolves_its_own_actor` | passed |
| 2 | A token from the pinned customer identity tenant is accepted, in both audience spellings | A | `…A_customer_identity_token_from_the_configured_tenant_is_accepted`, `…Both_pinned_spellings_of_a_scheme_own_audience_are_accepted` | passed |
| 3 | **Issuer confusion (T1).** A token signed for an attacker's own Entra tenant cannot claim the customer identity `tid`, for either audience; the pinned issuer with a foreign `tid` is refused; a workforce issuer disagreeing with its `tid` is refused; an issuer neither rule accepts is refused | A | `…A_token_signed_for_an_attacker_tenant_cannot_claim_the_customer_identity_tenant`, `…The_customer_identity_issuer_with_a_foreign_tenant_claim_is_refused`, `…A_workforce_token_whose_tenant_claim_disagrees_with_its_issuer_is_refused`, `…An_issuer_neither_scheme_accepts_is_refused` | passed |
| 4 | **Audience substitution (T2).** Each scheme validates only its own audience; another application's audience (the SPA, Microsoft Graph) is refused by both | A | `…Each_scheme_validates_only_its_own_audience`, `…A_token_for_another_application_is_refused_by_both_schemes` | passed |
| 5 | **ID-token and app-only substitution (T3).** An ID token and an app-only token are refused by both authorities | A | `…An_id_token_is_not_an_access_token_for_either_authority`, `…An_app_only_token_is_refused_by_both_schemes` | passed |
| 6 | **Untrusted and rotated-away signing keys.** A key the authority does not publish, and the other authority's real key, validate nothing | A | `…A_token_signed_with_a_key_the_authority_does_not_publish_is_refused`, `…A_key_set_without_the_tokens_signing_key_refuses_the_token` | passed |
| 7 | **Session expiry.** An expired and a not-yet-valid token are refused by both schemes, with no skew increase | A | `…An_expired_or_not_yet_valid_token_is_refused_by_both_schemes` | passed |
| 8 | A validated token without `access_as_user` is `403`, not `401`; a request with no readable bearer token is challenged | A | `…A_delegated_token_without_the_required_scope_is_forbidden_not_unauthorized`, `…A_request_without_a_readable_bearer_token_is_challenged` | passed |
| 9 | **Malformed or conflicting actor claims.** A conflicting `oid`, a non-GUID `oid`, a conflicting `tid` and a token missing the `profile` claims all resolve nobody | A | `…Conflicting_or_malformed_identity_claims_resolve_no_actor`, `…A_customer_identity_token_without_the_profile_claims_resolves_no_actor` | passed |
| 10 | **Provider outage and unavailable routes (T10).** An unreadable metadata document refuses that authority's tokens and leaves workforce sign-in working; an empty, half-filled, malformed or absent `ExternalId` section registers only the workforce scheme and is recorded as a startup error when it was meant to be filled | A | `…An_unavailable_customer_identity_authority_refuses_its_tokens_and_leaves_workforce_signin_working`, `ExternalIdBearerSchemeCompositionTests` (all cases) | passed |
| 11 | The end-to-end host still registers the synthetic scheme **and nothing else**, whatever is configured | A | `E2ETestAuthenticationCompositionTests`, `E2ETestAuthenticationFailsClosedTests` | passed |

### 2.2 Identity separation, which replaces linking validation (#713 deferred)

| # | What is verified | Kind | Evidence | Status |
| --- | --- | --- | --- | --- |
| 12 | **The same object id in two trusted tenants is two actors**, each resolving its own business and role | A | `TrustedIssuerTokenValidationTests.One_object_id_under_two_trusted_tenants_resolves_two_separate_actors` | passed |
| 13 | **Identical emails across providers.** A Google identity and a local email/password identity carrying an existing member's verified address inherit nothing, write no membership row, and leave that member's own access untouched | A | `…A_google_identity_that_shares_a_members_mailbox_inherits_nothing_from_them`, `…A_local_account_that_shares_a_members_mailbox_inherits_nothing_from_them` | passed |
| 14 | A second registration of one mailbox is a second actor with nothing of the first | A | `…A_second_registration_of_one_mailbox_is_a_second_actor_with_nothing_of_the_first` | passed |
| 15 | **Email verification decides nothing.** Neither an unverified nor a verified mailbox claim reaches a business endpoint, and a member's changed, unverified or absent address decides nothing about their membership — which is also the password-reset case, since a reset changes no `(tid, oid)` | A | `…Neither_an_unverified_nor_a_verified_mailbox_claim_reaches_a_business_endpoint`, `…A_members_mailbox_change_or_absence_decides_nothing_about_their_membership` | passed |
| 16 | The identity is the broker's `oid`, never the upstream Google `sub`; a token carrying only that subject is nobody | A | `…A_customer_identity_is_its_directory_object_id_never_the_upstream_google_subject` | passed |
| 17 | **Account-link conflict.** An identity that already holds an active membership cannot redeem a second invite: `409`, no business named, the invite unused and the existing seat unchanged — in both directions between the issuers | A | `CustomerIdentityRolloutMatrixTests.An_identity_that_already_belongs_somewhere_cannot_redeem_a_second_invite` | passed |

### 2.3 The membership lifecycle, through every identity route

| # | What is verified | Kind | Evidence | Status |
| --- | --- | --- | --- | --- |
| 18 | **Invitation redemption** through either issuer grants that business and the invite's role, reads only that business's data, and writes an ordinary membership row — a customer identity joining a workforce Owner's business and a work account joining a customer identity Owner's | A | `CustomerIdentityRolloutMatrixTests.An_invite_is_redeemable_through_either_issuer_and_grants_only_that_business` | passed |
| 18a | **Each supported hosted journey joins a business the same way**: a Google-brokered token (with its `idp` and upstream `sub`) and a local email/password token (with the tenant's `email_verified` and no `idp`) each redeem an invite and become ordinary members, and the same object id presented afterwards on a token carrying none of those claims resolves the same seat | A | `…Each_hosted_journey_joins_a_business_the_same_way_and_its_provider_claims_decide_nothing` | passed |
| 19 | **Applicant pending, applicant rejected (with its reason), business deactivated, membership revoked and no membership** are reported identically for an identity from each issuer, and every business endpoint refuses all of them | A | `…Every_non_member_account_state_is_reported_identically_through_both_issuers` (five cases) | passed |
| 20 | **Membership revocation** takes effect on the very next request through either issuer, and reinstatement returns the same seat | A | `…Revoking_a_membership_ends_access_on_the_next_request_through_either_issuer` | passed |
| 21 | **Business deactivation** ends its customer identity members' access, cancels its live invites, and reactivation restores the member's own role | A | `…Deactivating_a_business_ends_its_customer_identity_members_access_and_reactivation_restores_it` | passed |
| 22 | **Role permissions** are identical whichever issuer signed the member in: the same reported capability set, the same `200`s and the same `403`s for Operator and Owner | A | `…A_role_grants_the_same_capabilities_whichever_issuer_signed_the_member_in` | passed |
| 23 | **Super Admin boundary (T9).** A customer identity — member, stranger, or carrying the administrator's own object id — reaches no platform-admin read or write, while the configured workforce administrator does | A | `…A_customer_identity_reaches_no_platform_admin_endpoint`, `TrustedIssuerTokenValidationTests.A_second_trusted_issuer_is_no_route_to_the_platform_administrator` | passed |
| 24 | **Free identity creation does not widen anything (T6).** A customer identity with no membership authenticates and reads nothing but its own account state, which names no business | A | `TrustedIssuerTokenValidationTests.A_customer_identity_with_no_membership_authenticates_and_reads_nothing` | passed |
| 25 | **Two-business isolation with identities from both issuers (T8).** Each Owner reads only their own business; a business id in a query string and a forged support-session header change nothing | A | `CustomerIdentityRolloutMatrixTests.Two_businesses_whose_members_use_different_issuers_stay_isolated` | passed |

### 2.4 The browser journeys

| # | What is verified | Kind | Evidence | Status |
| --- | --- | --- | --- | --- |
| 26 | **An interrupted invite journey** survives the authority choice, a cancelled hosted sign-in and the retry, and the invite token reaches no MSAL request, no MSAL cache location and no stored destination — and is still readable by the acceptance page afterwards | B | `auth/customer-identity-journey.spec.ts` | passed |
| 27 | **Provider switching and cache separation.** The two journeys share no client id, authority or API audience, both keep the same cache location so the partition is the client id, and after a sign-out only the newly chosen authority is asked for a token | B | `auth/customer-identity-journey.spec.ts`, `auth-config.spec.ts`, `auth/selected-authority.spec.ts`, `auth/browser-auth-providers.spec.ts` | passed |
| 28 | **External return URLs.** A destination that is absolute, protocol-relative, `/auth` or `/sign-in` never becomes a redirect start page, and the destination is never caller-supplied in the first place | B | `auth/customer-identity-journey.spec.ts`, `auth/intended-destination.spec.ts` | passed |
| 29 | **Cancellation.** A cancelled journey is told apart from a broken one, reported once, retried only by the person, and never falls back to the other authority | B | `auth/sign-in.service.spec.ts`, `auth/sign-in.component.spec.ts` | passed |
| 30 | **A disabled or misconfigured route.** With any identifier empty or the instance not an `https` authority there is no second authority at all: no customer identity action, no second MSAL instance, nothing said about passwords, and a remembered choice is ignored | B | `auth-config.spec.ts`, `auth/selected-authority.spec.ts`, `auth/sign-in.component.spec.ts` | passed |
| 31 | A refused credential journey discloses no account existence: one of two application-authored sentences, never the provider's text | B | `auth/sign-in.component.spec.ts` | passed |
| 32 | The `NoMembership` welcome page explains that each way of signing in is a separate account | B | `access/welcome.component.spec.ts` | passed |

### 2.5 Rollback and observability

| # | What is verified | Kind | Evidence | Status |
| --- | --- | --- | --- | --- |
| 33 | **Rollback for newly registered users.** Clearing the configuration refuses every customer identity token (`401`, not "no membership") | A | `CustomerIdentityRollbackRehearsalTests.Clearing_the_configuration_refuses_customer_identity_tokens_and_leaves_workforce_signin_untouched` | passed |
| 34 | **Rollback for legacy users: no change whatsoever.** The same workforce token resolves the same business and role, on the same database, with the second issuer gone | A | same test | passed |
| 35 | **Preserved memberships and outstanding invitations.** A rolled-back deployment retains the customer identity membership (active, same role, same business) and its live invite (pending, not cancelled, not used); restoring the three settings restores the member's access unchanged and the invite is still redeemable | A | `…A_rollback_retains_the_memberships_and_the_live_invites_it_cannot_serve` | passed |
| 36 | **The legacy Super Admin recovery route stays tested.** The configured workforce pair reaches the platform-admin reads and can still deactivate a business while customer identity sign-in is off; the same object id presented from the customer tenant does not even authenticate | A | `…The_legacy_super_admin_recovery_route_still_works_while_customer_identity_signin_is_off` | passed |
| 37 | **Nothing sensitive is logged while observing failures.** A refused issuer, an expired token and a successful redemption leave no bearer token, no invitation token and no mailbox in the logs | A | `…An_authentication_failure_or_a_redemption_logs_no_token_and_no_invitation_secret` | passed |
| 38 | The browser half of the rollback lever reaches a tab part-way through the new journey: the pinned choice is dropped, the journey cannot be started against the removed authority, and a waiting invite is untouched | B | `auth/customer-identity-journey.spec.ts`, `auth/selected-authority.spec.ts` | passed |

### 2.6 The hosted-provider rows — all `not verified`

These are the rows a passing build must never be read as covering. Each is a step of § 3.

| # | What must be verified | Kind | Status | Why it is still open |
| --- | --- | --- | --- | --- |
| 39 | The External ID tenant exists, with the API and SPA registrations, the Google provider, the local email/password option, a sign-up/sign-in **user flow**, the providers enabled **inside** that flow, and the SPA application **associated** with it | C | **not verified** | No tenant is provisioned; every step is human portal work (design § 14.2) that no agent may perform |
| 40 | **Journey C (Google) end to end** in staging: offered on the hosted page, completable by a new Google user, returns to `/auth`, acquires an `access_as_user` token, and the API resolves `(tid, oid)` with **no** membership → `403` everywhere and the welcome page | C | **not verified** | Needs row 39 and a human test identity |
| 41 | **Journey D (email + password) end to end** in staging, including registration, the mailbox verification code, re-sign-in, a wrong password, the self-service password reset and an expired or reused code | C | **not verified** | Needs row 39 and a human test identity |
| 42 | **The decoded staging access token** from each supported journey carries the pinned issuer, a GUID `tid` equal to the configured tenant, a GUID `oid`, this API's audience and `access_as_user` (design § 13 item 3) | C | **not verified** | A real token is the only thing that can confirm the claim shape the whole design rests on |
| 43 | **Cross-issuer separation in practice**: the same mailbox through Google, through email/password and through a work account produces separate accounts with no inherited role | C | **not verified** | Needs rows 40 and 41 |
| 44 | **The invite window outlasts the slowest real journey** (registration plus mailbox verification) inside its 15 minutes | C | **not verified** | Only a real round trip can time it (design § 10.2) |
| 45 | **Microsoft through External ID (journey B)** | — | **not applicable in v1** | Not offered: External ID has no built-in Microsoft provider (design § 13 item 4). Work and school accounts stay on the unchanged workforce journey, which rows 1–11 and 18–25 cover |
| 46 | **The provider's own controls** — MFA methods, smart lockout, the password policy actually in force, and the accepted access-token version of each registration | C | **not verified** | Portal settings; the documented capability set and its gaps are in [README.md § Provider controls, and what a human must configure](../README.md#provider-controls-and-what-a-human-must-configure) |
| 47 | **A rollback drill against the staging tenant**: clear the settings, confirm a real customer identity user cannot sign in while a workforce user is unaffected, restore, confirm the member's access and role return | C | **not verified** | Rows 33–38 rehearse this against synthetic tokens; the drill confirms it with a provisioned tenant |

## 3. The staging smoke tests a human runs

Run these in a staging environment, in order, with a test identity the runner created. Record each
result against its row number in § 2.6, with the date and the runner's name, on issue #714 or in a
commit to this file. **Do not run any of this against production** until § 7 is signed off, and do
not use a real customer's account for it.

### 3.1 Preconditions (row 39)

Confirm, in the portal, each item of
[design § 14.2](identity-provider-decision.md#142-external-portal-changes-a-human-must-perform-no-agent-may-do-any-of-these):
the tenant, the API registration exposing `access_as_user`, the SPA registration as a PKCE public
client with exactly the redirect and post-logout URIs in use, the Google provider (its OAuth client
secret lives only in the tenant), the local email-with-password option with verification and
self-service reset, the **user flow**, the providers enabled **inside** that flow, and the SPA
application **associated** with the flow. The last three are the ones most often missed, and without
them a perfectly configured tenant offers no journey at all.

Then fill, for staging only, `ExternalId__Instance`, `ExternalId__TenantId` and `ExternalId__ClientId`
on the API, and the four `externalIdSettings` identifiers in the SPA build. None is a secret; all
four SPA values are build-time, so changing them is a build.

### 3.2 Journey C — Google (row 40)

1. Open the staging application signed out. `/sign-in` offers **Continue with Google or an email
   address** beside **Sign in with a Microsoft work account**.
2. Choose the customer identity action. The hosted page is the tenant's, and it **offers Google**.
3. Complete the journey with a Google account that has never used this application.
4. The browser returns to `/auth` and then to the application, which shows the `NoMembership`
   welcome page and its "each way of signing in is a separate account" explanation.
5. Every business page and endpoint answers `403`. `GET /api/me/account-state` answers `NoMembership`.
6. Sign out. `/sign-in` asks which authority again.
7. Repeat steps 1–3 and press Cancel on the hosted page: the application returns to `/sign-in`, says
   the sign-in was cancelled, offers both choices and starts no further redirect by itself.

### 3.3 Journey D — email and password (row 41)

1. From the same action, register a **new** email address on the hosted page.
2. Receive the one-time code in that mailbox and complete the verification.
3. Confirm the API answers `NoMembership` and `403` exactly as in § 3.2.
4. Sign out, sign in again with the same address and password: it works.
5. Sign in with a **wrong** password: the hosted page reports it, and nothing in this application
   discloses whether that address has an account here.
6. Use **forgot password** on the hosted page: a reset code arrives, the reset completes, and the new
   password signs in.
7. Let a verification or reset code expire (or reuse one): the hosted page refuses it. Starting again
   from `/sign-in` issues a new one.
8. Confirm no password, code or reset link ever appears in this application, in a URL, or in its logs.

### 3.4 Decode one token from each journey (row 42)

While signed in through each supported journey, take the API access token the SPA acquired and decode
it **locally** — a local decoder, or
`System.IdentityModel.Tokens.Jwt`/`jwt-cli` on the runner's own machine. **A token is a credential:**
never paste one into a web decoder, an issue, a pull request, a comment, a chat message or a log, and
discard it when the check is done. Confirm:

| Claim | Expected |
| --- | --- |
| `iss` | exactly the issuer the configured tenant's OpenID Connect metadata document publishes — `https://<tenant-id>.ciamlogin.com/<tenant-id>/v2.0` |
| `tid` | a GUID, equal to the configured `ExternalId__TenantId` |
| `oid` | a GUID, and the **same** value on a second sign-in by the same person |
| `aud` | the API app registration in that tenant (`<client-id>` or `api://<client-id>`; both are pinned) |
| `scp` | contains `access_as_user` |
| `sub` | present and **not** equal to `oid` — it is pairwise and this API reads it for nothing |

A missing or non-GUID `oid`/`tid` refutes the load-bearing assumption of design § 7 and is a **stop**:
it is a re-planning decision, not something to adapt to. Also confirm the member's own
`GET /api/me/access` is unchanged by whatever the token says about an email address.

### 3.5 Cross-issuer separation, in practice (row 43)

With one mailbox you control, sign in through Google and through email/password, and — if you hold a
work account with that address — through the workforce journey. Each produces a **separate** account
with `NoMembership`. Have an Owner invite one of them: only that one gains a seat, the others are
unchanged, and revoking it does not affect the others.

### 3.6 The invite window, timed (row 44)

Open an invite link while signed out, then complete the **slowest** journey (registration plus mailbox
verification). The invite must still be redeemable when you arrive at `/invite`. If it is not, the
15-minute pending-invite window is too short for a real journey: record the measured time and raise
extending it as its own reviewed issue (design § 10.2 and § 17) rather than parking the token
somewhere more durable.

### 3.7 The rollback drill (row 47)

Rehearse § 6.1 against staging, with a real customer identity member of a staging business:

1. Record, before the drill: that member's business and role, and the id and expiry of one live invite.
2. Clear the three `ExternalId__*` settings (and the SPA's, if the drill covers the browser).
3. Confirm that member cannot sign in at all, and that a workforce member's access is untouched.
4. Confirm in the database that their membership row and the invite are unchanged.
5. Restore the settings. Confirm the member's access, business and role return unchanged and the
   invite is still redeemable (if its 7 days have not run out — if they have, an Owner re-issues it,
   which is the documented outcome, not a failure).

## 4. Phased enablement

The phases are design § 14.1's; this is their current state. Each phase is independently revertible,
and the workforce journey is untouched in all of them.

| Phase | What it is | State |
| --- | --- | --- |
| 0 | Approve the design (#709) | **done** — merged |
| 1 | Human verification of every `[TBC-H]` item (design § 13) | **done** — recorded 18 October 2026 in design § 13, results on #762 |
| 2 | Provider setup in the portal (§ 3.1) | **not started** — human only |
| 3 | API accepts the second issuer (#710), code complete; its environment check is a human step | **code merged**; the signed-in check is § 3.2 step 5 |
| 4 | The SPA offers the second authority (#711, #712) | **code merged**; nothing rendered until a human configures it |
| 5 | An invited user joins through the new authority | **synthetically verified** (rows 18, 26); really verified by § 3.2 and § 3.5 |
| 6 | Consolidated security, staging and rollback validation (#714, this document) | **automated half done** (§ 2.1–2.5); § 2.6 open |
| 7 | Production activation | **blocked** — see § 7 |

There is no staged cutover and no decommissioning phase: Option C has no end state in which
workforce sign-in is removed.

## 5. Observability during the rollout

What an operator can actually see is deliberately narrow, and knowing which signals exist matters
more during a rollout than at any other time.

**At startup, one line says which issuers this deployment trusts.** It is logged under the category
`InventoryApi.Auth.ExternalIdentity`, which sits at `Information` in deployed environments, and it
takes one of three forms:

| State | Level | What it means |
| --- | --- | --- |
| "Customer identity sign-in is enabled …", with `ExternalIdTenantId` and `ExternalIdAuthority` | `Information` | The pinned scheme is registered beside the workforce one |
| "Customer identity sign-in is NOT enabled because its configuration is unusable …", with `ExternalIdConfigurationProblem` | **`Error`** | Somebody filled the section in and meant something that is not happening. Workforce sign-in is unaffected and every External ID token is refused |
| "Customer identity sign-in is not configured …" | `Information` | The shipped and rolled-back state |

**A provider outage looks like one error and a lot of `401`s.** "The customer identity authority's
metadata document could not be read …" is logged as an `Error` (with the exception) by
`FailClosedIdentityMetadata`, and every token from that authority is then refused. Workforce sign-in is
unaffected, nothing is accepted on a stale key set, and the health endpoint does not probe either
authority — a healthy API with an unreachable identity provider is the correct report, so **do not add
an identity-provider probe to the health check** to make an outage visible.

**Per-request authentication failures are not in the logs, by policy.** `Microsoft.AspNetCore` and
`Microsoft.Identity.Web` sit at `Warning` in deployed environments (see
[README.md § Logging levels](../README.md#logging-levels)), so a `401` is visible as **request
telemetry** — a request to the path with status 401 — and not as a log entry with a reason. That is
deliberate: the reason would be token detail. During a rollout, watch the ratio of `401`s on
`/api/*` and the count of `NoMembership` answers from `/api/me/account-state`; a new customer identity
user with no invitation is *supposed* to produce exactly that pair.

**What is never in a log, and is tested.** No bearer token, no invitation token, no password or
verification code (this application never receives one), and no mailbox on the paths row 37 covers.
Do not add a log line that carries a token, a token fragment, an invite token or an authorization
header to diagnose a rollout problem; the `(tid, oid)` pair, the route template and the status are
enough, and are what the existing audit events already carry.

**What is deliberately not instrumented.** There is no per-identity sign-in metric, no provider
dashboard in this repository, and no per-IP rate limiting (design § 11, T6, and § 17). Adding any of
them is its own reviewed issue; the right place to watch a provider's own sign-in health is the
tenant's portal.

## 6. Incident, recovery and rollback

### 6.1 Lever 1 — disable the new sign-in (configuration, minutes, no deployment)

Clear `ExternalId__Instance`, `ExternalId__TenantId` and `ExternalId__ClientId` on the API (and the
SPA's four `externalIdSettings`, which is a build). The second bearer scheme is not registered, the
customer identity action is not rendered, and every External ID token is refused. Verify afterwards:
the startup line reports "not configured", a workforce member signs in and sees their own business,
and a customer identity token is `401`.

Who is affected is design § 15.1's table, and the automated rehearsal of it is rows 33–38: workforce
users see no change at all; customer identity users lose access entirely, because there is no linking
and so no fallback identity; their memberships, their roles and the outstanding invites are
**retained, untouched**, and work again the moment the settings are restored; the provider, its
registrations and its users are not touched at all.

### 6.2 Lever 2 — revert the code

Reverting #710, #711 and #712 removes the second scheme and the second authority from the build. This
design changes no schema and migrates no data, so that is an ordinary deployment of an earlier commit
with no data step, no backfill and no down-migration, and the outcome for people is identical to
lever 1's.

### 6.3 Which lever, and when

| Symptom | Lever |
| --- | --- |
| The new journey is broken, abused, or offered when it should not be | **1.** It is a configuration edit and takes minutes |
| The startup line reports the configuration is unusable | **Neither.** Workforce sign-in is already unaffected and External ID tokens are already refused; fix the setting |
| The customer identity authority is unreachable | **Neither.** It already fails closed. Lever 1 only if the broken journey must stop being offered |
| A defect in the second scheme's validation, or in the SPA's authority handling | **1 first**, then 2 for the fix's own deployment |
| An External ID user must keep working | **Neither.** Invite a workforce identity they control — a new, separate membership. A rollback cannot recover their access |

### 6.4 What rollback is not

It is not a way to recover a customer identity user's access; it does not touch the identity provider,
and deleting the tenant is part of no rollback described here; and it is not a data operation, because
there is no data to revert.

## 7. Production go/no-go checklist

**The two halves of this checklist have different owners, and no agent may tick a row in the second.**

### 7.1 Agent-deliverable evidence — complete

- [x] Every kind-A and kind-B row of § 2 is `passed` and runs inside `scripts/validate.sh`.
- [x] Identity separation is validated in place of linking validation, as #713's deferral directs.
- [x] Threats T6, T8 and T9 are covered by rows 24, 25 and 23; T1, T2, T3 and T10 by rows 3, 4, 5
      and 10.
- [x] The § 15 rollback rehearsal exists as an automated test over one database (rows 33–38).
- [x] The documentation of record is consistent with what has shipped: this document,
      [README.md § Authentication configuration](../README.md#authentication-configuration),
      [docs/architecture.md § Authentication and authorization](architecture.md#authentication-and-authorization)
      and [docs/identity-provider-decision.md](identity-provider-decision.md) § 16.
- [x] No secret, production identifier or provider credential is in this repository, and no external
      identity call is made in CI.

### 7.2 The human gate — open, and blocking

- [ ] § 3.1 preconditions confirmed in the portal (row 39).
- [ ] § 3.2 Google journey completed end to end in staging (row 40).
- [ ] § 3.3 email-and-password journey completed end to end in staging (row 41).
- [ ] § 3.4 token decoded for each supported journey, with the pinned issuer and GUID `tid`/`oid`
      confirmed (row 42).
- [ ] § 3.5 cross-issuer separation confirmed with a real shared mailbox (row 43).
- [ ] § 3.6 invite window timed against the slowest real journey (row 44).
- [ ] § 3.7 rollback drill performed against staging and recorded (row 47).
- [ ] § 2.6 row 46 provider controls decided and recorded: MFA methods, lockout, the password policy
      actually in force, and each registration's accepted access-token version.
- [ ] The bot and sign-up fraud gap of design § 13 item 8 explicitly accepted or covered, with
      `SelfServiceOnboarding:Enabled` confirmed to be `false` in production.
- [ ] Production configuration filled by a human, then § 3.2–§ 3.4 repeated **in production** with a
      test identity, and the workforce journey confirmed unchanged on the same build.
- [ ] A named human records the go/no-go decision, with the date, on issue #714 or #708.

**Any unticked row in § 7.2 means `not verified`, and `not verified` blocks activation.** An
unperformed external staging verification and an unconfirmed required provider capability are not
risks to accept on the strength of a green build: nothing in § 7.1 can substitute for them, and no
smoke-test or rollback result may be recorded that somebody did not actually perform.

## 8. Known limitations, stated rather than worked around

- **Microsoft through External ID (journey B) is not in v1** (design § 13 item 4). Work and school
  accounts stay on the unchanged workforce journey; personal Microsoft accounts would need a custom
  OIDC provider and its own approved task.
- **Identity linking is deferred (#713).** The same person signing in a different way is a different
  actor, with no membership and no inherited access. The supported advice is to pick one way to sign
  in and keep using it; an Owner may invite the second identity, which creates a second, separate
  seat.
- **Bot and sign-up fraud protection is not built into External ID, and there is no built-in per-IP
  throttling** (design § 13 item 8). Nothing here implements either, so a free identity stays cheap
  to create — which is why `SelfServiceOnboarding:Enabled` stays off and why the per-identity
  application rate limit is not relied on as an anti-abuse control. Per-IP limiting would be its own
  reviewed issue.
- **The password policy actually in force is whatever the tenant is set to**, and was not confirmed.
- **A stolen, unexpired bearer token is usable until it expires** (design § 11, T4). Token binding
  and DPoP are out of scope, unchanged by this work.
- **Kinds A and B cannot prove a provider works.** Every claim about a hosted page, a user flow or a
  real token is a kind-C row, and all of them are open.
- **Cost and provider capability come from Microsoft's published documentation, read on 18 October
  2026** (design § 13), not from a provisioned tenant or an invoice.
