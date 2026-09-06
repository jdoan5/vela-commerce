# infra — Terraform for Vela Commerce

One root module. It stands up the Azure side of a public demo that is designed to cost
**$0.00/month**, and it is written so that the handful of arguments capable of breaking that
are commented at the point where someone would otherwise change them.

Applied on 2026-09-06: ten resources, and the shop is live at
<https://ca-vela-prod.nicesea-6ebff2dd.eastus.azurecontainerapps.io>. `terraform validate`
still runs in CI with no credentials and no subscription; `terraform plan` is still not run
there, and the reason has changed rather than gone away — see
[Why no plan output](#why-ci-still-does-not-run-a-plan).

---

## What this creates

| Resource | Terraform | Why | Standing cost |
|---|---|---|---|
| Resource group | `azurerm_resource_group.vela` | Blast radius and teardown in one command | $0.00 |
| Container Apps environment | `azurerm_container_app_environment.vela` | Consumption plan, default (Microsoft-managed) networking, no log destination | $0.00 |
| Container App | `azurerm_container_app.vela` | The one ASP.NET Core container serving both the API and the Blazor WASM storefront, `min_replicas = 0` | $0.00 within the free grant |
| Storage account + blob container | `azurerm_storage_account.dataprotection`, `azurerm_storage_container.dataprotection_keys` | Persisted ASP.NET Core Data Protection key ring | ~$0.00 (a few KB) |
| Deploy identity | `azurerm_user_assigned_identity.deploy` | Keyless OIDC deploys from GitHub Actions | $0.00 |
| Federated credentials ×2 | `azurerm_federated_identity_credential.*` | The GitHub → Entra trust | $0.00 |
| Custom role definition ×1 | `azurerm_role_definition.container_app_deployer` | The deploy role — the built-in one cannot work, see below | $0.00 |
| Role assignments ×2 | `azurerm_role_assignment.*` | Deploy identity → the app; app identity → its key blob | $0.00 |
| Log Analytics workspace | `azurerm_log_analytics_workspace.vela` | **Off by default.** `count = 0` unless you opt in | $0.00 (see below) |

**What it deliberately does not create:** no database (Neon Free, outside Azure), no
container registry (GHCR, free for public packages), no VNet, no load balancer, no public
IP, no private endpoint, no Key Vault, no Redis, no Application Insights.

---

## What it costs

Azure Container Apps grants **every subscription, every calendar month**: 180,000
vCPU-seconds, 360,000 GiB-seconds, 2,000,000 requests. It is an ongoing grant, not a trial
credit, and it does not expire. Scale-to-zero is supported.

At 0.25 vCPU / 0.5 GiB with `min_replicas = 0` and a 300-second scale-in cooldown, a single
cold visit that keeps the container alive for the full cooldown consumes:

```
300 s × 0.25 vCPU  =    75 vCPU-seconds
300 s × 0.5 GiB    =   150 GiB-seconds
```

which is **2,400 such visits per month** before the vCPU half of the grant is touched, and
2,400 before the memory half is. Recruiter-level traffic is two orders of magnitude below
that. **Expected Container Apps bill: $0.00.**

Everything else in the table above is either free by construction (managed identities,
federated credentials, role assignments, resource groups) or bills on a quantity that
rounds to zero (a few kilobytes of blob).

---

## The settings that would break the $0

Each of these is one line. Each is a thing a reasonable engineer would add. Each is
commented at the exact place in the code where it would be added.

| # | The change | What it costs | Where the comment lives |
|---|---|---|---|
| 1 | `infrastructure_subnet_id` on the environment ("bring your own VNet") | 2 × Standard static public IP (~$3.65/mo each) + 1 × Standard Load Balancer (~$18.25/mo) = **~$25.55/mo standing, ~$306/yr**, at zero traffic, forever | `container_app.tf`, Warning 1 |
| 2 | A private endpoint, **or** planned maintenance, on the environment | Dedicated Plan Management charge, **~$73/mo, ~$876/yr**, regardless of which plan the environment is on | `container_app.tf`, Warning 3 |
| 3 | A `workload_profile` block (Dedicated / D-series / Flexible) | Reserved instance-hours **plus** the same ~$73/mo management charge. Flexible additionally **cannot scale to zero** | `container_app.tf`, Warning 2 |
| 4 | `min_replicas = 1` | One always-on replica burns ~669,600 vCPU-seconds/month against a 180,000 grant — ~3.7× the entire allowance before a single visitor. Billed at the reduced idle rate, that lands at **~$4–10/mo, ~$48–120/yr** | `container_app.tf`, `template.min_replicas` |
| 5 | An Azure Container Registry | ACR has no free tier; Basic is ~$0.167/day ≈ **$5/mo, $60/yr** — to hold a copy of an image GHCR already hosts free | `container_app.tf`, above the `registry` block |
| 6 | Azure Database for PostgreSQL | No cloud offers a permanently free managed Postgres. Azure's Flexible Server allowance is a 12-month new-account offer that **ends in a bill, not a pause** | `container_app.tf`, above `VELA_DB_CONNECTION` |
| 7 | An uncapped Log Analytics workspace | ~$2.30–2.76/GB ingested past a 5 GB/month free allowance, plus ~$0.12/GB/month retention past 31 days. 10 GB/month = (10−5) × $2.76 = **$13.80/mo, $165.60/yr** for logs nobody opens | `observability.tf` |
| 8 | `zone_redundancy_enabled = true` | It is #1 in disguise — the provider **refuses** it without `infrastructure_subnet_id`. That refusal is quoted verbatim in the code | `container_app.tf` |

### What was chosen for logging, and what it costs

**No Log Analytics workspace.** `create_log_analytics_workspace` defaults to `false`, and the
environment leaves both `logs_destination` and `log_analytics_workspace_id` unset, which
makes the environment's log destination `"none"`.

**Cost: $0.00/month, with no meter that can drift upward.**

What you still have: `az containerapp logs show --follow` (the live stream — which is what
you actually use when a revision will not start), the app's own OpenTelemetry export to
Grafana Cloud Free, and Container Apps system logs in the portal for a short window. What
you lose: queryable log history *in Azure*. That history lives in Grafana, which
`docs/PLAN.md` had already chosen as the observability backend.

If you opt in anyway, the workspace this creates is capped at
`log_analytics_daily_quota_gb = 0.15` (~4.65 GB/month, inside the 5 GB free allowance;
ingestion **stops** rather than bills through) with `retention_in_days = 30` (inside the
free 31 days). Those two lines are the difference between a $0 workspace and an unbounded
one.

### Where the Data Protection keys live, and what that costs

ASP.NET Core Data Protection keys **must survive a deploy**. Without a shared, persisted key
ring, every deploy — and, with scale-to-zero, every cold start — generates a new one, and
every visitor's session cookie silently stops decrypting. Their cart is empty; the
order-retrieval link they were sent 404s. Nothing logs an error, because "this cookie did
not decrypt" is indistinguishable from "this visitor has no cookie".

**Chosen:** a single XML blob in a dedicated Standard/LRS/Hot storage account, read via the
container app's system-assigned managed identity (`Storage Blob Data Contributor`, scoped to
the one blob container).

