targetScope = 'resourceGroup'

// Verify GitHub OIDC and resource-group deployment permissions before the
// auth service infrastructure is implemented. This template creates no resources.
output resourceGroupId string = resourceGroup().id
output location string = resourceGroup().location
