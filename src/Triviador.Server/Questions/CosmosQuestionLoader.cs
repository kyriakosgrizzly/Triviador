using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Triviador.Domain.Questions;

namespace Triviador.Server.Questions;

/// <summary>
/// Loads the questions from Azure Cosmos DB into the <see cref="QuestionCatalog"/>.
///
/// On start-up it seeds the container from the embedded bank (only ids that are missing, by default),
/// reads every document with one cross-partition query and swaps the result into the catalog. Games
/// never wait for Cosmos: until the load succeeds they use the embedded bank. Failures (for example
/// the managed identity's role assignment still propagating after a fresh deployment) are retried
/// with back-off. After that it reloads every <see cref="CosmosOptions.RefreshMinutes"/>.
///
/// Cost: loading ~900 small documents is a few hundred RUs, which is nothing against the free tier's
/// 1000 RU/s; seeding writes ~10 RUs per new document once.
/// </summary>
public sealed class CosmosQuestionLoader(
    QuestionCatalog catalog,
    IOptions<CosmosOptions> options,
    TimeProvider time,
    ILogger<CosmosQuestionLoader> logger) : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.IsConfigured)
        {
            logger.LogInformation("Cosmos DB is not configured; playing with the {Count} embedded questions.", catalog.All.Count);
            return;
        }

        CosmosClient client;
        try
        {
            client = CreateClient(o);
        }
        catch (Exception ex)
        {
            // A malformed endpoint or connection string must not take the game server down with it.
            logger.LogError(ex, "Couldn't create the Cosmos DB client; playing with the embedded questions.");
            return;
        }

        using var _ = client;
        var seeded = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var container = await OpenContainerAsync(client, o, stoppingToken);
                    var documents = await ReadAllAsync(container, stoppingToken);
                    if (!seeded && o.Seed != SeedMode.Off)
                    {
                        documents = await SeedAsync(container, documents, o.Seed, stoppingToken);
                        seeded = true;
                    }
                    Publish(documents, o);
                    break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ArgumentException ex)
                {
                    // The container's contents can't run a game (e.g. a colour has almost no questions). Retrying won't help.
                    logger.LogError("Not using the Cosmos DB questions: {Problem}. Still serving {Origin} questions.", ex.Message, catalog.Origin);
                    break;
                }
                catch (Exception ex) when (attempt < RetryDelays.Length)
                {
                    logger.LogWarning("Loading questions from Cosmos DB failed ({Error}); retrying in {Delay}. Still serving {Origin} questions.",
                        Describe(ex), RetryDelays[attempt], catalog.Origin);
                    await Task.Delay(RetryDelays[attempt], time, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Giving up on Cosmos DB for now; still serving {Origin} questions.", catalog.Origin);
                    break;
                }
            }

            if (o.RefreshMinutes <= 0) return;
            await Task.Delay(TimeSpan.FromMinutes(o.RefreshMinutes), time, stoppingToken);
        }
    }

    private static CosmosClient CreateClient(CosmosOptions o)
    {
        var clientOptions = new CosmosClientOptions
        {
            ApplicationName = "triviador",
            UseSystemTextJsonSerializerWithOptions = Json,
        };

        if (!string.IsNullOrWhiteSpace(o.ConnectionString))
        {
            if (IsLocalEmulator(o.ConnectionString))
            {
                // The emulator runs in gateway mode with a self-signed certificate.
                clientOptions.ConnectionMode = ConnectionMode.Gateway;
                clientOptions.LimitToEndpoint = true;
                clientOptions.HttpClientFactory = () => new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                });
            }
            return new CosmosClient(o.ConnectionString, clientOptions);
        }

        // Managed identity in Azure; az login / Visual Studio sign-in on a dev machine.
        return new CosmosClient(o.Endpoint!, new DefaultAzureCredential(), clientOptions);
    }

    private static bool IsLocalEmulator(string connectionString) =>
        connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase)
        || connectionString.Contains("127.0.0.1", StringComparison.Ordinal);

    private static async Task<Container> OpenContainerAsync(CosmosClient client, CosmosOptions o, CancellationToken ct)
    {
        // Entra ID (RBAC) tokens can't create databases or containers — that is a control-plane
        // operation, done by Bicep in Azure. A key (the emulator) can, so create them there.
        if (string.IsNullOrWhiteSpace(o.ConnectionString))
            return client.GetContainer(o.Database, o.Container);

        Database database = await client.CreateDatabaseIfNotExistsAsync(
            o.Database, ThroughputProperties.CreateManualThroughput(o.DatabaseThroughput), cancellationToken: ct);
        Container container = await database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(o.Container, "/category"), cancellationToken: ct);
        return container;
    }

    private async Task<List<QuestionDocument>> ReadAllAsync(Container container, CancellationToken ct)
    {
        var documents = new List<QuestionDocument>();
        double charge = 0;
        using var feed = container.GetItemQueryIterator<QuestionDocument>(
            "SELECT * FROM c", requestOptions: new QueryRequestOptions { MaxItemCount = 1000 });
        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct);
            charge += page.RequestCharge;
            documents.AddRange(page);
        }
        logger.LogInformation("Read {Count} question documents from Cosmos DB for {Charge:F1} RUs.", documents.Count, charge);
        return documents;
    }

    /// <summary>Writes embedded questions into the container and returns the documents including them.</summary>
    private async Task<List<QuestionDocument>> SeedAsync(
        Container container, List<QuestionDocument> existing, SeedMode mode, CancellationToken ct)
    {
        var byId = existing.ToDictionary(d => d.Id);
        var toWrite = SampleQuestions.All
            .Select(QuestionDocument.From)
            .Where(d => mode == SeedMode.Upsert || !byId.ContainsKey(d.Id))
            .ToList();
        if (toWrite.Count == 0) return existing;

        double charge = 0;
        var written = 0;
        // Small batches keep us well under the provisioned RU/s; the SDK retries any 429s itself.
        foreach (var batch in toWrite.Chunk(25))
        {
            var responses = await Task.WhenAll(batch.Select(WriteAsync));
            foreach (var (doc, c) in responses)
            {
                charge += c;
                if (doc is null) continue;
                byId[doc.Id] = doc;
                written++;
            }
        }

        async Task<(QuestionDocument? Doc, double Charge)> WriteAsync(QuestionDocument d)
        {
            try
            {
                var key = new PartitionKey(d.Category);
                var response = mode == SeedMode.Upsert
                    ? await container.UpsertItemAsync(d, key, cancellationToken: ct)
                    : await container.CreateItemAsync(d, key, cancellationToken: ct);
                return (d, response.RequestCharge);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                return (null, ex.RequestCharge); // already there (another instance got there first)
            }
        }

        logger.LogInformation("Seeded {Written} questions into Cosmos DB ({Mode}) for {Charge:F0} RUs.", written, mode, charge);
        return [.. byId.Values];
    }

    private void Publish(List<QuestionDocument> documents, CosmosOptions o)
    {
        var questions = new List<Question>(documents.Count);
        var skipped = 0;
        foreach (var d in documents)
        {
            var q = d.ToQuestion(out var problem);
            if (q is not null) { questions.Add(q); continue; }
            skipped++;
            if (problem != "disabled")
                logger.LogWarning("Skipping question {Id} in {Container}: {Problem}.", d.Id, o.Container, problem);
        }

        catalog.Replace(questions, QuestionOrigin.CosmosDb, time.GetUtcNow());
        logger.LogInformation("Now playing {Count} questions from Cosmos DB ({Skipped} skipped).", questions.Count, skipped);
    }

    private static string Describe(Exception ex) => ex switch
    {
        CosmosException { StatusCode: HttpStatusCode.Forbidden } =>
            "403 Forbidden — the identity has no Cosmos DB data role yet (role assignments can take a few minutes)",
        CosmosException { StatusCode: HttpStatusCode.NotFound } =>
            "404 — the database or container doesn't exist; deploy infra/main.bicep or use a connection string",
        CosmosException c => $"{(int)c.StatusCode} {c.StatusCode}",
        AuthenticationFailedException or CredentialUnavailableException => "couldn't get an Entra ID token: " + ex.Message,
        _ => ex.GetType().Name + ": " + ex.Message,
    };
}
