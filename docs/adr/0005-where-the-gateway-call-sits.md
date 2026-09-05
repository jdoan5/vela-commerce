# 0005 — Where the payment gateway call sits, relative to the transaction

**Status:** Accepted · recorded 2026-09-05 · the decision itself predates it (Phase 4 and Phase 6b)

## Context

Checkout and refunds have the same three ingredients: a database write, an HTTP call to a payment
processor, and a second database write recording the answer. There is no arrangement of them that is
safe in every direction, and this system makes **opposite** choices for the two — which looks like an
inconsistency until you ask what each is defending.

## Decision

**Checkout calls the gateway between two committed transactions, with no row locked.**

```
tx1: reserve stock, insert the order (Pending) and its reservations (Held). Commit.
---: authorize the payment. No transaction is open. No row is locked.
tx2: apply the answer.
```

**A refund calls the gateway inside one transaction, holding `SELECT … FOR UPDATE` on the order row.**

The difference is what is contended. Checkout's contended rows are `stock_items` rows every other
shopper wants; holding them across an external call serialises the whole shop behind one processor's
latency, and a `COMMIT` that then fails leaves money moved and no order. A refund's contended row is
one order only its own owner can refund, and releasing the lock first lets two requests with
different keys both read the same `RefundableRemaining`, both pass the "within what is left" check,
and both reach the gateway before either writes a row — the money gone twice.

The `refunded <= captured` CHECK constraint cannot save that case, and this is the subtle part: every
racing handler writes the same **absolute** figure, so the column ends up correct above a ledger with
twenty rows in it. The lock is the only load-bearing thing.

Checkout can afford the split because it has recovery machinery for the state it creates — a
fifteen-minute reservation window swept by `ReservationReaper`, plus the settlement outbox. A refund
has none, so it buys serialisation instead.

## Consequences

Checkout's split converts an atomicity failure into a **durable half-state**: an order `Pending` with
stock held, produced by a gateway that abandons, declines and then fails to settle, or cannot be
reached. That is accepted openly — it is visible, recoverable, and precisely what the reservation
expiry and the settlement webhook exist to resolve — but it makes the reaper and the outbox
load-bearing rather than optional, and it means a unit can be unsellable for up to fifteen minutes
after a checkout nobody completed.

It also forces `CancellationToken.None` on tx2. A shopper closing the tab after a capture must not
leave a paid order sitting in `Pending` with nothing to reconcile it against.

**What enforces the refund half:** `Twenty_simultaneous_refunds_of_one_balance_return_it_exactly_once`,
`A_gateway_that_refuses_leaves_no_ledger_row_and_no_money_moved` and
`A_refused_refund_does_not_spend_its_idempotency_key`, all in `RefundTests.cs`, plus the Demo Lab's
refund-race scenario.

**What enforces the checkout half: nothing.** This was checked rather than assumed. No architecture
rule constrains transaction shape, and every checkout integration test passes unchanged against a
single-transaction implementation — a decline still cancels, a deferred payment still writes its
outbox row, fifty racing shoppers still sell exactly five — because the in-repo simulator never
blocks. The half of this decision with the worse failure mode is currently held by a comment.

The refund's lock is affordable **only** because the gateway is in-process and does no I/O. With a
real acquirer it would have to become the two-phase arrangement checkout already uses, so this record
does not survive replacing the simulator — see [ADR 0008](0008-a-payment-simulator-that-signs.md).
