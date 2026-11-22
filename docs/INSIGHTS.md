# Application Insights

The server sends its telemetry to **Application Insights** (resource `appi-triviador`) with
**OpenTelemetry**, using Microsoft's Azure Monitor distro (`Azure.Monitor.OpenTelemetry.AspNetCore`).
Everything is set up by `infra/main.bicep`; nothing to configure by hand.

## What gets collected

| Table | What | From |
|---|---|---|
| `requests` | HTTP requests: page loads, `/api/me`, `/api/questions`, SignalR negotiate (not `/healthz`) | ASP.NET Core instrumentation |
| `dependencies` | Calls to Cosmos DB (seeding, loading questions) | Azure SDK tracing, switched on in Program.cs |
| `traces` | Everything the server logs: Cosmos loads and RU charges, warnings | `ILogger` |
| `exceptions` | Unhandled errors, with stack traces | ASP.NET Core |
| `customMetrics` | The game's own numbers (below) | `Telemetry/GameMetrics.cs` |
| `performanceCounters` | CPU, memory, GC | the distro |

Hub messages (playing cards, answering) go over the WebSocket, so they aren't requests. The game
metrics count them instead.

### Game metrics (`Telemetry/GameMetrics.cs`)

| Metric | Kind | Dimensions |
|---|---|---|
| `triviador.games.started` | counter | `humans`, `seats` |
| `triviador.games.ended` | counter | `how` = finished / abandoned, `winner` = human / bot / none |
| `triviador.questions` | counter | `colour` = Geography / Science / History / Tech, `outcome` = right / wrong / stolen / timeout |
| `triviador.game.duration` | histogram, minutes | |
| `triviador.game.turns` | histogram | |
| `triviador.games.active` | gauge | |
| `triviador.players.online` | gauge | |

Metrics are pre-aggregated and sent once a minute, so they cost almost nothing however many questions
are answered.

## Where to look

Portal → **appi-triviador**:
- **Live metrics:** requests, failures and CPU in real time while you play.
- **Application map:** `triviador` → Cosmos DB, with call counts and timings.
- **Failures** / **Performance:** slowest and failing requests and dependencies.
- **Metrics:** choose the *Custom* namespace `azure.applicationinsights` (or `Triviador.Game`) to chart
  the game metrics, split by a dimension.
- **Logs:** KQL, examples below.

The first data appears about 2–5 minutes after the app starts. The app scales to zero when idle, so
there are gaps between sessions.

## KQL to try (Logs)

```kusto
// Questions by colour and outcome, last 7 days
customMetrics
| where timestamp > ago(7d) and name == "triviador.questions"
| extend colour = tostring(customDimensions.colour), outcome = tostring(customDimensions.outcome)
| summarize answers = sum(valueSum) by colour, outcome
| evaluate pivot(outcome, sum(answers))

// Which colour is hardest? Share of questions answered right
customMetrics
| where name == "triviador.questions"
| extend colour = tostring(customDimensions.colour), outcome = tostring(customDimensions.outcome)
| summarize right = sumif(valueSum, outcome == "right"), total = sum(valueSum) by colour
| extend pctRight = round(100.0 * right / total, 1)
| order by pctRight asc

// Games per day and how they ended
customMetrics
| where name == "triviador.games.ended"
| summarize games = sum(valueSum) by bin(timestamp, 1d), how = tostring(customDimensions.how), winner = tostring(customDimensions.winner)

// Cosmos DB: RU charge of every question load (from the server's own log line)
traces
| where message startswith "Read " and message has "Cosmos DB"
| project timestamp, message

// Slowest requests
requests
| summarize count(), p95 = percentile(duration, 95) by name
| order by p95 desc

// Errors in the last day
exceptions
| where timestamp > ago(1d)
| summarize count() by type, outerMessage
```

## Cost

- Application Insights is **workspace-based**: its data is stored in the existing Log Analytics
  workspace `log-triviador`. The first 5 GB per month are free, and the workspace is capped at 0.1 GB a
  day. On a very busy day collection stops until midnight UTC instead of costing money.
- Playing doesn't send a request per click: the game uses one WebSocket, and the metrics are
  aggregated per minute. The liveness probe (`/healthz`, every 30 s) is filtered out.
- To check usage, open **Log Analytics workspace → Usage and estimated costs**, or run
  `Usage | where TimeGenerated > ago(30d) | summarize GB = sum(Quantity) / 1024 by DataType`.

## Locally

Nothing is sent unless the `APPLICATIONINSIGHTS_CONNECTION_STRING` environment variable is set. To send
telemetry from your PC, copy the connection string from the portal (appi-triviador → Overview) and set
it before `dotnet run`. Your machine shows up as its own role instance.

## Exam notes (AI-200)

- OpenTelemetry with the Azure Monitor distro is Microsoft's recommended way to instrument new .NET
  apps; the classic Application Insights SDK is the older route.
- Signal to table: traces become `requests` (server spans) and `dependencies` (client spans), logs become
  `traces`, metrics become `customMetrics`, exceptions become `exceptions`. The service name
  (`OTEL_SERVICE_NAME`) becomes `cloud_RoleName`.
- The connection string (not the old instrumentation key) tells the exporter where to send data. Here
  it's a Container App secret referenced with `secretRef`.
- Workspace-based Application Insights is the only kind you can create now. The daily cap, retention
  and pricing come from the Log Analytics workspace.
- Custom metrics come from `System.Diagnostics.Metrics` (`Meter`, `Counter`, `Histogram`,
  `ObservableGauge`) plus `AddMeter(...)`. Custom spans come from `ActivitySource` plus `AddSource(...)`.
