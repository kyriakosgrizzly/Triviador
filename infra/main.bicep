// Triviador on Azure Container Apps, sized to stay inside the free monthly grant.
//
// Deployed by the GitHub Actions workflow on every push to main:
//   az deployment group create -g rg-triviador -f infra/main.bicep -p image=ghcr.io/<owner>/triviador:<sha>
//
// What keeps it free:
//   - Consumption plan, 0.25 vCPU / 0.5 GiB, scales to ZERO when nobody is connected.
//   - Max 1 replica: matches live in server memory, so a second replica would split players.
//   - Log Analytics capped at 0.1 GB/day (~3 GB/month, under the 5 GB free allowance).
//   - Image comes from GitHub Container Registry, so no Azure Container Registry (which has no free tier).
//   - Questions live in Azure Cosmos DB for NoSQL on the FREE TIER (first 1000 RU/s + 25 GB free). The
//     database shares 400 RU/s across its containers and the account is capped at 1000 RU/s, so it can't
//     be billed past the free allowance even by mistake. Only ONE free-tier account is allowed per
//     subscription: if you already have one, deploy with cosmosFreeTier=false (then 400 RU/s costs ~$23/month).
//   - The app signs in to Cosmos with its managed identity (no keys anywhere); key auth is switched off.

@description('Short name used for every resource.')
param name string = 'triviador'

@description('Azure region. Defaults to the resource group\'s region.')
param location string = resourceGroup().location

@description('Full container image reference, e.g. ghcr.io/anri/triviador:<git sha>.')
param image string

@description('Only needed if the GHCR package is private: a GitHub username.')
param registryUsername string = ''

@description('Only needed if the GHCR package is private: a GitHub token with read:packages.')
@secure()
param registryPassword string = ''

@description('Put the Cosmos DB account on the free tier. Only one account per subscription can have it, and it can only be chosen when the account is created.')
param cosmosFreeTier bool = true

@description('Optional: your own Microsoft Entra object id (az ad signed-in-user show --query id -o tsv), so you can browse and edit questions in Data Explorer or run the server locally against Azure.')
param cosmosAdminPrincipalId string = ''

var usePrivateRegistry = !empty(registryUsername) && !empty(registryPassword)

// Cosmos account names are global DNS names: lowercase, 3–44 characters.
var cosmosAccountName = 'cosmos-${name}-${uniqueString(resourceGroup().id)}'
var cosmosDatabaseName = 'triviador'
var cosmosContainerName = 'questions'
// Built-in "Cosmos DB Built-in Data Contributor": read and write items. It's a data-plane role, assigned on
// the Cosmos account itself (sqlRoleAssignments), not an Azure RBAC role in Access control (IAM).
var cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      dailyQuotaGb: json('0.1')
    }
  }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: cosmosAccountName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    enableFreeTier: cosmosFreeTier
    // Hard ceiling for the whole account, equal to the free allowance.
    capacity: {
      totalThroughputLimit: 1000
    }
    // Entra ID (managed identity / your login) only — no account keys or connection strings.
    disableLocalAuth: true
    minimalTlsVersion: 'Tls12'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
  }
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: cosmos
  name: cosmosDatabaseName
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
    // Shared (database-level) throughput: every container in this database draws from the same 400 RU/s.
    options: {
      throughput: 400
    }
  }
}

resource questions 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: cosmosDatabase
  name: cosmosContainerName
  properties: {
    resource: {
      id: cosmosContainerName
      // Category ("Azure", "Geography", ...) spreads documents over a few dozen logical partitions and
      // matches how the game picks questions. The whole set is far below the 20 GB logical-partition limit.
      partitionKey: {
        paths: [
          '/category'
        ]
        kind: 'Hash'
        version: 2
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [
          {
            path: '/category/?'
          }
          {
            path: '/type/?'
          }
        ]
        // The server reads everything with one query, so indexing the text and options would only cost RUs on writes.
        excludedPaths: [
          {
            path: '/*'
          }
        ]
      }
    }
  }
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${name}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  // The identity the server uses to read the questions from Cosmos DB.
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto' // HTTP/1.1 + WebSockets for SignalR
        allowInsecure: false
      }
      secrets: usePrivateRegistry ? [
        {
          name: 'registry-password'
          value: registryPassword
        }
      ] : []
      registries: usePrivateRegistry ? [
        {
          server: 'ghcr.io'
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ] : []
    }
    template: {
      containers: [
        {
          name: name
          image: image
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'Cosmos__Endpoint'
              value: cosmos.properties.documentEndpoint
            }
            {
              name: 'Cosmos__Database'
              value: cosmosDatabaseName
            }
            {
              name: 'Cosmos__Container'
              value: cosmosContainerName
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '100'
              }
            }
          }
        ]
      }
    }
  }
}

// Let the app read and write question documents. On a first deployment this can take a few minutes to
// take effect; the server keeps playing the embedded questions and retries until it works.
resource appCosmosAccess 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, app.id, cosmosDataContributorRoleId)
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    principalId: app.identity.principalId
    scope: cosmos.id
  }
}

resource adminCosmosAccess 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = if (!empty(cosmosAdminPrincipalId)) {
  parent: cosmos
  name: guid(cosmos.id, cosmosAdminPrincipalId, cosmosDataContributorRoleId)
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    principalId: cosmosAdminPrincipalId
    scope: cosmos.id
  }
}

@description('Public address of the game.')
output url string = 'https://${app.properties.configuration.ingress.fqdn}'

@description('Cosmos DB endpoint holding the questions.')
output cosmosEndpoint string = cosmos.properties.documentEndpoint
