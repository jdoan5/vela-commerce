# 0003 — Static SSR for the admin, beside a WebAssembly shop

**Status:** Accepted · 2026-09-04 · Phase 7

## Context

The shop is Blazor WebAssembly and obeys one rule that shapes the whole project: **nothing on the
first-paint path may query the database or call `/api`**. It browses and searches a static catalog
snapshot entirely client-side, which is what lets the API and the database scale to zero.

The admin needs the opposite. Every page it renders is a database read scoped to the caller's
session — orders, overrides, categories — and none of that can come from a snapshot, because the
whole point is that it is *this visitor's* data as of now.

Adding it to the WASM client would mean shipping admin components into every shopper's download,
authenticating from the client, and putting an `HttpClient` in a project that deliberately has none.
Adding interactive server rendering would mean a websocket circuit per admin, which is per-visitor
server state — exactly what a scale-to-zero host makes expensive and what the session cookie was
designed to avoid.

## Decision

The admin is **Blazor static SSR**, hosted inside the API project, rendering per request with no
circuit and no client-side runtime. Forms post to minimal-API endpoints that answer `303 See Other`,
so a reload cannot resubmit. There is no JavaScript.

Two render modes in one solution is the decision, and the boundary is drawn at the URL:

- `/admin` is in `ReservedPrefixes`, so the storefront's SPA fallback never claims it —
  [`StorefrontHostingExtensions`](../../src/VelaCommerce.Api/Hosting/StorefrontHostingExtensions.cs).
  Removing it does not look like a failure: an admin URL would answer `200` with a shop, and
  nothing would land in a log. `An_admin_path_with_no_page_behind_it_is_a_404_rather_than_the_shop`
  is the test that notices, and it has to use a path with no page behind it, because a real page
  would answer either way.
- The handlers live in `VelaCommerce.Api.Endpoints`, not beside the pages in `Api/Admin`, because
  `PersistenceBoundaryRules` permits only four namespaces to name the `DbContext`. The architecture
  test moved the file, which is the rule working rather than the rule being annoying.
- Pages take the request's `HttpContext` as a `[CascadingParameter]`. `IHttpContextAccessor` is an
  ambient dependency — it reaches for whatever request happens to be in flight — and it is not
  registered by default, so asking for it is a 500 on the first page view rather than a compile
  error. It cost one to learn that.

## Consequences

The rule survives intact: the admin is not on the first-paint path of the shop, so it can query the
database freely without weakening the claim the README makes about the storefront. A cold start on
`/admin` is a real cold start, and that is acceptable — nobody arrives at an admin console by
accident.

Antiforgery is now enabled host-wide (`UseAntiforgery()`), which is a change to the JSON surface's
pipeline and not only the admin's. Admin writes validate the token through an endpoint filter
rather than the model binder, because the handlers read the form themselves. The regression check
is the Bruno collection: 54 requests, 98 tests, 129 assertions, all still green with the middleware
in place.

Static SSR costs the interactivity a QuickGrid would have given — no client-side sorting, no
paging without a round trip. For a console whose largest table is one visitor's own overrides, that
is a trade worth taking to avoid per-visitor server state on a host that is meant to scale to zero.
