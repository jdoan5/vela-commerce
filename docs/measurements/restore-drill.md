# Rebuilding the database from nothing, timed

**Measured 2026-09-06.** An empty `postgres:18-alpine` container, rebuilt with the same two commands
`.github/workflows/migrate.yml` runs against production, and then inspected.

The question this answers is not "how fast is the restore" — it is **whether there is a restore at
all**, since this project takes no backups. [ADR 0011](../adr/0011-no-database-backup.md) is the
decision; this is the evidence under it.

## The result

| Step | Command | Time |
|---|---|---|
| Build the migration bundle | `dotnet-ef migrations bundle --self-contained` | 3.62 s |
| Apply the schema | `./vela-migrate --connection "$CONN"` | **1.31 s** |
| Seed the catalog | `dotnet run --project src/VelaCommerce.Api -- --seed` | **1.28 s** |
| | **Database work** | **2.59 s** |

Verified afterwards, against the rebuilt database rather than against the log:

```
 tables      | 13
 products    | 288
 variants    | 691
 stock_items | 691
 indexes     | 33
 pg_trgm     | 1
 db size     | 10142 kB
```

The seeder's own line: `Seeded 288 products, 691 variants, 691 stock rows (last-unit demo SKU:
VC-RIG-0236-20M)`. That SKU matters — it is the stocked-at-1 product the concurrency demonstration
sells, so its presence is the check that the rebuild produced the *demo*, not merely a schema.

## What this number is not

**It is a local container over loopback, not Neon over the internet.** The 2.59 s is the work, not
the wall clock a real recovery would show. Against Neon add the round trips and, if the project has
autosuspended, a compute resume — the same resume that is a measurable part of the
[32-second cold start](cold-start.md). A realistic figure is tens of seconds, not milliseconds, and
this document does not claim otherwise. It was not measured against production because doing so
means pointing a schema-applying bundle at the live database to time something already known to
work.

**The bundle build is not on the recovery path** in the way the table might suggest. CI builds it;
a human recovering by hand would pay it once, and it needs a checkout and an SDK, which is a
dependency worth naming.

## What is rebuilt, and what is simply gone

Everything durable is rebuilt, because everything durable is committed:

- **Schema** — seven migrations, 13 tables, 33 indexes, the `pg_trgm` extension and the two GIN
  trigram indexes the [search measurement](trigram-search.md) is about.
- **Catalog** — 288 products and 691 variants from `seed/catalog.seed.json`, generated
  deterministically and committed, so the rebuilt shop is byte-for-byte the shop that was lost.

What is gone is gone on purpose:

- **Every demo row** — carts, orders, refunds, reservations, price overlays. These have a 24-hour
  life by design; `DemoDataPurge` deletes them on a schedule anyway, so a recovery that discards
  them is doing early what the system does routinely.
- **Stock levels drift back to seeded values.** `stock_items` is written by shoppers, so a rebuild
  resets `on_hand` and `reserved` to what the seed says. For a demo that is a correction rather
  than a loss: it is the same reset the "sold out" state would otherwise need by hand.
- **The order-number sequence restarts.** Harmless, because the orders that held those numbers are
  gone in the same event.

And one thing that is **not** in the database at all, which is the part worth knowing: the Data
Protection key ring lives in blob storage, not in PostgreSQL. Losing the database does not
invalidate a single session cookie or order-retrieval link. Losing the *storage account* would, and
that is a separate resource with separate Terraform.

## Reproducing it

```bash
docker run -d --name vela-restore -e POSTGRES_PASSWORD=vela -e POSTGRES_USER=vela \
  -e POSTGRES_DB=vela -p 55433:5432 postgres:18-alpine
```

```bash
dotnet tool run dotnet-ef migrations bundle --project src/VelaCommerce.Infrastructure \
  --startup-project src/VelaCommerce.Infrastructure --configuration Release \
  --self-contained --target-runtime osx-arm64 --output /tmp/vela-migrate --force
```

Then apply and seed against `Host=localhost;Port=55433;Database=vela;Username=vela;Password=vela`,
exactly as `migrate.yml` does. `--seed` refuses to run if any migration is unapplied, so the
ordering is enforced by the program rather than by the person following these steps.