**Cost:** storage accounts have no standing fee. A few kilobytes at ~$0.018/GiB-month plus a
handful of transactions per replica start. **Rounds to $0.00.**

> ### Wired, with one way left to get it wrong
> `Program.cs` reads `VELA_DATAPROTECTION_BLOB_URI`, calls `PersistKeysToAzureBlobStorage`
> with the app's managed identity and `SetApplicationName("vela-commerce")`, and throws on a
> URI that is set but malformed — so a typo cannot be mistaken for an absent value.
>
> The remaining hazard is the opposite one: an **unset** variable is deliberately not an
> error, because a developer's machine and the build-time OpenAPI generator both run without
> it. So a deploy that forgets it starts happily, logs a warning nobody reads, and empties
> every visitor's cart on the next revision. This Terraform sets it unconditionally, which is
> the point of setting it here rather than by hand.

---

## The OIDC subject, and how it was verified

Since July 2026, GitHub mints the OIDC `sub` claim in an **immutable** form built from
numeric IDs:

```
repo:<owner>@<owner_id>/<repo>@<repo_id>:<scope>
```

Every tutorial, and most vendor documentation, still shows the old
`repo:OWNER/NAME:ref:refs/heads/main`. **That string no longer matches anything.** When it
does not match, Entra rejects the exchange with an error that names the application and the
issuer and never mentions the subject.

**Verified** — read off a real token by running this repo's own
`.github/workflows/oidc-claims.yml`, with both numeric IDs then confirmed against the GitHub
REST API:

```
repo:jdoan5@30330279/vela-commerce@1355259325:ref:refs/heads/main
```

**Also verified**, on 2026-09-06 and before the first deploy — the environment-scoped
subject this Terraform writes:

```
repo:jdoan5@30330279/vela-commerce@1355259325:environment:production
```

