# 0007 — The tenancy filter fails closed

**Status:** Accepted · recorded 2026-09-05 · the decision itself predates it (Phase 3)

## Context

There is no login here, so a cookie is the only thing distinguishing two people browsing at once.
Every cart and every order belongs to a demo session, and the question is what happens on a code path
that has **no** session — a background job, a misordered middleware, a test harness, a request that
fails before the cookie is read.

## Decision

The filter is written as *"there is a session, AND the row belongs to it"*:

```csharp
cart => CurrentDemoSessionId != null && cart.DemoSessionId == CurrentDemoSessionId
```

so the null case collapses to `false` and returns **nothing**.

The obvious alternative — `CurrentDemoSessionId == null || …`, meaning "no session, therefore no
restriction" — reads more naturally and is exactly backwards. It turns every path that forgot to
establish a session into a full-table read of every visitor's carts and orders. The direction of the
default *is* the security property, so it is stated explicitly rather than left to SQL's
three-valued logic to arrive at by accident.

Two further choices follow:

- **It is a named filter.** Because `SoftDelete` and `DemoTenancy` are separate names, a caller
  suppressing one keeps the other. A single anonymous filter would mean any query for deleted rows
  silently became a cross-visitor query too.
- **The session arrives as an accessor, never a value**, so it is bound per query rather than baked
  into the cached model — asserted by
  `The_session_id_is_bound_per_query_rather_than_baked_into_the_cached_model`.

**Stock is deliberately outside it.** One `stock_items` row per variant, shared by everybody, because
the property worth demonstrating is fifty shoppers racing for the last unit — and that race is only
real if they contend for the same row. See [ADR 0004](0004-the-admin-cannot-ship-or-restock.md).

## Consequences

Seventeen tests cover it, all verified present, including
`A_context_with_no_session_sees_no_carts_at_all` and
`Suppressing_soft_delete_leaves_tenancy_in_force`.

The cost is opt-outs. Legitimate cross-session work — the webhook receiver, the reaper, the Demo Lab
— must say so explicitly, and there are seven named `IgnoreQueryFilters(["DemoTenancy"])` call sites
plus **twenty-four bare `IgnoreQueryFilters()`** in `src/`. A bare call suppresses *every* filter,
including soft delete, which is a wider hole than any of those sites needs. That is the honest cost
of the design and the obvious next hardening.

**What is not enforced**, checked rather than assumed:

- **Nothing gates a new tenanted entity.** No test enumerates the model looking for an entity with a
  `DemoSessionId` and no `DemoTenancy` filter. A fourth one added tomorrow would be unfiltered, and
  the first symptom would be one visitor seeing another's rows.
- The three `demo_session_id IS NOT NULL` CHECK constraints have no test.
- `DemoSession.Bind`'s guards — rejecting `Guid.Empty`, rejecting a rebind — are exercised by nothing.

The named `SoftDelete` opt-out that justifies the two-filter design has **zero** production callers.
The design is still right; the example given for it is hypothetical, and the comment should say so.
