metadata description = 'Service Bus namespace and the booking-created queue.'

param namespaceName string
param location string
param queueName string
param tags object

@allowed(['Basic', 'Standard'])
param sku string

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: sku
    tier: sku
  }
  properties: {
    // TLS 1.2 minimum, and local (SAS) auth disabled entirely: every client
    // authenticates with a managed identity, so there is no connection string to
    // leak, rotate or accidentally commit.
    minimumTlsVersion: '1.2'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: namespace
  name: queueName
  properties: {
    // Dead-letter after 5 delivery attempts. The worker abandons a message it
    // considers transient, so this is the backstop for a "transient" failure
    // that turns out to be permanent.
    maxDeliveryCount: 5
    lockDuration: 'PT1M'
    defaultMessageTimeToLive: 'P14D'
    deadLetteringOnMessageExpiration: true
    enableBatchedOperations: true

    // Duplicate detection is a Standard-tier feature. On Basic the worker's own
    // ledger is the only defence against a redelivered message, which is why it
    // is a durable table rather than an in-process cache.
    requiresDuplicateDetection: sku == 'Standard'
    duplicateDetectionHistoryTimeWindow: sku == 'Standard' ? 'PT10M' : null

    // Sessions would give per-property ordering, but they are Standard-only and
    // bookings for different properties are genuinely independent.
    requiresSession: false
  }
}

output namespaceName string = namespace.name
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
output queueName string = queue.name
output queueResourceId string = queue.id
