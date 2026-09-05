# Cold start, measured

**Measured 2026-09-05.** Apple M-series, macOS 26.6, Docker 29.7.2, images built and run natively
for `linux/arm64`, PostgreSQL 18 in a sibling container on the same Docker network,
`ASPNETCORE_ENVIRONMENT=Production` so the container neither migrates nor seeds — the path a
deployed revision takes.

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

The image cost is correspondingly small here: **88.4 MB against 88.1 MB**, about +1 MB across
`/app`, essentially all of it in that one assembly. `deploy.yml` records +6.3 MB for the x64 build,
which is a different architecture measured at a different time and is not contradicted by this.

## What this does and does not license

**It does not say ReadyToRun is useless.** It says this configuration of it does nothing measurable
on this hardware. Two reasons to be careful about generalising:

- **Architecture and CPU.** This is arm64 on a fast laptop core. Container Apps runs x64 on a shared
  vCPU, where JIT costs more absolute time, so the same missing native code could matter more there.
  That measurement needs a deployment and is [blocked](../../README.md#status).
- **Coverage.** The obvious next experiment is to set `PublishReadyToRun` for every project, and
  `PublishReadyToRunComposite` to take in the framework, then re-run exactly this. That is a real
  change with a real image-size cost, and it should be made because a measurement asked for it —
  not turned on because it sounds like it should help, which is how the current claim got written.

**What is not measured here at all** is Container Apps' own scale-from-zero: scheduling a container,
pulling the image, and the platform's own latency before the process starts. On a `min_replicas = 0`
deployment that is very likely the dominant term, and none of it is affected by ReadyToRun.

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
