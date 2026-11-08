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

var usePrivateRegistry = !empty(registryUsername) && !empty(registryPassword)

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

@description('Public address of the game.')
output url string = 'https://${app.properties.configuration.ingress.fqdn}'
