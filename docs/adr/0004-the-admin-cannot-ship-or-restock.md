# 0004 — The admin cannot ship an order or adjust stock

**Status:** Accepted · 2026-09-04 · Phase 7 · **Diverges from [`PLAN.md`](../PLAN.md) phase 7**

## Context

The plan for phase 7 listed "mark-packed/shipped" and "stock adjustment" among the admin's
capabilities. Both were dropped while building it. Recording that here rather than quietly shipping
a smaller console is the point of this file: the plan is committed to the repository, so a reviewer
can see what was promised, and an unexplained gap reads as an oversight.

Stock is the one ledger in this system that is genuinely shared. `stock_items` carries `on_hand`
and `reserved` for every variant, there is exactly one row per variant for everybody, and it is
deliberately **not** covered by the demo-session tenancy filter — because the interesting property
of this project is fifty simultaneous shoppers racing for the last unit, and that race is only real
if they are racing over the same row.

So an admin button that adjusts stock is an admin button that reaches across every visitor at once.
The same argument applies to shipping: `Shipped` is the state that consumes the reservation and
decrements `on_hand`, which is the ledger move, wearing a fulfilment word.

## Decision

The console packs orders and moves prices. It does not ship, and it does not adjust stock.

`Packed` is safe precisely because it is the one transition that touches nothing shared: it moves
the order's own status and no other row. Pack is also scoped by the model, not by a check in the
handler — packing another session's order is a **404, not a 403**, because the row was never
loaded. A 403 would confirm the order exists, which would make the endpoint a way to enumerate
order numbers.

The order timeline still reaches `Shipped` on its own. `OrderTimelineWorker` advances real rows on
a demo clock — 20 seconds to Packed, 40 more to Shipped — so the state machine's full path is
demonstrated by the thing that was already demonstrating it, without a button that lets one visitor
move another's inventory.

## Consequences

The console's own page says what it cannot do and why, because a capability list with silent gaps
invites the reader to assume the gaps are bugs. It cannot change the shop, cannot adjust stock
because stock is shared, and cannot ship because shipping is what moves that ledger.

This leaves the admin unable to demonstrate the `Packed → Shipped` edge by hand. That edge is
covered where it belongs — `OrderStateMachine`'s legal-edge table, asserted as a whole set in the
domain tests, and the timeline worker driving it against real PostgreSQL.

If the demo ever gets per-visitor stock, this decision should be revisited rather than inherited:
the reason to refuse is that the ledger is shared, and it would no longer be.
