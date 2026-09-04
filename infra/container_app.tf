# =========================================================================================
# CONTAINER APPS ENVIRONMENT
#
# THIS RESOURCE IS WHERE $0/YEAR TURNS INTO $306/YEAR OR $876/YEAR. READ THE THREE WARNINGS
# BEFORE ADDING ANY ARGUMENT TO IT.
# =========================================================================================
#
# The baseline being protected: Container Apps grants every subscription, every calendar
# month, 180,000 vCPU-seconds + 360,000 GiB-seconds + 2,000,000 requests. It is an ongoing
# grant, not a trial credit, and it does not expire. Scale-to-zero is supported. At
# recruiter-level traffic on 0.25 vCPU / 0.5 GiB with min_replicas = 0, the expected
# Container Apps bill is $0.00.
#
# Nothing below is a default you get for free by leaving arguments out. Three specific
# arguments, each of which looks like good practice, each of which is one line, take that
# $0.00 and make it a permanent standing charge that bills at zero traffic, forever.

resource "azurerm_container_app_environment" "vela" {
  name                = "cae-${local.stem}"
  resource_group_name = azurerm_resource_group.vela.name
  location            = azurerm_resource_group.vela.location

  # ---------------------------------------------------------------------------------------
  # WARNING 1 OF 3 — infrastructure_subnet_id. THE $306/YEAR LINE.
  # ---------------------------------------------------------------------------------------
  # There is no `infrastructure_subnet_id` argument set here, and there must never be one.
  #
  # Setting it — "bring your own VNet", which every enterprise reference architecture and
  # every security checklist tells you to do — makes Azure provision networking that YOU are
  # billed for instead of networking Microsoft absorbs:
  #
  #     2 x Standard static public IP   ~$3.65/month each   = ~$7.30/month
  #     1 x Standard Load Balancer      ~$18.25/month       = ~$18.25/month
  #     -------------------------------------------------------------------
  #                                      ~$25.55/month STANDING
  #                                      ~$306.60/year
  #
  # That bills at zero traffic, with the app scaled to zero, with nobody visiting, forever.
  # It is not usage. It is rent.
  #
  # With the argument absent, Container Apps generates and owns the network itself and bills
  # none of it. This single omission is the largest cost decision in the entire repository.
  #
  # If you are reading this because a linter, a policy, or a blog post told you to add a
  # VNet: the thing you would be protecting is a public storefront with a public catalog and
  # a payment simulator whose money is imaginary. There is nothing on the private side to
  # reach. Do not add it. If you genuinely need it later, change the README's cost claim in
  # the same commit.
  #
  # Related and equally forbidden: `internal_load_balancer_enabled = true` requires
  # infrastructure_subnet_id, so it is the same charge wearing a different name.

  # ---------------------------------------------------------------------------------------
  # WARNING 2 OF 3 — workload profiles. THE ~$876/YEAR LINE.
  # ---------------------------------------------------------------------------------------
  # There is no `workload_profile` block here, and there must never be one for a Dedicated
  # (D-series) or Flexible profile.
  #
  # An environment with NO workload_profile block is a Consumption-only environment. That is
  # the plan the free grant applies to, and the only plan that scales to zero.
  #
  #   * Dedicated / D-series or E-series profiles bill per reserved instance-hour whether or
  #     not anything runs, plus a Dedicated Plan Management charge of ~$73/month (~$876/yr).
  #   * The Flexible workload profile carries that same management charge AND cannot scale
  #     to zero, so it defeats both halves of this design at once.
  #
  # Consumption only. If autocomplete offers you a workload_profile block, close it.

  # ---------------------------------------------------------------------------------------
  # WARNING 3 OF 3 — private endpoints and planned maintenance. ALSO ~$876/YEAR.
  # ---------------------------------------------------------------------------------------
  # Enabling a private endpoint on this environment, or configuring planned maintenance,
  # attracts the Dedicated Plan Management charge of ~$73/month — ~$876/year — REGARDLESS of
  # which plan the environment is otherwise on. A Consumption environment does not protect
  # you from this; enabling either feature is what triggers it.
  #
  # Neither is expressed as an argument on this resource today (private endpoints would be a
  # separate azurerm_private_endpoint pointed at this environment; planned maintenance is a
  # portal/CLI setting). That means a future maintainer will add them somewhere else in the
  # tree and never see this comment. So: it is also in README.md, and it is the reason the
  # README has a cost table rather than a sentence.

  # ---------------------------------------------------------------------------------------
  # Logging destination. See observability.tf for the full reasoning and the arithmetic.
  # ---------------------------------------------------------------------------------------
  # Both of these are null by default, which sets the environment's log destination to
  # "none": no Azure Monitor meter exists, so it bills exactly $0.00. The live log stream and
  # the app's own OTLP export to Grafana Cloud are unaffected.
  #
  # The two must move together. Setting logs_destination = "log-analytics" without a
  # workspace ID, or a workspace ID without the destination, is rejected at apply.
  logs_destination           = var.create_log_analytics_workspace ? "log-analytics" : null
  log_analytics_workspace_id = var.create_log_analytics_workspace ? azurerm_log_analytics_workspace.vela[0].id : null

  # `zone_redundancy_enabled` is deliberately absent, and this is not a judgement call — the
  # azurerm provider REFUSES to accept it without infrastructure_subnet_id:
  #
  #   Error: Missing required argument
  #   "zone_redundancy_enabled": all of
  #   `infrastructure_subnet_id,zone_redundancy_enabled` must be specified
  #
  # (that is real output from `terraform validate` on this file, not a paraphrase). So zone
  # redundancy is Warning 1 wearing a different hat: enabling it forces the BYO-VNet that
  # costs ~$25.55/month standing. It would also be meaningless here, because there is
  # normally zero replicas to spread across zones.

  tags = local.tags
}

