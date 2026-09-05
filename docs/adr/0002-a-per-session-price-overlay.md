# 0002 — A per-session price overlay, never a write to the catalog

**Status:** Accepted · 2026-09-04 · Phase 7

## Context

An admin that cannot change a price is not an admin. But the catalog is 288 products and 691
variants generated deterministically by `SeedGen`, shared by every visitor at once, and the
storefront browses it from a **static snapshot downloaded on first paint** — so a visitor who marks
everything down 90% would be editing the shop out from under strangers, and the nightly reset would
be the only way back.

The obvious implementation is `UPDATE product_variants SET price_amount = …`. It is one line, it is
what the plan originally said, and it is wrong for a shared demo in a way that only shows up when
two people are looking.

## Decision

Admin prices land in a separate table, `demo_catalog_price_overrides`, keyed on
`(DemoSessionId, VariantId)` and covered by the same `DemoTenancy` query filter as carts and
orders. Seeded rows are never written.

The table is deliberately unlike every other table here:

- **No `Entity` base and no soft delete.** Clearing an override means restoring the shared price,
  and there is no history worth keeping in a number nobody else could ever see. A `DeletedAt`
  column would be a tombstone for a value that never existed publicly.
- **No foreign key to the variant.** The overlay is disposable and the catalog is not; a cascade
  from a catalog row into per-session scratch data is a dependency pointing the wrong way.
- **No currency column.** A price in a currency the variant does not use is not a price, so the
  currency is read from the variant and cannot drift out of step with it.
- **One resolution point.** [`EffectiveCatalogPrices`](../../src/VelaCommerce.Infrastructure/Persistence/CatalogOverrides/EffectiveCatalogPrices.cs)
  is the only place that reads or writes the overlay. Eight call sites go through it — four reads
  (the cart's capture site, the cart's live-price re-read, checkout's price-change check, the admin
  console's list) and four writes (bulk reprice, single override, and the clear that the admin
  button and the demo reset each call). None can bypass it without naming a type they cannot see.

## Consequences

The bulk reprice is still one statement (`ExecuteUpdateAsync` over the overlay rows, seeded from an
`INSERT … SELECT … ON CONFLICT DO NOTHING`), so the operation keeps the shape a bulk operation
wants — it simply cannot leave the session, because the filter is a property of the model rather
than a `WHERE` clause somebody remembered to write.

Two behaviours fall out of compounding on the overlay rather than resetting from the seed:
repricing twice compounds from the current price, and the integer arithmetic truncates toward zero,
so a markdown rounds in the shopper's favour by at most a penny. Both are asserted
(`A_reprice_compounds_from_the_current_price_and_truncates`,
`A_discount_past_free_clamps_at_zero_rather_than_failing_the_statement`) rather than left as
whatever the expression happened to do.

The immutability claim is not asserted by reading prices back — that would pass for code that wrote
the catalog and then wrote it back. `No_admin_write_ever_touches_a_shared_row` snapshots
PostgreSQL's own `xmin` across `products`, `product_variants` and `stock_items`, drives every admin
write, and compares. `xmin` changes on any `UPDATE`, including one that stores an identical value,
so the assertion catches a write that leaves no visible trace.

The visible cost is the one the console says out loud: **the shop's grid will not show the new
price**, because the grid renders from the snapshot the client downloaded once and the overlay is
server-side. That is a real seam, and pretending otherwise would be the worse choice — so the page
names it and points at where the price *does* apply, which is the cart's capture and checkout's
charge. Filling a cart, repricing, and checking out produces a `409` naming the line rather than a
quiet charge at either price.

---

## Addendum, 2026-09-05: the claim above was prose, and prose lost

**The record stands; this corrects what it asserted about enforcement.**

As first written, this ADR said the resolution point could not be bypassed "without naming a type
they cannot see". That was not true when the sentence was written. `DemoCatalogPriceOverride` was
`public`, no test mentioned it, and the only thing keeping the convention was three comments saying
it was kept. It survived less than a day: the admin console's own page reader queried the table
directly — a LINQ `Join` across the overlay and the catalog would not translate, the fix was two
queries, and the second one named the entity. Three documents went on asserting uniqueness while a
fourth read path sat in the API project.

The original bullet also miscounted: three call sites, when there were six at the time and eight
now. A number nobody could check is a number that drifts.

Both halves are now mechanical, because the failure mode of the first version was that the claim
and the code had no connection:

- The entity is `internal`, with `InternalsVisibleTo` for the integration suite alone. Outside
  Infrastructure the compiler refuses — reverting the page reader gives `CS0122` before a test runs.
- Inside Infrastructure, where `internal` protects nothing,
  [`CatalogOverlayRules`](../../tests/VelaCommerce.Architecture.Tests/CatalogOverlayRules.cs) walks
  the compiled IL and admits four names: the gateway, the entity, its EF mapping, and the
  `DbContext` that attaches the tenancy filter. It reads IL rather than source so a read hidden in a
  lambda or a Razor component's closure is still attributed to the type that wrote it — verified by
  adding one and watching the rule name it.

The rule carries the same `namers > 0` sanity assertion as `PersistenceBoundaryRules`, so a walk
that stops seeing IL fails loudly instead of ticking green over nothing.

**The decision has not changed.** What changed is that it is now checked by something other than
whoever last read the comment.
