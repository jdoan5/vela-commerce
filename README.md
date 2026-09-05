# Vela Commerce

A .NET 10 storefront built around the parts a tutorial e-commerce clone skips: the last unit sold
to exactly one of fifty simultaneous shoppers, a double-clicked checkout that makes one order, and
a payment webhook that can arrive twice without charging twice — each enforced by a PostgreSQL
constraint rather than by C# that merely happens to run first.

[![CI](https://github.com/jdoan5/vela-commerce/actions/workflows/ci.yml/badge.svg)](https://github.com/jdoan5/vela-commerce/actions/workflows/ci.yml)
[![CodeQL](https://github.com/jdoan5/vela-commerce/actions/workflows/codeql.yml/badge.svg)](https://github.com/jdoan5/vela-commerce/actions/workflows/codeql.yml)

![Browsing the catalog, searching, opening a product, adding to the cart, checking out, and the order timeline advancing from Placed to Shipped](docs/demo.gif)

> The clip is sampled, not real time: the run took about ninety seconds and plays in nine. **It is a recording — nothing is deployed yet.** You do not have to take a recording's word for it, though: `docker compose up` runs the real thing in one command. See [Status](#status) below.

Recorded by driving the running shop, not assembled from mock-ups: catalog, search, a product
page, add to cart, the cart drawer, checkout with a sample address, then order `VELA-PPTNK4G`
moving **Placed → Paid → Packed → Shipped**. The timeline is not an animation. It is
[`OrderTimelineWorker`](src/VelaCommerce.Infrastructure/Fulfilment/OrderTimelineWorker.cs), a
background service moving real rows in real PostgreSQL on a demo clock — 20 seconds to Packed,
40 more to Shipped — and the page stops polling by itself once the order reaches a terminal state,
which is the "no longer checking" the last frames show.

## Status

Phases 1–4 and 6 of 10 are done, Phase 0 all but its deployment, and Phase 7's admin
half — domain, seeded catalog, storefront, cart, tenancy, checkout, payments, transactional outbox,
the order timeline, the Demo Lab, refunds and a session-scoped admin console — on **398 passing
tests** (202 domain, 9 architecture, 187 integration against a real PostgreSQL 18 in
Testcontainers), at **68.1% line coverage** over the three production assemblies — a floor CI
enforces rather than a badge it decorates, and [`coverage.runsettings`](coverage.runsettings) says
what is counted and why. The domain also carries a **71.6% mutation score** — Stryker's first run
found that `checked` could be deleted from every `Money` operator with every test still green, which
is [written up](docs/measurements/mutation-testing.md) along with the ten other edges it found. Phases 5, 8 and 9 are not started, and Phase 7's other half is not either: nightly
reset and backups, preview environments, the measured cold start. **There is no hosted demo, deliberately.** The Azure free-trial credit expired
2026-09-04 and was allowed to lapse. The subscription still carries its spending limit, so it is
disabled rather than billing; deploying now would mean upgrading to Pay-As-You-Go, which removes
that limit permanently and cannot re-enable it. The decision was to finish the app first and deploy
once, on purpose, rather than half-deploy onto a subscription that can start charging — and nothing
is burned by waiting, since the Terraform, the resource names and the OIDC subject all stay valid. The GIF above is what a
clone of this repo does today, after `dotnet build` and `dotnet run --project src/VelaCommerce.Api`
against a local PostgreSQL. Both steps matter: the API project does not reference the storefront, so
the solution build is what puts the shop's files where the host serves them from.

---

## Read the interesting parts

If you have ten minutes, read these seven files. Each opens with a comment block stating the rule it
enforces and the plausible implementation that gets it wrong, so you can judge the reasoning
without reading the bodies.

- **[`src/VelaCommerce.Api/Endpoints/CheckoutEndpoints.cs`](src/VelaCommerce.Api/Endpoints/CheckoutEndpoints.cs)**
  — the header comment is the one to read first. Why stock is taken by a conditional `UPDATE`
  whose row count picks the winner and never by loading a `StockItem`; why the double-submit fix
  is to let *both* inserts race a unique index instead of `SELECT`ing first; and why the gateway
  call sits *between* two transactions rather than inside one.
- **[`src/VelaCommerce.Api/Endpoints/WebhookEndpoints.cs`](src/VelaCommerce.Api/Endpoints/WebhookEndpoints.cs)**
  — the only endpoint whose caller is authenticated by something other than a cookie. No endpoint
  here needs a pre-existing session, since the middleware mints one for anybody; this is the one
  where that would prove nothing, because the caller is a payment gateway. Four numbered rules:
  verify the HMAC over the
  bytes that arrived (never a re-serialization), make exactly-once the database's job, refuse
  out-of-order arrivals by construction, and keep everything before verification off PostgreSQL.
- **[`src/VelaCommerce.Api/Endpoints/RefundEndpoints.cs`](src/VelaCommerce.Api/Endpoints/RefundEndpoints.cs)**
  — giving money back, in the one order that survives a gateway saying no: ask first, record
  second. Read the note on why the order row is locked *across* the gateway call — normally the
  wrong shape, and the lesser evil here. The companion argument, for why the
  `refunded <= captured` CHECK constraint cannot catch a concurrent over-refund on its own, is in
  [`DemoLabEndpoints.cs`](src/VelaCommerce.Api/Endpoints/DemoLabEndpoints.cs) beside the scenario
  that demonstrates it: every racing handler writes the same absolute figure, so the column ends up
  correct above a ledger with twelve rows in it. The lock is the only thing load-bearing.
- **[`src/VelaCommerce.Domain/Orders/OrderStateMachine.cs`](src/VelaCommerce.Domain/Orders/OrderStateMachine.cs)**
  — 34 lines, five legal edges, written as a table so a test can assert the whole set. Look for
  the absent self-transitions: `Paid -> Paid` is missing on purpose, which is what turns a
  replayed webhook into a recognised duplicate instead of a second capture.
- **[`src/VelaCommerce.Storefront/Catalog/CatalogService.cs`](src/VelaCommerce.Storefront/Catalog/CatalogService.cs)**
  — the whole shop held in memory. Note what it does *not* have: no `HttpClient` pointed at the
  API, and no method that could grow one. That absence is the architecture.
- **[`src/VelaCommerce.Infrastructure/Persistence/VelaCommerceDbContext.cs`](src/VelaCommerce.Infrastructure/Persistence/VelaCommerceDbContext.cs)**
  — tenancy as a property of the model, not of the query somebody remembered to write. The
  session arrives as an *accessor*, never a value, and a missing accessor means no session, which
  matches no rows. It fails closed.
- **[`tests/VelaCommerce.Integration.Tests/CheckoutStockRaceTests.cs`](tests/VelaCommerce.Integration.Tests/CheckoutStockRaceTests.cs)**
  — fifty shoppers, five units, one instant. Every number asserted exactly: five orders, forty-five
  409s, zero 500s, and a ledger finishing at `reserved = 5` against `on_hand = 5`. "At most five"
  would pass for a shop that sold nothing.

One more, if the admin is what caught your eye:
**[`EffectiveCatalogPrices`](src/VelaCommerce.Infrastructure/Persistence/CatalogOverrides/EffectiveCatalogPrices.cs)**
— the one expression that decides whether a visitor pays the shared price or their own. Eight call
sites go through it and none can bypass it: the overlay entity is `internal`, so outside
Infrastructure the compiler refuses, and inside it
[`CatalogOverlayRules`](tests/VelaCommerce.Architecture.Tests/CatalogOverlayRules.cs) walks the IL
and admits four names. Worth reading for why it is enforced twice — the comment there was an
unenforced claim for exactly one day before the admin console's own page reader falsified it. The
reasoning behind the overlay, the passwordless sign-in and the two render modes is in
[`docs/adr/`](docs/adr/) — along with why the gateway call sits *outside* checkout's transaction and
*inside* a refund's, why every race here is decided by the database rather than by a `SELECT` that
ran first, and which of those records' own justifications turned out to rest on something that was
never built.

Two more worth the detour:
[`OutboxDispatcher`](src/VelaCommerce.Infrastructure/Messaging/OutboxDispatcher.cs) claims each
message with `SELECT … FOR UPDATE SKIP LOCKED` and transmits the stored bytes unchanged, because
re-serializing a signed payload invalidates its own signature; and
[`ClockRules`](tests/VelaCommerce.Architecture.Tests/ClockRules.cs) walks the compiled IL for reads
of `DateTimeOffset.UtcNow` and friends, with an exemption list that is deliberately empty.

## The rule that shapes everything

> **Nothing on the first-paint path may query the database or call `/api`.**

The API and the database are meant to scale to zero, which is what keeps the eventual hosting free
and what makes a cold start otherwise unavoidable. So the first paint is moved off the database
entirely: browsing, search, filtering, sorting and paging all run client-side, in the browser,
against a static catalog snapshot fetched once from the app's own origin.

**Where that stops, as built.** The API container still serves the shell, the WebAssembly runtime
and the snapshot, so the first request after an idle window pays that container's cold start before
any of the above applies. Putting those files on a CDN with `/api/*` rewritten to the container is
what would finish the argument, and it is in [the plan](docs/PLAN.md) and not in this repository —
there is no CDN, no static host and no `vercel.json` here, and `infra/` creates none. What the split
buys today is real but narrower than "no cold start": once the snapshot is in the browser, every
browse, search and filter is answered from memory, and the database is never woken to render a page.

The measured shape of that snapshot, from `src/VelaCommerce.Storefront/wwwroot/catalog.snapshot.json`:

| | |
|---|---|
| Contents | 288 products, 691 variants, 8 categories, no stock |
| Size | 301,727 bytes on disk, **42,876 gzipped** (`gzip -9`; a CDN at its default level serves ~43,600) |
| Requests to `/api` on a fresh page load | **zero** |
| Generated by | a fixed seed (`20260214`), no clock, no `Guid.NewGuid` — byte-identical between runs, and CI regenerates **both this snapshot and the seed** into a temp directory and fails on any difference |

Stock, cart and checkout genuinely need the API. They are reached lazily, only once a shopper
commits to something, and the UI says "waking the shop up" honestly rather than hiding a cold start
behind a spinner that looks like a slow page. The split is visible in the code: the storefront's
[`CartApiClient`](src/VelaCommerce.Storefront/Cart/CartApiClient.cs) and
[`CheckoutApiClient`](src/VelaCommerce.Storefront/Checkout/CheckoutApiClient.cs) both document that
they are never reached on a first paint, and `CatalogService` never calls the API — it fetches one static file and nothing else.

## The invariants, and where they are actually enforced

Business rules live in the domain, but the two that cost real money are enforced by PostgreSQL as
well, because a rule that only exists in C# is a rule two concurrent processes can both walk past.

**Stock cannot be over-reserved.** `StockItem.TryReserve` states the rule and returns `false`
rather than throwing — but it is an in-memory check on an in-memory copy, so two shoppers holding
two valid instances for the last unit both pass it. Checkout therefore never loads a `StockItem`.
It issues one statement and reads the row count:

```sql
UPDATE stock_items
SET    reserved = reserved + $q
WHERE  variant_id = $v
  AND  deleted_at IS NULL
  AND  on_hand - reserved >= $q
```

One row means this shopper won; zero means they lost, and lost arrives as a 409 that names the SKU,
not a 500. The database carries `CHECK (reserved <= on_hand)` as the backstop if anyone ever writes
the racy version anyway:

```
ERROR:  new row for relation "stock_items" violates check constraint
        "ck_stock_items_reserved_within_on_hand"
```

**A double-submitted checkout creates one order.** Not by `SELECT`ing for the key first — two
simultaneous submits both find nothing and both insert, which is the race, not the fix. Both are
allowed to insert and a unique index on `(demo_session_id, idempotency_key)` picks the winner; the
loser catches the violation, rolls back (which releases its own reservations with it) and returns
the winner's order with a 200.

```
ERROR:  duplicate key value violates unique constraint
        "ux_orders_demo_session_id_idempotency_key"
```

**A settlement applies exactly once.** The insert into `processed_webhook_events` and the order
transition are one transaction, so a duplicate delivery loses on the primary key and takes the
transition down with it. There is deliberately no "have I seen this event?" query. Two copies of one
settlement, released through a single gate at the same instant, produce one `settled` and one
`duplicate` acknowledgement, both 200, and the order captured once — a non-2xx would be retried five
times and abandoned for a delivery that was handled perfectly.

**Order transitions are a table, not a switch.** `OrderStateMachine` holds exactly five legal edges.
Self-transitions are illegal on purpose.

```
Pending -> Paid       Pending -> Cancelled
Paid    -> Packed     Paid    -> Cancelled
Packed  -> Shipped
```

`Pending` is the stored status; the order page labels it **Placed**, which is why the recording
above shows four stages and the table shows `Pending`.

**The cart never silently reprices.** If a price moved between adding an item and checking out, the
cart shows the captured price beside the live one and checkout refuses with a 409 rather than
charging either. A withdrawn variant is refused through the same path.

**Architecture is a test, not a convention.** Eight rules run against the built assemblies — some
through ArchUnitNET, some walking IL directly with Mono.Cecil, one over reflected metadata: the
domain depends on nothing, dependencies point inward, the `DbContext` does not escape persistence,
entities stay sealed and keep the constructor EF needs, and no type reads the ambient clock. That
last rule failed on its first run and was right to — `Order` set `PlacedAt` from
`DateTimeOffset.UtcNow`, which is now a parameter.

**Money is never a float.** Amounts are `long` minor units plus a currency, mapped to `bigint` +
`varchar(3)`. Mixing currencies throws rather than coercing, and `Money.Allocate` splits an amount
across n parts without losing or inventing a cent.

**Payments are simulated, on purpose.** The default gateway is
[an in-repo simulator](src/VelaCommerce.Infrastructure/Payments/SimulatedPaymentGateway.cs), so this
repo clones and completes a purchase with no payment account and no network. It signs its
settlements with HMAC precisely so the webhook receiver has something real to verify, and it offers
six scenarios — succeed, decline, abandon, duplicate, delay, reorder — which is more failure surface
than a live test key would give you.

Five Testcontainers tests in
[`DatabaseInvariantTests`](tests/VelaCommerce.Integration.Tests/DatabaseInvariantTests.cs) prove the
constraints fire by bypassing the domain and writing the illegal row deliberately.

## Running it

**With Docker, and nothing else:**

```bash
docker compose up
```

Then open <http://localhost:5008>. That is the whole shop — a real PostgreSQL 18, the API, the
Blazor storefront, the Demo Lab and the admin console — with no .NET SDK, no database to create and
no connection string to set. The image is built and published by CI on every push to `main`; the
container migrates and seeds itself, so the catalog is the same 288 products the tests run against.

It is the actual system rather than a recording, which is the point: the last unit really is sold to
exactly one of fifty simultaneous shoppers, because it is really PostgreSQL deciding. Read
[`docker-compose.yml`](docker-compose.yml) for the two settings that are not the defaults and why.

**From source**, if you want to change something. Requires the .NET 10 SDK, 10.0.400 or later
(`global.json` sets that as a floor and rolls forward across feature bands), and PostgreSQL 18.
Docker is needed only for the integration tests.

```bash
dotnet build                       # builds the storefront too; the API serves its files
createdb vela_dev
export VELA_DB_CONNECTION="Host=localhost;Port=5432;Database=vela_dev;Username=$USER"
dotnet run --project src/VelaCommerce.Api
```

Then open <http://localhost:5008>. The API migrates and seeds itself on first run in Development,
and serves interactive API documentation at `/scalar`.

The shop and the API share one origin on purpose, which is why `dotnet build` comes first: the demo
session is an `HttpOnly; SameSite=Lax` cookie, a browser will not attach it to a fetch from another
origin, and a storefront on a second port would get a fresh anonymous session and an eternally empty
cart with no error to explain it. So the API host serves the storefront's build output — see
[`StorefrontHostingExtensions`](src/VelaCommerce.Api/Hosting/StorefrontHostingExtensions.cs).

No connection string is committed. It is machine-specific, so it comes from the environment variable
above, or from user-secrets if you prefer it to persist:

```bash
dotnet user-secrets set "ConnectionStrings:Vela" \
  "Host=localhost;Port=5432;Database=vela_dev;Username=$USER" \
  --project src/VelaCommerce.Api
```

Tests, as CI runs them:

```bash
dotnet test tests/VelaCommerce.Domain.Tests
dotnet test tests/VelaCommerce.Architecture.Tests
dotnet test tests/VelaCommerce.Integration.Tests   # needs Docker: Testcontainers starts postgres:18-alpine
```

To regenerate the catalog — both the committed seed file and the client snapshot, which must stay
byte-identical between runs or CI fails:

```bash
dotnet run --project tools/VelaCommerce.SeedGen
```

The HTTP surface has an executable description as a [Bruno collection](api-tests/README.md):
plain-text `.bru` files that CI runs headless against a live API, covering **all eighteen of its
JSON operations** in 55 requests and 99 tests. The admin console's six operations are HTML form
posts rather than JSON and are covered by the integration suite instead. It is the closest thing here to a demo you can run —
`dotnet run` in one terminal, `bru run -r --env local` in another, and about three seconds later
you have watched a cart become an order, an idempotency key refuse to charge twice, a refund
recorded once and refused twice, a settlement forgery turned away three different ways, and a
race for the last unit sell exactly five of five.

## Phases

Ten phases. **This table is the status**; [`docs/PLAN.md`](docs/PLAN.md) is the plan as written on
2026-09-02, before any of it existed, and is kept unedited rather than revised to agree with the
outcome. A check of all 992 of its lines against the repository on 2026-09-05 found 124 divergences,
so it is a record of a forecast and not a description of the present — its own header says so, and
lists where the two part company.

| # | Phase | Status |
|---|---|---|
| 0 | Toolchain, repo, CI | Mostly done — Azure/Terraform/OIDC deployment deliberately deferred |
| 1 | Domain, data, seeded catalog | Done — money in integer minor units, five-edge order state machine, 288 products / 691 variants generated deterministically |
| 2 | The 60-second storefront | Done — Blazor WebAssembly, browsing and search entirely client-side from a static snapshot |
| 3 | Cart, tenancy | Done — demo-session cookie sealed with Data Protection, DbContext-level tenancy filter that fails closed |
| 4 | Checkout, payments, order timeline | Done — payment port and signing simulator, atomic reservation, idempotent checkout, transactional outbox, signed webhook receiver, accelerated timeline |
| 5 | Anti-rot hardening | Started — coverage floor and mutation score enforced in CI; nightly reset, backups and uptime monitoring need a deployment |
| 6 | Demo Lab + refunds | Done — nine lab scenarios with per-scenario permalinks and verdicts; refunds and cancellation with a ledger, a row lock that serialises concurrent refunds, and restock on cancellation |
| 7 | Admin + preview environments | Admin console done; server-side search on a [trigram index](docs/measurements/trigram-search.md); preview environments blocked on accounts that do not exist |
| 8 | Make it legible | Started — nine ADRs in [`docs/adr/`](docs/adr/); [cold start](docs/measurements/cold-start.md), [mutation testing](docs/measurements/mutation-testing.md) and [trigram search](docs/measurements/trigram-search.md) measured; the ACA number and the `/platform` page need a deployment |
| 9 | Optional differentiators | Not started — pgvector, passkeys, multi-cloud |

Also not built, and not hidden: no preview environments, and no real payment processor. Search
exists on both sides and they are different mechanisms — the shop browses and filters entirely
client-side from the static snapshot, and `GET /api/catalog/products?q=` runs an escaped `ILIKE`
over name and description for anything querying the API directly. Neither is full-text; the
`pg_trgm` index that would make the server-side one fast is phase 7 work that has not landed. The admin console packs orders and moves prices, and
deliberately cannot ship an order or adjust stock — [ADR 0004](docs/adr/0004-the-admin-cannot-ship-or-restock.md)
says why.

## Layout

```
src/VelaCommerce.Domain           Aggregates and invariants. No dependencies.
src/VelaCommerce.Infrastructure   EF Core mapping, migrations, seeding, outbox, payments, workers.
src/VelaCommerce.Api              Minimal API host; also serves the storefront in one origin.
src/VelaCommerce.Api/Admin        Blazor static SSR admin console, rendered per request.
src/VelaCommerce.Storefront       Blazor WebAssembly shop and its catalog snapshot.
tests/                            Domain, architecture and Testcontainers integration tests.
tools/VelaCommerce.SeedGen        Deterministic catalog generator.
api-tests/                        Bruno collection, run headless in CI.
docs/PLAN.md                      The original build plan, unedited. Not the status.
docs/adr/                         Decisions a reviewer is likely to read as a mistake.
docs/measurements/                Numbers, with the method that produced them.
docker-compose.yml                The whole shop in one command, against the published image.
```

(`Pending` is the stored status; the order page labels it *Placed*.)


## Licence

MIT. Catalog imagery is drawn client-side from the product's own attributes rather than committed,
so there is no third-party artwork in this repository to attribute. The image block in
`seed/catalog.seed.json` records filenames and a placeholder licence field against the day real
photography is sourced; it attributes nothing today, and says so.
