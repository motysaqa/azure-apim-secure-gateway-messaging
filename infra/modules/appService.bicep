metadata description = 'Linux App Service plan and the Booking API web app, with its managed identity wiring.'

param planName string
param apiAppName string
param location string
param tags object

@allowed(['F1', 'B1', 'S1'])
param sku string

param serviceBusNamespaceHost string
param serviceBusQueueName string
param keyVaultName string
param backendKeySecretUri string
param applicationInsightsConnectionString string = ''

// Azure Service Bus Data Sender
var serviceBusDataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
// Key Vault Secrets User
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

// F1 has no Always On, so the app is unloaded when idle and the outbox publisher
// stops with it. Messages are not lost - they stay in the outbox - but nothing
// drains until the next request wakes the app.
var alwaysOn = sku != 'F1'

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  tags: tags
  sku: {
    name: sku
    tier: sku == 'F1' ? 'Free' : (sku == 'B1' ? 'Basic' : 'Standard')
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource apiApp 'Microsoft.Web/sites@2023-12-01' = {
  name: apiAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    // System-assigned: the identity's lifetime is the app's, so deleting the app
    // deletes the principal instead of leaving an orphaned one with live grants.
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: alwaysOn
      http20Enabled: true
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health'
      appSettings: concat([
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'ServiceBus__FullyQualifiedNamespace'
          value: serviceBusNamespaceHost
        }
        {
          name: 'ServiceBus__QueueName'
          value: serviceBusQueueName
        }
        {
          // A Key Vault reference, not the value. App Service resolves it with the
          // app's managed identity at start-up, so the secret never appears in the
          // template, the portal, or a deployment log.
          name: 'Gateway__BackendKey'
          value: '@Microsoft.KeyVault(SecretUri=${backendKeySecretUri})'
        }
        {
          // App Service gives every app a writable /home that survives restarts.
          // SQLite here is a stand-in for SQL Server; see the README.
          name: 'ConnectionStrings__Bookings'
          value: 'Data Source=/home/data/bookings.db'
        }
        {
          name: 'Outbox__PollIntervalSeconds'
          value: '2'
        }
      ], empty(applicationInsightsConnectionString) ? [] : [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: applicationInsightsConnectionString
        }
      ])
    }
  }
}

// The app's identity may send to the queue - and only send, and only to this
// queue. It never needs to receive; that is the worker's identity.
resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: split(serviceBusNamespaceHost, '.')[0]

  resource queue 'queues' existing = {
    name: serviceBusQueueName
  }
}

resource senderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace::queue.id, apiApp.id, serviceBusDataSenderRoleId)
  scope: serviceBusNamespace::queue
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSenderRoleId)
    principalId: apiApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Secrets User, not Secrets Officer: the app reads the backend key and can
// neither write nor delete it.
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource secretsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, apiApp.id, keyVaultSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: apiApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output apiAppName string = apiApp.name
output apiDefaultHostName string = apiApp.properties.defaultHostName
output apiPrincipalId string = apiApp.identity.principalId
output planId string = plan.id
output alwaysOnEnabled bool = alwaysOn
