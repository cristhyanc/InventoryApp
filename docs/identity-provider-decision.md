# Customer identity: the Microsoft Entra External ID design (issue #709)

**Status: approved design (merged with #709). § 5 is implemented by #710; everything else is still
design.** What ships today is the API's side of the trust decision: a second named bearer scheme
pinned to one configured External ID tenant, behind an issuer-selecting policy scheme, with the
multi-tenant workforce validator unchanged — see
[docs/architecture.md § Trusted issuers and the two bearer schemes](architecture.md#trusted-issuers-and-the-two-bearer-schemes-issue-710)
and [README.md § Customer identity: a second trusted issuer](../README.md#customer-identity-a-second-trusted-issuer-issue-710)
for the behaviour of record, and § 16 below for what #710 did and did not do.

**Deferred, and not current behaviour:** the SPA's authority selection and the Google (#711) and
local email/password (#712) journeys — the Angular application still signs in against the workforce
`common` authority alone and offers no second option — identity linking (#713, deferred and not
required under this recommendation), and the consolidated security, staging and rollback validation
(#714). The `ExternalId` configuration section is **empty in source control**, so a deployment no
human has configured behaves exactly as it did before #710: workforce Microsoft Entra ID on the
`common` authority, MSAL in the SPA, `Microsoft.Identity.Web` validating bearer tokens on the API,
and memberships bound to the validated Entra `(tid, oid)` claim pair.

**Merging the pull request that adds this file is the human approval of the design** (issue #709's
decision gate, under parent #708). Until it is merged, no downstream implementation subtask —
#710, #711, #712, #713, #714 — may begin. Merging it approves the *design*: it provisions nothing,
registers nothing in any external portal, changes no configuration, and grants no agent authority to
do any of those things.

## 1. How to read the confirmation markers

The agent that wrote this document has **no web access** and could not read Microsoft's current
product documentation, pricing pages or tenant portals. Every statement about what Microsoft Entra
External ID *currently does*, *currently costs*, or *currently supports* is therefore recollection,
not fact, and is marked:

> **[TBC-H]** — to be confirmed by a human against current Microsoft documentation or a real tenant
> before the statement is relied on.

Statements about **this repository** carry no marker: they were read from the code and tests on the
branch this document was written on, and the file and type names are cited so a reviewer can check
them. Section 2 is entirely of that second kind.

A **[TBC-H]** item that a human checks and finds to be wrong, or cannot confirm, is a blocker for the
dependent task, not a detail to work around — see § 13 and § 16.

**Verification results (2026-10-18).** Every **[TBC-H]** item was checked against Microsoft's
official External ID documentation; the outcome of each is recorded in § 13. Where a check corrected
a statement, the corrected text is in place in the section concerned (§ 4 journey B, § 4 "The
`profile` scope is required", § 5.1 rule 2, § 11 T6, § 14.2, § 16). The remaining inline
**[TBC-H]** markers are kept so the original reasoning stays traceable; § 13 is authoritative for
their status.

## 2. What exists today (read from the code, not recollected)

| Fact | Where it is |
| --- | --- |
| The API registered exactly one real bearer scheme, `AddMicrosoftIdentityWebApi(configuration.GetSection("AzureAd"))`, for every environment except the `E2ETest` host. **Since #710** it registers that same scheme plus, only when the `ExternalId` section names one complete tenant, the pinned `ExternalIdBearer` scheme behind the `IssuerSelectingBearer` policy scheme; with the section empty - the shipped state - the registration is unchanged | `backend/InventoryApi/Auth/E2ETesting/InventoryApiAuthenticationExtensions.cs`, `backend/InventoryApi/Auth/ExternalIdentity/` |
| The bound configuration is `Instance: https://login.microsoftonline.com/`, `TenantId: common`, `ClientId` (the API app registration's public application id), `Scopes: access_as_user` | `backend/InventoryApi/appsettings.json`, `AzureAd` section |
| Every controller carries `[Authorize]` and `[RequiredScope("access_as_user")]`; every endpoint additionally declares one of exactly four authorization classes | `docs/architecture.md` § Capability authorization; `EndpointAuthorizationClassificationTests` |
| The actor is the validated `(tid, oid)` pair, **both halves required, both parsed as GUIDs**, normalised to canonical upper-case dashed form; every recognised spelling of a claim that is present must agree, or the caller is unidentifiable | `Inventory.Domain.Tenancy.ActorIdentity`, `InventoryApi.Auth.EntraActorIdentityAccessor` |
| `sub` is not read anywhere. Email, display name and the directory tenant alone are explicitly not identity | `ActorIdentity` remarks; `EntraActorIdentityAccessor` remarks |
| `name` and `preferred_username`/`email` are read by a **separate** accessor and stored on the membership as display-only personal data that decides nothing | `InventoryApi.Auth.EntraActorProfileAccessor`; `docs/architecture.md` § Member display name and email |
| A business is resolved from exactly one active `BusinessMembership` on an `Active` business; none, several, or any other business status denies | `Inventory.Domain.Tenancy.BusinessMembershipResolutionPolicy` |
| One identity may hold at most one active membership anywhere, enforced by a filtered unique index over the active rows and by `MembershipEligibility` inside the write transaction | `Inventory.Domain.Tenancy.MembershipEligibility`; migration `AddOneActiveMembershipPerIdentity` |
| The Super Admin is one `(tid, oid)` pair in configuration — `PlatformAdmin:DirectoryTenantId` / `PlatformAdmin:ObjectId` — held **outside** the database, empty in source control, and both-empty means nobody is a platform administrator | `InventoryApi.Auth.PlatformAdmin.PlatformAdminOptions`, `ConfiguredPlatformAdmin` |
| An invite stores its creator's `(tid, oid)`, a SHA-256 hash of a 256-bit token, the role it grants, and a 7-day fixed lifetime; redemption creates the caller's **own** membership | `Inventory.Infrastructure.Models.MemberInvite`; `docs/architecture.md` § Invite redemption |
| The invite token crosses the sign-in redirect in the tab's `sessionStorage`, never in the address bar, MSAL's redirect start page or MSAL's `localStorage` cache | `frontend/inventory-app/src/app/access/pending-invite-token.ts`, `access/invite-address-bar.guard.ts`; `docs/architecture.md` § Invite acceptance page |
| The SPA uses one MSAL `Configuration`: one `clientId`, the `common` authority, `<origin>/auth` as the redirect URI, and `BrowserCacheLocation.LocalStorage` | `frontend/inventory-app/src/app/auth-config.ts` |
| Business applications are rate-limited **per identity** (5 per sliding window) because the caller belongs to no business | `Inventory.Application.Onboarding.BusinessApplicationRateLimiter` |
| Self-service onboarding is behind `SelfServiceOnboarding:Enabled`, `false` in source control | `backend/InventoryApi/appsettings.json`; `docs/tenant-rollout.md` |
| The E2E host swaps in a synthetic scheme carrying the same `(tid, oid)` pair and `scp: access_as_user`; every other environment gets the real scheme | `backend/InventoryApi/Auth/E2ETesting/` |

Two consequences of that table matter for everything below.

**`(tid, oid)` is trustworthy only because `tid` is bound to the validated issuer.** On the `common`
authority `Microsoft.Identity.Web` validates the token's issuer against the tenant the token claims
to come from, so `tid` is not a self-asserted value. The pair's security rests on that binding. Any
design that adds a second issuer must preserve it per issuer, or `(tid, oid)` stops being an
identity and becomes two request-supplied strings.

**`tid` is never used alone, anywhere.** `ActorIdentity` requires both halves, `ConfiguredPlatformAdmin.Matches`
compares the whole pair, and memberships and invites store both columns. Nothing in the application
treats a directory tenant as an organisation, a trust level or a business. That invariant is what
makes a single shared customer directory safe to add; it must not be weakened.

## 3. The decision

**Microsoft Entra External ID is the identity provider.** The owner selected it on 2026-10-17. This
document does not re-evaluate that choice and no downstream task may reopen it.

**The recommended rollout is coexistence with no migration and no account linking** (Option C in
§ 6): the existing workforce Microsoft sign-in on `common` stays exactly as it is, a new External ID
tenant is added beside it as a second trusted issuer, existing users keep their current `(tid, oid)`
and never change identity, and identity linking is deferred — **#713 is not required under this
recommendation** (§ 12).

### Why the alternatives were not chosen (short note, not a re-evaluation)

| Alternative | Why not |
| --- | --- |
| **Auth0 / Okta CIC** | A third identity vendor beside the Microsoft Entra tenant the business already runs. Workforce Microsoft sign-in would have to be federated *through* it or kept as a second issuer anyway, so it adds a vendor without removing one, plus a second billing relationship and a second place where a token-validation mistake is possible. |
| **Amazon Cognito** | The application is hosted entirely on Azure (App Service, Static Web Apps, Key Vault, Application Insights, managed identities). Putting the identity boundary in another cloud adds a cross-cloud trust and operational dependency for no capability the Microsoft option lacks. |
| **Firebase Authentication** | Strong for mobile-first consumer apps; weaker fit for an API that already validates Entra v2 tokens with `Microsoft.Identity.Web`, and it would mean replacing rather than extending the validation stack. |
| **Azure AD B2C (the older product)** | The predecessor of External ID for customer scenarios; new deployments are directed to External ID **[TBC-H]**. Choosing the superseded product would buy a migration later. |
| **Own password storage / own OIDC server** | Explicitly excluded by parent #708, and rightly: password hashing, credential-stuffing defence, email verification, password reset and account recovery are a permanent security liability this repository has no reason to own. |

## 4. The identity decision table

This is the authoritative statement of what each journey issues and what the API trusts. The
distinction the table turns on: **the upstream provider identity is not the identity the API sees.**
External ID is a broker. A Google user and an email/password user are *user objects in the External
ID directory*; the API receives a token the External ID tenant issued about that object, not a token
Google issued.

**An ID token is never an API access token.** The SPA receives an ID token to know who is signed in;
the API accepts only an access token acquired for the API's own scope. The API must reject a token
whose audience is the SPA's client id, whose `scp` does not contain `access_as_user`, or that carries
`nonce`/`at_hash`-style ID-token-only claims as its proof of purpose. Nothing in the current code
accepts an ID token, and no implementation task may introduce a path that does.

| | **A. Workforce Microsoft (today, unchanged)** | **B. Microsoft through External ID** | **C. Google through External ID** | **D. Local email + password in External ID** |
| --- | --- | --- | --- | --- |
| **Supported account types** | Entra ID work/school accounts in *any* Entra tenant (`common`) | **Not offered in v1 (§ 13 item 4).** External ID has no built-in Microsoft account provider: personal Microsoft accounts are added as a custom OIDC provider pointing at `login.live.com`, and work/school accounts only one named Entra tenant at a time, with no "any tenant" option equivalent to `common`. Work/school users therefore stay on journey A. Personal Microsoft accounts through custom OIDC may be added later as its own approved task; no child of #708 currently owns it | Google consumer and Workspace accounts, through a Google OAuth client the tenant is configured with **[TBC-H]** | Email address plus password, created in the External ID tenant **[TBC-H]** |
| **SPA authority** | `https://login.microsoftonline.com/common` (in source today) | `https://<tenant-subdomain>.ciamlogin.com/<tenant-id>/` (endpoints under `oauth2/v2.0/`); verified in § 13 item 2 | Same as B (the SPA never talks to Google; External ID does) | Same as B |
| **Who the user authenticates to** | Their own Entra tenant | External ID, which redirects to Microsoft and back | External ID, which redirects to Google and back | External ID itself |
| **Upstream provider identity** | — (this *is* the upstream) | Microsoft `sub`/`oid` in the user's home tenant | Google `sub` | — (no upstream) |
| **Identity the API receives** | `tid` = the user's home tenant id, `oid` = their object id in it | `tid` = the **External ID tenant id** (one value for every B/C/D user), `oid` = the user's object id in that tenant **[TBC-H]** | Same shape as B **[TBC-H]** | Same shape as B **[TBC-H]** |
| **API access-token issuer** | `https://login.microsoftonline.com/<tid>/v2.0`, validated against the token's own `tid` | The External ID tenant's v2 issuer, one fixed value: `https://<tenant-id>.ciamlogin.com/<tenant-id>/v2.0`, taken from the tenant's metadata document (§ 13 item 2) | Same as B | Same as B |
| **API access-token audience** | The audience of the **workforce** API app registration (`AzureAd:ClientId` today) | The audience of a **separate API app registration in the External ID tenant** — a different client id, because an app registration belongs to one tenant | Same as B | Same as B |
| **Required scope** | `access_as_user` in `scp` (unchanged) | `access_as_user` in `scp`, exposed by the External ID API registration | Same as B | Same as B |
| **Claims the API maps identity from** | `tid` + `oid` only | `tid` + `oid` only | `tid` + `oid` only — **never** the Google `sub`, and never an `idp` claim | `tid` + `oid` only — **never** the email address |
| **Claims the API reads display-only** | `name`, `preferred_username`/`email` | same | same | same |
| **Authoritative source of a verified email** | The user's Entra tenant verified the mailbox when the account was created; the token's `preferred_username` is nevertheless display-only here (§ 10) | The upstream Microsoft account's verification, as reflected by External ID **[TBC-H]** | Google's verification, as reflected by External ID **[TBC-H]** | External ID's own sign-up verification **[TBC-H]** |
| **What the API does with "verified email"** | **Nothing today, and nothing under this design.** No authorization, membership, invite or Super Admin decision reads an email address (§ 10) | same | same | same |

### The `profile` scope is required

`oid` and `tid` are issued to an application only when the token request includes the `profile`
scope (§ 13 item 3). MSAL Browser adds `openid` and `profile` to interactive requests by default,
but the External ID token request must not rely on that silently: #711 and #712 must include
`profile` explicitly or prove with a test that it is present, and #710's tests must show that an
External ID token missing `oid` or `tid` leaves the caller unidentifiable (`ActorIdentity` fails
closed, so the risk is that nobody can sign in, not that the wrong person can).

### Why one shared `tid` is safe, and what it costs

Every B/C/D user has the *same* `tid`. That is fine, because `tid` is never used alone (§ 2) and the
pair stays unique: `oid` is unique inside the directory. Two things it does cost:

- **A trusted `tid` stops implying "somebody's employer vouched for this account".** Anybody who can
  complete self-service sign-up in the External ID tenant holds a validly issued token with the
  trusted `tid`. "Authenticated" becomes "anyone on the internet who can receive an email", which
  changes the threat picture for the membership-free endpoints (§ 11, T6).
- **Per-identity rate limiting weakens**, because a new identity is free to create. The business
  application limiter is keyed on the identity (§ 2); under External ID an abuser can get a new
  bucket per sign-up. This is a finding for #714, not a reason to change the limiter now:
  self-service onboarding is off by default and must stay off until a human verifies the gate.

## 5. Token validation design

The target state is **exactly two validation schemes, each with its own complete, self-contained
validation, and no issuer-agnostic path.** "Two schemes" is not "two issuer strings": the workforce
scheme is deliberately multi-tenant and keeps accepting a tenant-specific issuer from *any* Entra
tenant, each bound to that token's own `tid`, exactly as `common` does today; the External ID scheme
accepts one pinned issuer and nothing else. Preserving today's workforce sign-in *requires* that
asymmetry, and § 5.1 states each scheme's rule separately so neither is tightened or loosened into
the other.

### 5.1 The rules, stated exactly

For a request to be authenticated, all of the following must hold, **within the one scheme the token's
issuer selects** — never by pooling the two schemes' accepted values. A failure is `401`; a validated
token that fails the scope check is `403`, as today.

1. **Signature.** Verified against the signing keys published by the authority that issued it,
   discovered from that authority's own OpenID Connect metadata document. The two authorities have
   separate metadata and separate keys; a key from one must never validate a token claiming the
   other's issuer.
2. **Issuer, stated per scheme.** There are two issuer rules, and neither scheme accepts the other's
   issuers:
   - **Workforce scheme (unchanged, and deliberately multi-tenant).** `iss` must be a
     tenant-specific Entra v2 issuer of the form `https://login.microsoftonline.com/<tid>/v2.0`
     (including the issuer aliases `Microsoft.Identity.Web` already recognises), for **any** Entra
     tenant, validated by the same `Microsoft.Identity.Web` Entra issuer validator the `common`
     authority uses today — which matches the issuer against the token's own `tid` (rule 3) rather
     than against a list of tenants this repository holds. That is the whole meaning of "workforce
     Microsoft sign-in is preserved": a work or school account in a tenant nobody here has ever
     heard of still signs in. It must **not** be replaced by a literal issuer string, a single-tenant
     authority, or a tenant allowlist, because any of those would silently break existing users in
     other tenants.
   - **External ID scheme (new, single-tenant and pinned).** `iss` must equal **one literal value**:
     the v2 issuer of the one configured External ID tenant, which Microsoft documents as
     `https://<tenant-id>.ciamlogin.com/<tenant-id>/v2.0` and says to take from the tenant's metadata
     document rather than build by hand (§ 13 item 2). Pinned, not templated: no
     wildcard, no repository-authored pattern, no tenant placeholder, and no second External ID
     tenant.

   So the accepted issuer *set* is "any Entra tenant, bound to its own `tid`" plus "one fixed External
   ID issuer" — not two literal strings. No wildcard or hand-written regular expression is accepted
   on either side, and a token whose issuer satisfies neither rule matches no scheme and is `401`.
3. **Issuer-to-tenant binding, in both schemes.** `tid` must be the tenant the validated issuer
   names. In the workforce scheme that binding is what makes multi-tenant acceptance safe, and it is
   exactly what `Microsoft.Identity.Web`'s Entra issuer validation already performs on `common`: the
   token can only ever assert the tenant that signed it. It must be preserved as-is and never widened
   — in particular, `ValidateIssuer` must not be turned off, and no custom `IssuerValidator` may
   replace the Entra one, for the second scheme's benefit. In the External ID scheme the bound value
   is a single fixed `tid` and must be asserted explicitly. **A design that validated the signature
   and then read `tid` without binding it to the issuer would be the central vulnerability of this
   whole change** (§ 11, T1).
4. **Audience, per scheme.** `aud` must be the audience of the API app registration *in the tenant
   that issued the token* — the workforce registration for A, the External ID registration for B/C/D.
   The two audiences are different values; **each scheme validates only its own**, and they are never
   merged into one pool, because merging them would let a token issued by one tenant for its own API
   be accepted as the other's. Per-scheme audience validation survives the asymmetry in rule 2
   unchanged: the workforce scheme accepts many issuers but still exactly one audience, and the
   External ID scheme accepts one issuer and one audience. The implementation task must **pin the
   accepted audiences explicitly** rather than rely on whatever `Microsoft.Identity.Web` derives from
   `ClientId` by default, and must pin each registration's accepted access-token version **[TBC-H]**
   so `api://<client-id>` versus the bare client id is a decision rather than a default.
5. **Scope.** `scp` must contain `access_as_user`. Unchanged, and still enforced by
   `[RequiredScope]` on every controller.
6. **Token type.** The token must be an access token for the API. An ID token, a token for Microsoft
   Graph, a token whose audience is the SPA, and an app-only token with `roles` but no `scp` are all
   rejected. There is no delegated-vs-application branch to add: this API has no app-only surface.
7. **Lifetime.** `nbf`/`exp` validated with the framework default clock skew; no skew increase.
8. **Subject.** `sub` is read by nothing and must stay that way. It is pairwise per application in
   Entra v2 **[TBC-H]** and therefore not a stable cross-application identifier; substituting it for
   `oid` is explicitly out of bounds (§ 7).

### 5.2 How to register that in ASP.NET Core

Two shapes are available. The recommendation is the first; #710 chooses and verifies with tests.

- **Recommended: two named JWT bearer schemes — the existing multi-tenant workforce validator and a
  second pinned to the External ID tenant — behind a policy scheme that picks one by the token's
  issuer.** Register `AddMicrosoftIdentityWebApi` twice: one scheme bound to the existing `AzureAd`
  section, keeping `TenantId: common` and therefore keeping the Entra issuer validator that accepts
  any tenant's issuer bound to that token's `tid`; one bound to a new `ExternalId` section, pinned to
  that tenant's single issuer and its own audience. Front them with an
  `AddPolicyScheme`/`ForwardDefaultSelector` that reads the unvalidated `iss` **only** to choose
  which scheme performs the real validation. Each scheme then validates signature, issuer, audience
  and tenant binding completely for its own authority, under its own rule from § 5.1. Picking a
  scheme is not trusting a claim: the chosen scheme still rejects everything that does not match it,
  so routing an External ID-issued token to the workforce scheme (or the reverse) ends in `401`
  rather than in an acceptance, and a token matching neither rule reaches no scheme and is `401`.
  The selector itself must be covered by § 5.4's mismatch tests, because its input is unvalidated.
- **Rejected: one scheme with both issuer rules in `ValidIssuers`/`IssuerValidator` and both
  audiences in `ValidAudiences`.** Fewer moving parts, but it makes the issuer-to-audience pairing
  implicit — any accepted issuer with any accepted audience passes — which is exactly the confusion
  rule 4 exists to prevent. It is also the shape in which the two schemes' *different* issuer rules
  get flattened: a single validator that must accept both "any Entra tenant, bound to its `tid`" and
  "one pinned External ID issuer" is one edit away from accepting any Entra tenant's token with the
  External ID audience. It would have to be re-tightened with a custom validator, at which point it
  is no simpler.

`AddInventoryApiAuthentication` stays the single composition-root decision point, and the `E2ETest`
branch stays an either/or that registers the synthetic scheme **and nothing else** — the E2E host
must not gain a second real scheme as a side effect. `E2ETestAuthenticationCompositionTests` and
`E2ETestAuthenticationFailsClosedTests` are the tests that pin that, and #710 extends them rather
than replacing them.

### 5.3 Configuration shape, and what is secret

```jsonc
// In place in backend/InventoryApi/appsettings.json since #710, in this shape and empty. No real
// identifier is committed there, exactly as PlatformAdmin and BusinessBootstrap commit none.
"ExternalId": {
  "Instance": "",      // the External ID authority host                        [TBC-H]
  "TenantId": "",      // the External ID tenant id (a GUID) — one fixed value  [TBC-H]
  "ClientId": "",      // the API app registration in the External ID tenant
  "Scopes": "access_as_user"
}
```

**Nothing in this design introduces a new secret.** The SPA is a public client using authorization
code with PKCE and holds no client secret; the API validates tokens with the authority's public
signing keys and holds no client secret either. Every value above is an identifier, not a credential
— the same reasoning `PlatformAdminOptions` already records — so these are ordinary App Service
application settings and need no Key Vault reference. **Empty must fail closed:** with `ExternalId`
empty the second scheme is not registered at all and only workforce sign-in works, which is both the
shipped state and the rollback state (§ 15). No implementation task may make an absent or
half-filled section mean "accept any issuer".

### 5.4 Required regression tests for the two schemes (#710)

The two schemes are a security boundary, and the asymmetry in § 5.1 rule 2 — many accepted workforce
issuers, one pinned External ID issuer — is the part a later edit is most likely to get wrong in a
way nothing notices. **#710 is not complete without regression tests proving each scheme accepts
only what its own rule allows.** At minimum:

| What must be covered | Expected outcome |
| --- | --- |
| **Two different workforce tenants.** A valid workforce access token from tenant X and one from tenant Y, each with the workforce audience and `access_as_user` | Both accepted; the actor resolves to `(X, oid)` and `(Y, oid)` respectively. **One tenant is not enough**: a regression that pinned the workforce scheme to a single tenant would still pass a one-tenant test while having broken sign-in for every other tenant's users |
| **A valid External ID token.** Issued by the configured External ID tenant, with the External ID API audience and `access_as_user` | Accepted; the actor resolves to `(ExternalIdTenantId, oid)` |
| **Issuer mismatch — a third tenant claiming the External ID `tid`.** A token correctly signed by an attacker-controlled Entra tenant whose `tid` claim is the External ID tenant id | `401` (§ 11, T1) |
| **Issuer mismatch — the External ID issuer with a foreign `tid`** | `401` |
| **An issuer neither rule accepts** (a non-Entra issuer, or a malformed or absent `iss`) | `401`, matching no scheme |
| **Tenant mismatch inside the workforce scheme.** `iss` names tenant X, `tid` claims tenant Y | `401`; adding the second scheme must not loosen the issuer-to-`tid` binding |
| **Audience mismatch across the schemes.** A workforce-audience token presented as an External ID token, and an External ID-audience token presented as a workforce token | `401` each — per-scheme audience validation, never a merged pool (§ 11, T2) |
| **Audience mismatch within a scheme.** The SPA's audience, or a Microsoft Graph token, from either authority | `401` (§ 11, T2) |
| **An ID token used as a bearer token**, for each authority | `401` (§ 11, T3) |
| **Missing scope.** A validly issued token from either scheme without `access_as_user` in `scp` | `403`, as today |
| **Empty and half-filled `ExternalId` configuration** | The second scheme is not registered, every External ID token is `401`, and workforce sign-in is unaffected (§ 11, T10) |
| **The `E2ETest` host** | `E2ETestAuthenticationCompositionTests` and `E2ETestAuthenticationFailsClosedTests` extended, not replaced: it still registers the synthetic scheme **and nothing else** |

These are requirements on #710, not tests this design-only document adds. How a token is minted in a
test (a test signing key with a stubbed metadata document, rather than a real tenant) is #710's
choice, provided no test key or metadata stub is reachable from a non-test code path.

## 6. Rollout options, evaluated

| | **Option A — full cutover** | **Option B — staged cutover with linking** | **Option C — coexist, no migration (recommended)** |
| --- | --- | --- | --- |
| Workforce `common` sign-in | Removed | Kept during a transition, then removed | **Kept indefinitely, unchanged** |
| Existing users' `(tid, oid)` | Changes — every membership row must be re-keyed | Changes once each user links | **Never changes** |
| Account linking (#713) | Mandatory, up front | Mandatory | **Not required** |
| Schema change | Likely (identity re-key, or a link table) | Likely | **None** |
| Existing-account continuity | At risk: a failed re-key strands a user from their business | At risk during the transition window | **Guaranteed — nothing about existing accounts is touched** |
| Rollback | Hard: reverting code does not un-re-key rows | Medium: partially linked users are in a mixed state | **Easy: remove one configuration section** (§ 15) |
| Operational complexity | One authority, one issuer — simplest *end state* | Two authorities plus a linking flow and its own threat surface — the most complex | Two authorities, no linking — one extra issuer and nothing else |
| Cost | One External ID tenant **[TBC-H]** | Same **[TBC-H]** | Same **[TBC-H]** |
| New Google / email-password users | Supported | Supported | **Supported** |
| The person who wants both logins | Gets one identity | Gets one identity, after proving both | **Gets two separate identities with no shared membership** (§ 12) |

**Recommendation: Option C.** It is the only option that cannot strand an existing user, because it
changes nothing about them; it delivers the parent issue's actual business outcome (a customer can
sign in with Google or an email address without holding a Microsoft work account); it needs no
schema change and no migration; and its rollback is a configuration edit. Its one real cost is that
the application has two authorities to keep configured and tested for as long as both exist, and the
one real product limitation is the two-identities consequence in § 12 — which is a limitation to
state plainly to users, not a security problem.

### Cost and operational complexity

- **Provider cost.** External ID is billed per monthly active user with a free allowance **[TBC-H]**;
  the current tenancy is one live business, so the expected bill is at or near zero at launch
  **[TBC-H]**. The workforce side adds no licence cost today and Option C does not change that
  **[TBC-H]**. A human must confirm the current price, the free tier's size, and whether MFA or any
  anti-abuse feature is charged separately or requires a premium tier **[TBC-H]** before approving
  #710.
- **Operational complexity.** Two authorities means: two app registrations per side (SPA + API),
  two redirect-URI lists to keep correct across local/production, two metadata endpoints whose
  availability the API depends on, and two sign-in paths in every regression pass. Section 14 lists
  the human portal work; section 16 lists what must be tested.

## 7. Mapping each trusted issuer to `(tid, oid)`

**The rule, for every issuer, is identical: the actor is `(tid, oid)`, both halves required, both
GUIDs, read only from a token whose issuer validated the `tid` they came with.** No per-issuer
special case. `EntraActorIdentityAccessor` and `ActorIdentity` are not changed by this design: a
B/C/D user is an ordinary `(tid, oid)` actor whose `tid` happens to be the External ID tenant.

Specifically forbidden, and a reviewer should treat any of them as a blocker:

- Substituting `sub` for `oid`, for any issuer, under any condition.
- Deriving a GUID from a non-GUID value — hashing an email address, a Google `sub` or a `sub` into a
  GUID-shaped string so it fits the column. That manufactures an identity the identity provider
  never issued and makes the uniqueness of the pair this application's problem rather than the
  directory's.
- Relaxing the "every recognised spelling present must agree" rejection in
  `EntraActorIdentityAccessor.TryResolveGuidClaim`, or the GUID parse in `ActorIdentity.Normalize`,
  to accommodate a claim shape that turns out not to be a GUID.
- Reading an `idp`, `identity_provider` or `federated_claims` value into identity, membership, or any
  authorization decision.

**This mapping holds only if the GUID assumption holds.** The load-bearing assumption is:

> An access token the External ID tenant issues for journeys B, C and D carries `tid` = the External
> ID tenant id (a GUID) and `oid` = the user object's id in that tenant (a GUID), for federated
> users exactly as for local ones. **[TBC-H]**

If a human confirms it, **no schema change, no migration and no new identity model are needed**, and
#710 is a token-validation and configuration task only.

### If the GUID assumption is refuted

If External ID turns out not to issue `oid`, or to issue a non-GUID subject identifier for local or
federated users, then `(tid, oid)` cannot represent a B/C/D user and a **new identity model is
necessary**. That is not something #710 may improvise. It would need a separately approved, bounded
task, and its blast radius is:

| Affected | What changes |
| --- | --- |
| Schema | `BusinessMembership.DirectoryTenantId`/`ObjectId`, `MemberInvite.CreatedByDirectoryTenantId`/`CreatedByObjectId`, `SupportSession`'s actor pair, and any costing-repair or audit row recording a creating identity — each needs an issuer-qualified identity (for example `(issuer, subject)` with a declared normalisation) |
| Indexes | The filtered unique index `AddOneActiveMembershipPerIdentity` created over the active rows' identity columns — the one-active-membership guarantee is an index, so changing the identity columns rebuilds it |
| Memberships | `MembershipEligibility`, `BusinessMembershipResolutionPolicy`, `MembershipWriteGuard` and every store that names the identity columns |
| Invitations | Invite creation records the creator's identity; redemption writes the redeemer's. Token hashing and the 256-bit token are unaffected |
| Audit references | Every structured audit event and every row that records "who did this" |
| Administrator configuration | `PlatformAdminOptions` is a `(tid, oid)` pair in App Service configuration; a new model changes the keys a human must set, which is a production configuration change (§ 8) |
| Tests | The two-business isolation tests, the E2E synthetic actors, and `ActorIdentityTests` |

A migration of that size over the identity of a live business's members is exactly the kind of change
`AGENTS.md` reserves for explicit human approval with a backup and rollback plan. **Confirm the GUID
assumption before approving #710**; a refutation means stopping and re-planning, not adapting in
flight.

## 8. Where the Super Admin identity lives

**Unchanged: `PlatformAdmin:DirectoryTenantId` / `PlatformAdmin:ObjectId`, one pair, in App Service
configuration, outside the database — and under the recommendation it stays the Super Admin's
existing *workforce* pair.** `ConfiguredPlatformAdmin` keeps resolving it through the same
`ActorIdentity.TryCreate` normalisation, both-empty still means nobody is a platform administrator,
and the pair is still an identifier rather than a secret.

Three deliberate consequences:

- **The platform administrator does not live in the customer directory.** Anybody who completes
  self-service sign-up in the External ID tenant shares the administrator's `tid`, which would leave
  `oid` as the only thing separating a self-registered stranger from the platform administrator.
  That is already how `Matches` compares — the whole pair, never the tenant — so it would not be a
  vulnerability; but keeping the administrator on the workforce tenant preserves a second,
  independent directory boundary for the one identity that can read across businesses. Recommended,
  and it is the no-change option.
- **The Super Admin is never linked or migrated through any customer flow.** #713 already forbids
  it; under Option C there is no linking flow at all, so the prohibition is structural.
- **Re-pointing the pair to an External ID identity is possible but is a human-only production
  configuration change**, and because `PlatformAdminOptions` holds exactly one pair it is a
  *replacement*, not an addition: there is no path to two platform administrators, and no
  implementation task may add one. Doing it would also lose the directory separation above.

The support-session and role-preview surfaces are unaffected. They are gated on
`PlatformAdminPolicy`, which is that one configured pair; nothing about them reads an issuer, an
email address or a provider.

## 9. The sign-in flow in the SPA

### 9.1 Flow and validation

- **Authorization code with PKCE**, for both authorities. MSAL Browser's redirect flow already does
  this; the implicit and hybrid flows must stay disabled in both app registrations **[TBC-H that
  External ID offers no other default]**.
- **`state` and `nonce`** are generated and validated by MSAL per request; no code may construct an
  authorize request by hand or reuse a `state`.
- **Fixed allowed callbacks.** The redirect URI stays `<origin>/auth` and the post-logout URI stays
  the origin, as in `auth-config.ts` today. The allowed list in each registration is exactly the
  origins in use — `http://localhost:4200/auth` and the deployed Static Web App origin — and the
  return destination inside the app is resolved from the application's own router state, never from a
  URL parameter the authorize response carries. Open-redirect protection is "there is no
  caller-supplied return URL", not a validation routine.
- **The invite token does not take part in the redirect** and must not start to. It waits in the
  tab's `sessionStorage` (§ 10).

### 9.2 Account selection and token-cache separation

- **Two MSAL configurations, two caches.** A second authority must not share a cache partition with
  the first: MSAL partitions by client id and authority, so the design is one MSAL instance per
  authority with distinct client ids, rather than one instance whose authority is switched at
  runtime. Switching the authority on a live instance is the shape that produces a token from
  authority X being served from the cache for authority Y.
- **The person chooses the authority once, explicitly, before any redirect** — two buttons
  ("Sign in with Microsoft work account" / "Sign in with Google or email"), not a silent
  probe. #711 and #712 design the actual wording.
- **The chosen authority is remembered per tab/browser**, so a token refresh goes back to the
  authority that issued the session.
- **No silent fallback, ever.** If the chosen authority cannot issue a token — outage, consent
  refused, account not found, expired refresh token — the result is an explicit error and a prompt to
  retry or sign in again. The application must never try the other authority, acquire a token for the
  other API audience, or present a different identity to the API. That would silently change who the
  caller is, and `(tid, oid)` means it would silently change which business they resolve.
- **One active account at a time.** The application resolves a business from one identity; there is no
  account switcher and no multi-account UI in this design.

### 9.3 Refresh, expiry, sign-out and outage

- **Refresh** uses MSAL's silent acquisition against the issuing authority. A silent failure
  surfaces as an interactive sign-in prompt for *that* authority.
- **Expiry** is a `401` from the API, which is already how the SPA learns a session ended.
- **Sign-out** must clear the chosen authority's MSAL cache *and* the application's own caches — the
  account-state and capability caches, and any support-session or role-preview state. A redirect
  sign-out at one authority does not end a session at the other; with two authorities the application
  must not imply it did.
- **Provider outage.** The API depends on each authority's metadata and signing keys. An outage of
  the External ID authority must not affect workforce sign-in, and must not cause the API to accept
  anything: a token that cannot be validated is `401`, and a cached key set that has expired fails
  closed. Validation must never degrade to "skip issuer validation" or "trust without signature
  check" under any failure mode. The health endpoint must not report healthy on the basis of an
  identity provider being reachable, and must not start probing one.

## 10. Verified email: what is authoritative, and the invitation continuation

### 10.1 Email is display-only today and stays display-only

This is the single most important thing to preserve. `EntraActorProfileAccessor` reads
`name`, `preferred_username` and `email` as display data; `ActorIdentity` excludes them from identity
by construction; `MembershipEligibility`, `BusinessMembershipResolutionPolicy`, the invite
redemption, the business application and `ConfiguredPlatformAdmin` all decide without reading an
address. The design's position:

> **No authorization, membership, invite, onboarding or Super Admin decision reads an email address,
> before or after this change.** "Verified email" is a provider-side account-quality control, not an
> application-side identity.

That is why the "authoritative evidence for a verified email" question has a short answer for every
journey: it is whatever External ID verified at sign-up or inherited from the upstream provider
**[TBC-H]**, and the API consumes none of it. If a future feature ever wants to *display* "verified",
it must read a verification claim explicitly — a plain `email`/`preferred_username` value is not
evidence of verification **[TBC-H whether External ID issues a verification claim at all]** — and it
still must not decide anything.

An invite is **not** addressed to an email address today: it is a 256-bit token, stored only as a
SHA-256 hash, scoped `(BusinessId, TokenHash)`, redeemable by whoever presents it, granting the role
the invite recorded. That design is why no email-verification question arises on redemption, and it
must not be changed into email matching: matching by address would make a mailbox a credential and
reintroduce exactly the takeover risk #713 exists to prevent.

### 10.2 Invitation continuation across redirects

Unchanged, and that is the whole requirement. The token is handed to the tab's pending invite by
`inviteTokenAddressBarGuard` during route recognition, stripped from the address bar with
`history.replaceState`, and the navigation is redirected to `/invite` **before `MsalGuard` runs** —
so a signed-out visitor's sign-in redirect starts from, is stored by MSAL as, and returns to
`/invite`.
The token is therefore not in MSAL's redirect start page, not in MSAL's `localStorage`, not in a
history entry, and not in any page address a `Referer` could be built from. It expires from
`sessionStorage` after 15 minutes and is read-and-removed in one step.

Adding a second authority does not change any of that, and the implementation tasks must keep it
true:

- The guard runs before *any* `canActivate` guard, so it is authority-agnostic by construction.
  Adding an authority-selection step must leave it at `canMatch` and must not move the token into a
  route parameter, a query string, a `state` value, an MSAL request, or `localStorage`.
- The 15-minute pending-invite expiry must comfortably outlast the *slowest* new journey. A Google or
  email/password sign-up is a longer round trip than an existing Microsoft sign-in (consent, possibly
  a mailbox verification step), so #711/#712 must verify the window in practice and, if it is too
  short, extend it as its own reviewed decision rather than parking the token somewhere more
  durable.
- The token must never be logged or audited, by either authority's path, and must not appear in any
  error message a failed sign-in produces.
- **Redemption and eligibility rules are untouched**: the same single-use token, the same 7-day
  lifetime, the same "already a member of a business" refusal with its one fixed sentence, the same
  one-active-membership index, the same membership-not-required allowlist of exactly three endpoints.
  Nothing in this design adds a fourth.

## 11. Threat model

The required coverage is issuer confusion, token replay/substitution, malicious account linking,
invite redemption, tenancy, and administrator elevation.

| # | Threat | Scenario | Mitigation in this design | Residual risk / who verifies |
| --- | --- | --- | --- | --- |
| **T1** | **Issuer confusion** | A token signed by any Entra tenant (an attacker's own, created in minutes) is accepted, and its self-asserted `tid`/`oid` are read as identity. Worst case: the attacker mints `tid` = the External ID tenant id and `oid` = a real member's object id, and reads that member's business. | Two separate validation schemes with separate signing keys, and **`tid` bound to the validated issuer in both** (§ 5.1 rules 2–3). The workforce scheme *does* accept the attacker's own tenant's issuer — `common` always has, and preserving workforce sign-in means it must keep doing so — but it binds `tid` to that issuer, so such a token can only ever assert the attacker's own `(tid, oid)`, which resolves no membership and is `403` on every business endpoint. The External ID scheme accepts only the one pinned issuer, so a token the attacker signed can never carry the External ID `tid`. The mitigation is therefore the binding, not the issuer list. | The highest-severity risk of the change. It is wholly a correctness property of #710's validation, and § 5.4's issuer and tenant mismatch rows are its required tests — in particular, a token correctly signed by a *third* Entra tenant but carrying the External ID `tid` must be `401`. |
| **T2** | **Audience substitution** | A token legitimately issued for the SPA, for Microsoft Graph, or for the *other* tenant's API registration is replayed against this API. | Per-scheme audience validation, pinned explicitly, never merged into one pool across the two schemes (§ 5.1 rule 4); `scp` must contain `access_as_user`. | Required tests are § 5.4's audience and scope rows: SPA-audience token → `401`; workforce-audience token presented as an External ID token and the reverse → `401`; valid token with no `access_as_user` → `403`. |
| **T3** | **ID token used as an access token** | The SPA's ID token is sent as the bearer token; it names the right user and is signed by a trusted authority. | Audience and `scp` checks reject it (§ 4, § 5.1 rule 6). No code path accepts an ID token. | Required test, per authority. |
| **T4** | **Token replay / stolen token** | A bearer token is captured and reused before it expires. | HTTPS only; short access-token lifetime; no token in a URL, a log, an audit event or an error message; the invite token likewise never in a URL (§ 10.2). Unchanged by this design. | Residual and accepted, as today: a stolen unexpired bearer token is usable until it expires. Token binding/DPoP is out of scope. |
| **T5** | **Malicious account linking** | An attacker links their Google or email identity to a victim's account — classically by claiming the victim's email address — and inherits the victim's business and role. | **Structurally impossible under Option C: there is no linking flow.** A new identity is a new actor with no membership and no inherited anything; email is never an identity (§ 10.1); no automatic linking by email, ever. | The strongest argument for Option C. If linking is ever revived, #713's full requirements apply and this row must be re-done. |
| **T6** | **Free identity creation widens the membership-free surface** | Self-service sign-up makes a validly issued, trusted-`tid` token available to anyone. The three membership-not-required endpoints then face an unauthenticated-in-practice population, and per-identity rate limits can be reset by signing up again. | The three endpoints are already written for a caller who belongs nowhere: `/api/me/account-state` names no business; `/api/invites/redeem` needs a 256-bit token; `/api/onboarding/business-applications` is behind a flag that is `false` and is rate-limited. Every other endpoint is `403` without a membership. | **New risk introduced by this change.** #714 must test it with an External ID identity that holds no membership. Verified in § 13 item 8: email verification at sign-up, smart lockout and basic HTTP protections are built in and on by default, but **bot and sign-up fraud protection is not built in** (it needs a third-party integration or a WAF) and no built-in per-IP throttling setting exists. The onboarding flag must stay off until a human decides on that protection. Consider per-IP limiting as its own reviewed issue if the data warrants it. |
| **T7** | **Invite redemption abuse** | A guessed or replayed invite token grants a membership; or an invite is redeemed by the wrong person. | 256 bits of randomness, stored only hashed, single-use, 7-day fixed lifetime, `(BusinessId, TokenHash)` scoped, redeemable only into the caller's **own** membership, refused outright when the caller already holds an active membership. All unchanged. | An invite link is a bearer credential by design: whoever holds it may join. Unchanged by this design; stated so it is not mistaken for a regression. |
| **T8** | **Cross-tenant / cross-business leakage** | An identity resolves the wrong business, or a request chooses one. | Business comes only from an active membership, a support session's stored row, or nothing. No business id is ever accepted from request input. Query filters plus `BusinessOwnershipEnforcer`. All unchanged — this design adds no business-resolution path. | #714 re-runs the two-business isolation tests with identities from both issuers. |
| **T9** | **Administrator elevation** | Somebody becomes the Super Admin, or reaches a platform-admin surface. | The Super Admin is one configured `(tid, oid)` pair outside the database, matched as a whole pair; no membership row, email address or role can create it; it stays on the workforce tenant (§ 8). Support sessions and role preview are gated on that same policy and are read-only. | A human with production configuration access can change the pair — that is the intended control. #714 must verify an External ID identity reaches no platform-admin endpoint. |
| **T10** | **Downgrade under provider failure** | An authority outage causes validation to be skipped, keys to be trusted stale, or a fallback to the other authority. | Fail closed: unvalidatable token → `401`; empty `ExternalId` section → scheme not registered; no silent authority fallback in the SPA (§ 9.3). | #710 must test the empty/half-filled configuration path explicitly. |
| **T11** | **Claim-shape mismatch handled by improvisation** | External ID issues an `oid` that is not a GUID, and an implementer loosens `ActorIdentity` or synthesises a GUID so the task can finish. | § 7's explicit prohibitions, and the requirement that a refutation stops the task for re-planning. | This is a process risk, not a code one. The reviewer enforces it. |
| **T12** | **Rollback strands users** | New login is switched off and users created in External ID lose access, or are quietly re-admitted later through a different identity. | § 15 names exactly who loses access and what is retained; their memberships are retained and become reachable again if the configuration is restored. | Accepted and documented, not mitigated: an External ID-only user cannot sign in if External ID sign-in is removed. That is the point of recording it before launch. |

## 12. Linking: not offered, and #713 is not required

**Decision: identity linking is not offered in v1.** Under Option C there is no linking flow, no link
table, no link intent, no unlink, and no email-based recovery path.

- **#713 is deferred and not required under this recommendation.** Its own first conditional
  criterion says so: "do not start if #709 approves coexistence with linking/migration deferred". It
  must be **removed from the launch prerequisites** of #708, and a human records its deferral status
  on the issue. #714 validates **identity separation** instead of linking, as #713 directs.
- **The consequence to state plainly to users:** the same person signing in a different way is a
  different person to this application. A member who normally signs in with their Microsoft work
  account and then signs in with Google gets a *new* identity, with **no membership, no role and no
  inherited access** — they will see the "no membership" welcome screen, not their business. The
  one-active-membership rule is per *identity*, so the second identity is eligible and an Owner
  **can** invite it into the same business: the result is a second, separate member seat with its own
  role, its own audit trail and its own name and address in the members list, not a second way into
  the first seat. Revoking one does not affect the other. The supported advice is: **pick one way to
  sign in and keep using it.**
- **Nothing is irreversible.** Deferring linking does not foreclose it: no schema changes, so if a
  human later approves linking, #713 is rewritten against the design of that day with nothing to
  undo.
- **Required in the product**: the "no membership" welcome screen is the page every user who signs in
  a second way will land on, so #711/#712 must make sure its wording accounts for it. That is a copy
  change inside the existing page, not a new flow.

## 13. Human verification checkpoint (blocking)

The design above is approved by merging. **The dependent tasks are not unblocked by that merge
alone.** Before #710 is given a readiness label, a human must record — on #709, #708, or in a
follow-up commit to this file — the result of verifying each item. **An item that is unsupported, or
that cannot be confirmed, blocks approval. Model recollection is not verification.**

### Results recorded 2026-10-18

Checked against Microsoft's official External ID documentation, read from its published source
(the public `MicrosoftDocs/entra-docs` repository as of 2026-10-17, which learn.microsoft.com is
built from). Paths below are relative to that repository's `docs/external-id/` folder. Two items
are only partly confirmed and one statement was corrected; none blocks #710.

- [x] **External ID is the current Microsoft product for customer identity, and a new tenant can be
      created today.** Confirmed. Azure AD B2C has not been sold to new customers since
      1 May 2025. Evidence: `customers/overview-customers-ciam.md` § About Azure AD B2C;
      `customers/how-to-create-external-tenant-portal.md`.
- [x] **The authority form and the exact v2 issuer.** Confirmed. Issuer
      `https://<tenant-id>.ciamlogin.com/<tenant-id>/v2.0`; endpoints
      `https://<tenant-subdomain>.ciamlogin.com/<tenant-id>/oauth2/v2.0/…`. Validate against the
      issuer the metadata document returns, not a hand-built value. Evidence:
      `customers/migrate-from-cognito-to-external-id.md` (endpoints and claim-mapping table).
- [x] **The claim shape of an access token for all three journeys.** Confirmed, with one
      condition. Every sign-up, local or federated, creates a user object in the External ID
      directory; `oid` is that object's immutable GUID, the same across applications, and `tid`
      is the tenant GUID. `sub` is pairwise and is not equal to `oid`, which confirms § 5.1 rule
      8. **Condition:** `oid` and `tid` are only issued when the `profile` scope is requested
      (§ 4, "The `profile` scope is required"). Evidence:
      `customers/concept-authentication-methods-customers.md` ("A user object is created for them
      in your directory"); `../identity-platform/access-token-claims-reference.md` (`oid`, `tid`,
      `sub`); `customers/enable-external-id-high-scale-compatibility-mode.md`. Decoding one token
      from a test tenant during § 14.2's staging verification remains the final practical check.
- [x] **Which Microsoft account types can be federated in.** Answered, and the original assumption
      was wrong. There is no built-in Microsoft provider. Personal Microsoft accounts are added as
      a custom OIDC provider (`login.live.com`, with its own app registration and client secret);
      work/school accounts only one named Entra tenant at a time. Journey B is therefore not
      offered in v1 (§ 4). Evidence: `customers/how-to-microsoft-accounts-federation-customers.md`;
      `customers/how-to-entra-id-federation-customers.md`.
- [x] **Google federation.** Confirmed as a built-in social provider. It needs a Google OAuth
      client and must be added to the user flow. Evidence:
      `customers/how-to-google-federation-customers.md`.
- [x] **How sign-up/sign-in user flows work.** Confirmed exactly as § 14.2 describes: a provider
      configured in the tenant appears only once added to a user flow, an application gets a
      sign-in experience only once added to a flow, and an application can have only one flow.
      Evidence: `customers/how-to-add-identity-provider-to-user-flow-customers.md`;
      [Add your application to the user flow](https://learn.microsoft.com/en-us/entra/external-id/customers/how-to-user-flow-add-application).
- [x] **Local email/password sign-up verifies the mailbox and offers password reset.** Confirmed:
      a one-time code verifies the email at sign-up, the reset link sends a code to the mailbox, and
      "Email with password" is the default local option. Evidence:
      `customers/concept-authentication-methods-customers.md`.
- [x] **MFA and anti-abuse controls.** Partly built in. MFA by email one-time code (no extra
      charge), SMS (paid add-on, also usable for password reset) and passkeys for local accounts;
      smart lockout and basic HTTP protections are on by default. Bot and sign-up fraud protection
      is **not** built in: it needs a third party (Arkose Labs, HUMAN Security) or a WAF
      (Cloudflare, Akamai, Azure WAF). No built-in per-IP throttling setting was found. Password
      policy configurability was not checked. Evidence:
      `customers/concept-multifactor-authentication-customers.md`;
      `customers/concept-security-customers.md`; `customers/concept-supported-features-customers.md`.
- [x] **Current price and free allowance.** Confirmed: the core offer is free for the first 50,000
      monthly active users; add-ons have no free tier; SMS is billed per transaction by country.
      Per-unit prices are published only on the Azure pricing page. Evidence:
      `customers/faq-customers.md`; `external-identities-pricing.md` § Premium add-ons.
- [x] **An External ID API registration can expose `access_as_user`.** Confirmed ("Expose an API":
      same as workforce). Evidence: `customers/concept-supported-features-customers.md`.
- [x] **Authorization code with PKCE is available and implicit/hybrid can stay disabled.**
      Confirmed: PKCE is supported, and implicit grant is supported but opt-in. Evidence:
      `customers/concept-supported-features-customers.md`.
- [x] **Whether a verification claim for email exists.** Partly. For federated OIDC providers,
      External ID requires the upstream `email_verified` to be true before it creates the account;
      no "verified" claim is documented in the tokens External ID issues to the application. § 10's
      position (no decision reads an email address) is unaffected. Evidence:
      `customers/reference-oidc-claims-mapping-customers.md`.

## 14. Deployment, rollout and the human-only portal work

### 14.1 Incremental coexistence, in phases

Each phase is independently deployable and independently revertible. The workforce path is untouched
in all of them.

1. **Phase 0 — approval.** Merge this document. No code, no configuration, no tenant.
2. **Phase 1 — human verification.** § 13's checklist. No code.
3. **Phase 2 — provider setup (human, outside this repository).** § 14.2, which includes creating the
   sign-up/sign-in user flow, enabling the intended providers inside it, and associating the SPA
   application with it. No code.
4. **Phase 3 — API accepts the second issuer (#710), code complete; its environment check is still
   a human step.** The second scheme is registered **only when
   the `ExternalId` section is filled**, so merging and deploying it with the section empty changes
   nothing observable. Then a non-production environment is configured and a human signs in with a
   test External ID identity, which resolves no membership and is correctly `403` on every business
   endpoint. Validation of that `403`-with-no-membership behaviour *is* the phase's success
   criterion.
5. **Phase 4 — the SPA offers the second authority (#711, #712).** Shipped behind the authority
   selection; with no External ID configuration present the second button is not rendered. Google and
   email/password journeys land on the existing welcome screen.
6. **Phase 5 — an invited user joins through the new authority.** An Owner creates an invite, a
   Google or email/password identity redeems it, and the existing redemption rules decide. No new
   code path; it is the verification that the pieces compose.
7. **Phase 6 — consolidated security, staging and rollback validation (#714).** Including the
   rollback rehearsal in § 15 and the T6/T8/T9 tests above.
8. **Phase 7 — production activation.** Gated on § 14.2's staging journey verification having passed
   for every supported journey. A human fills the production configuration, verifies in
   production, and separately decides whether `SelfServiceOnboarding:Enabled` changes — which has its
   own release gate (`docs/tenant-rollout.md`) and is **not** part of this work.

There is no staged cutover and no decommissioning phase: Option C has no end state in which workforce
sign-in is removed. Removing it later would be its own decision, with its own migration question.

### 14.2 External portal changes a human must perform (no agent may do any of these)

- Create the Microsoft Entra External ID tenant, in the correct subscription and data region.
- Register the **API** application in that tenant and expose an `access_as_user` delegated scope.
- Register the **SPA** application in that tenant as a public client using authorization code with
  PKCE, with exactly the redirect URIs in use (`http://localhost:4200/auth`, the deployed Static Web
  App origin's `/auth`) and the post-logout URIs, and grant it delegated access to the API scope.
- Configure the Google identity provider **in the tenant** (journey B, Microsoft through External ID,
  is not offered in v1; see § 13 item 4), including creating the
  Google OAuth client in Google Cloud and registering its client id and secret **in the External ID
  tenant** — that secret lives in the provider, never in this repository, a pipeline, or an
  application setting.
- **Create the sign-up/sign-in user flow.** An External ID tenant with registered applications and
  configured identity providers still has no sign-in experience until a user flow exists; this step
  is not optional and is not implied by any step above **[TBC-H]**.
- **Enable the intended identity providers inside that user flow.** Enabling a provider at tenant
  level is not the same as offering it in a flow: Google and email + password must each be selected
  in the flow itself, or the journey simply does not appear on the sign-in page (§ 13 item 6).
  Microsoft federation (journey B) is not enabled in v1; adding it needs a separate future approval
  (§ 13 item 4).
- **Associate the SPA application with that user flow.** A registered application that is not
  associated with a user flow has no journey to send users to, and sign-in fails even though every
  registration, provider and configuration value is correct. See Microsoft's
  [Add your application to the user flow](https://learn.microsoft.com/en-us/entra/external-id/customers/how-to-user-flow-add-application)
  **[TBC-H]**. If the API application also needs an association, record that at the same time.
- Configure, inside that flow, the local email/password sign-up journey, its email verification, and
  the password reset and recovery experience.
- Configure MFA and anti-abuse controls to whatever § 13 found is available.
- Set the production application settings `ExternalId__Instance`, `ExternalId__TenantId`,
  `ExternalId__ClientId` (identifiers, not secrets — no Key Vault reference needed) and the SPA's
  build-time/configuration equivalents.
- Decide and record who administers the External ID tenant, and keep that separate from the
  `PlatformAdmin` pair (§ 8).

**Staging verification of the user flow association, before production activation.** Configuring a
provider is not the same as a user being able to use it, so the setup above is not finished until a
human has confirmed **in staging** that the associated application actually exposes and completes
**every** supported journey. The supported journeys are Google (journey C) and local email +
password (journey D); Microsoft federation (journey B) is excluded from v1 and needs a separate
future approval (§ 13 item 4). For each supported journey: the journey is offered on the sign-in page the SPA reaches
through the associated user flow; a new user can complete it end to end, including the email
verification and the password-reset entry point for D; the SPA returns to `/auth` and acquires an API
access token for `access_as_user`; and the API accepts that token and resolves the expected
`(tid, oid)` with no membership — `403` on every business endpoint, as § 14.1 phase 3 requires. A
journey that is configured but not offered, or offered but not completable, is a failed phase, not a
detail to finish in production. The result is recorded with § 13's checklist, and #714 owns the
consolidated run across both issuers (§ 16).

### 14.3 Secrets handling

No new secret enters this repository, a pull request, an issue, a log or a build artifact. The SPA
holds no client secret (PKCE public client); the API holds none (public signing keys). The Google
OAuth client secret exists only inside the External ID tenant's provider configuration. The identity
tenant id, client ids and authority host are identifiers and are ordinary application settings — the
same classification `PlatformAdminOptions` already records for an Entra object id. Source control
keeps the `ExternalId` section present and **empty**, exactly as `PlatformAdmin`,
`BusinessBootstrap` and `NayaxTokenProtection` are kept empty, so the setting is discoverable and the
shipped state is fail-closed.

## 15. Rollback

Rollback has two distinct levers, and conflating them is the mistake to avoid.

### 15.1 Lever 1 — disable the new login (configuration, minutes, no deployment)

Clear the production `ExternalId__*` application settings (and the SPA's equivalent). The second
bearer scheme is not registered, the second authority button is not rendered, and the API rejects
every External ID token as an untrusted issuer.

**Observable outcomes:**

| Who | Outcome |
| --- | --- |
| Existing workforce Microsoft users | **No change whatsoever.** They never had an External ID identity, their `(tid, oid)` never changed, and their authority is untouched. This is the guarantee Option C exists for. |
| Users created through External ID (Google, email/password) | **Lose access — they cannot sign in at all.** There is no fallback identity to offer them, because there is no linking. |
| Those users' memberships | **Retained in the database, untouched.** Role, business, activation and audit history all remain, and become usable again the moment the configuration is restored. Nothing is deleted or deactivated by rollback, and nothing should be: deleting them would make the rollback the irreversible step. |
| Outstanding invites | **Retained and unaffected**, including those created by or for External ID identities: an invite is a token and a role, not an identity. Their 7-day lifetime keeps running, so one may expire during a long rollback — an Owner re-issues it. |
| Provider configuration | **Retained.** The External ID tenant, its registrations, its users and its federations are not torn down; only this application stops trusting the issuer. Restoring is re-entering the three settings. |
| Super Admin | **No change** — the pair is a workforce identity and never depended on External ID. |
| Support sessions, role previews | No change; they are gated on that same pair. |
| Business data | **No change.** No schema change and no data migration is part of this design, so there is nothing to revert. |

### 15.2 Lever 2 — revert the code

Reverting #710/#711/#712 removes the second scheme and the SPA's second authority from the build.
Because **this design changes no schema and migrates no data**, a code revert is an ordinary
deployment of an earlier commit with no data step, no backfill and no down-migration. The outcomes
for people are identical to § 15.1.

### 15.3 What rollback is *not*

- It is **not** a way to recover an External ID user's access. If such a user must keep working, the
  answer is an invite redeemed by a workforce Microsoft identity they control — a new, separate
  membership — not a rollback.
- It does **not** need to touch the identity provider. Deleting the tenant would destroy users and is
  not part of any rollback described here.
- It must be **rehearsed in #714** before production activation, including restoring the
  configuration and confirming an External ID member's retained membership and role come back
  unchanged.

## 16. Phased issue impacts

| Issue | Status under this recommendation | Scope it should carry |
| --- | --- | --- |
| **#708** (parent) | Proceed; **remove #713 from the launch prerequisites** | Record Option C as the approved rollout and this file as the design of record |
| **#709** (this) | Complete on merge; § 13's checklist is a separate human step before #710 | Design, decision table, threat model, rollout, rollback. No code |
| **#710** (identity foundation and API token validation) | **Implemented** (see the implementation note below this table) | The `ExternalId` configuration section; the second named bearer scheme and the issuer-selecting policy scheme, with the existing multi-tenant workforce validator and its issuer-to-`tid` binding left exactly as it is; per-scheme issuer, audience and tenant-binding rules (§ 5.1), with the External ID issuer taken from the tenant's metadata document; a test that an External ID token without `oid` or `tid` (no `profile` scope) is unidentifiable; empty-configuration fail-closed; **the complete § 5.4 regression suite, including two different workforce tenants and every issuer/tenant/audience mismatch**, which covers T1/T2/T3/T10; the composition tests extended so the `E2ETest` host still registers the synthetic scheme and nothing else. **No change to `ActorIdentity` or `EntraActorIdentityAccessor`**, and no schema change |
| **#711** (Google sign-in) | After #710 | SPA authority selection, the second MSAL instance with its own cache, the `profile` scope in the External ID token request (§ 4), Google journey end to end, the welcome-screen wording of § 12, invite continuation re-verified |
| **#712** (email/password) | After #710 | The same for the local journey, including the provider-hosted reset/recovery entry point and the `profile` scope (§ 4). Mailbox verification is enforced by the provider's sign-up flow (§ 13 item 7). **No password, hash, reset token or recovery code in this repository** |
| **#713** (linking/migration) | **Deferred — not required.** A human records that status; it is removed from the launch prerequisites and must not be given a readiness label unless a human first reopens the linking decision | — |
| **#714** (security, staging, rollout validation) | Last | The provider/account-state matrix across both issuers; **identity separation in place of linking validation**, as #713 directs; T6, T8, T9; two-business isolation with identities from both issuers; **§ 14.2's staging verification that the associated application exposes and completes each supported journey**; the § 15 rollback rehearsal; human staging sign-off |

### How #710 implemented § 5, and where it chose differently

The shape is § 5.2's recommendation: two named schemes behind a policy scheme that reads the
unvalidated `iss` only to choose a validator, with the workforce scheme left on `TenantId: common`
and its Entra issuer-to-`tid` binding untouched. `ActorIdentity` and `EntraActorIdentityAccessor`
are unchanged, there is no schema change, and the complete § 5.4 table is covered by
`backend/Inventory.IntegrationTests/Auth/TrustedIssuerTokenValidationTests` (real tokens, test
signing keys, stubbed metadata documents) and `ExternalIdBearerSchemeCompositionTests`. Four
decisions are worth recording because they are not literally what § 5.2 and § 5.4 say:

- **The pinned scheme is a plain `AddJwtBearer`, not a second `AddMicrosoftIdentityWebApi`.** The
  identity library's Entra issuer validation derives the expected issuer from the *token's own*
  `tid`, which is exactly right for a multi-tenant workforce scheme and the opposite of rule 2's
  "one literal value, taken from the tenant's metadata document". The pinned scheme therefore
  leaves `ValidIssuer`/`ValidIssuers` unset so the bearer handler accepts exactly the issuer the
  configured tenant's metadata publishes, and asserts `tid` against the configured tenant
  explicitly (rule 3). It is still a supported validation library and the same `JwtBearerOptions`;
  what changed is which component decides the issuer.
- **Rule 6 is enforced for both schemes, as § 5.4's "for each authority" requires.** A token that
  carries no delegated scope at all, or that carries the `nonce`/`at_hash`/`c_hash` claims of an ID
  token, is refused at authentication with `401` - so an ID token and an app-only token are `401`
  on the workforce authority too, where they would previously have been authenticated and then
  refused `403` by `[RequiredScope]`. That is the one behaviour change to the workforce path, it
  can only refuse more, and the `401`/`403` separation is unchanged for a delegated access token
  that merely lacks `access_as_user`, which is still `403`.
- **An unreadable metadata document is `401`, not a failed request.** The bearer handler lets a
  metadata or key-refresh failure escape, which surfaces as a server error rather than as § 9.3's
  "a token that cannot be validated is `401`". `FailClosedIdentityMetadata` wraps the pinned
  scheme's metadata source and answers with a document naming no issuer and publishing no key, so
  validation fails the ordinary way and the real cause is logged as an error.
- **Rule 4's access-token version stays a human portal decision.** Both audience spellings
  (`<client-id>` and `api://<client-id>`) are pinned per scheme, so whichever version a
  registration is set to issue validates, and the workforce scheme's accepted set is the same two
  values the library derived by default - now stated rather than inferred.

Everything this task was not allowed to do is still undone: no provider resource was created, no
configuration value was filled in, no SPA journey was added, nothing was linked or migrated, and
the staging verification of § 14.2 and the phase-3 sign-in check of § 14.1 remain human steps
against a human-provisioned tenant.

## 17. Known limitations of this document

- Every **[TBC-H]** item is unverified, including the load-bearing claim-shape assumption of § 7.
  This document is a design conditional on that verification, not a statement of provider fact.
- It specifies no DTO, no class and no test method. The implementation tasks own those, and § 5.2's
  recommendation between two registration shapes is a recommendation #710 must confirm with tests.
- It deliberately leaves two things to their own reviewed decisions if evidence calls for them:
  per-IP or non-identity-keyed rate limiting (T6), and extending the pending-invite window (§ 10.2).
- It does not describe how to *remove* workforce sign-in. Option C has no such end state, and a later
  decision to consolidate would reopen the migration question this document closes by avoiding it.
