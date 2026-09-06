# Keeping this demo alive

A portfolio project fails quietly. Nobody emails you when the link 404s — a reader clicks it, sees
nothing, and closes the tab. This is the routine that catches that, written down because a ritual
you have to remember is one you will stop doing.

**Roughly 15 minutes, once a month.** Put it in a calendar with a reminder; the whole point is that
it survives you forgetting about the project for a quarter.

---

## The monthly pass

### 1. Open the demo and buy something

<https://ca-vela-prod.nicesea-6ebff2dd.eastus.azurecontainerapps.io>

**The first page takes about 35 seconds. That is correct, not a fault** — the container runs at zero
replicas and Neon autosuspends after five minutes, so the first click pays a
[measured 32 s p50 / 37 s p95](measurements/cold-start.md) cold start. If it is still blank after a
minute, that is the failure.

Then actually complete a purchase: add to cart, check out with the sample address, and watch the
order move **Placed → Paid → Packed → Shipped**. Browsing alone proves almost nothing here — the
storefront serves from a static snapshot and renders fine with the API and the database both dead.
The checkout is the only click that proves the whole stack is up.

While you are there, press **Reset my data** so you leave the demo as you found it.

### 2. Merge the Dependabot PRs

Weekly PRs, grouped so a routine patch bump is one review rather than eight. Two reasons to merge
them rather than let them pile up:

- The obvious one: unpatched dependencies in a public repository.
- The non-obvious one: **GitHub disables scheduled workflows in a repository with 60 days of no
  activity.** The only cron here is `codeql.yml`, and merging anything resets that clock.

If CI is red on one, that is this month's actual work. Read the failure rather than re-running it.

### 3. Look at the money

**Azure** — the portal's Cost Management for the subscription. Expect **$0.00**, or a few pence of
blob storage. The subscription is Pay-As-You-Go with **no spending limit**, which was a deliberate
choice recorded as one, so the protections are architectural rather than a hard cap:
`min_replicas = 0`, no custom VNet, no Log Analytics workspace, Consumption-only. Anything above a
pound a month means one of those changed — start with
[ADR 0009](adr/0009-no-log-analytics-workspace.md) and the three warnings at the top of
`infra/container_app.tf`.

**Cost data lags up to 72 hours.** A zero today is not proof about yesterday.

**Neon** — the project's usage graph. The free plan allows **100 compute-hours a month** and
**0.5 GB** of storage. Compute should be single digits; nothing in this system polls the database on
a timer, and [ADR 0010](adr/0010-the-purge-runs-on-visits-not-on-a-clock.md) explains why that is a
rule rather than an accident. Storage should sit near the seeded ~10 MB, because demo rows are
purged after 24 hours. **Storage climbing month over month is the signal that the purge has stopped
running**, and it is the only signal there is.

### 4. Check the badges and the checks

The README's CI and CodeQL badges should be green. Then open the most recent `ci.yml` run and look
past the tick at three numbers that can degrade without failing anything:

| What | Where it fails | Today |
|---|---|---|
| Line coverage | Below 65% fails the build | 68.1% |
| Mutation score | Below 70 fails the build | 71.6% |
| Link checker | **Advisory — cannot fail the build** | 185 links |

The link job is `continue-on-error`, so a dead link is a green run with a red job inside it. It is
the one that needs looking at rather than trusting.

---

## Once a year

- **Re-verify every free-tier claim.** The README and `docs/PLAN.md` §9 carry a cost table with
  "verified on" dates. Free tiers change; a table with a two-year-old date is worse than no table,
  because it reads as current. Update the dates or update the claims.
- **Retarget the framework on a branch first.** .NET 10 is LTS with support to November 2028. The
  upgrade is a branch, a green CI run and a deploy — never a push to `main`.
- **Re-run the restore drill.** [`measurements/restore-drill.md`](measurements/restore-drill.md) has
  the two commands and the numbers to compare against. A rebuild that is never rehearsed is a
  rebuild that does not work, which is the entire argument of
  [ADR 0011](adr/0011-no-database-backup.md).

---

## What this ritual does not cover, and should

**Nothing watches this demo but you.** There is no uptime monitor, no heartbeat and no alerting of
any kind — [ADR 0009](adr/0009-no-log-analytics-workspace.md) records the absence, and it is still
absent. If the site goes down the day after you run this pass, you find out next month.

`docs/PLAN.md` §4 designs the fix and it is not built: an UptimeRobot Free keyword monitor on the
storefront, a **30-minute** check on `/alive` — deliberately longer than the ~5-minute idle window,
so it measures cold start instead of silently pinning the container warm and eating the free grant —
and a public status page. `/alive` exists and does not touch the database, so it costs no Neon
compute. What is missing is the account and the three monitors, which is web-UI work rather than
code.

Until that exists, **step 1 is the monitoring**, and the honest description of this project's
availability story is "somebody checks monthly".
