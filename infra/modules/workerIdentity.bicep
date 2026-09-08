metadata description = 'User-assigned identity for the worker, with receive rights on the queue.'

param identityName string
param location string
param serviceBusNamespaceName string
param serviceBusQueueName string
param tags object

// Azure Service Bus Data Receiver
var serviceBusDataReceiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName

  resource queue 'queues' existing = {
    name: serviceBusQueueName
  }
}

// Scoped to the queue, not the namespace: the worker can receive from this one
// queue and nothing else. Scoping at the namespace would be one line shorter and
// quietly grant access to every queue added later.
resource receiverAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(namespace::queue.id, identity.id, serviceBusDataReceiverRoleId)
  scope: namespace::queue
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataReceiverRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output resourceId string = identity.id
output clientId string = identity.properties.clientId
output principalId string = identity.properties.principalId
