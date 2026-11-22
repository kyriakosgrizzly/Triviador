namespace Triviador.Server.Questions;

/// <summary>
/// Settings for the Cosmos DB question store (configuration section "Cosmos"; environment variables
/// <c>Cosmos__Endpoint</c>, <c>Cosmos__ConnectionString</c> and so on). Leave both Endpoint and
/// ConnectionString empty to play with the embedded bank only.
/// </summary>
public sealed class CosmosOptions
{
    public const string Section = "Cosmos";

    /// <summary>
    /// Account endpoint, e.g. https://cosmos-triviador-xyz.documents.azure.com:443/. Signs in with
    /// DefaultAzureCredential: the Container App's managed identity in Azure, your az/VS login locally.
    /// The database and container must already exist (Bicep creates them).
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Key-based connection string, meant for the local emulator. With a key the server also creates the
    /// database and container if they are missing.
    /// </summary>
    public string? ConnectionString { get; set; }

    public string Database { get; set; } = "triviador";
    public string Container { get; set; } = "questions";

    /// <summary>Throughput for the database when the server creates it (emulator only). 400 is the minimum.</summary>
    public int DatabaseThroughput { get; set; } = 400;

    /// <summary>How the embedded bank is copied into the container on start-up.</summary>
    public SeedMode Seed { get; set; } = SeedMode.Missing;

    /// <summary>Reload the container this often so questions added in Data Explorer show up. 0 = only at start-up.</summary>
    public int RefreshMinutes { get; set; } = 30;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) || !string.IsNullOrWhiteSpace(ConnectionString);
}

public enum SeedMode
{
    /// <summary>Never write; just read what is in the container.</summary>
    Off,

    /// <summary>Insert embedded questions whose id isn't in the container yet. Edits made in Cosmos are kept.</summary>
    Missing,

    /// <summary>Overwrite every embedded question with the embedded version (undoes edits made in Cosmos).</summary>
    Upsert,
}
