# ---------------------------------------------------------------------------------------
# Log Analytics — OFF BY DEFAULT, AND THAT IS THE CHOICE
# ---------------------------------------------------------------------------------------
#
# A Container Apps environment is usually shown with a Log Analytics workspace attached, and
# the portal wizard creates one for you without asking. It is also the only resource in this
# whole design that meters on a quantity the application controls: bytes of log text.
#
# WHAT WAS CHOSEN: no workspace. The environment in container_app.tf leaves both
# `logs_destination` and `log_analytics_workspace_id` unset, which makes the environment's
# log destination "none".
#
# WHAT THAT COSTS: $0.00/month, with no path to drift upward, because there is no meter.
#
# WHAT IT COSTS YOU IN CAPABILITY: no queryable log history in Azure. What still works:
#   * `az containerapp logs show --name <app> --resource-group <rg> --follow` — the live
#     stream, which is what you actually use when debugging a revision that will not start.
#   * The application's own OpenTelemetry export to Grafana Cloud Free (10k series /
#     50 GB logs / 50 GB traces / 14 days, no expiry), which docs/PLAN.md already chose as
#     the observability backend. Log history lives there, not here.
#   * Container Apps system logs in the portal for the last short window.
#
# THE ALTERNATIVE, AND ITS NUMBERS: flip create_log_analytics_workspace to true and you get
# the workspace below. Azure Monitor Logs bills Analytics ingestion at roughly $2.30-$2.76
# per GB by region, against a 5 GB per billing account per month free allowance, plus
# retention past 31 days at about $0.12/GB/month. The failure mode is not exotic: ASP.NET
# Core at Information level under a scraper, or one EF Core command-logging misconfiguration,
# is tens of megabytes an hour. 10 GB in a month bills (10 - 5) x $2.76 = $13.80, i.e.
# $165.60/year, for logs nobody opens. Hence the two guardrails below, which are the whole
# reason this resource is not just a three-line default.

resource "azurerm_log_analytics_workspace" "vela" {
  count = var.create_log_analytics_workspace ? 1 : 0

  name                = "log-${local.stem}"
  resource_group_name = azurerm_resource_group.vela.name
  location            = azurerm_resource_group.vela.location

  # PerGB2018 is the only generally available SKU. The legacy "Free" SKU was retired and
  # cannot be selected on a new workspace, so "just use the free tier" is not an option here
  # — the cap below is the free tier.
  sku = "PerGB2018"

  # GUARDRAIL 1 — the hard stop. Ingestion halts for the remainder of the UTC day once this
  # is exceeded; it does not bill past it. 0.15 GB/day is ~4.5 GB over a 31-day month, which
  # stays inside the 5 GB/month free allowance with headroom for a bad day.
  #
  # DO NOT set this to -1. -1 means unlimited, it is the provider's default, and it is the
  # single edit that converts this from a $0 resource into an unbounded one.
  daily_quota_gb = var.log_analytics_daily_quota_gb

  # GUARDRAIL 2 — retention. The first 31 days of retention are included at no charge; past
  # that it is about $0.12/GB/month, forever, on data that is only growing. 30 is the
  # provider minimum for PerGB2018 and is inside the free window.
  #
  # DO NOT raise this to 90 or 730 "for compliance". This is a portfolio demo with no
  # retention obligation, and the bill is monthly and permanent.
  retention_in_days = 30

  # Ingestion from the public internet is how Container Apps delivers logs. Private link
  # scoping here would require a private endpoint, and a private endpoint on this
  # architecture is one of the two triggers for the ~$73/month Dedicated Plan Management
  # charge. See the warning in container_app.tf.
  internet_ingestion_enabled = true
  internet_query_enabled     = true

  tags = local.tags
}
