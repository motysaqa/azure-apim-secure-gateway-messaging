metadata description = 'Key Vault holding the one secret that a managed identity cannot replace.'

param vaultName string
param location string
param tags object

@secure()
param backendKey string

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId

    // RBAC rather than access policies. Access policies are per-vault ACLs that
    // drift; RBAC role assignments are visible in the same place as every other
    // permission in the subscription and can be audited centrally.
    enableRbacAuthorization: true

    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    // Purge protection is deliberately off for a portfolio deployment: with it
    // on, the vault name is unusable for 90 days after a delete. Turn it on for
    // anything real.
    enablePurgeProtection: null

    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

// The only secret in the system. Everything else - Service Bus, storage - is
// reached with a managed identity, so there is nothing else to store.
resource backendKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'backend-key'
  properties: {
    value: backendKey
    contentType: 'text/plain'
    attributes: {
      enabled: true
    }
  }
}

output vaultName string = vault.name
output vaultUri string = vault.properties.vaultUri
output backendKeySecretUri string = backendKeySecret.properties.secretUri
output backendKeySecretUriWithoutVersion string = '${vault.properties.vaultUri}secrets/backend-key/'
