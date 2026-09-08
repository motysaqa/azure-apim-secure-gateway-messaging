metadata description = 'API Management, its backend, named values and products. The API itself is imported by scripts/Import-ApimApi.ps1.'

param serviceName string
param location string
param tags object

@allowed(['Consumption', 'Developer', 'Basic'])
param sku string

param publisherEmail string
param publisherName string
param backendUrl string
param keyVaultName string
param backendKeySecretUri string
param entraTenantId string = ''
param entraApiApplicationId string = ''

// Key Vault Secrets User
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

// Consumption is serverless and has no fixed capacity; every other tier is
// billed per unit whether or not a request arrives.
var capacity = sku == 'Consumption' ? 0 : 1

resource apim 'Microsoft.ApiManagement/service@2023-05-01-preview' = {
  name: serviceName
  location: location
  tags: tags
  sku: {
    name: sku
    capacity: capacity
  }
  identity: {
    // Needed before the Key Vault-backed named value below can resolve.
    type: 'SystemAssigned'
  }
  properties: {
    publisherEmail: publisherEmail
    publisherName: publisherName
    customProperties: sku == 'Consumption' ? {} : {
      // Old TLS and the RC4/3DES ciphers, off explicitly. Several are still
      // enabled by default on the non-Consumption tiers.
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Tls10': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Tls11': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Ssl30': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Ciphers.TripleDes168': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Protocols.Server.Http2': 'True'
    }
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

// APIM reads the backend key from Key Vault so the same secret is used by the
// gateway that injects it and the app that checks it, with one place to rotate.
resource secretsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, apim.id, keyVaultSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: apim.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource backendKeyNamedValue 'Microsoft.ApiManagement/service/namedValues@2023-05-01-preview' = {
  parent: apim
  name: 'backend-key'
  properties: {
    displayName: 'backend-key'
    secret: true
    keyVault: {
      // No version in the URI: rotating the secret in Key Vault propagates here
      // within four hours without a redeployment.
      secretIdentifier: replace(backendKeySecretUri, '/${last(split(backendKeySecretUri, '/'))}', '')
    }
  }
  dependsOn: [
    secretsUserAssignment
  ]
}

// Tenant and audience live as named values so the policy XML in apim/policies/
// stays environment-agnostic and can be committed as-is.
resource tenantIdNamedValue 'Microsoft.ApiManagement/service/namedValues@2023-05-01-preview' = if (!empty(entraTenantId)) {
  parent: apim
  name: 'entra-tenant-id'
  properties: {
    displayName: 'entra-tenant-id'
    value: entraTenantId
    secret: false
  }
}

resource audienceNamedValue 'Microsoft.ApiManagement/service/namedValues@2023-05-01-preview' = if (!empty(entraApiApplicationId)) {
  parent: apim
  name: 'entra-api-audience'
  properties: {
    displayName: 'entra-api-audience'
    value: 'api://${entraApiApplicationId}'
    secret: false
  }
}

resource bookingBackend 'Microsoft.ApiManagement/service/backends@2023-05-01-preview' = {
  parent: apim
  name: 'booking-api'
  properties: {
    title: 'Booking API (App Service)'
    protocol: 'http'
    url: backendUrl
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
    credentials: {
      header: {
        // Injected on every forwarded request. Combined with the App Service
        // IP restriction, reaching the origin directly is not enough.
        'X-Backend-Key': ['{{backend-key}}']
      }
    }
  }
  dependsOn: [
    backendKeyNamedValue
  ]
}

// Two products, two SLAs. Both require a subscription, so an unapproved caller
// never reaches the gateway policies at all.
resource starterProduct 'Microsoft.ApiManagement/service/products@2023-05-01-preview' = {
  parent: apim
  name: 'booking-starter'
  properties: {
    displayName: 'Booking API - Starter'
    description: 'Evaluation tier. 20 calls per minute, approved automatically.'
    subscriptionRequired: true
    approvalRequired: false
    subscriptionsLimit: 100
    state: 'published'
  }
}

resource premiumProduct 'Microsoft.ApiManagement/service/products@2023-05-01-preview' = {
  parent: apim
  name: 'booking-premium'
  properties: {
    displayName: 'Booking API - Premium'
    description: 'Production tier. 600 calls per minute, manually approved.'
    subscriptionRequired: true
    // Manual approval is the point of the tier: someone signs off before a
    // partner gets production throughput.
    approvalRequired: true
    subscriptionsLimit: 25
    state: 'published'
  }
}

output serviceName string = apim.name
output gatewayUrl string = apim.properties.gatewayUrl
output developerPortalUrl string = sku == 'Consumption' ? '' : apim.properties.developerPortalUrl
output principalId string = apim.identity.principalId
output backendName string = bookingBackend.name
output starterProductName string = starterProduct.name
output premiumProductName string = premiumProduct.name
