targetScope = 'subscription'

metadata description = '''
Booking platform infrastructure: API Management in front of an App Service-hosted
.NET API, with Service Bus for asynchronous processing and Key Vault for the one
secret that cannot be replaced by a managed identity.

Everything defaults to the cheapest SKU that still demonstrates the pattern:
APIM Consumption, App Service B1, Service Bus Basic, Key Vault Standard. See
docs/cost.md for what that actually costs and what each upgrade buys.
'''

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('Short name used to derive every resource name. Lower case letters and digits only.')
@minLength(3)
@maxLength(11)
param namePrefix string

@description('Azure region for every resource.')
param location string = 'westeurope'

@description('Environment discriminator; becomes part of resource names and tags.')
@allowed(['dev', 'test', 'prod'])
param environment string = 'dev'

@description('App Service plan SKU. F1 is free but has no Always On, so the outbox publisher is suspended when the app idles - use B1 for anything you intend to watch working.')
@allowed(['F1', 'B1', 'S1'])
param appServicePlanSku string = 'B1'

@description('Service Bus SKU. Basic supports queues and dead-lettering. Duplicate detection and topics need Standard; the worker deduplicates in its own store either way.')
@allowed(['Basic', 'Standard'])
param serviceBusSku string = 'Basic'

@description('API Management SKU. Consumption is serverless and pay-per-call; Developer costs roughly USD 50/month whether or not it is used.')
@allowed(['Consumption', 'Developer', 'Basic'])
param apimSku string = 'Consumption'

@description('Publisher email shown on the API Management developer portal.')
param apimPublisherEmail string

@description('Publisher organisation shown on the API Management developer portal.')
param apimPublisherName string = 'Booking Platform'

@description('Entra ID tenant id used by the validate-jwt policy. Leave empty to deploy without JWT validation configured.')
param entraTenantId string = ''

@description('Entra ID application (client) id of the API app registration; becomes the expected audience.')
param entraApiApplicationId string = ''

@description('Shared key APIM injects as X-Backend-Key so the App Service can reject traffic that bypassed the gateway. Generate a random value; it is stored only in Key Vault.')
@secure()
param backendKey string

@description('Deploy Log Analytics and Application Insights. Off by default because ingestion is the one component here that is billed by volume.')
param deployMonitoring bool = false

@description('Additional tags applied to every resource.')
param tags object = {}

// ---------------------------------------------------------------------------
// Naming
// ---------------------------------------------------------------------------

// A subscription-stable suffix keeps globally-unique names (Key Vault, Service
// Bus, App Service) unique without hard-coding anything.
var suffix = substring(uniqueString(subscription().subscriptionId, namePrefix, environment), 0, 6)

var resourceGroupName = 'rg-${namePrefix}-${environment}'

var defaultTags = union(tags, {
  application: 'booking-platform'
  environment: environment
  managedBy: 'bicep'
  repository: 'azure-apim-secure-gateway-messaging'
})

// ---------------------------------------------------------------------------
// Resources
// ---------------------------------------------------------------------------

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: defaultTags
}

module monitoring 'modules/monitoring.bicep' = if (deployMonitoring) {
  name: 'monitoring'
  scope: resourceGroup
  params: {
    namePrefix: namePrefix
    environment: environment
    location: location
    tags: defaultTags
  }
}

module serviceBus 'modules/serviceBus.bicep' = {
  name: 'serviceBus'
  scope: resourceGroup
  params: {
    namespaceName: 'sb-${namePrefix}-${environment}-${suffix}'
    location: location
    sku: serviceBusSku
    queueName: 'booking-created'
    tags: defaultTags
  }
}

module keyVault 'modules/keyVault.bicep' = {
  name: 'keyVault'
  scope: resourceGroup
  params: {
    vaultName: 'kv-${namePrefix}-${suffix}'
    location: location
    backendKey: backendKey
    tags: defaultTags
  }
}

module appService 'modules/appService.bicep' = {
  name: 'appService'
  scope: resourceGroup
  params: {
    planName: 'plan-${namePrefix}-${environment}'
    apiAppName: 'app-${namePrefix}-api-${environment}-${suffix}'
    location: location
    sku: appServicePlanSku
    tags: defaultTags
    serviceBusNamespaceHost: serviceBus.outputs.fullyQualifiedNamespace
    serviceBusQueueName: serviceBus.outputs.queueName
    keyVaultName: keyVault.outputs.vaultName
    backendKeySecretUri: keyVault.outputs.backendKeySecretUri
    applicationInsightsConnectionString: deployMonitoring ? monitoring!.outputs.connectionString : ''
  }
}

module apim 'modules/apim.bicep' = {
  name: 'apim'
  scope: resourceGroup
  params: {
    serviceName: 'apim-${namePrefix}-${environment}-${suffix}'
    location: location
    sku: apimSku
    publisherEmail: apimPublisherEmail
    publisherName: apimPublisherName
    backendUrl: 'https://${appService.outputs.apiDefaultHostName}'
    keyVaultName: keyVault.outputs.vaultName
    backendKeySecretUri: keyVault.outputs.backendKeySecretUri
    entraTenantId: entraTenantId
    entraApiApplicationId: entraApiApplicationId
    tags: defaultTags
  }
}

// A user-assigned identity for the worker, created here so the Service Bus role
// assignment can be made now. The worker itself is not provisioned by this
// template - see docs/setup.md, "Where the worker runs" - so whatever ends up
// hosting it (Container Apps, a WebJob, a VM) is given this identity rather than
// a new role assignment being invented at deploy time.
module workerIdentity 'modules/workerIdentity.bicep' = {
  name: 'workerIdentity'
  scope: resourceGroup
  params: {
    identityName: 'id-${namePrefix}-worker-${environment}'
    location: location
    serviceBusNamespaceName: serviceBus.outputs.namespaceName
    serviceBusQueueName: serviceBus.outputs.queueName
    tags: defaultTags
  }
}

// ---------------------------------------------------------------------------
// Outputs - consumed by scripts/Import-ApimApi.ps1 and Invoke-SmokeTests.ps1
// ---------------------------------------------------------------------------

output resourceGroupName string = resourceGroup.name
output location string = location

output apiAppName string = appService.outputs.apiAppName
output apiUrl string = 'https://${appService.outputs.apiDefaultHostName}'
output apiPrincipalId string = appService.outputs.apiPrincipalId

output apimName string = apim.outputs.serviceName
output apimGatewayUrl string = apim.outputs.gatewayUrl
output apimPrincipalId string = apim.outputs.principalId

output serviceBusNamespace string = serviceBus.outputs.fullyQualifiedNamespace
output serviceBusQueueName string = serviceBus.outputs.queueName

output keyVaultName string = keyVault.outputs.vaultName
output workerIdentityClientId string = workerIdentity.outputs.clientId
output workerIdentityResourceId string = workerIdentity.outputs.resourceId
