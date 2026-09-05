# 0001 — A demo admin with no password

**Status:** Accepted · 2026-09-04 · Phase 7

## Context

The admin console needs to be reachable by a stranger who has just opened the repository's demo
link. Every ordinary answer to "how does an admin sign in" fails that requirement in a different
way. Real accounts need a registration flow, an email sender and a password reset — all of which
would be the *only* authentication in the project, since the shop itself deliberately has no
accounts. A shared password published in the README is a password in a public repository, which is
worse than none because it looks like security. A password *not* published means the console is
functionally invisible: the point of building it is that someone can click it.

So the honest framing is not "how do we authenticate the admin" but "what is the credential
actually protecting, and from whom".

## Decision

`/admin` shows a button that mints a cookie. No field, no password, no account.

The cookie is an ASP.NET Core authentication ticket sealed with Data Protection, carrying one
claim: the demo session id of whoever pressed the button. A policy handler compares that claim to
the session id on the current request and fails when they differ, so a ticket copied into another
browser authenticates fine and authorises nothing.

The sign-in endpoint takes **no input at all**. It cannot be given a session id to be an admin of,
because a sign-in that accepted one would be an impersonation endpoint with a friendly name.

- [`DemoAdminAuthentication.cs`](../../src/VelaCommerce.Api/Admin/DemoAdminAuthentication.cs) —
  the scheme, the claim, and `BoundToTheCallersSessionHandler`.
- [`AdminConsoleTests.An_admin_cookie_from_one_session_is_inert_in_another`](../../tests/VelaCommerce.Integration.Tests/AdminConsoleTests.cs)
  — drives the lift with a genuine ticket *and* a matching antiforgery pair, so a 400 would fail
  the test: the binding has to be what refuses it, not an unrelated defence.

## Consequences

The credential gates the **feature**; the model gates the **data**. Those are separate mechanisms
and they fail independently, which is the whole argument. Every query behind the console runs
through the same `DemoTenancy` filter the shop uses — a filter that fails closed, matching no rows
when no session is bound — so deleting the policy tomorrow would lose the front door and change
nothing about what a caller can reach.

That claim is asserted on its own rather than inferred from the first:
`An_admin_cookie_from_one_session_is_inert_in_another` checks the refusal **and** that nothing was
written under either session, and `CatalogOverrideTests` proves the isolation with no admin
involved at all.

What this costs: nothing here demonstrates password hashing, lockout, MFA or session fixation
defence, and the README should not imply otherwise. What it buys is that the interesting property —
a multi-tenant write path that cannot cross tenants — is demonstrated rather than described, by a
console anyone can open in one click.

**This is not a pattern for a real admin.** A real one has accounts, roles and an audit trail. The
transferable part is the shape: authentication that says *who*, authorisation that says *whether*,
and a data model that would still contain the blast radius if both were removed.