For a while this section said that one was unverified, because the probe had only ever run
**outside** a GitHub Environment and the environment form was documented rather than
observed. `oidc-claims.yml` now takes an `environment` input; dispatching it with
`production` printed exactly the string above, matching what `local.github_subject_environment`
composes, character for character.

**Re-verify after anything that could change it** — a rename, a transfer, a new environment
name. `terraform output federated_credential_subjects` prints what Terraform wrote, for
comparison.

Environment scoping is the control that stops a pull request from a **fork** minting a
deploy token — a fork PR cannot select a protected environment. That is why the branch-scoped
credential (`create_branch_scoped_federated_credential`) exists only as a bootstrap aid and
should be set to `false` once the environment subject is confirmed.

---

## Layout, and why it is one root

`docs/PLAN.md` sketches `infra/modules/` consumed by thin `envs/production` and
`envs/preview` roots. That is the right shape for two environments. There is one. A module
with exactly one caller is not an abstraction, it is a directory you have to open twice to
read one resource — so this is a single root of ~9 files that a reviewer can read top to
bottom.

```
infra/
├── versions.tf          terraform{} block, provider pin, the commented-out backend + why
├── providers.tf         azurerm provider, RP registration, storage_use_azuread
├── variables.tf         every input, with descriptions saying what a good value looks like
├── main.tf              locals (incl. the OIDC subject composition) + resource group
├── container_app.tf     the environment and the app — and the three cost warnings
├── dataprotection.tf    the key-ring storage account and blob container
├── observability.tf     the opt-in, capped Log Analytics workspace
├── identity.tf          deploy identity, federated credentials, the custom deploy role, both assignments
├── outputs.tf           the URL, and the three values the deploy workflow needs
├── terraform.tfvars.example
└── .gitignore           the repo root .gitignore has no Terraform section — this is load-bearing
```

**The split that was considered and rejected:** putting the Container Apps *environment* in
its own state, separate from the *app*, on churn grounds. That argument does not hold here,
because the deploy pipeline does not run Terraform — it rolls a revision by digest with
`az containerapp update`, and Terraform ignores the image. So the app's Terraform is no
churnier than the environment's, and splitting would buy a cross-state data source for
nothing.

**The split that is real** is bootstrap versus this root, and it is handled by hand on
purpose. See below.

---

## Bootstrap: the chicken-and-egg, stated honestly

Remote state lives in an Azure Storage container. **That storage account must exist before
`terraform init` can talk to it, and Terraform cannot create it**, because creating it needs
a state file and the state file needs it. Every "bootstrap it with Terraform then migrate"
recipe is the same loop with an extra local state file in the middle.

So the backend block in `versions.tf` is **commented out**, and it stays commented out in the
repository for a second, independent reason: a backend block cannot interpolate variables or
locals — it is read before the graph is built — so its values must be literals, and a storage
account name is globally unique across all of Azure. Committing one both publishes this
account's name and guarantees that every fork of this public repo fails `terraform init`
against a storage account it does not own.

**A human runs these once. They are the only things Terraform does not own.**

```bash
# 1. Register the Container Apps resource provider. Free, idempotent, creates nothing.
#    Was `NotRegistered` on this subscription until the 2026-09-06 apply registered it:
#    `az provider show -n Microsoft.App` previously
#    returns NotRegistered, and without this the first apply dies on a
#    MissingSubscriptionRegistration error that names the provider but not the fix.
az provider register --namespace Microsoft.App --wait
az provider register --namespace Microsoft.OperationalInsights --wait

# 2. A resource group for state ONLY, separate from the app's group, so that
#    `terraform destroy` on this root cannot delete the state recording what it destroyed.
az group create --name rg-vela-tfstate --location eastus

# 3. The state storage account. THE NAME MUST BE GLOBALLY UNIQUE — append random hex.
#    No standing fee; LRS hot at ~$0.018/GiB-month on a state file of tens of KB.
STATE_ACCOUNT="stvelatfstate$(openssl rand -hex 3)"
az storage account create \
  --name "$STATE_ACCOUNT" \
  --resource-group rg-vela-tfstate \
  --location eastus \
  --sku Standard_LRS \
  --kind StorageV2 \
  --min-tls-version TLS1_2 \
  --allow-blob-public-access false \
  --allow-shared-key-access false
echo "Write this down: $STATE_ACCOUNT"

# 4. Grant YOURSELF blob data access. Owner and Contributor are CONTROL-plane roles and do
#    NOT grant data-plane blob access. This step is why step 5 works and why
#    `azurerm_storage_container` in dataprotection.tf can run against an account with
#    shared keys disabled.
SUB=$(az account show --query id -o tsv)
ME=$(az ad signed-in-user show --query id -o tsv)
az role assignment create \
  --assignee "$ME" \
  --role "Storage Blob Data Contributor" \
  --scope "/subscriptions/$SUB/resourceGroups/rg-vela-tfstate/providers/Microsoft.Storage/storageAccounts/$STATE_ACCOUNT"

# 5. The state container. Entra auth, not a shared key.
az storage container create \
  --name tfstate \
  --account-name "$STATE_ACCOUNT" \
  --auth-mode login
```

