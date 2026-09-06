# =========================================================================================
# THE DEPLOY IDENTITY — keyless OIDC from GitHub Actions to Azure
# =========================================================================================
#
# The property this buys: the repository holds ZERO Azure secrets. No client secret, no
# service principal password, no publish profile, nothing to rotate and nothing to leak in a
# log. GitHub Actions mints a short-lived OIDC token at job start, Entra swaps it for an
# Azure access token, and the token dies with the job.
#
# WHY A USER-ASSIGNED MANAGED IDENTITY AND NOT AN ENTRA APP REGISTRATION
# ----------------------------------------------------------------------
# Both work. Almost every tutorial shows an app registration + service principal, which
# needs the azuread provider, needs Application Administrator (or equivalent) in the tenant,
# and creates an object that lives in Entra rather than in a resource group — so
# `terraform destroy` on this root leaves it behind, and nothing in the Azure portal's
# resource view will ever show it to you again.
#
# A user-assigned managed identity is a first-class Azure resource in this resource group,
# supports federated identity credentials identically, needs only the azurerm provider, and
# needs no directory role to create. It also costs nothing — managed identities are free.

resource "azurerm_user_assigned_identity" "deploy" {
  name                = "id-${local.stem}-deploy"
  resource_group_name = azurerm_resource_group.vela.name
  location            = azurerm_resource_group.vela.location
  tags                = local.tags
}

# -----------------------------------------------------------------------------------------
# Federated credential 1 — GitHub Environment scoped. THE ONE THE DEPLOY SHOULD USE.
# -----------------------------------------------------------------------------------------
#
# #########################################################################################
# #                                                                                       #
# #   THE SUBJECT ON THIS CREDENTIAL WAS VERIFIED ON 2026-09-06, BEFORE THE FIRST DEPLOY.  #
# #                                                                                       #
# #   oidc-claims.yml was dispatched with `environment: production` and printed            #
# #   repo:jdoan5@30330279/vela-commerce@1355259325:environment:production — exactly the   #
# #   string this file composes. For a while this block said the opposite, in capitals,    #
# #   because the probe had only ever run outside an Environment and a documented shape    #
# #   is not a verified one.                                                               #
# #                                                                                       #
# #   RE-VERIFY AFTER ANYTHING THAT COULD CHANGE IT — a repo rename, a transfer to an      #
# #   org, a different environment name. Re-run .github/workflows/oidc-claims.yml from a   #
# #   job that declares `environment: production`, read the `sub` claim it prints, and     #
# #   make this string equal to it exactly. If it differs, the deploy fails at token       #
# #   exchange with AADSTS700213 / "No matching federated identity record found" — an      #
# #   error that names the application and the issuer and NEVER tells you the subject      #
# #   was wrong. Budget a day if you skip this step.                                       #
# #                                                                                       #
# #########################################################################################
#
# WHY ENVIRONMENT SCOPING AT ALL: a branch-scoped credential trusts anything that runs on
# main. An environment-scoped one trusts only a job that selected the `production`
# environment — and a workflow triggered by a pull request from a FORK cannot select a
# protected environment. That is the control that stops a drive-by PR against a public
# repository from minting a token that can touch this subscription.
resource "azurerm_federated_identity_credential" "github_environment" {
  name      = "github-${var.github_deploy_environment}"
  parent_id = azurerm_user_assigned_identity.deploy.id

  # Fixed for github.com. A GitHub Enterprise Server installation issues from its own host.
  issuer = local.github_oidc_issuer

  # Fixed. Entra workload identity federation accepts only this audience value, and the
  # oidc-claims.yml probe requests exactly it.
  audience = [local.azure_oidc_audience]

  # Composed in main.tf from named parts so the immutable format is legible.
  subject = local.github_subject_environment
}