# =========================================================================================
# THE CONTAINER APP
# =========================================================================================
#
# One ASP.NET Core container serving BOTH the JSON API and the Blazor WebAssembly
# storefront. That is not laziness — MapStorefront() is mapped last in Program.cs
# specifically so the two share an origin, because the demo-session cookie is HttpOnly and
# SameSite=Lax and a cross-origin fetch would simply not send it. Splitting the storefront
# onto a different hostname without also moving the cookie to SameSite=None would present as
# "every request is a new visitor, and the cart is always empty".

resource "azurerm_container_app" "vela" {
  name                         = "ca-${local.stem}"
  resource_group_name          = azurerm_resource_group.vela.name
  container_app_environment_id = azurerm_container_app_environment.vela.id

  # Single revision mode: one revision takes 100% of traffic and the previous one is
  # deactivated. Multiple mode is for blue/green and canary, which needs a traffic-splitting
  # story and a second warm revision — and a second warm revision is a second replica the
  # free grant has to pay for.
  revision_mode = "Single"

  # DO NOT set workload_profile_name. Leaving it unset keeps this app on Consumption.
  # Naming a profile here would require a workload_profile block on the environment above,
  # which is Warning 2.

  # Inactive revisions are free, but the list becomes unreadable after a few dozen deploys
  # and the portal paginates it. Three is enough to roll back by hand.
  max_inactive_revisions = 3

  # System-assigned managed identity. This is the identity the RUNNING APP uses — distinct
  # from the deploy identity in identity.tf, which is what GitHub Actions uses. The app needs
  # it for exactly one thing: reading and writing its Data Protection key ring in blob
  # storage (see dataprotection.tf and the role assignment in identity.tf).
  #
  # System-assigned rather than user-assigned so that DefaultAzureCredential in the app needs
  # no AZURE_CLIENT_ID to disambiguate, and so the identity dies with the app rather than
  # outliving it as an orphan with a live role assignment.
  identity {
    type = "SystemAssigned"
  }

  # ---------------------------------------------------------------------------------------
  # Ingress
  # ---------------------------------------------------------------------------------------
  ingress {
    # Public. This is a portfolio demo whose entire purpose is a link a stranger can open.
    external_enabled = true

    # Must match what the process actually listens on. The .NET 10 ASP.NET Core base image
    # sets ASPNETCORE_HTTP_PORTS=8080; the env var below restates it so the two cannot drift.
    # A mismatch does not error — the revision just never becomes ready, while the container
    # logs a perfectly healthy "Now listening on http://[::]:8080".
    target_port = local.container_port

    # Container Apps terminates TLS at the edge with a free managed certificate on the
    # *.azurecontainerapps.io hostname. This flag governs the hop from the edge to the
    # container, which is inside the environment's own network. Keeping it false forces
    # HTTPS on the public side, which is what makes the Secure cookie flag meaningful.
    allow_insecure_connections = false

    # "auto" negotiates HTTP/1.1 or HTTP/2. Blazor WASM benefits from HTTP/2 multiplexing on
    # the initial payload; nothing here needs raw TCP or gRPC-only.
    transport = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  # ---------------------------------------------------------------------------------------
  # Registry
  # ---------------------------------------------------------------------------------------
  #
  # NO AZURE CONTAINER REGISTRY. ACR has no free tier — Basic is roughly $0.167/day, about
  # $5/month, about $60/year — and it would exist only to hold a copy of an image that is
  # already hosted for free. GitHub Container Registry is free and unmetered for public
  # packages, and this repository is public. That is not a compromise; it is the better
  # answer at this scale.
  #
  # The registry block is conditional because a PUBLIC ghcr.io package needs no credential at
  # all: Container Apps pulls it anonymously. Adding a username and PAT to pull a public
  # image buys nothing but a token that expires at an inconvenient moment. Set
  # ghcr_username/ghcr_token only if the package is made private.
  dynamic "registry" {
    for_each = var.ghcr_username == null ? [] : [1]

    content {
      server               = "ghcr.io"
      username             = var.ghcr_username
      password_secret_name = "ghcr-token"
    }
  }

  # ---------------------------------------------------------------------------------------
  # Secrets
  # ---------------------------------------------------------------------------------------
  #
  # These declare NAMES, not values. The values here are placeholders (see the long note
  # above the sensitive variables in variables.tf): the real ones are written once by
  # `az containerapp secret set` in the deploy workflow, and the lifecycle block at the
  # bottom of this resource stops Terraform reverting them on the next apply.
  #
  # That is what keeps the Neon connection string and the payment signing key out of
  # Terraform state. `sensitive = true` on a variable hides it from console output; it does
  # not encrypt state, and a state blob is a file somebody can read.
  secret {
    name  = "vela-db-connection"
    value = var.database_connection_string
  }

  secret {
    name  = "payment-signing-secret"
    value = var.payment_signing_secret
  }

  dynamic "secret" {
    for_each = var.ghcr_token == null ? [] : [1]

    content {
      name  = "ghcr-token"
      value = var.ghcr_token
    }
  }

  template {
    # =====================================================================================
    # min_replicas = 0. THE ~$48-120/YEAR LINE, AND THE WHOLE REASON THIS FITS THE GRANT.
    # =====================================================================================
    # A single replica held warm at 0.25 vCPU / 0.5 GiB runs 2,678,400 seconds a month.
    # Against a grant of 180,000 vCPU-seconds, one always-on replica burns 669,600
    # vCPU-seconds — roughly 3.7x the entire monthly allowance — before a single visitor
    # arrives. Idle time is billed at a reduced idle rate, which is what puts the real
    # figure at roughly $4-10/month rather than the full active rate, but the direction is
    # not in doubt: min_replicas = 1 is the difference between $0 and $48-120 a year.
    #
    # The price paid is a cold start on the first request after an idle window. That is a
    # deliberate, stated trade: the storefront shows an honest "waking up" state rather than
    # a spinner that lies, and docs/PLAN.md carries a demo_profile toggle for flipping this
    # to 1 for the duration of an interview.
    #
    # DO NOT "just set it to 1 so the demo feels snappy". Measure the cold start, publish the
    # number, and flip it deliberately for a day if you must.
    min_replicas = 0

    # Spend ceiling, not a capacity plan. A scraper cannot cost more than this many replicas.
    max_replicas = var.max_replicas

    # Give the outbox dispatcher time to finish an in-flight delivery and release its row
    # lock before the replica is killed on scale-in. Rows held by a dead dispatcher stay
    # invisible until the lock times out.
    termination_grace_period_seconds = 30

    # How long the last replica lingers after the last request before scaling to zero. This
    # is a direct cost lever and it is stated explicitly rather than left to the default,
    # because the default is invisible and this number multiplies every visit:
    #
    #   grant burn per idle visit = cooldown_seconds x cpu vCPU-seconds
    #   300s x 0.25 = 75 vCPU-seconds per visit that arrives cold
    #
    # Against 180,000 vCPU-seconds/month that is 2,400 cold visits before the grant is
    # touched by idle time alone. Raising this to 900 to "make the demo feel warmer" cuts
    # that to 800 and does nothing for the first visitor, who still pays the cold start.
    #
    # 300 also happens to match Neon Free's fixed 5-minute autosuspend, so the container and
    # the database go to sleep at roughly the same time instead of one holding the other
    # awake.
    cooldown_period_in_seconds = 300

    container {
      name = "api"

      # Only ever used for the FIRST revision. Ignored thereafter — see the lifecycle block.
      # The deploy pipeline sets this by DIGEST (ghcr.io/...@sha256:...), not by tag, so
      # "which build is live?" has an answer that a retag cannot invalidate.
      image = var.container_image

      # Must be a valid Container Apps pair. 0.25 vCPU / 0.5Gi is the smallest, and is what
      # the free-grant arithmetic above assumes. Doubling to 0.5/1.0Gi doubles BOTH meters
      # and therefore halves the traffic the grant covers.
      cpu    = var.cpu
      memory = var.memory

      # -----------------------------------------------------------------------------------
      # Environment
      # -----------------------------------------------------------------------------------

      # Production is load-bearing in three separate places in this codebase, and getting it
      # wrong is silently destructive rather than loud:
      #   * Program.cs migrates and seeds ONLY in Development. Under Development this
      #     container would run migrations at startup against Neon on every cold start, and
      #     a slow migration would then fail the startup probe.
      #   * DemoSessionMiddleware sets the session cookie's Secure flag on everything that
      #     is not Development.
      #   * PaymentSimulatorOptions.AssertUsable refuses to authorize a payment or verify a
      #     settlement outside Development while a publicly-known signing secret is in use.
      #     Development disarms that refusal, which is why this must not be Development.
      #
      #     Note what it does NOT do: it does not stop the host booting. The container starts,
      #     serves the shop and passes its health probe with a public key configured; the
      #     failure lands on the first shopper who tries to pay. Setting the real secret with
      #     `az containerapp secret set` is therefore not verifiable from a deploy smoke test.
      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }

      # Restates the base image's own default so the listen port and ingress.target_port are
      # visibly the same number in one file.
      env {
        name  = "ASPNETCORE_HTTP_PORTS"
        value = tostring(local.container_port)
      }

      # Container Apps terminates TLS at the edge and forwards X-Forwarded-Proto. ASP.NET
      # Core ignores forwarded headers unless told not to, so without this Request.Scheme is
      # "http" behind the proxy and any absolute URL the app generates comes out as http on
      # an https site.
      #
      # This does not currently affect the session cookie — DemoSessionMiddleware derives
      # Secure from the environment name, not from Request.IsHttps — but it does affect
      # generated links, and it is the setting people reach for after the fact.
      # VERIFY THIS ONE against a real deployment; it is the least tested line in this file.
      env {
        name  = "ASPNETCORE_FORWARDEDHEADERS_ENABLED"
        value = "true"
      }

      # Workstation GC. Server GC is the ASP.NET Core default and reserves per-core heaps
      # sized for a machine, not for a 0.5 GiB container; on this size it typically costs
      # memory that the container does not have, and memory is one of the two metered
      # dimensions. MEASURE THIS before trusting it — it is a plausible default, not a
      # published number for this app.
      env {
        name  = "DOTNET_gcServer"
        value = "0"
      }

      # Neon pooled connection string. Read by Program.cs, which prefers this environment
      # variable over ConnectionStrings:Vela.
      #
      # NO AZURE DATABASE FOR POSTGRESQL. There is no permanently free managed Postgres on
      # any major cloud — Azure's Flexible Server free allowance is a 12-month new-account
      # offer, not an ongoing grant, and it ends with a bill rather than a pause. The
      # database is Neon Free and lives outside Azure entirely. Nothing in this Terraform
      # creates a database, and adding one is the single easiest way to give this design an
      # expiry date.
      env {
        name        = "VELA_DB_CONNECTION"
        secret_name = "vela-db-connection"
      }

      # Binds to Payments:Simulator:SigningSecret. The double underscore is ASP.NET Core's
      # environment-variable spelling of the configuration colon; a single underscore binds
      # nothing and fails as "still using the development default", which is a confusing way
      # to learn about underscores.
      env {
        name        = "Payments__Simulator__SigningSecret"
        secret_name = "payment-signing-secret"
      }

      # Where the Data Protection key ring lives, and Program.cs reads it: it persists the
      # ring to this blob through the app's managed identity and sets the application name,
      # so the ring survives a deploy and a scale from zero.
      #
      # Set unconditionally, and that is deliberate. The application treats an ABSENT value as
      # legitimate — a developer's machine and the build-time OpenAPI generator both run
      # without one — so nothing downstream would refuse a deployment that omitted it. It would
      # simply invalidate every cart and order link on the next revision, silently.
      env {
        name  = "VELA_DATAPROTECTION_BLOB_URI"
        value = "${azurerm_storage_account.dataprotection.primary_blob_endpoint}${azurerm_storage_container.dataprotection_keys.name}/keys.xml"
      }

      # -----------------------------------------------------------------------------------
      # Probes. THE ASYMMETRY BETWEEN THESE TWO IS THE POINT.
      # -----------------------------------------------------------------------------------
      #
      # /alive returns a constant and never touches the database.
      # /health calls Database.CanConnectAsync and reports 503 when it fails.
      #
      # Neon Free autosuspends after 5 minutes idle. That interval is fixed and cannot be
      # disabled or lengthened. So "the database is asleep" is the NORMAL state of this
      # system, not an incident, and the probes have to be wired with that in mind.

      # LIVENESS -> /alive. NEVER /health.
      #
      # Liveness failure kills the container. Pointing liveness at /health means: Neon
      # autosuspends, the probe fails, Container Apps kills the container, the replacement
      # starts, its liveness probe hits the same suspended database, and it is killed too.
      # A crash loop caused by a database doing exactly what its free tier documents.
      # /alive exists in Program.cs for this one reason.
      liveness_probe {
        transport = "HTTP"
        port      = local.container_port
        path      = "/alive"

        # Generous: this is a .NET cold start on 0.25 vCPU, not a warm process.
        initial_delay           = 10
        interval_seconds        = 30
        timeout                 = 5
        failure_count_threshold = 3
      }

      # READINESS -> /health. This one SHOULD check the database.
      #
      # Readiness failure withholds traffic; it does not kill anything. A 503 from a
      # replica whose database is still resuming is the correct answer, and the probe is
      # self-healing: CanConnectAsync is itself the connection that wakes Neon, so the probe
      # that reports "not ready" is the same call that makes it ready, typically within a
      # few seconds.
      #
      # failure_count_threshold is at the maximum on purpose. Neon's resume plus a cold
      # Npgsql pool can exceed a couple of intervals, and the cost of being wrong here is a
      # visitor seeing 503 on a demo that was, in fact, about to work.
      #
      # Note the second-order effect: this probe polls the database for as long as a replica
      # exists, which keeps Neon awake for the replica's lifetime and consumes Neon CU-hours
      # (100/month free). With min_replicas = 0 a replica only exists during and just after
      # real traffic, and Neon's own 5-minute autosuspend is roughly the same window, so
      # this is close to free. It would NOT be free with min_replicas = 1 — that setting
      # costs money on the Azure side and CU-hours on the Neon side simultaneously.
      readiness_probe {
        transport = "HTTP"
        port      = local.container_port
        path      = "/health"

        interval_seconds        = 10
        timeout                 = 5
        failure_count_threshold = 10
        success_count_threshold = 1
      }

      # STARTUP -> /alive, for the same reason as liveness. This is the probe that has to
      # tolerate a .NET cold start plus Blazor static asset enumeration on a quarter of a
      # core, and it must not be gated on a database that has not been woken yet.
      startup_probe {
        transport = "HTTP"
        port      = local.container_port
        path      = "/alive"

        interval_seconds        = 5
        timeout                 = 5
        failure_count_threshold = 30 # up to ~150s to come up before the revision is failed
      }
    }

    # Scale on concurrent HTTP requests. An explicit rule rather than the implicit default,
    # because the implicit default is invisible and this number is a cost lever: a lower
    # concurrency threshold spawns replicas sooner and burns the grant faster.
    #
    # This rule is also what makes scale-to-zero work — an HTTP scale rule is what lets the
    # KEDA scaler drop the replica count to 0 when no requests arrive.
    http_scale_rule {
      name                = "http-concurrency"
      concurrent_requests = "20"
    }
  }

  lifecycle {
    ignore_changes = [
      # The deploy pipeline owns the image. Terraform creating the app with a placeholder
      # tag and then reverting every deployed digest on the next `terraform apply` is the
      # classic way an IaC repo and a CD pipeline fight each other in production.
      template[0].container[0].image,

      # And the pipeline owns the secret VALUES, written with `az containerapp secret set`.
      # Without this, an apply would overwrite the real connection string with the
      # placeholder in variables.tf, and the app would come back up unable to reach the
      # database with no obvious cause in the diff.
      #
      # THE COST OF THIS LINE, so nobody rediscovers it at 2am: ignore_changes applies to
      # the whole secret set, not just to values. ADDING a new secret block here will not
      # take effect either — Terraform plans no change. To introduce a third secret you must
      # temporarily remove `secret` from this list, apply, and put it back; or add it with
      # `az containerapp secret set` and declare it here for documentation only.
      secret,
    ]
  }

  tags = local.tags
}
