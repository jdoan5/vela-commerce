# 0011 — No database backup

**Status:** Accepted · recorded 2026-09-06 · **overturns `docs/PLAN.md` §6 Phase 5 and §10** (Phase 5 / anti-rot)

## Context

`docs/PLAN.md` specifies a nightly `pg_dump` running as an Azure Container Apps Job, writing rolling
archives into the Terraform storage account, with a PG18 client because `ubuntu-latest`'s bundled
`pg_dump` refuses a version mismatch. §10's risk table names it as the mitigation for losing the
Neon project, and §9 budgets its storage at under ten cents a month.

None of it was built. Until this record, nothing said whether that was a decision or an omission —
which is the actual problem, because an unbuilt safeguard that nobody decided against is
indistinguishable from one that was forgotten.

## Decision

**No backup, and the reason is what is in the database rather than what a backup costs.**

After a deployment has been running for a month, the Neon project holds exactly three kinds of row:

1. **The schema** — seven migrations, committed.
2. **The catalog** — 288 products and 691 variants generated deterministically from
   `seed/catalog.seed.json`, committed.
3. **Demo data** — carts, orders, refunds, reservations, price overlays and outbox messages, every
   one of which [ADR 0010](0010-the-purge-runs-on-visits-not-on-a-clock.md) deletes after 24 hours
   as a matter of design.

A backup of that is a backup of two things already in git and one thing the system deliberately
throws away. Restoring a 20-hour-old dump would recover a stranger's abandoned cart, four hours
before the purge deleted it again.

The plan already reached half of this conclusion and stopped short of acting on it: §9 says, in
bold, "**The committed seed plus migrations is the primary reconstruction path; the dump is a
convenience.**" This record finishes that thought and declines the convenience.

**What replaces it is a drill that was actually run.** An empty `postgres:18-alpine`, rebuilt with
the same two commands `migrate.yml` runs against production: schema in **1.31 s**, catalog in
**1.28 s**, then inspected — 13 tables, 33 indexes, `pg_trgm` present, 288 products, 691 variants,
691 stock rows, including the stocked-at-1 SKU the concurrency demonstration sells. The numbers, the
method and the honest caveats are in [`docs/measurements/restore-drill.md`](../measurements/restore-drill.md).

A rehearsed rebuild is worth more here than an unrehearsed dump. The common way a backup strategy
fails is not that the dump is missing; it is that nobody ever restored one and the restore does not
work.

## Consequences

**A window of demo data is unrecoverable.** Up to 24 hours of visitor activity — carts mid-checkout,
orders mid-timeline — is lost if the Neon project is lost. For a shop whose money is imaginary and
whose data has a one-day life by design, that is not a loss worth infrastructure.

**Stock levels reset to seeded values** on a rebuild, because `stock_items` is written by shoppers.
For a demo that is a correction rather than a loss.

**The order-number sequence restarts.** Harmless: the orders holding those numbers are gone in the
same event.

**The ACA-Job costs from ADR 0010 applied here too**, and are worth naming because they are the same
four: a second Terraform resource, the Neon connection string pasted by hand into a second secret
store, an image the deploy identity has no permission to update, and — unique to this one — a
`pg_dump` binary that has to be PG18 and is not in the app's chiseled image, meaning a second image
as well. That was never the deciding argument, though. Even if a backup job were free, it would be
backing up a catalog that is in git.

**This decision does not extend to the storage account.** The Data Protection key ring lives there,
not in PostgreSQL, and losing it invalidates every session cookie and every order-retrieval link
that has been issued. That is the durable state in this system, and it is the thing a future record
about backups should be about. It is not covered here and it is not covered anywhere else either.

## What would flip this

Any row that is not reconstructible from a committed file and is not disposable. Real customer
orders, obviously — but short of that: an admin-authored catalog change that is not regenerated from
the seed, or any retention promise made to a visitor. If the demo ever grows a way to write
something durable, this record is the one to reopen, not `docs/PLAN.md`.
