# ---------------------------------------------------------------------------------------
# Azure placement
# ---------------------------------------------------------------------------------------

variable "subscription_id" {
  description = <<-EOT
    The Azure subscription GUID to deploy into, e.g. "00000000-0000-0000-0000-000000000000".
    Find it with `az account show --query id -o tsv`. Not a secret, but it is
    account-specific, so it belongs in a tfvars file or ARM_SUBSCRIPTION_ID rather than
    hard-coded here.
  EOT
  type        = string

  validation {
    condition     = can(regex("^[0-9a-fA-F-]{36}$", var.subscription_id))
    error_message = "subscription_id must be a 36-character GUID."
  }
}

variable "location" {
  description = <<-EOT
    Azure region short name, e.g. "eastus", "westeurope". The Container Apps free grant is
    per subscription and does not vary by region, so this is a latency decision, not a cost
    one — and the latency that matters is to Neon, not to the visitor, because every page
    render is at least one round trip to the database.

    "eastus" is the default because it is physically close to AWS us-east-1, where a Neon
    Free project provisioned in the default region lives. If you put the Neon project in
    eu-central-1, move this to "westeurope" or "germanywestcentral" to match.
  EOT
  type        = string
  default     = "eastus"
}

variable "name_prefix" {
  description = <<-EOT
    Short lowercase token prefixed to every resource name, e.g. "vela". Keep it under 10
    characters: the Container App name is capped at 32 and is built from this.
  EOT
  type        = string
  default     = "vela"

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{1,9}$", var.name_prefix))
    error_message = "name_prefix must be 2-10 lowercase alphanumeric characters starting with a letter."
  }
}

variable "environment_name" {
  description = <<-EOT
    Environment discriminator used in resource names and tags, e.g. "prod" or "preview".
    This is a naming input, not a switch: there is one root module and one state file per
    environment, selected by which tfvars and which backend key you init with.
  EOT
  type        = string
  default     = "prod"

  validation {
    condition     = can(regex("^[a-z0-9]{2,10}$", var.environment_name))
    error_message = "environment_name must be 2-10 lowercase alphanumeric characters."
  }
}

# ---------------------------------------------------------------------------------------
# GitHub identity — the OIDC trust
# ---------------------------------------------------------------------------------------
#
# These four values compose the federated credential subject. They are separate variables
# rather than one pasted string on purpose: the July-2026 immutable subject format is easy
# to get subtly wrong, and a wrong subject fails at token exchange with an error that never
# mentions the subject. Composing it in locals.tf from named parts makes the shape reviewable.

variable "github_owner" {
  description = "GitHub account or org that owns the repository, e.g. \"jdoan5\"."
  type        = string
  default     = "jdoan5"
}

variable "github_owner_id" {
  description = <<-EOT
    Numeric GitHub owner ID, e.g. "30330279". This is the immutable half of the subject and
    is NOT the login name. Confirm with:
      curl -s https://api.github.com/users/<owner> | jq .id
    The value below was confirmed against the GitHub API for jdoan5.
  EOT
  type        = string
  default     = "30330279"
}

variable "github_repository" {
  description = "Repository name without the owner, e.g. \"vela-commerce\"."
  type        = string
  default     = "vela-commerce"
}

variable "github_repository_id" {
  description = <<-EOT
    Numeric GitHub repository ID, e.g. "1355259325". Confirm with:
      curl -s https://api.github.com/repos/<owner>/<repo> | jq .id
    The value below was confirmed against the GitHub API for jdoan5/vela-commerce.
  EOT
  type        = string
  default     = "1355259325"
}

variable "github_deploy_environment" {
  description = <<-EOT
    Name of the GitHub Environment the deploy job runs in, e.g. "production". Environment
    scoping is the control that stops a pull request from a fork minting a deploy token,
    because a fork PR cannot select a protected environment.

    Changing this changes the federated credential subject, and the subject must match the
    token exactly. See the loud warning above the environment-scoped credential in
    identity.tf: that subject string is UNVERIFIED.
  EOT
  type        = string
  default     = "production"
}

