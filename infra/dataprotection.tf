# ---------------------------------------------------------------------------------------
# ASP.NET Core Data Protection key ring
# ---------------------------------------------------------------------------------------
#
# THE PROBLEM THIS SOLVES, because it is easy to not notice you have it.
#
# Program.cs currently calls `builder.Services.AddDataProtection()` with no persistence
# configured. The default key repository is the filesystem — ~/.aspnet/DataProtection-Keys
# — which is correct locally and wrong in a container two ways at once:
#
#   1. The container filesystem is ephemeral. Every deploy generates a brand-new key ring.
#   2. With scale-to-zero, "every deploy" also means "every cold start after an idle
#      window", and above one replica each instance generates its own ring.
#
# The symptom is not an error. It is that the demo-session cookie — which is what carries a
# visitor's cart, and what signs the order-retrieval link — silently stops decrypting.
# Every visitor is a new visitor. Their cart is empty. The "view your order" link they were
# emailed 404s. Nothing logs a failure, because "this cookie did not decrypt" is
# indistinguishable from "this visitor has no cookie", and the app is designed to treat an
# unknown visitor as a fresh one rather than an error.
#
# THE FIX: persist the key ring to a blob outside the container, and let every replica of
# every revision read the same one.
#
# WHAT IT COSTS: $0.00, to the nearest cent, and this is the reason blob storage was chosen
# over the alternatives. Storage accounts carry no standing fee. The key ring is one XML
# blob of a few kilobytes; at ~$0.018 per GiB-month LRS hot that is a rounding error, and
# the transaction count is a handful per replica start at ~$0.005 per 10,000 reads.
#
# WHAT WAS REJECTED, and why:
#   * Azure Files mounted into the container — works, but pulls in a storage mount on the
#     Container Apps environment and bills on provisioned share capacity. More moving parts
#     for the same $0-ish.
#   * Azure Key Vault as the key repository — Key Vault standard also has no standing fee
#     ($0.03 per 10,000 operations), so it is not a cost objection: it is a scope one. It
#     would add a vault, an RBAC grant and a second thing to bootstrap for a demo whose
#     threat model is "a public storefront with imaginary money".
#   * Storing the ring in Neon Postgres via a custom IXmlRepository — genuinely free and
#     genuinely appealing, and rejected because it makes session decryption depend on the
#     database being awake at the exact moment the app starts, which is the one thing this
#     design works hardest to avoid.
#
# THE UPGRADE, named so nobody has to invent it: blobs are encrypted at rest by Azure
# Storage SSE, but the key ring itself is stored unencrypted within that blob, so anyone
# with data-plane read on this container can forge a session cookie. If that ever matters,
# add `.ProtectKeysToAzureKeyVault(...)` in Program.cs alongside the blob repository. That
# is envelope encryption, it costs pennies, and it does not change anything in this file.
#
# ############################################################################
# # NOT WIRED UP YET. Program.cs calls AddDataProtection() with no arguments. #
# # The env var this file exports (VELA_DATAPROTECTION_BLOB_URI) is read by   #
# # nothing today. Creating the blob is necessary and not sufficient — the    #
# # application change is:                                                    #
# #                                                                           #
# #   builder.Services.AddDataProtection()                                    #
# #       .SetApplicationName("vela-commerce")                                #
# #       .PersistKeysToAzureBlobStorage(                                     #
# #           new Uri(blobUri), new DefaultAzureCredential());                #
# #                                                                           #
# # (package Azure.Extensions.AspNetCore.DataProtection.Blobs, plus           #
# # Azure.Identity). SetApplicationName matters as much as the blob: without  #
# # it the ring is namespaced by the entry assembly name and a rename         #
# # invalidates every cookie again.                                           #
# ############################################################################

resource "azurerm_storage_account" "dataprotection" {
  name                = var.dataprotection_storage_account_name
  resource_group_name = azurerm_resource_group.vela.name
  location            = azurerm_resource_group.vela.location

  # Standard/LRS/Hot is both the cheapest and the correct pairing for a few-KB blob read on
  # every cold start. Do not "save money" with Cool or Cold access tiers: they carry early
  # deletion penalties and higher per-read transaction costs, and there is no capacity here
  # to save on. Do not use GRS/ZRS — geo-redundancy roughly doubles the per-GB rate to
  # protect a file that can be regenerated by restarting the app.
  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  access_tier              = "Hot"

  # Security posture. The key ring is, functionally, the signing key for every session.
  https_traffic_only_enabled      = true
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  public_network_access_enabled   = true

  # No shared account keys: access is Entra-only, so the grant in identity.tf is the whole
  # access story and there is no connection string to leak. This is what `storage_use_azuread`
  # in providers.tf exists to support — and it is why the human applying this needs the
  # Storage Blob Data Contributor role, not merely Owner. See README.md § Bootstrap step 4.
  shared_access_key_enabled = false

  # DO NOT add a network_rules block restricting this to a VNet, and DO NOT add a private
  # endpoint. A private endpoint here is one of the two triggers for the ~$73/month
  # Dedicated Plan Management charge on the Container Apps side, and the container has no
  # VNet to come from anyway (see the infrastructure_subnet_id warning in container_app.tf).
  # Access is already restricted: Entra RBAC, one role assignment, one container.

  blob_properties {
    # Versioning and soft delete on a key ring are a trap: a resurrected old key ring is a
    # security event, not a recovery. Keep a short delete window for fat-finger protection
    # only. Seven days of a few-KB blob costs nothing.
    delete_retention_policy {
      days = 7
    }
  }

  tags = local.tags
}

resource "azurerm_storage_container" "dataprotection_keys" {
  name = "dataprotection-keys"

  # Addressed by resource ID rather than by account name, which routes container creation
  # through the ARM management plane instead of the blob data plane. That is what lets this
  # work against an account with shared_access_key_enabled = false.
  storage_account_id = azurerm_storage_account.dataprotection.id

  # The default, restated because the consequence of getting it wrong is the session signing
  # key being world-readable over anonymous HTTP.
  container_access_type = "private"
}
