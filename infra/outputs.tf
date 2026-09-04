output "storefront_url" {
  description = "Public HTTPS URL of the demo. This is the link that goes on the CV."
  value       = "https://${azurerm_container_app.vela.ingress[0].fqdn}"
}

output "container_app_name" {
  description = "Container App name, for `az containerapp update --name`."
  value       = azurerm_container_app.vela.name
}

output "resource_group_name" {
  description = "Resource group name, for `az containerapp update --resource-group`."
  value       = azurerm_resource_group.vela.name
}

# -----------------------------------------------------------------------------------------
# The three values the deploy workflow needs as GitHub repository variables (NOT secrets —
# none of them is one; publishing a client ID grants nobody anything without a matching
# federated credential, which is the whole point of the design).
#
#   gh variable set AZURE_CLIENT_ID       --body "$(terraform output -raw deploy_client_id)"
#   gh variable set AZURE_TENANT_ID       --body "$(terraform output -raw tenant_id)"
#   gh variable set AZURE_SUBSCRIPTION_ID --body "$(terraform output -raw subscription_id)"
# -----------------------------------------------------------------------------------------

output "deploy_client_id" {
  description = "Client ID of the deploy managed identity. Pass as client-id to azure/login@v2."
  value       = azurerm_user_assigned_identity.deploy.client_id
}

output "tenant_id" {
  description = "Entra tenant ID. Pass as tenant-id to azure/login@v2."
  value       = azurerm_user_assigned_identity.deploy.tenant_id
}

output "subscription_id" {
  description = "Subscription ID. Pass as subscription-id to azure/login@v2."
  value       = var.subscription_id
}

# -----------------------------------------------------------------------------------------
# Verification aids
# -----------------------------------------------------------------------------------------

output "federated_credential_subjects" {
  description = <<-EOT
    The exact subject strings Entra will match against the incoming OIDC token. Compare
    these, character for character, against the `sub` claim printed by
    .github/workflows/oidc-claims.yml. The environment-scoped one is UNVERIFIED — see the
    warning in identity.tf.
  EOT
  value = {
    environment_scoped_UNVERIFIED = local.github_subject_environment
    main_branch_scoped_verified   = var.create_branch_scoped_federated_credential ? local.github_subject_main_branch : null
  }
}

output "dataprotection_blob_uri" {
  description = <<-EOT
    Blob URI for the ASP.NET Core Data Protection key ring. Program.cs reads it from the
    VELA_DATAPROTECTION_BLOB_URI environment variable that container_app.tf sets, and
    persists the ring here through the app's managed identity. See the header comment in
    dataprotection.tf for why the ring must outlive the container.
  EOT
  value       = "${azurerm_storage_account.dataprotection.primary_blob_endpoint}${azurerm_storage_container.dataprotection_keys.name}/keys.xml"
}

output "log_analytics_note" {
  description = "One-line statement of what logging costs in this deployment."
  value = var.create_log_analytics_workspace ? format(
    "Log Analytics workspace log-%s is ATTACHED, capped at %s GB/day (~%.1f GB/month) with 30-day retention. Inside the 5 GB/month free allowance; costs $0.00 unless the cap is raised.",
    local.stem,
    tostring(var.log_analytics_daily_quota_gb),
    var.log_analytics_daily_quota_gb * 31,
  ) : "No Log Analytics workspace. Environment log destination is 'none'. Azure Monitor cost: $0.00. Logs are the live stream plus the app's OTLP export."
}