# -----------------------------------------------------------------------------------------
# Federated credential 2 — main-branch scoped. VERIFIED, AND MEANT TO BE TEMPORARY.
# -----------------------------------------------------------------------------------------
#
# This is the subject the probe actually printed:
#
#     repo:jdoan5@30330279/vela-commerce@1355259325:ref:refs/heads/main
#
# It exists so the first deploy can work, and so oidc-claims.yml can be re-run to verify
# credential 1. Once that verification is done, set
# create_branch_scoped_federated_credential = false: while this credential exists, anything
# that can push to main can mint a deploy token without any environment protection rule in
# the way, which is most of what credential 1 was for.
resource "azurerm_federated_identity_credential" "github_main_branch" {
  count = var.create_branch_scoped_federated_credential ? 1 : 0

  name      = "github-main-branch"
  parent_id = azurerm_user_assigned_identity.deploy.id
  issuer    = local.github_oidc_issuer
  audience  = [local.azure_oidc_audience]
  subject   = local.github_subject_main_branch
}

# =========================================================================================
# WHAT THE DEPLOY IDENTITY IS ALLOWED TO DO
# =========================================================================================
#
# SCOPE: one resource. Not the subscription, not the resource group — the container app
# itself, `azurerm_container_app.vela.id`.
#
# WHY THAT IS ENOUGH: the deploy pipeline does not run Terraform. It builds an image, pushes
# it to GHCR, and then reads and rolls this one app:
#
#     az containerapp update        --name ca-vela-prod --resource-group rg-vela-prod \
#                                   --image ghcr.io/jdoan5/vela-commerce@sha256:...
#     az containerapp show          (before, and again to verify)
#     az containerapp revision show (to wait for Provisioned)
#     az containerapp logs show     (on failure, to attach the reason)
#
# Note what is NOT in that list: `az containerapp secret set`. The workflow deliberately does
# not set secrets - they are placed by a human, once, out of band, so no live secret ever
# passes through a pipeline or through Terraform state.
#
# Both are operations on that single resource. The obvious choice, "Container Apps
# Contributor", DOES NOT WORK — see the role definition below for why, verified against the
# live tenant. A token stolen out of a compromised workflow can
# roll this one app to a different image. It cannot create resources, cannot read the
# storage account, cannot touch the identity that granted it, and cannot see anything else
# in the subscription.
#
# IT CAN, HOWEVER, READ THIS APP'S SECRET VALUES. `containerApps/listSecrets/action` is
# granted below, and the app carries `vela-db-connection` (the production Neon connection
# string) and `payment-signing-secret`. So the blast radius of a stolen deploy token is one
# app's image AND those two secrets — which is the whole of the data tier and the ability to
# forge a settlement. The action is granted because the CLI resolves the app's secret set on
# some `az containerapp update` paths; if it is to be dropped, drop it and prove a real
# image update still succeeds before believing it is unnecessary.
#
# WHAT THIS DELIBERATELY DOES NOT GRANT, and the consequence:
#   * `terraform apply` from CI. That would need Contributor on the resource group at
#     minimum (to create and modify the environment, the storage account and the app) and
#     User Access Administrator to manage these very role assignments. Granting that to a
#     token mintable from a public repository is a much larger bet than it looks. So
#     Terraform is applied BY A HUMAN, locally, with their own credentials — see README.md
#     § "First deploy". The pipeline only ever rolls revisions.
#   * Anything on the Log Analytics workspace, the storage account, or the resource group.
#
# IF THE PIPELINE FAILS WITH AuthorizationFailed: widen deliberately and narrowly. The most
# likely genuine gap is a read on the parent resource group; add a `Reader` assignment
# scoped to the resource group before reaching for Contributor. Do not "fix" it with
# Contributor at subscription scope, which is what every search result will suggest.

