# Cold start, measured

Two measurements of two different things, and the gap between them is the point.

## On Azure Container Apps — the number that matters

**Measured 2026-09-06**, against the live deployment: Container Apps Consumption, 0.25 vCPU /
0.5 GiB, `min_replicas = 0`, Neon PostgreSQL 18 with its own five-minute autosuspend. Each sample
waited for the revision to report `ScaledToZero` first, so every one is a genuine scale-from-zero.

| | p50 | p95 | min | max | n |
|---|---|---|---|---|---|
| **Cold** — first request after scale-to-zero | **32.13 s** | 37.45 s | 32.07 s | 37.45 s | 7 |
| **Warm** — the next request | **0.16 s** | 0.28 s | 0.09 s | 0.28 s | 7 |

Cold samples, sorted: 32.07, 32.09, 32.13, 32.13, 32.14, 32.62, **37.45**.

**A 196× gap at the median**, and six of the seven land inside a 0.55 s band. That says the time is
structural rather than contended — the platform does the same work each time and takes the same time
to do it — with one sample five seconds slower for a reason this measurement cannot see.

**An earlier version of this file reported n=3 with a 0.07 s spread and called the result "almost
suspiciously stable".** Four more samples arrived after it was written and one was the 37.45 s. The
median did not move; the claim about the tail was wrong, and wrong in the direction that flatters
the result — which is the direction to be suspicious of. A p95 five seconds above the p50 is not a
tail anybody should describe from three points.
Both requests are `GET /api/catalog/products?pageSize=24`, which is a real EF Core query against
real PostgreSQL, serialised through the whole pipeline.

### What is in those 32 seconds — and what is not measured

Four things happen and only their total was measured: Container Apps schedules a replica, pulls the
206 MB image, starts .NET, and Neon resumes its compute. **The split between them is not measured
here**, and the reason is a decision this project made on purpose: with no Log Analytics workspace
there is no queryable log history, and by the time `az containerapp logs show` can be run against a
replica, the startup lines have aged out of the live stream. That is
[ADR 0009](../adr/0009-no-log-analytics-workspace.md)'s stated cost arriving in practice, and it is
worth recording as such rather than as an oversight. Decomposing it would mean either attaching a
workspace or catching the stream in the same second the replica starts.

### Whether 32 seconds is acceptable

It is the price of `min_replicas = 0`, and that setting is doing two jobs at once: it is why an idle
month costs nothing on Azure, and it is why Neon's compute stays suspended and the 100 CU-hour free
allowance is not consumed by a demo nobody is looking at. `min_replicas = 1` would remove most of
the wait and cost money on both sides simultaneously.

So the trade is a slow first click in exchange for a demo that is still up in 2029, and the honest
way to present that is to say so rather than to hide it behind a spinner. What makes it defensible
is the architecture's central rule: **browse, search, filter and sort never touch the API at all**.
A visitor landing on the shop gets the catalogue from a static snapshot immediately. The 32 seconds
is paid by the first person to put something in a cart, not by the first person to look.

---

## Locally, under Docker — the ReadyToRun study

**Measured 2026-09-05.** Apple M-series, macOS 26.6, Docker 29.7.2, images built and run natively
for `linux/arm64`, PostgreSQL 18 in a sibling container on the same Docker network,
`ASPNETCORE_ENVIRONMENT=Production` so the container neither migrates nor seeds.

This measured the *application*; the section above measures the *platform*. At 0.41 s locally
against 32.13 s deployed, the application's own start is roughly 1% of what a deployed visitor
waits — which is why the ReadyToRun result below, correct as it is, changes nothing that matters.

This file exists because `deploy.yml` carried a claim nobody had checked. Beside
`PublishReadyToRun=true` it said: *"precompiled native code, which is the cheapest available win on
the cold start that `min_replicas = 0` guarantees every idle visitor will pay."* The image-size cost
was measured. The win was not.

## The result

Twelve runs per arm for the two request measurements, ten for the log-derived one. Each run is a
fresh `docker run` of a fresh container against an already-running database.

| | ReadyToRun **on** | | ReadyToRun **off** | | Δ p50 |
|---|---|---|---|---|---|
| | p50 | p95 | p50 | p95 | |
| Container start → Kestrel listening | 250 ms | 278 ms | 248 ms | 263 ms | +2 ms |
| Container start → first `/alive` | 368 ms | 392 ms | 370 ms | 377 ms | −2 ms |
| Container start → first catalog page | 408 ms | 430 ms | 410 ms | 428 ms | −2 ms |

