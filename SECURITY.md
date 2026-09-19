# Security policy

## What this is

Vela Commerce is a **portfolio demonstration**, not a product. It is one public deployment with no
users, no accounts and no real money:

- **Payments are simulated.** The gateway is an in-repo simulator that signs its own webhooks. No
  card is ever asked for, no payment processor is involved, and no card data exists to leak. See
  [ADR 0008](docs/adr/0008-a-payment-simulator-that-signs.md).
- **There is no personal data.** The only thing a visitor supplies is a shipping address they made
  up, on an order that ships nothing. There are no accounts and no passwords — the admin console is
  a button, bound to the visitor's own demo session, and [ADR 0001](docs/adr/0001-a-demo-admin-with-no-password.md)
  argues why.
- **Visitor data is deleted after 24 hours** by a background purge, and each visitor sees only their
  own rows — a query filter that fails closed, so a request with no session sees nothing rather than
  everything ([ADR 0007](docs/adr/0007-the-tenancy-filter-fails-closed.md)).

There are no released versions and no version support matrix. There is `main`, and whatever `main`
is, is what is deployed.

## Reporting a vulnerability

Please open a **[private security advisory](https://github.com/jdoan5/vela-commerce/security/advisories/new)**
rather than a public issue.

This is a personal project maintained in spare time, so the honest expectation is:

- **Acknowledgement within about a week.**
- **No bounty**, and no guaranteed remediation timeline.
- If it is real and cheap to fix, it will be fixed and the reasoning written down where the fix
  lives — that is the convention throughout this repository.
- If it is real and not worth fixing for a demo with no data, it will be recorded as a known
  limitation instead of quietly closed.

## What is already known and deliberate

Reporting these is welcome but they are not news, and all three are argued in the repository rather
than overlooked:

| | |
|---|---|
| **The demo is publicly writable.** Anyone can place orders, refund them and use the admin console. | Bounded by per-session tenancy, rate limiting, per-session row caps, HTML sanitisation, a strict CSP, no uploads and no outbound email. |
| **No uptime monitoring or alerting exists.** | Recorded in [ADR 0009](docs/adr/0009-no-log-analytics-workspace.md) and in [`docs/MAINTENANCE.md`](docs/MAINTENANCE.md), which says plainly that nothing watches this demo but a monthly human pass. |
| **No database backups.** | A deliberate decision, not an omission — [ADR 0011](docs/adr/0011-no-database-backup.md). The database holds a committed schema, a catalog generated from a committed seed, and rows the purge deletes anyway. |

## What runs on every push

Not a claim about being secure — a list of what is actually wired, so a reporter knows what has
already been looked at:

- **CodeQL** over the C# *and* the workflow files.
- **Dependabot** on NuGet packages and GitHub Actions, with every action pinned by full commit SHA.
- **414 tests**, including integration tests against a real PostgreSQL, plus architecture rules read
  from compiled IL. Coverage and mutation scores are enforced as build failures, not badges.
- Workflows run with `contents: read`, and deploys authenticate by **OIDC** — the pipeline stores no
  cloud credentials at all.
