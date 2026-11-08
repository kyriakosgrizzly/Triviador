// A $1/month cost alert for the whole subscription. Deploy ONCE, before anything else:
//   az deployment sub create -l westeurope -f infra/budget.bicep -p contactEmail=you@example.com
//
// A budget only SENDS EMAILS — it does not stop or delete anything.
// It warns at 50% and 100% of actual spend, and when the month is forecast to go over.

targetScope = 'subscription'

@description('Where the alerts go.')
param contactEmail string

@description('Monthly budget in your billing currency.')
param amount int = 1

@description('First day of the current month. Leave the default.')
param startDate string = utcNow('yyyy-MM-01')

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: 'triviador-monthly'
  properties: {
    category: 'Cost'
    amount: amount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: startDate
    }
    notifications: {
      actualHalf: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 50
        thresholdType: 'Actual'
        contactEmails: [
          contactEmail
        ]
      }
      actualFull: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: [
          contactEmail
        ]
      }
      forecastFull: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: [
          contactEmail
        ]
      }
    }
  }
}