# WHY A CUSTOM ROLE AND NOT THE BUILT-IN "Container Apps Contributor".
#
# Because the built-in role cannot read or write a container app. Its actions are:
#
#     Microsoft.App/containerApps/*/read      Microsoft.App/managedEnvironments/read
#     Microsoft.App/containerApps/*/write     Microsoft.App/managedEnvironments/*/read
#     Microsoft.App/containerApps/*/delete
#     Microsoft.App/containerApps/*/action
#
# The `*` occupies the CHILD RESOURCE TYPE slot, so `containerApps/*/read` is a four-segment
# pattern and cannot match the three-segment operation `containerApps/read`. Note that the
# same role lists BOTH `managedEnvironments/read` and `managedEnvironments/*/read` — and then
# omits the plain form for containerApps. The built-in "ContainerApp Reader" lists both forms
# too. Microsoft's own role authors treat them as distinct, which settles it.
#
# Verified on this tenant with `az role definition list` and `az provider operation show`.
# The failure it causes is worse than an obvious one: `az containerapp show` returns
# AuthorizationFailed, the deploy workflow's guard step swallows it and reports that the app
# "was not found", and the operator is told to run a terraform apply they have already run.
#
# This role is not a subset of the built-in one, and saying so would contradict the argument
# directly above. It ADDS the plain `containerApps/read` and `containerApps/write` that the
# built-in's four-segment wildcards cannot match, and a `logstream` dataAction the built-in
# has no dataActions for at all; it OMITS every `*/delete`. Differently shaped, and smaller
# where it matters — not strictly narrower.
resource "azurerm_role_definition" "container_app_deployer" {
  name        = "vela-container-app-deployer"
  scope       = azurerm_resource_group.vela.id
  description = "Roll a new revision of one Container App. Nothing else."

  permissions {
    actions = [
      "Microsoft.App/containerApps/read",
      "Microsoft.App/containerApps/write",
      "Microsoft.App/containerApps/revisions/read",
      "Microsoft.App/containerApps/listSecrets/action",
    ]

    # The deploy workflow tails logs after a rollout to show what the new revision said.
    data_actions = ["Microsoft.App/containerApps/logstream/action"]
  }

  assignable_scopes = [azurerm_resource_group.vela.id]
}

resource "azurerm_role_assignment" "deploy_container_app" {
  scope              = azurerm_container_app.vela.id
  role_definition_id = azurerm_role_definition.container_app_deployer.role_definition_resource_id
  principal_id       = azurerm_user_assigned_identity.deploy.principal_id

  # Stated explicitly to skip the provider's principal-type lookup, which can fail on a
  # freshly created identity that has not finished replicating through Entra. Without it,
  # a first apply intermittently fails with "PrincipalNotFound" and succeeds on retry.
  principal_type = "ServicePrincipal"
}

# =========================================================================================
# WHAT THE RUNNING APP IS ALLOWED TO DO
# =========================================================================================
#
# Exactly one thing: read and write its own Data Protection key ring.
#
# SCOPE: the blob container, not the storage account. `.id` is correct here only because
# dataprotection.tf addresses the container by `storage_account_id`: that puts the resource
# on the ARM management plane, so its `id` is the ARM path
# (.../storageAccounts/<name>/blobServices/default/containers/<name>) that RBAC accepts as a
# scope. Address the container the old way — by `storage_account_name` — and `id` becomes
# the data-plane URL instead, and this assignment silently grants nothing.
#
# (`resource_manager_id` is the attribute that used to bridge that gap. It still exists but
# azurerm 4.81 emits a deprecation warning for it and removes it in 5.0. Do not reach for it.)
#
# ROLE: Storage Blob Data Contributor. The app must CREATE the key ring blob on first run
# and APPEND to it when a key rolls (every 90 days by default), so Reader is not enough.
# Blob Data Owner would additionally grant POSIX ACL and RBAC-assignment powers the app has
# no use for.
#
# Note this is a DATA-plane role. Owner and Contributor on the subscription do not grant
# blob data access; that separation is deliberate on Azure's part and is the same reason the
# human bootstrapping this needs an explicit grant (README.md § Bootstrap step 4).
resource "azurerm_role_assignment" "app_dataprotection_keys" {
  scope                = azurerm_storage_container.dataprotection_keys.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_container_app.vela.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}
