# Terraform and provider versions.
#
# WHY ONE ROOT MODULE AND NOT modules/ + envs/production + envs/preview
# ---------------------------------------------------------------------
# The plan in docs/PLAN.md sketches reusable modules under infra/modules/ consumed by two
# thin roots. That is the right shape for two environments. Today there is one, and a module
# with exactly one caller is not an abstraction — it is a directory that has to be opened
# twice to read one resource. This root is ~250 lines of HCL a reviewer can read top to
# bottom and believe.
#
# The other split that was considered and rejected: putting the Container Apps *environment*
# in its own state, separate from the container *app*. The argument for it is churn — the
# environment is created once, the app changes on every deploy. That argument does not hold
# here, because the deploy pipeline does not run Terraform at all: it pushes a new revision
# by digest with `az containerapp update`, and Terraform ignores the image (see
# container_app.tf). So the app's Terraform is no more churny than the environment's, and
# splitting them would buy a cross-state data source and a second `terraform apply` to
# maintain, for nothing.
#
# The split that IS real is bootstrap versus this root — see the backend block below and
# README.md. That one is a genuine chicken-and-egg and is handled by hand, on purpose.

terraform {
  # Pinned to the version actually installed and used here. Terraform state is
  # forward-only: a newer minor writes a state file an older binary refuses to read, so
  # letting this float would let one contributor's laptop lock everyone else out.
  required_version = "~> 1.16"

  required_providers {
    azurerm = {
      source = "hashicorp/azurerm"
      # Major pinned here; the exact patch is pinned by the committed .terraform.lock.hcl,
      # which is the file that actually makes builds reproducible. Do not widen to ">= 4.0":
      # azurerm ships breaking changes in majors and the 3.x -> 4.x jump alone changed
      # provider block requirements, storage container addressing and several defaults.
      version = "~> 4.0"
    }
  }

  # ---------------------------------------------------------------------------------------
  # REMOTE STATE — DELIBERATELY COMMENTED OUT. THIS IS NOT SOLVED, AND PRETENDING IT IS
  # WOULD BE THE LIE.
  # ---------------------------------------------------------------------------------------
  # Remote state lives in an Azure Storage container. That storage account has to exist
  # before `terraform init` can talk to it, and Terraform cannot create it, because creating
  # it needs a state file and the state file needs it. Every "just bootstrap it with
  # Terraform and then migrate" recipe is that same loop with an extra local state file in
  # the middle.
  #
  # So a human creates it once, by hand, with three `az` commands. README.md § "Bootstrap"
  # has them verbatim.
  #
  # It stays commented out in the repository for a second, separate reason: a backend block
  # cannot interpolate variables or locals — it is read before the graph is built. So the
  # values would have to be literals, and a storage account name is globally unique across
  # all of Azure. Committing one both publishes this account's name and guarantees that
  # every fork of this public repo fails `terraform init` against a storage account it does
  # not own.
  #
  # After bootstrap, uncomment the empty block below and pass the values at init time:
  #
  #   terraform init \
  #     -backend-config="resource_group_name=rg-vela-tfstate" \
  #     -backend-config="storage_account_name=<YOUR GLOBALLY UNIQUE NAME>" \
  #     -backend-config="container_name=tfstate" \
  #     -backend-config="key=production.tfstate" \
  #     -backend-config="use_azuread_auth=true"
  #
  # Locking needs nothing extra: the azurerm backend takes a blob lease, so the one storage
  # account is the whole state infrastructure. No DynamoDB-equivalent, no second resource.
  #
  # Cost of that storage account: no standing fee, LRS hot at ~$0.018/GiB-month, and the
  # state file is tens of kilobytes. Under one cent a month.
  #
  # backend "azurerm" {}
  # ---------------------------------------------------------------------------------------
}
