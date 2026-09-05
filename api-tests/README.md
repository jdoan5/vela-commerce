# Vela Commerce API collection

An executable description of the HTTP surface: browse the catalog, drive a cart through its whole
life, buy something, refund it, cancel an order, watch the settlement receiver refuse three
different forgeries, run a Demo Lab scenario, and check both health probes. All eighteen of the
API's JSON operations are covered; the admin console's six are HTML form posts, covered by the
integration suite instead. It runs in the [Bruno](https://usebruno.com)
desktop app for exploring, and headless in CI as a smoke test.

## Why Bruno and not Postman

**A Bruno collection is plain text in the repository.** Every request in this folder is a
`.bru` file you can read, diff and review in a pull request, sitting in the same commit as
the endpoint it exercises. That is the whole reason for the choice:

- **A change to the API and the change to its collection arrive together.** Adding a field
  and updating the request that asserts it is one diff, reviewed by one person, merged
  atomically. Postman's collections live in a workspace behind an account, so the API moves
  in git and the collection moves somewhere else, and nothing forces them to move together.
- **The requests are reviewable.** `git diff` shows that an assertion was relaxed from
  `eq 200` to `isDefined`. An exported Postman JSON blob shows a reordered 4,000-line file.
- **Cloning is the whole setup.** No account, no sign-in, no shared workspace to be invited
  to, nothing to export and re-import when it drifts. `bru run` and the runner is the same
  runner CI uses.
- **It has no server in it.** Nothing here phones home, so a collection full of a demo's
  endpoints cannot end up in someone else's cloud.

## Running it

The API must be up, seeded, and in **Development** — the environment matters twice. Only in
Development does `Program.cs` migrate and seed, so there is a catalog to browse; and only in
Development is the session cookie issued without `Secure`, so it survives plain `http://`.
Against a Production host on http, every cart request would silently be a different visitor.

```bash
# Terminal 1 - the API on the port environments/local.bru points at.
dotnet run --project src/VelaCommerce.Api

# Terminal 2
npm install --global @usebruno/cli
cd api-tests
bru run -r --env local
```

Run one folder on its own with `bru run Catalog -r --env local`. The **Cart folder
is self-contained** — it finds its own product and variant before it starts — but individual
files inside a folder are not, because they depend on ids captured by the requests before
them. A request that needs a missing variable fails with a sentence saying so rather than
with a confusing 404.

Nothing in here hard-codes a product id, slug or price. Ids are generated when the seed is
applied, so a fixture would pass on the machine that wrote it and 404 in CI. Every id used
below is discovered from the catalog during the run.

## The session cookie, and what was actually measured

The demo has no login. Identity is a signed, HttpOnly `vela.session` cookie, and it is the
only thing that decides whose cart a request touches — so a collection that lost it between
requests would test five unrelated empty carts and pass.

**Bruno's cookie jar carries it, verified rather than assumed.** These are measurements
against a running API with Bruno CLI 4.1.0, not claims from the documentation:

| Question | Answer |
| --- | --- |
| Does the CLI carry `Set-Cookie` between requests in a run? | **Yes.** It is on by default; `--disable-cookies` turns it off. |
| Does the jar survive between separate `bru run` invocations? | **No.** It is in memory, per run. Every run is a brand-new visitor with an empty cart, and nothing is written to disk. |
| Can a request override the jar with its own `Cookie:` header? | **No.** Once the jar holds a cookie for the origin, it wins and the header is ignored. |

The collection therefore needs no cookie variable and no wiring; the jar is enough. But it
does not take that on trust — **`Cart/04-get-empty-cart` asserts the cookie carried**, and
it does so more sharply than "the cart persisted":

```
expect(res.getHeader("set-cookie")).to.be.undefined;
```

The middleware issues `Set-Cookie` only when a request arrives *without* a readable session.
By that point in the run the catalog requests have already been answered, so a session
exists. If Bruno had dropped it, the server would mint a fresh one right there. Asserting on
its absence catches the failure at the moment it happens, instead of six requests later as a
mysteriously empty cart that looks like a cart bug.

### The test that is deliberately not here

The obvious next test is the security property this phase exists to establish: *a forged
cookie must see an empty cart, not somebody else's*. It is not in this collection, and that
is a finding rather than an omission.

It cannot be written here honestly. Sending `Cookie: vela.session=forged` alongside a jar
that already holds a real session does not produce a forged request — **the jar's cookie
wins**. Measured directly: with a cart holding 7 items, a request carrying an explicit
forged cookie header came back with those same 7 items and no `Set-Cookie`, proving the
server had received the *real* cookie and never saw the forged one.

A test written that way would pass, and it would be proving nothing. Worse, it would look
like coverage of the one property most worth covering. Tenancy isolation is verified where
the client cannot get in the way — in `tests/VelaCommerce.Integration.Tests`, against a real
PostgreSQL, where a second session is a second `DbContext` rather than a second cookie.

## What each folder is for

| Folder | Covers |
| --- | --- |
| **Catalog** | The three read endpoints. Paging clamps instead of rejecting, price sorts run on minor units and not on formatted strings, an unknown slug is a problem document, and the category facet counts must sum to the catalog total — two different queries over the same soft-delete predicate, which diverge if either stops filtering. |
| **Cart** | The lifecycle in order: discover a variant, start empty, read empty, add, merge a repeat add, set an absolute quantity, re-read, then the four refusals (over the 99 cap, zero quantity, unknown variant, missing line), remove, remove again idempotently, refill, clear. |
| **Checkout** | The money path. Fill a cart, place the order, replay the same idempotency key and get the same order back with a `200` instead of a second `201`, send a header and body key that disagree and get a `400`, reopen the order through its signed link — then a refused card, and the cart still holding what the shopper was trying to buy. |
| **Refunds** | Buys its own order so it runs alone. A partial refund moves the money and writes exactly one ledger row; the same key replayed returns the first refund rather than issuing a second; more than is left is refused rather than clamped; the remainder is then taken. Finally a cancellation, which refunds the whole balance and puts both units back on the shelf as one act. |
| **Webhooks** | The only endpoint an attacker can reach, approached as one. Three refusals told apart: a current signature whose MAC is wrong is `401` with a challenge, one outside the replay window is `400` because the timestamp is checked first, and an unparseable header is `400` because there was nothing to check. No request carries a real signature, which is the point. |
| **Demo Lab** | The lab's own contract — every scenario must name the test file that proves the same thing in CI, and must publish what a button press will create before you press it. Then one run of `oversell`, asserting every individual check rather than the roll-up, and that it touched no row belonging to anybody else. Ends with the demo reset. |
| **Health** | Both probes, kept apart. Liveness must not touch the database — wiring a container's liveness probe to the readiness one is how a sleeping database gets the container restart-looped instead of merely reported unhealthy. |

Assertions are split by what they are good at: the `assert` block for status codes and flat
field values, where a table of `res.status: eq 200` reads better than JavaScript; the `tests`
block for anything relational — that the subtotal equals the catalog's unit price times the
quantity, that facet counts add up, that a PATCH of 5 over a quantity of 3 yields 5 and not 8.

Several requests assert on the *shape of failure* as much as the shape of success: that a
broken domain rule is a `400` problem document rather than a `500`, that its detail names the
cap (`99`) because the domain's own wording is passed through, and that a `404` on a missing
line points at the verb that would have worked. Those are the responses a client actually has
to handle, and they are the ones that rot quietly.

## In CI

`.github/workflows/ci.yml` runs this collection against a real API and a real PostgreSQL on
every push to `main` and every pull request against `main` — a push to a feature branch with no
open PR runs nothing. See the `api-collection` job for how the API is started and why a service
container is used instead of Testcontainers.

The `baseUrl` in `environments/local.bru` is `http://localhost:5008`, matching
`src/VelaCommerce.Api/Properties/launchSettings.json`. CI starts the API on that same port so
there is exactly one URL in the repository and no second copy to drift.
