# Vela Commerce

A storefront built for the first sixty seconds.

> **Status: in progress.** Phase 1 of 10 is done. There is no live demo yet — this
> README will carry the link, the screenshots and the measured numbers once the
> storefront and checkout land. Nothing below is claimed that the repo cannot show.

## The idea

Vela Commerce is a maritime outfitter: foul-weather gear, deck hardware, navigation
instruments, rope, charts and lamps. It exists to demonstrate how a .NET 10 service
behaves under the conditions e-commerce interviews actually ask about — two shoppers
racing for the last unit, a double-clicked checkout, a payment webhook delivered twice.

The organising constraint is an inversion of the usual demo:

> **Nothing on the first-paint path may depend on a resource that sleeps.**

Browsing, search and filtering run client-side against a catalogue snapshot served from
a CDN, so the store looks finished in under a second while the API and database are
still cold. That is what lets the demo live on free, scale-to-zero infrastructure without
a visitor ever seeing a spinner.

## What works today

| Area | State |
|---|---|
| Domain model | Money, catalog, inventory, cart, orders, order state machine |
| Domain tests | 161 passing |
| Database | PostgreSQL 18, EF Core 10, first migration applied |
| Catalog API | Paging, search, sort, per-variant availability |
| Seed data | 288 products / 691 variants, byte-identical between runs |
| Storefront | Not started (Phase 2) |
| Checkout & payments | Not started (Phase 4) |
| Deployment | Not started (Phase 0 remainder) |

## The invariants, and where they are actually enforced

Business rules live in the domain, but the two that cost real money are enforced by
PostgreSQL as well, because a rule that only exists in C# is a rule two concurrent
processes can both walk past.

**Stock cannot be over-reserved.** `StockItem.TryReserve` returns `false` rather than
throwing, and the database carries `CHECK (reserved <= on_hand)`:

```
ERROR:  new row for relation "stock_items" violates check constraint
        "ck_stock_items_reserved_within_on_hand"
```

**A double-submitted checkout creates one order.** The client sends an idempotency key
and a unique index on `(demo_session_id, idempotency_key)` makes the second insert lose:

```
ERROR:  duplicate key value violates unique constraint
        "ux_orders_demo_session_id_idempotency_key"
```

**Order transitions are a table, not a switch.** `OrderStateMachine` holds exactly five
legal edges. Self-transitions are illegal on purpose: `Paid -> Paid` must be recognised
as a duplicate webhook, not silently accepted.

```
Pending -> Paid       Pending -> Cancelled
Paid    -> Packed     Paid    -> Cancelled
Packed  -> Shipped
```

**Money is never a float.** Amounts are `long` minor units plus a currency, mapped to
`bigint` + `varchar(3)`. Mixing currencies throws rather than coercing. `Money.Allocate`
splits an amount across n parts without losing or inventing a cent.

## Running it

Requires the .NET 10 SDK and PostgreSQL 18.

```bash
createdb vela_dev
export VELA_DB_CONNECTION="Host=localhost;Port=5432;Database=vela_dev;Username=$USER"
dotnet run --project src/VelaCommerce.Api
```

The API migrates and seeds itself on first run in Development, then serves interactive
API documentation at `/scalar`. To regenerate the catalog:

```bash
dotnet run --project tools/VelaCommerce.SeedGen -- seed/catalog.seed.json
```

## Layout

```
src/VelaCommerce.Domain           Aggregates and invariants. No dependencies.
src/VelaCommerce.Infrastructure   EF Core mapping, migrations, seeding.
src/VelaCommerce.Api              Minimal API host.
tests/                            Domain tests, integration tests.
tools/VelaCommerce.SeedGen        Deterministic catalog generator.
docs/PLAN.md                      The full build plan, 10 phases.
```

## Licence

MIT. Catalog images are placeholders and are not committed; see the attribution manifest
in `seed/catalog.seed.json`.