Then uncomment `backend "azurerm" {}` in `versions.tf` and init with partial config:

```bash
terraform init \
  -backend-config="resource_group_name=rg-vela-tfstate" \
  -backend-config="storage_account_name=$STATE_ACCOUNT" \
  -backend-config="container_name=tfstate" \
  -backend-config="key=production.tfstate" \
  -backend-config="use_azuread_auth=true"
```

State locking needs nothing extra — the azurerm backend takes a blob lease, so that one
storage account is the whole state infrastructure.

---

## First deploy, in order

> **Done on 2026-09-06.** Kept for the record of what the first apply took. The Azure
> subscription was a Free Trial with `spendingLimit: On` expiring 2026-09-04; it was
> upgraded to Pay-As-You-Go on 2026-09-05, which removed that limit permanently and
> irreversibly. The earlier decision was to finish the application, then
> upgrade to Pay-As-You-Go and deploy once. Upgrading permanently removes the spending limit
> and it cannot be re-enabled — it is the only genuine hard stop this subscription will ever
> have. Do not run step 6 before you mean it.

1. **Verify the OIDC subject.** Add `environment: production` to a job in
   `.github/workflows/oidc-claims.yml`, run it, and read the `sub` claim. If it is not
   `repo:jdoan5@30330279/vela-commerce@1355259325:environment:production`, fix
   `local.github_subject_environment` in `main.tf` before going further. *(See the warning
   section above. Skipping this costs a day.)*
2. **Create the GitHub Environment** named `production` in repo settings, with a required
   reviewer. Without the environment, the credential in step 6 trusts a subject nothing can
   present.
3. **Create the Neon project** (Postgres 18, Free plan) and copy the **pooled** connection
   string. Keep `No Reset On Close=true` in it — PgBouncer in transaction mode rejects the
   reset Npgsql otherwise issues.
4. **Push an image to GHCR** so the first revision has something real to pull:
   `dotnet publish src/VelaCommerce.Api -c Release /t:PublishContainer -r linux-x64` with
   the GHCR repository and tag set, from CI (an `ubuntu-latest` runner) or from this Mac
   (`-r linux-x64` cross-builds from arm64 — verify the produced image's architecture, it is
   easy to get an arm64 image that Container Apps will not run).
5. **Run the bootstrap** above.
6. **Apply, as a human, locally.** Not from CI — see *What the deploy identity may do*.
   ```bash
   cp terraform.tfvars.example terraform.tfvars   # fill in the two required values
   terraform init -backend-config=...             # as above
   terraform plan -out=tfplan                     # READ IT. Expect ~9 resources, no VNet,
                                                  # no public IP, no load balancer.
   terraform apply tfplan
   ```
7. **Set the real secrets.** They are deliberately not in tfvars, and this is the step that
   keeps them out of Terraform state:
   ```bash
   az containerapp secret set \
     --name    "$(terraform output -raw container_app_name)" \
     --resource-group "$(terraform output -raw resource_group_name)" \
     --secrets vela-db-connection="Host=ep-....neon.tech;..." \
               payment-signing-secret="$(openssl rand -base64 48)"
   ```
   Then roll a revision so the app picks them up (step 9 does this).
8. **Publish the identity values to GitHub** as repository *variables*, not secrets — none
   of them is a secret, which is the entire point of OIDC:
   ```bash
   gh variable set AZURE_CLIENT_ID       --body "$(terraform output -raw deploy_client_id)"
   gh variable set AZURE_TENANT_ID       --body "$(terraform output -raw tenant_id)"
   gh variable set AZURE_SUBSCRIPTION_ID --body "$(terraform output -raw subscription_id)"
   ```
9. **Run the deploy workflow.** It logs in with OIDC and rolls a revision by digest.
10. **Verify both probes against the live URL**, not just locally:
    `curl -f "$(terraform output -raw storefront_url)/alive"` and `.../health` (the output
    already carries the `https://` scheme).
    `/health` must return 200 with `"database":"reachable"` — and the first call may be slow
    while Neon resumes, which is correct behaviour, not a fault.
