# 0010 — The purge runs on visits, not on a clock

**Status:** Accepted · recorded 2026-09-06 · **overturns `docs/PLAN.md` §6 Phase 5** (Phase 5 / anti-rot)

## Context

Demo data has to expire. Every visitor mints a session, and carts, orders, price overlays and outbox
rows accumulate against a Neon Free project with a half-gigabyte cap and no expiry date. That is the
obvious problem and it is the less serious one.

The serious one is `stock_items`. That ledger is **global** — one row per variant, no session id,
shared by every visitor — so an abandoned checkout that lands in a state `ReservationReaper` is
designed not to touch holds its units against everybody, permanently. The reaper only sweeps orders
still `Pending`; a `Paid` order whose settlement confirmed the order but not its reservations keeps
them, correctly, because somebody bought those units. Enough of those and the stocked-at-1 product
that the whole race demonstration is built on is sold out for good, with nothing in a log to say why.

`docs/PLAN.md` §6 Phase 5 specifies the fix as an **Azure Container Apps Job on a cron**, and §9
gives the reasoning: never a GitHub `schedule:`, because GitHub silently disables scheduled workflows
in a public repository after 60 days without activity — which is exactly when a quiet portfolio gets
clicked. That argument is correct and this record does not dispute it. What it disputes is that an
ACA Job is the only alternative.

## Decision

`DemoDataPurge` is an in-process `BackgroundService` that sweeps while the container is already
awake. Sixty seconds after start, then every six hours. No cron, no second Azure resource.

**The reasoning is about what wakes the container, not about what is easier.** This app runs at
`min_replicas = 0`. It is asleep unless somebody is looking at it. The rows the purge deletes exist
only because somebody visited, and a visit is also the only thing that starts this process — so the
work and the trigger have the same cause. Tying the sweep to visits is not a weaker approximation of
a nightly cron; for this system it is a closer fit to when the work actually arises.

Three things follow, and each was a cost the ACA Job would have carried:

1. **No second secret ritual.** A Container Apps Job's `secret` set is its own storage, shared with
   nothing. The Neon connection string is deliberately kept out of Terraform state and written once
   by hand with `az containerapp secret set`; a job would mean performing that ritual a second time
   against a second resource, doubling the surface of the one step in this deployment that a human
   does by pasting a credential.
2. **No image that nothing can update.** The container app survives its placeholder image only
   because the deploy pipeline updates it by digest *and* `container_app.tf` ignores the image. A
   Terraform-created job has the first half of that and not the second: nothing in any workflow
   updates a job's image, and the deploy identity's role grants only `Microsoft.App/containerApps/*`
   — it has no permission over `Microsoft.App/jobs/*` at all. The job would sit on
   `ghcr.io/jdoan5/vela-commerce:main` indefinitely, running a build that is not the live one
   against the live schema.
3. **No new RBAC.** Widening the deploy role to cover jobs is a deliberate security decision, and
   this is not a good enough reason to make it.

Those are the same four moving parts — a second image, a second registry path, a second Terraform
resource, and an identity that needs new permissions — that `.github/workflows/migrate.yml` already
weighed and declined for the migration bundle. This decision reaches the same answer from the same
premises rather than quietly contradicting it.

**Neon's cost points the same way.** The free plan allows 100 compute-hours a month, and the plan's
own §9 arithmetic forbids any keep-alive cron against the database. A nightly job would wake Neon
every night whether or not anyone visited — 30 nights against a fixed 5-minute autosuspend is about
0.6 CU-hours a month. Small, and genuinely affordable. But the in-process version costs **zero**
additional CU-hours, because the container is only running when a visitor has already woken the
database, and the readiness probe is already polling it. The cheaper option is also the simpler one,
which does not happen often enough to pass up.

## Consequences

**A demo nobody visits is never swept, and that is the whole cost of this decision.** It is stated
plainly here rather than discovered later.