variable "create_branch_scoped_federated_credential" {
  description = <<-EOT
    Whether to also trust a token minted by a workflow running on the main branch OUTSIDE
    any GitHub Environment. That is the only subject anyone has read off a real token, so
    it exists as the escape hatch for confirming the environment-scoped one.

    THE SAFE END STATE IS `false`, AND IT IS NOT OPTIONAL. While this credential exists the
    trusted subject is `...:ref:refs/heads/main`, which ANY workflow on main can present —
    including oidc-claims.yml, which already sits there with `id-token: write`. A two-line
    edit to any workflow on main mints a deploy token declaring no `environment:`, so the
    production environment's required reviewer becomes a convention rather than a control.

    It defaults to false so the weak credential is a deliberate, temporary act rather than
    something that exists from the first apply and is never revisited. Set it true only for
    as long as it takes to run the probe inside the production environment and confirm the
    environment-scoped subject, then set it back and re-apply.
  EOT
  type        = bool
  default     = false
}

# ---------------------------------------------------------------------------------------
# The container image
# ---------------------------------------------------------------------------------------

variable "container_image" {
  description = <<-EOT
    Fully qualified image reference for the initial revision, e.g.
    "ghcr.io/jdoan5/vela-commerce:sha-abc1234" or, better, pinned by digest:
    "ghcr.io/jdoan5/vela-commerce@sha256:<64 hex>".

    This value is only ever used to create the FIRST revision. Terraform ignores it
    afterwards (lifecycle.ignore_changes in container_app.tf) because the deploy pipeline
    owns the image, by digest. So the default below is a placeholder that will not resolve;
    override it on the first apply with a tag your CI has actually pushed.
  EOT
  type        = string
  default     = "ghcr.io/jdoan5/vela-commerce:main"
}

variable "ghcr_username" {
  description = <<-EOT
    GitHub username used to pull from ghcr.io, e.g. "jdoan5". Leave null — the default —
    when the package is PUBLIC, which it is for this repo: Container Apps pulls a public
    GHCR image anonymously and adding a credential buys nothing but a token to rotate.

    Set it (with ghcr_token) only if the package is made private. Both must be set together.
  EOT
  type        = string
  default     = null
}

variable "ghcr_token" {
  description = <<-EOT
    GitHub token with read:packages, used only when the GHCR package is private. A
    fine-grained PAT scoped to this one package, or a classic PAT with read:packages.
    Leave null when the package is public.
  EOT
  type        = string
  default     = null
  sensitive   = true
}

# ---------------------------------------------------------------------------------------
# Application secrets
# ---------------------------------------------------------------------------------------
#
# READ THIS BEFORE SETTING EITHER OF THESE.
#
# Terraform writes every value it manages into state, including values marked sensitive —
# `sensitive` suppresses console output, it does not encrypt state. So a real connection
# string passed through here lands in the state blob in plaintext (encrypted at rest by
# Azure Storage, readable by anyone with data-plane access to the container).
#
# The design here keeps them OUT of state: Terraform declares the secret NAMES with the
# placeholder defaults below, and container_app.tf ignores subsequent changes to the secret
# set, so the real values are written once by
#
#   az containerapp secret set --name <app> --resource-group <rg> \
#     --secrets vela-db-connection="..." payment-signing-secret="..."
#
# and are never reverted by a later apply. That is the mechanism behind the "the pipeline
# holds zero cloud secrets and Terraform state holds zero app secrets" claim.
#
# You CAN override these variables to set real values from Terraform instead. If you do,
# understand that you have moved your production database credential into the state file,
# and stop claiming otherwise in the README.

variable "database_connection_string" {
  description = <<-EOT
    Npgsql connection string for the Neon POOLED endpoint, e.g.
    "Host=ep-xxx-pooler.us-east-1.aws.neon.tech;Database=vela;Username=vela;Password=...;SSL Mode=Require;No Reset On Close=true".

    Use the -pooler host for the app (the direct host is for migrations and pg_dump), and
    keep "No Reset On Close=true" — PgBouncer in transaction mode rejects the reset Npgsql
    otherwise issues.

    Leave at the placeholder and set the real value with `az containerapp secret set`.
  EOT
  type        = string
  default     = "PLACEHOLDER-set-with-az-containerapp-secret-set"
  sensitive   = true
}

variable "payment_signing_secret" {
  description = <<-EOT
    HMAC-SHA256 key for the payment simulator's settlement signatures. Any high-entropy
    string of 32 characters or more; `openssl rand -base64 48` is fine. It must NOT be the
    committed development default — PaymentSimulatorOptions.Validate refuses to start
    outside Development if it is, which is the check that stops a public demo shipping a
    public signing key.

    Leave at the placeholder and set the real value with `az containerapp secret set`.
  EOT
  type        = string
  default     = "PLACEHOLDER-set-with-az-containerapp-secret-set"
  sensitive   = true
}

