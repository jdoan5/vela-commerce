# Trigram search, and when the index earns its place

**Measured 2026-09-05.** PostgreSQL 18, the seeded catalog of 288 products / 691 variants, plus a
synthetic table of the same shape grown to 100,000 rows to find where the planner changes its mind.

`GET /api/catalog/products?q=` runs `ILIKE '%term%'` against `products.name` and
`products.description`. No B-tree can serve that — a leading wildcard leaves no prefix to seek on,
so the planner has to read every row. A `gin_trgm_ops` GIN index is the structure that can, and
`pg_trgm` ships in `postgres:18-alpine`, in the `postgres:18` image CI uses, and on Neon, so it
costs no image change anywhere.

## The result nobody wants to publish

**At the real catalog size the planner ignores the index, and it is right to.**

```
EXPLAIN (ANALYZE, BUFFERS) SELECT id FROM products
WHERE deleted_at IS NULL AND (name ILIKE '%lamp%' OR description ILIKE '%lamp%');

Seq Scan on products  (cost=0.00..21.32 rows=14) (actual time=0.014..0.342 rows=7)
  Buffers: shared hit=17
```

The whole table is 17 buffers. A sequential scan costs 21.32 and finishes in 0.36 ms. No index path
beats that, so PostgreSQL does not take one — which is the planner working, not failing.

**The index is nonetheless correct**, and that is a separate question with a separate answer. Asking
for the best *index-based* plan shows it serving the query properly:

```
SET LOCAL enable_seqscan = off;

Bitmap Heap Scan on products  (cost=34.55..52.23 rows=14) (actual time=0.021..0.029 rows=7)
  Recheck Cond: ((name ~~* '%lamp%') OR (description ~~* '%lamp%'))
  Heap Blocks: exact=6
```

7 rows, 6 heap blocks, 0.029 ms. `enable_seqscan = off` is a diagnostic and never a production
setting; it is the only way to ask *could this index answer this query* separately from *is it worth
it at this size*.

## Where it starts being chosen

A table of the same shape, filled so roughly 1 row in 200 matches the term — realistic selectivity
for a catalog search — and `ANALYZE`d at each size:

| Rows | Plan |
|---|---|
| 288 | Seq Scan (cost 7.32) |
| 1,000 | Seq Scan (cost 26.00) |
| 3,000 | Seq Scan (cost 79.00) |
| 10,000 | Seq Scan (cost 263.00) |
| 30,000 | Seq Scan (cost 790.00) |
| 100,000 | **Bitmap Heap Scan** (cost 2258.71..2331.05) |

The crossover is somewhere between **30,000 and 100,000 rows**. This catalog is 288, so the index is
carried for a shop roughly 100× larger than the one that exists.

An earlier version of this measurement was wrong and is worth recording: the first synthetic table
put the search term in *every* row, so the query returned everything and a sequential scan was
correct at every size. A predicate matching 100% of rows will never use an index, and the numbers
looked like a flat result rather than a broken experiment.

## Is it worth keeping?

Yes, on three grounds, none of which is "it makes search fast today":

- **It costs 248 kB** (168 kB on `description`, 80 kB on `name`) and no measurable write penalty on
  a catalog that is regenerated rather than edited.
- **It is the reason `CatalogEndpoints` can keep its query shape.** The endpoint uses `ILIKE`
  against untouched columns specifically so an index *can* serve it; the alternative,
  `lower(name) LIKE …`, is unindexable by anything but a matching expression index. Without the
  trigram index that comment is aspirational, and aspirational comments are what this repository
  treats as bugs.
- **The failure mode it prevents is silent.** A catalog that grows past the crossover with no index
  gets slower with nothing in a log to say why.

## What is not here

**Typo tolerance.** `pg_trgm` also offers similarity matching (`%` and `similarity()`), which is
what "typo tolerance" in `docs/PLAN.md` means, and it is deliberately not implemented. It is a
behaviour change — "lantren" returning the lantern — not an index change, and it needs a similarity
threshold chosen against real terms plus a decision about ranking. Adding it because the extension
happens to be installed would be turning on a feature because it was cheap rather than because it
was asked for.

**Anything about the storefront's search.** The shop browses and searches entirely client-side from
a static snapshot, which is the architecture's central rule and is unaffected by any of this. The
`q` parameter is an API capability, covered by `CatalogSearchTests` and one Bruno request.

## Reproducing it

```bash
psql -d vela_dev -c "EXPLAIN (ANALYZE, BUFFERS) SELECT id FROM products
  WHERE deleted_at IS NULL AND (name ILIKE '%lamp%' OR description ILIKE '%lamp%');"
```

`CatalogSearchIndexTests` pins the parts that must not silently change: the extension is installed,
both indexes are GIN with `gin_trgm_ops`, and the endpoint's query shape is one the index can
answer. Dropping either index makes the third test fail even with sequential scans forbidden —
PostgreSQL falls back to a Seq Scan when no index path exists at all.
