provider "azurerm" {
  # Required from azurerm 4.0 onward. Supply it as a variable rather than relying on
  # whatever `az account set` last pointed at — a plan that silently retargets a different
  # subscription is the worst possible failure mode for infrastructure code.
  # ARM_SUBSCRIPTION_ID in the environment works too and is what the pipeline would use.
  subscription_id = var.subscription_id

  # Data-plane calls (creating the blob container for the Data Protection key ring) go over
  # Entra ID rather than a shared storage account key. This is what lets
  # `shared_access_key_enabled = false` on the storage account in dataprotection.tf be true
  # instead of aspirational.
  #
  # Consequence for whoever applies this: Owner or Contributor on the subscription is NOT
  # sufficient for data-plane blob operations. See README.md § "Bootstrap" step 4.
  storage_use_azuread = true

  # Container Apps lives under the Microsoft.App resource provider, which is NOT registered
  # on a fresh subscription (it was NotRegistered on this one until the 2026-09-06 apply
  # registered it). azurerm 4.x registers only a "core" set by default and
  # Microsoft.App is not in it, so without this the first apply fails on a
  # MissingSubscriptionRegistration that names the provider but not the fix.
  #
  # Registration is free, subscription-scoped, and idempotent. It creates no resource and
  # bills nothing.
  resource_provider_registrations = "core"
  resource_providers_to_register = [
    "Microsoft.App",
    "Microsoft.OperationalInsights", # required by Container Apps environments even when logs are off
  ]

  features {
    resource_group {
      # Fail loudly rather than silently deleting resources this root does not manage.
      # A demo resource group is exactly where someone drops a hand-made resource.
      prevent_deletion_if_contains_resources = true
    }
  }
}