**ReadyToRun makes no measurable difference to this application's cold start.** A 2 ms median
difference against a 60–70 ms spread between the fastest and slowest run in each arm is noise, and
it changes sign between measurements, which is what noise looks like.

The third row is the one that matters for a shop: it includes a real EF Core query against a real
PostgreSQL, serialised as JSON through the whole pipeline. About 120 ms of every figure is Docker's
own container creation, which is why the first row is quoted separately — it is the application's
share alone, read from the container's `StartedAt` and Kestrel's own "Now listening on" log line.

## Why it made no difference

`PublishReadyToRun=true` on the entry project compiled **one assembly**. Extracting `/app` from both
images and hashing:

| Assembly | ReadyToRun on | off | |
|---|---|---|---|
| `VelaCommerce.Api.dll` | 1473 KB | 565 KB | differs |
| `VelaCommerce.Domain.dll` | 120 KB | 120 KB | **byte-identical** |
| `VelaCommerce.Infrastructure.dll` | 1048 KB | 1048 KB | **byte-identical** |
| `Microsoft.EntityFrameworkCore.dll` | — | — | **byte-identical** |
| `Npgsql.dll` | — | — | **byte-identical** |

So the setting produced native code for the thin assembly holding the endpoint definitions, and for
nothing else. The domain, the entire persistence layer, EF Core and the Npgsql driver — which is
where a startup and a first catalog query actually spend their time — were still JITed from IL in
both arms. The null result is not surprising once the images are opened; it is exactly what this
configuration should produce.

The image cost is correspondingly small: about **+1 MB across `/app`** (56 MB against 55 MB by
extraction), essentially all of it in that one assembly.

**A caution about how that was measured, because it caught me.** `docker image inspect --format
'{{.Size}}'` under-reports a foreign-platform image on Docker Desktop by more than half: the x64
image reads 94 MB on this Mac, 206 MB on an x64 GitHub runner, and 214 MB by extracting the
filesystem and counting bytes. The `/app` figures above are extraction-derived and therefore sound;
any whole-image number taken from `inspect` on a non-native platform is not. `deploy.yml` carried
85.9 → 92.2 MB from that same broken method for months, and CI's own gate is what found it, by
failing against a budget that turned out to be the thing that was wrong.

## What this does and does not license

**It does not say ReadyToRun is useless.** It says this configuration of it does nothing measurable
on this hardware. Two reasons to be careful about generalising:

- **Architecture and CPU.** This is arm64 on a fast laptop core. Container Apps runs x64 on a shared
  vCPU, where JIT costs more absolute time. That comparison is now possible and has **not** been
  run: it would mean publishing a second image with `PublishReadyToRun=false`, deploying it, and
  measuring — and against a 32-second platform cost, a change worth a fraction of a second on the
  application's 0.41 s would be undetectable without far more samples than it is worth.
- **Coverage.** The obvious next experiment is to set `PublishReadyToRun` for every project, and
  `PublishReadyToRunComposite` to take in the framework, then re-run exactly this. That is a real
  change with a real image-size cost, and it should be made because a measurement asked for it —
  not turned on because it sounds like it should help, which is how the current claim got written.

**What was not measured here** was Container Apps' own scale-from-zero. It is now, at the top of
this file, and the guess was right: it is the dominant term by two orders of magnitude, and none of
it is affected by ReadyToRun.

## Reproducing it

```bash
docker compose up -d db

dotnet publish src/VelaCommerce.Api/VelaCommerce.Api.csproj \
  --configuration Release --os linux --arch arm64 \
  -p:OpenApiGenerateDocumentsOnBuild=false \
  -p:ContainerFamily=noble-chiseled-extra \
  -p:PublishReadyToRun=true \
  -p:ContainerRepository=vela-commerce -p:ContainerImageTag=r2r \
  /t:PublishContainer
```

Then the same with `-p:PublishReadyToRun=false -p:ContainerImageTag=nor2r`, and for each image time
a fresh `docker run` until the first `200` from `/api/catalog/products?pageSize=24`. Discard the
first two runs per arm; they are warm-up for the page cache, not for the runtime.

`-p:OpenApiGenerateDocumentsOnBuild=false` is required, not optional: the generator runs the built
assembly to describe the API, and a cross-RID assembly cannot be loaded to do that.
