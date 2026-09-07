# What I would do differently at 100× traffic

**Written 2026-09-07.** The honest answer turned out to be more interesting than the one I expected
to write, so it goes first.

## The short version

**At 100× this demo's traffic, I would change one line and nothing else.** `min_replicas = 0` becomes
`min_replicas = 1`, and the bill goes from nothing to **$48–120 a year, probably the top of that
range or above**. No part of the architecture is under strain at that point, and saying otherwise
would be performing scale rather than reasoning about it.

The range comes from `infra/`, and it carries a condition the summary version of it usually drops:
it assumes Azure's *reduced idle rate*, which requires no request in flight, under 0.01 vCPU and
under 1,000 bytes/second — per replica. At 100× traffic the replica is by assumption not idle, and
the active rate is roughly eight times the idle one. So this is the one number in this document I
would refuse to quote without saying that.

The thing that breaks first is not the design. It is the invoice, and it breaks embarrassingly
early — at about **80 visits a day**.

## The arithmetic, because the whole answer depends on it

Container Apps grants every subscription 180,000 vCPU-seconds, 360,000 GiB-seconds and 2,000,000
requests a month. The request meter is nowhere near binding at this volume and can be set aside. The
other two, at this app's size — 0.25 vCPU, 0.5 GiB — convert to the same number, which is worth
noticing because it means neither is wasted:

```
180,000 vCPU-s ÷ 0.25 vCPU = 720,000 replica-seconds
360,000 GiB-s  ÷ 0.5  GiB  = 720,000 replica-seconds
                           = 200 replica-hours per month = 6.7 hours a day
```

A replica lingers 300 seconds after the last request (`cooldown_period_in_seconds`). So a visit that
arrives with nobody else around costs a full five minutes of replica time whether the visitor reads
one page or twenty:

```
720,000 ÷ 300 = 2,400 isolated visits per month = 80 a day
```

**That is the ceiling, and it is a ceiling on *visits*, not on load.** Eighty people a day, spread
out, exhausts a grant that eight hundred people arriving in one hour would barely touch — because
the second group shares one warm replica and the first group each pay for their own. The cost model
is dominated by *arrival pattern*, not volume, which is the opposite of the intuition most capacity
planning starts from.

Neon is the looser of the two constraints, which surprised me: 100 CU-hours a month at the free
plan's 0.25 CU floor is 400 wall-clock hours, twice Azure's 200. The database is not what runs out
first.

## So what actually happens at 100×

Take the demo at a few visits a day. 100× is a few hundred a day, and three things follow:

1. **The grant is gone.** Not close — several times over.
2. **Visits start overlapping.** Once arrivals are closer together than the 300-second cooldown, the
   container is simply always warm, and the marginal cost of a visit drops to nearly nothing. The
   expensive regime is the sparse one.
3. **Nobody waits 32 seconds any more.** The [cold start](measurements/cold-start.md) — 32.1 s at the
   median, 37.5 s at the 95th — is the design's deliberate trade, and it is only defensible while
   the demo is mostly idle. At 100× the container is warm anyway, so `min_replicas = 0` is buying a
   discount that no longer exists while still charging the first visitor of every quiet spell.

That is the whole change: flip the replica floor, accept a real bill, and delete the "waking the
shop up" notice from the cart drawer, because it would never be seen. The boot and product skeletons
stay — they cover the WebAssembly download and the snapshot fetch, not the container. `docs/PLAN.md` anticipated this as a
`demo_profile` variable and it was never built; at 100× it stops being a nicety and becomes the one
setting that matters.

**What I would NOT do at 100× is touch the application.** Not one line. It is worth being precise
about why, because "it would scale fine" is the kind of claim that is usually hand-waving.

## Why the application does not care

**Reads barely reach it.** The catalog is a **302 KB** static file — 43 KB gzipped — that the browser
fetches once, and browsing, search, filtering and sorting all run client-side against it. A
browse-only visit costs the origin that file plus a revalidation of the shell, and the shop's own
architecture rule means that is most traffic.