# ---------------------------------------------------------------------------------------
# Sizing and scale
# ---------------------------------------------------------------------------------------

variable "cpu" {
  description = <<-EOT
    vCPU per replica. 0.25 with 0.5Gi memory is the smallest valid Consumption combination
    and is what the free-grant arithmetic in README.md assumes. Container Apps only accepts
    CPU/memory in fixed pairs — 0.25/0.5Gi, 0.5/1.0Gi, 0.75/1.5Gi, 1.0/2.0Gi, and so on —
    so these two variables must move together.
  EOT
  type        = number
  default     = 0.25
}

variable "memory" {
  description = "Memory per replica, as a Container Apps quantity string, e.g. \"0.5Gi\". Must pair with cpu."
  type        = string
  default     = "0.5Gi"
}

variable "max_replicas" {
  description = <<-EOT
    Ceiling on concurrent replicas, e.g. 3. This is a spend guard, not a capacity plan: a
    scraper hitting the demo cannot cost more than max_replicas x per-replica burn. Three at
    0.25 vCPU can only consume the whole monthly free grant if held saturated for
    ~66 hours, which recruiter-level traffic will not do.

    Note the correctness angle too: the outbox dispatcher uses FOR UPDATE SKIP LOCKED and is
    safe above one replica, but the in-memory output cache is not shared, so above one
    replica the cache warms per instance.
  EOT
  type        = number
  default     = 3
}

# ---------------------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------------------

variable "create_log_analytics_workspace" {
  description = <<-EOT
    Whether to create a Log Analytics workspace and send container stdout/stderr to it.

    DEFAULT IS false, AND THAT IS THE COST DECISION. With no destination configured, the
    Container Apps environment collects logs to nowhere: the live stream
    (`az containerapp logs show --follow`) still works, the app's own OpenTelemetry export
    to Grafana Cloud Free still works, and Azure Monitor bills exactly $0.00 with no path to
    drift upward.

    Turning this on creates a PerGB2018 workspace. Azure Monitor Logs bills ingestion at
    roughly $2.30-2.76 per GB depending on region, against a 5 GB per billing account per
    month free allowance, plus retention beyond 31 days at about $0.12/GB/month. An
    uncapped workspace under a chatty Information-level ASP.NET Core app ingesting 10 GB in
    a month bills (10 - 5) x $2.76 = $13.80, or $165.60 a year, for logs nobody reads.

    That is why the workspace created here is capped (daily_quota_gb) and its retention is
    held inside the free 31 days — see observability.tf.
  EOT
  type        = bool
  default     = false
}

variable "log_analytics_daily_quota_gb" {
  description = <<-EOT
    Hard daily ingestion cap in GB when create_log_analytics_workspace is true, e.g. 0.15.
    0.15 GB/day is ~4.5 GB over a 31-day month, which stays inside the 5 GB/month free
    allowance with headroom. Ingestion stops for the rest of the UTC day when the cap is
    hit; it does not bill through it.

    Do not set this to -1 ("unlimited"). That is the setting that turns a $0 workspace into
    an unbounded bill.
  EOT
  type        = number
  default     = 0.15
}

# ---------------------------------------------------------------------------------------
# Data Protection key ring
# ---------------------------------------------------------------------------------------

variable "dataprotection_storage_account_name" {
  description = <<-EOT
    Globally unique Azure Storage account name for the ASP.NET Core Data Protection key
    ring, e.g. "stveladpk7f3q9". 3-24 characters, lowercase letters and digits only, and
    unique across all of Azure — append a random suffix.

    This is separate from the Terraform state storage account on purpose: state is a
    bootstrap artifact a human owns, this is application data Terraform owns, and they have
    different audiences on their RBAC.

    Cost: storage accounts have no standing fee. The key ring is a single XML blob of a few
    kilobytes at ~$0.018/GiB-month LRS hot, read once per replica start. Rounds to $0.00.
  EOT
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9]{3,24}$", var.dataprotection_storage_account_name))
    error_message = "Storage account names are 3-24 characters, lowercase letters and digits only."
  }
}

# ---------------------------------------------------------------------------------------
# Tags
# ---------------------------------------------------------------------------------------

variable "extra_tags" {
  description = "Additional tags merged onto every resource, e.g. { owner = \"jdoan\" }."
  type        = map(string)
  default     = {}
}
