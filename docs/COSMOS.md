# Questions in Azure Cosmos DB

The game's questions live in an **Azure Cosmos DB for NoSQL** container. The server loads all of them
into memory at start-up, so answering a question never touches the database. Players see no difference;
the point is to have a real data store to practise on for AI-200.

```
Container App (managed identity) ──DefaultAzureCredential──▶ Cosmos DB account (free tier, keys disabled)
                                                              └─ database "triviador"   400 RU/s shared
                                                                 └─ container "questions"  partition key /category
```

## How the server uses it

`src/Triviador.Server/Questions/CosmosQuestionLoader.cs` is a `BackgroundService`:

1. **Start immediately with the embedded bank.** `QuestionCatalog` begins with the questions compiled
   into `Triviador.Domain`, so games can start before Cosmos answers and keep working if it's down.
2. **Read everything:** one cross-partition query `SELECT * FROM c`. The log line shows the RU charge.
3. **Seed:** insert every embedded question whose `id` isn't in the container (`CreateItemAsync` with
   the category as partition key, in batches of 25). A `409 Conflict` means it is already there.
4. **Publish:** turn the documents into questions, skip disabled or malformed ones (logged), and swap
   them into the catalog. New games use them; running games keep their set. A set that leaves any card
   colour with fewer than 10 questions is rejected and the previous set stays.
5. **Retry and refresh.** Failures are retried after 5 s, 15 s, 30 s, 1, 2 and 5 minutes. A fresh
   deployment usually logs a few `403 Forbidden` lines while the role assignment propagates. After a
   successful load it reloads every 30 minutes, so documents you add in Data Explorer go into play.

`GET /api/questions` returns `origin` (`Embedded` or `CosmosDb`), when the set was loaded and the counts
per colour. It never returns answers.

## Document shape

```json
{ "id": "mc-301", "category": "Azure", "type": "choice",
  "text": "Which Cosmos DB feature lets you process inserts and updates in the order they happened?",
  "options": ["Time to live", "Change feed", "Analytical store", "Stored procedures"], "correct": 1 }

{ "id": "num-56", "category": "History", "type": "number",
  "text": "In what year did the Titanic sink?", "answer": 1912 }
```

- `category` is the partition key. Quiz Cards maps it to a colour: `Geography`; `Science`;
  `Azure`/`.NET`/`Web`/`Tech` → Tech; anything else (History, Literature, Music, Sport, Art...) → History.
- `options` has 2–4 entries; `correct` is the 0-based index of the right one.
- **To retire a question, set `"disabled": true`.** If you delete a seeded question instead, the next
  start-up inserts it again from the embedded bank.
- To change a seeded question, edit it in Cosmos. The default seed mode (`Missing`) never overwrites.
  `Cosmos__Seed=Upsert` pushes the embedded versions over everything; `Off` never writes at all.

## Why these choices (exam notes)

| Choice | Why |
|---|---|
| **Free tier** | First 1000 RU/s and 25 GB per month are free. One free-tier account per subscription, chosen at creation only, and it needs provisioned throughput (serverless can't use it). |
| **Database-level (shared) throughput, 400 RU/s** | Every container in the database shares the same RUs. 400 RU/s is the minimum; more containers later cost nothing extra. |
| **`capacity.totalThroughputLimit: 1000`** | An account-wide ceiling equal to the free allowance, so a later change can't create a bill. |
| **Partition key `/category`** | Spreads documents over a few dozen logical partitions. Each logical partition can hold up to 20 GB, and this whole set is under 1 MB. A point read needs id + category. |
| **Indexing only `/category` and `/type`** | The server reads everything in one query, so indexing text and options would only make writes cost more RUs. |
| **Session consistency** | The default: you read your own writes, at lower cost than Strong. |
| **Managed identity + `disableLocalAuth: true`** | No keys or connection strings anywhere. The app gets the built-in **Cosmos DB Built-in Data Contributor** role (`…0002`). That is a *data-plane* role assignment on the account (`sqlRoleAssignments`), not an IAM role. Data-plane tokens can't create databases or containers, so Bicep creates those. |
| **Load once, keep in memory** | Each game would otherwise query per question. Loading all 864 documents measured **14.2 RUs** in Azure (logged on every load), every 30 minutes — next to nothing against 400 RU/s. |

## Looking at the data in the portal

Key auth is off, so Data Explorer signs in as you, and you need the same data role. Get your object id
with `az ad signed-in-user show --query id -o tsv`. Then set it as the repository variable
`COSMOS_ADMIN_PRINCIPAL_ID` (GitHub → Settings → Secrets and variables → Actions → Variables) and push,
or assign it yourself once:

```powershell
$account = az cosmosdb list -g rg-triviador --query "[0].name" -o tsv
az cosmosdb sql role assignment create -g rg-triviador -a $account `
  --role-definition-id 00000000-0000-0000-0000-000000000002 `
  --principal-id (az ad signed-in-user show --query id -o tsv) --scope "/"
```

Then open Portal → your Cosmos account → **Data Explorer** → `triviador` → `questions` → Items. Try
`SELECT c.category, COUNT(1) AS n FROM c GROUP BY c.category` and compare the **Query Stats** RU charge
with a point read of one item.

## Already using the free tier somewhere?

Deployment fails with a message that the free tier is already in use. Set the repository variable
`COSMOS_FREE_TIER` to `false`. The account then bills 400 RU/s (about $23/month), so you might rather
delete the other free-tier account. The free-tier flag can't be changed on an existing account.

## Running locally against Cosmos

**Nothing configured (default):** the server plays the embedded bank. Nothing to install.

**Cosmos DB emulator** (Docker; the .NET SDK needs the HTTPS protocol):

```powershell
docker run --detach --name cosmos -p 8081:8081 -p 1234:1234 `
  mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest --protocol https
$env:Cosmos__ConnectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="
dotnet run --project src/Triviador.Server
```

That key is the emulator's fixed, public development key, not a secret. With a connection string, the
server creates the database and container itself and accepts the emulator's self-signed certificate
(localhost only). Data Explorer is at http://localhost:1234.

**Your Azure account:** give yourself the data role (above), run `az login`, then
`$env:Cosmos__Endpoint = "<cosmosEndpoint output of the deployment>"`. DefaultAzureCredential picks up
your login.

## Settings

| Setting (env var) | Default | |
|---|---|---|
| `Cosmos__Endpoint` | empty | Account endpoint; signs in with DefaultAzureCredential. Set by Bicep in Azure. |
| `Cosmos__ConnectionString` | empty | Key-based (emulator). Creates the database and container if missing. |
| `Cosmos__Database` / `Cosmos__Container` | `triviador` / `questions` | |
| `Cosmos__Seed` | `Missing` | `Missing`, `Upsert` or `Off`. |
| `Cosmos__RefreshMinutes` | `30` | `0` = load only at start-up. |
