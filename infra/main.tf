locals {
  # One name stem so every resource is greppable in the portal and in a bill.
  stem = "${var.name_prefix}-${var.environment_name}"

  tags = merge(
    {
      application = "vela-commerce"
      environment = var.environment_name
      managed_by  = "terraform"
      repository  = "github.com/${var.github_owner}/${var.github_repository}"
      # Named so that anyone opening Cost Analysis knows the intent before they read this file.
      cost_intent = "free-grant-only"
    },
    var.extra_tags,
  )

  # ---------------------------------------------------------------------------------------
  # THE GITHUB OIDC SUBJECT. GET THIS WRONG AND NOTHING TELLS YOU.
  # ---------------------------------------------------------------------------------------
  # Since July 2026 GitHub mints the `sub` claim in an IMMUTABLE form built from numeric IDs,
  # not from names:
  #
  #     repo:<owner>@<owner_id>/<repo>@<repo_id>:<scope>
  #
  # Every tutorial, and most vendor documentation, still shows the old
  # `repo:OWNER/NAME:ref:refs/heads/main`. That string no longer matches anything. When it
  # does not match, Entra rejects the token exchange with AADSTS700213 / "No matching
  # federated identity record found" — which names the app and the issuer and never tells
  # you the subject was the problem. People lose days here.
  #
  # The prefix below is not copied from documentation. It was read off a real token by
  # running .github/workflows/oidc-claims.yml in this repository, and both numeric IDs were
  # then confirmed against the GitHub REST API. If you fork this repo, YOUR IDs are
  # different — run the probe, do not edit the names and hope.
  github_subject_prefix = "repo:${var.github_owner}@${var.github_owner_id}/${var.github_repository}@${var.github_repository_id}"

  # VERIFIED. Printed by the probe. A workflow running on main, outside any Environment.
  github_subject_main_branch = "${local.github_subject_prefix}:ref:refs/heads/main"

  # Verified against a real token on 2026-09-06. See the block in identity.tf for how, and
  # for when it must be re-checked.
  github_subject_environment = "${local.github_subject_prefix}:environment:${var.github_deploy_environment}"

  # GitHub's OIDC issuer. Fixed for github.com; a GitHub Enterprise Server install differs.
  github_oidc_issuer = "https://token.actions.githubusercontent.com"

  # The audience Entra requires for workload identity federation. This is not a URL that
  # resolves; it is a fixed identifier, and `api://AzureADTokenExchange` is the only value
  # Azure accepts. The oidc-claims.yml probe defaults to requesting exactly this.
  azure_oidc_audience = "api://AzureADTokenExchange"

  # The port the container listens on. The .NET 10 ASP.NET Core base image
  # (mcr.microsoft.com/dotnet/aspnet:10.0, which `dotnet publish /t:PublishContainer` uses)
  # sets ASPNETCORE_HTTP_PORTS=8080 and EXPOSEs it. container_app.tf re-states it as an
  # explicit env var so the ingress port and the listen port cannot drift apart in silence
  # — a mismatch presents as an ingress that never becomes ready, with a healthy container
  # in the logs.
  container_port = 8080
}

# ---------------------------------------------------------------------------------------
# Resource group
# ---------------------------------------------------------------------------------------
#
# Everything this root creates lives here, which makes the teardown story one command and
# makes the deploy identity's blast radius describable in one sentence.
#
# NOTE: the Terraform state storage account is deliberately NOT in this group. It is
# bootstrapped by hand into rg-vela-tfstate (see README.md) precisely so that
# `terraform destroy` on this root cannot delete the state that records what it destroyed.
resource "azurerm_resource_group" "vela" {
  name     = "rg-${local.stem}"
  location = var.location
  tags     = local.tags
}