At 100× I would put a CDN in front of the snapshot. That is *nearly* configuration: the file is
currently served `public, max-age=0, must-revalidate`, so it revalidates at the origin on every
visit, and a CDN in front of it would need a real TTL to be worth having. The build already produces
a Brotli variant at 27 KB that nothing negotiates, because there is no response-compression
middleware. Both are small changes, but "just point a CDN at it" would be untrue.

**Writes are rare and small.** At a 2% conversion rate — my assumption, not a measurement — a few
hundred visits a day is a handful of checkouts an hour. The write path is nowhere near anything.

One correction to the tidy version of this story: the storefront *does* call the API while browsing,
once. The snapshot carries SKUs and the cart endpoints address lines by variant id, so the first
add-to-cart for a product calls `/api/catalog/products/{slug}` to resolve one for the other. Its own
comment notes it "doubles as a warm-up: on a cold start this is usually the call that pays for the
container waking". So the boundary is not "browsing never touches the API" but "browsing never
touches the API until you reach for the cart" — which is the same architectural claim, stated
accurately.

## What breaks much later, and what I would do about it

These are the real answers to "how does this end", and none of them is a 100× problem. They are
10,000× problems and beyond. Listing them in the order they would actually bite:

### 1. One row per variant, and every buyer of that variant queues on it

The checkout reserves stock with a single conditional statement, which the file summarises in its
own header comment as:

```sql
UPDATE stock_items SET reserved = reserved + q
WHERE variant_id = v AND on_hand - reserved >= q
```

(the executed version also carries `AND deleted_at IS NULL`)

This is [ADR 0006](adr/0006-the-database-decides-races.md) working exactly as intended — the database
picks the winner, not a `SELECT` that ran first — and it is the correct design. It is also a
serialisation point. Every buyer of one SKU takes the same row lock, so throughput on a *single hot
variant* is bounded by how fast PostgreSQL can update and commit one row: somewhere around
**200–1,000 checkouts per second**, less across a network.

That is enormous compared to anything this shop will see, and it is *small* compared to a real
flash sale on one product, which is exactly the shape of traffic that would find it. The fix is not
to abandon the conditional update — it is the only thing that makes overselling impossible — but to
stop making one row the unit of contention. The standard approach is to split a variant's stock into
N bucket rows, take one at random, and fall back to scanning buckets when the chosen one is empty.
It trades a little accuracy in "how much is left" for N× the write throughput. I would want the
contention measured before doing it, because bucketing is a real complexity cost paid against a
bottleneck that may never arrive.

### 2. The outbox dispatcher polls once a second

`OutboxOptions.PollInterval` is one second with a batch of ten. I watched it in the production logs
during the deploy — `FROM outbox_messages`, once a second, forever, whether or not anything is
waiting. Per replica that is trivial. At N replicas it is N queries a second against one table, all
taking `FOR UPDATE SKIP LOCKED`, and the polling cost grows with fleet size while the useful work
does not.

The textbook fix is `LISTEN`/`NOTIFY` so the dispatcher wakes on a write instead of a clock. **It
would not work here**, and the reason is recorded in the plan: Neon's pooled endpoint is PgBouncer in
transaction mode, which does not support `LISTEN`/`NOTIFY` at all. So the real options are a
dedicated connection on the direct endpoint, or moving the outbox onto a queue and accepting a second
piece of infrastructure. At the scale where this matters, a queue is the right answer and the outbox
table becomes the durable write-ahead of it rather than the transport.

### 3. There is no caching layer of any kind

Every response carrying per-visitor state is `Cache-Control: no-store` — the cart, checkout, refund,
demo and lab groups each attach it as a group filter, and the session middleware adds it to whatever
response mints the cookie. That is correct: caching them would be a data-leak bug rather than a
performance win.