It is a smaller cost than it sounds, because the growth and the sweeping have the same cause: an
unvisited demo is also one where nothing new is accumulating. What sits unswept is whatever the last
visitor left, and the first visit after that pays for it — sixty seconds in, off the cold-start path,
before the shopper is likely to reach a cart. The one thing that genuinely degrades in the meantime
is stranded stock, and it degrades no further while nobody is there to strand any more.

The failure mode is bounded in the other direction too: `docs/PLAN.md` §9 already records that
per-visitor tenancy means the demo does not *need* a reset to look clean. A stranger who trashes
their sandbox leaves only their own sandbox trashed. This purge is hygiene and storage, not the
primary defence, which is why it is allowed to be opportunistic.

**No UptimeRobot heartbeat.** The plan pairs the nightly job with a heartbeat monitor so that a
silently failing reset raises an email. That mechanism does not transfer: "did the nightly job run?"
has no answer when there is no nightly job, and a heartbeat pinged on every container wake would
measure traffic rather than health. Uptime monitoring is still absent from this project, ADR 0009
already records that there is no alerting of any kind, and this record does not improve that.

**If this ever needs to be a cron, the condition is specific:** demo data that must expire on a
schedule for a reason other than growth — a retention promise made to a visitor, say — or a
deployment where `min_replicas` is no longer 0, which breaks the premise that a running container
implies a recent visitor. Neither is true today.

## What the purge does, and the two rules that are not obvious

**It releases stock before it deletes anything.** The reservation rows are the only record of how
much to hand back, and `stock_reservations` has no foreign key to `orders` — so deleting an order
first does not cascade to its reservations, it orphans them. An orphaned `Held` reservation is
unreachable by every existing sweep in this solution: the reaper finds candidates by joining
`orders`, and the visitor's own reset scopes to order ids the tenancy filter returned. Nothing would
ever find those units again.

**It ages carts by their own primary key**, because carts carry no timestamp column of any kind —
the table is `id`, `demo_session_id`, `currency`, `deleted_at`. Every id in this schema is a UUIDv7
minted by `Guid.CreateVersion7()`, and PostgreSQL 18's `uuid_extract_timestamp()` recovers the
instant it was minted. Verified through Npgsql's binary wire format against `postgres:18-alpine`
rather than assumed: a .NET Guid round-trips byte-identical, PostgreSQL reports version 7, and the
extracted age matches the moment of generation.

That is a PG18 dependency where the rest of the codebase deliberately has none — ids are generated
in .NET specifically so server-side `uuidv7()` is an optimisation rather than a requirement. It is
accepted here for one reason: **it fails closed.** `uuid_extract_timestamp` returns NULL for any id
that is not a v1 or v7 UUID, and `NULL < cutoff` is NULL, so a row whose age cannot be established is
a row that is kept. The failure direction of an unexpected id is "lives forever", never "deleted
early", which is the only acceptable asymmetry for a worker that deletes. The alternative — a
`created_at` column on `carts` — is a migration, and remains the right answer if the ageing ever
needs to mean "last used" rather than "first created".

## Notes

The retention window is 24 hours, which is shorter than the 14-day session cookie. A visitor
returning the next day still has an identity and will find an empty cart behind it. That is a real
consequence and the cheaper of the two failures: matching the cookie would keep fourteen days of
abandoned checkouts holding units on a ledger every visitor shares.

`DemoDataPurgeTests` proves the parts that would be silent if they broke. One of them was written
badly first and the mutation found it: the original shipped-order test asserted the ledger was
unchanged after purging a shipped order, and **passed** with `Shipped` wrongly added to
`OrderStateMachine.HoldingStock` — because the release runs into `AND reserved >= quantity`, finds
nothing, and refuses. The guard hid the bug from the test. The test now sets up a second live order
holding units on the same variant, which is the case where the guard permits the double release and
the second shopper's stock quietly disappears.