11. **Turn off the branch-scoped credential.** Set
    `create_branch_scoped_federated_credential = false` and re-apply, so a push to `main`
    alone can no longer mint a deploy token.

---

## What the deploy identity may do

A **custom** role, `vela-container-app-deployer` (`azurerm_role_definition.container_app_deployer`,
defined at resource-group scope), assigned at **one resource**: the container app itself. Not the
subscription, not the resource group.

The built-in `Container Apps Contributor` is deliberately not used, and cannot be: its
`containerApps/*/read` is a four-segment pattern and cannot match the plain three-segment
`containerApps/read` operation. Verified against the live tenant — see the comment above the
role definition in `identity.tf`.

That is enough for what the pipeline does against Azure — `az containerapp secret set` and
`az containerapp update --image <digest>` — and little else. A token stolen out of a
compromised workflow can roll this one app to a different image. It cannot create resources,
read the storage account, touch the identity that granted it, or see anything else in the
subscription.

**It can read this app's secret values.** The role grants
`Microsoft.App/containerApps/listSecrets/action`, and the app holds `vela-db-connection` (the
production Neon connection string) and `payment-signing-secret`. So the honest blast radius of
a stolen deploy token is: this app's image, the database, and the ability to forge a
settlement. The action is granted because the CLI resolves the app's secret set on some
`update` paths; removing it means proving a real image update still works first.

**It deliberately cannot run `terraform apply`.** That would need Contributor on the resource
group plus User Access Administrator to manage these very role assignments, granted to a
token mintable from a public repository. So Terraform is applied by a human, locally, with
their own credentials. If the pipeline ever fails with `AuthorizationFailed`, the most likely
genuine gap is a read on the parent resource group: add `Reader` scoped to the resource group
before reaching for anything wider. Do not "fix" it with Contributor at subscription scope,
which is what every search result will suggest.

The **app's** identity is separate and has exactly one grant: `Storage Blob Data Contributor`
on the one blob container holding its Data Protection key ring.

---

## Verification: real output

```
$ terraform fmt -check
FMT: clean, no files reformatted

$ terraform init -backend=false
Initializing provider plugins...
- Finding hashicorp/azurerm versions matching "~> 4.0"...
- Installing hashicorp/azurerm v4.81.0...
- Installed hashicorp/azurerm v4.81.0 (signed by HashiCorp)
Terraform has been successfully initialized!

$ terraform validate
Success! The configuration is valid.
```

`terraform validate` needs no credentials and no subscription. `.terraform.lock.hcl` pins
azurerm to 4.81.0 and is committed.

`tflint` is **not installed** on this machine, so no lint pass was run.

### Why CI still does not run a plan

A plan was eventually run, on 2026-09-06, immediately before the apply: **10 to add, 0 to
change, 0 to destroy**, with every cost-sensitive setting read back out of the plan file
rather than out of the source. It is not run in CI, and that reasoning is unchanged.

The azurerm provider performs resource-provider registration **when the provider is
configured**, which happens during `plan`, not only during `apply`. `providers.tf` sets
`resource_providers_to_register = ["Microsoft.App", ...]` — so a plan is a subscription-level
write, not a read-only preview. Registration creates no billable resource, but "CI does not
write to the subscription" is a rule about writes, not about bills, and it matters more now
that the subscription holds a live deployment than it did when it held nothing.

`Microsoft.App` was `NotRegistered` until the first apply registered it.

---

## Values a human must supply

| Value | Where | Notes |
|---|---|---|
| `subscription_id` | `terraform.tfvars` | `az account show --query id -o tsv` |
| `dataprotection_storage_account_name` | `terraform.tfvars` | Globally unique, 3–24 lowercase alphanumerics |
| `container_image` | `terraform.tfvars` (first apply only) | A tag CI has actually pushed; ignored afterwards |
| State storage account name | `-backend-config` at init | Created by hand in Bootstrap step 3 |
| Neon pooled connection string | `az containerapp secret set` | **Never** in tfvars — that puts it in state |
| Payment signing secret | `az containerapp secret set` | `openssl rand -base64 48`; must not be the committed dev default |
| `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | GitHub repo **variables** | From `terraform output` |
| GitHub Environment `production` | GitHub repo settings | Must exist before the environment-scoped credential means anything |
| The **verified** environment OIDC subject | `main.tf` `local.github_subject_environment` | Verified 2026-09-06. Re-run the probe after a rename or transfer. |
| `ghcr_username` / `ghcr_token` | Only if the GHCR package is made private | Public packages pull anonymously; leave `null` |