**What I had believed, and what checking found:** I first wrote that *every* response is `no-store`,
which is what `infra/variables.tf` also claimed. Neither is true. Static assets are cached hard,
the SPA shell is `no-cache`, and **`/api/catalog` sends no cache header at all** — so a shared cache
would apply heuristic freshness to the one API surface nobody decided a policy for. That is a small
live bug rather than a scaling item, and it is the sort of thing a document like this is for.

At scale the catalog API would want a deliberate edge cache and the order-read path a short private
one. Both are additive, and neither exists today: there is no `AddOutputCache`, no
`AddResponseCaching` and no `IMemoryCache` anywhere in `src/`.

### 4. The client-side catalog stops being free

302 KB and 288 products is nothing to hold in a browser. 100× the *catalog* — not the traffic — is
28,800 products, so the snapshot goes to roughly 30 MB and the in-memory search index becomes
something nobody should be building on a phone. That is the point at which the architecture's central rule inverts and
browsing has to move server-side, behind a CDN and a real search index. Worth being explicit: this is
a *catalog size* threshold, not a traffic one, and the two are usually confused. This design is
excellent for a big audience shopping a small catalog and wrong for the reverse.

### 5. One region, and the database round trip is the page

Everything is in `eastus`, chosen to sit near the Neon project rather than near any visitor. Page
renders that touch the database pay a transatlantic round trip for European visitors. At scale that
is read replicas near the traffic and a routing decision about which reads may be stale — the single
largest piece of work on this list, and the one I would want the most evidence before starting.

### 6. There is no way to see any of this happening

[ADR 0009](adr/0009-no-log-analytics-workspace.md) records that there is no Log Analytics workspace
and no alerting, deliberately, because an unbounded ingestion meter is the likeliest way a $0 demo
starts costing money. That trade is right for a portfolio and indefensible for anything real: during
this project's own deploy I could not confirm whether `DemoDataPurge` had swept, because it runs once
at sixty seconds and the live stream had already rolled past it. Note the contrast with the outbox
poll above — that one was easy to observe precisely because it repeats every second. A recurring
worker is visible in a live stream; a one-shot one is only visible if you happen to be watching. **The first thing I would buy at real scale is not capacity, it is
tracing** — because every other item on this list is a guess until something measures it.

## What I would not change at any scale

The list is short and I would defend all of it in a design review:

- **Money as `long` minor units plus a currency**, with `checked` arithmetic. There is no traffic
  level at which a float becomes acceptable.
- **The database decides races.** More important at scale, not less — the whole point is that it does
  not depend on how many application instances exist.
- **Idempotency via unique indexes** rather than check-then-insert. Same reasoning.
- **The transactional outbox.** Its transport would change; the pattern is the reason a payment and
  its notification cannot disagree, and that is a property of correctness, not of scale.
- **Filters that fail closed.** [ADR 0007](adr/0007-the-tenancy-filter-fails-closed.md) writes the
  filter as *"there is a session, AND the row belongs to it"*, so the null case collapses to false
  and returns nothing. A real system would swap that session id for a customer id — my extrapolation,
  not the ADR's — but the shape survives the swap, and it is worth keeping exactly as it is.

## What I would measure first

In order, because each one decides whether the next is worth doing:

1. **Arrival pattern, not request count.** The cost model above lives or dies on how clustered visits
   are. This is the single number I would want and the one nothing currently records.
2. **Contention on the hottest variant** — lock wait time on `stock_items`, not average checkout
   latency, which would hide it entirely.
3. **The split inside the cold start.** [The measurement](measurements/cold-start.md) is explicit that
   it never separated scheduling from image pull from .NET startup from Neon resume. At `min_replicas = 1` that
   question disappears; below it, it decides whether the 32 seconds is worth attacking at all.

The honest summary of this whole document: at 100× I change a replica count, and everything
interesting on this list is somewhere past 10,000×. Most of what is written above is an argument for
*not* building things yet.
