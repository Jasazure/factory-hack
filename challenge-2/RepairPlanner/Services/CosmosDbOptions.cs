namespace RepairPlannerAgent.Services;

/// <summary>
/// Holds the Cosmos DB connection settings loaded from environment variables.
/// </summary>
public sealed class CosmosDbOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
}
