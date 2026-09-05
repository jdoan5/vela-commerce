# 0006 — Races are decided by the database, never by a SELECT that ran first

**Status:** Accepted · recorded 2026-09-05 · the decision itself predates it (Phase 4)

## Context

Three of this project's four interesting concurrency problems have the same shape: two requests
arrive at once, and something must pick one. The tempting implementation for all three is to look
before you leap — read, decide, write — and it is wrong for all three in the same way. Between the
read and the write there is a gap, and the other request is in it.

## Decision

Every race here is resolved by a database primitive, and never by a prior `SELECT`.

| Race | What decides it |
|---|---|
| Fifty shoppers, five units | A conditional `UPDATE … WHERE available >= n` whose **row count** picks the winner |
| A double-clicked checkout | A **unique index** on `(demo_session_id, idempotency_key)` — both inserts are allowed to race it |
| A payment webhook arriving twice | A **primary key** on the settlement id, so exactly-once is the database's job |

The stock case is the clearest. The version a reviewer expects — load the aggregate, call
`StockItem.TryReserve`, save — reads better and is wrong: two requests hold two copies of the row,
both see `Available >= 1`, and EF then writes an **absolute** value with no guard, so the second
silently overwrites the first.

The idempotency case is the one people argue with. Selecting the key first and inserting if absent
is not a fix — two simultaneous submits both find nothing and both insert. So **both are allowed to
insert**, and the unique index picks the winner. The `SELECT` survives only as a fast path for the
ordinary replay.

## Consequences

Correctness lives in SQL that EF did not generate, which has a real cost: the ledger `UPDATE` is
hand-written in six places, and the `ORDER BY variant_id` that prevents deadlocks between the
checkout loop, the cancel path, the reaper's sweeps and the refund handler is repeated by hand at
each. A deadlock found during reaper testing (`40P01`) was exactly this rule being broken in one
place, and the fix was to reorder the reaper's locks to match every other writer.

**What enforces it** — and these were verified to exist at the lines cited, not taken on trust:
`Fifty_shoppers_racing_for_five_units_sell_exactly_five` asserts exact numbers (five orders,
forty-five 409s, zero 500s, `reserved = 5` against `on_hand = 5`), because "at most five" would pass
for a shop that sold nothing. `The_conditional_update_is_what_makes_the_last_unit_race_safe` in
`DatabaseInvariantTests` drives the statement directly and asserts the second call affects zero rows.
`A_checkout_that_cannot_fill_every_line_gives_back_what_it_had_already_taken` covers partial
rollback.

**Two claims in the code are stronger than what backs them**, found while writing this record and
worth stating rather than tidying away:

- The CHECK constraint is described as "the backstop if anybody writes the racy version" in three
  places. It is not, for the failure mode named: the racy version writes a value that satisfies the
  constraint. The conditional `UPDATE` is the whole defence.
- "Returns the winner's order with a 200" is stale in three places, including the OpenAPI
  description. The losing double-submit can legitimately answer 202 — it re-reads between the order
  commit and the settlement commit, so both answers are truthful. The behaviour is right; the
  sentence is out of date.
