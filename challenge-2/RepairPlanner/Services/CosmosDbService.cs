using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlannerAgent.Models;

namespace RepairPlannerAgent.Services;

/// <summary>
/// Handles all Cosmos DB operations: reading technicians and parts, saving work orders.
/// </summary>
public sealed class CosmosDbService
{
    private readonly Container _techniciansContainer;
    private readonly Container _partsContainer;
    private readonly Container _workOrdersContainer;
    private readonly ILogger<CosmosDbService> _logger;

    public CosmosDbService(CosmosDbOptions options, ILogger<CosmosDbService> logger)
    {
        _logger = logger;

        // CosmosClient is the main entry point for the Azure Cosmos DB SDK
        var client = new CosmosClient(options.Endpoint, options.Key);
        var database = client.GetDatabase(options.DatabaseName);

        _techniciansContainer = database.GetContainer("Technicians");
        _partsContainer       = database.GetContainer("PartsInventory");
        _workOrdersContainer  = database.GetContainer("WorkOrders");
    }

    /// <summary>
    /// Returns technicians whose skills list contains ANY of the required skills
    /// and whose availability is "available".
    /// </summary>
    public async Task<List<Technician>> GetAvailableTechniciansWithSkillsAsync(
        IReadOnlyList<string> requiredSkills,
        CancellationToken ct = default)
    {
        // Build an OR filter using parameterized values to avoid injection
        var paramNames = requiredSkills
            .Select((_, i) => $"@skill{i}")
            .ToList();

        var skillsFilter = paramNames.Count > 0
            ? string.Join(" OR ", paramNames.Select(p => $"ARRAY_CONTAINS(c.skills, {p})"))
            : "true";

        var query = new QueryDefinition(
            $"SELECT * FROM c WHERE c.availability = 'available' AND ({skillsFilter})");

        for (var i = 0; i < requiredSkills.Count; i++)
            query = query.WithParameter($"@skill{i}", requiredSkills[i]);

        var results = new List<Technician>();
        using var feed = _techniciansContainer.GetItemQueryIterator<Technician>(query);

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct);
            results.AddRange(page);
        }

        _logger.LogInformation("Found {Count} available technicians matching skills", results.Count);
        return results;
    }

    /// <summary>
    /// Fetches parts from PartsInventory by their part numbers.
    /// </summary>
    public async Task<List<Part>> GetPartsInventoryAsync(
        IReadOnlyList<string> partNumbers,
        CancellationToken ct = default)
    {
        if (partNumbers.Count == 0)
            return [];

        // Build a parameterized IN-style filter to avoid injection
        var paramNames = partNumbers.Select((_, i) => $"@p{i}").ToList();
        var query = new QueryDefinition(
            $"SELECT * FROM c WHERE c.partNumber IN ({string.Join(", ", paramNames)})");

        for (var i = 0; i < partNumbers.Count; i++)
            query = query.WithParameter($"@p{i}", partNumbers[i]);

        var results = new List<Part>();
        using var feed = _partsContainer.GetItemQueryIterator<Part>(query);

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct);
            results.AddRange(page);
        }

        _logger.LogInformation("Fetched {Count} parts", results.Count);
        return results;
    }

    /// <summary>
    /// Saves a new work order to Cosmos DB and returns the saved WorkOrder.
    /// </summary>
    public async Task<WorkOrder> CreateWorkOrderAsync(
        WorkOrder workOrder,
        CancellationToken ct = default)
    {
        // ?? means "if null or empty, use this fallback" (like Python's "or")
        if (string.IsNullOrWhiteSpace(workOrder.Id))
            workOrder.Id = Guid.NewGuid().ToString();

        // Partition key for WorkOrders container is /status
        var response = await _workOrdersContainer.UpsertItemAsync(
            workOrder,
            new PartitionKey(workOrder.Status),
            cancellationToken: ct);

        _logger.LogInformation(
            "Saved work order {WorkOrderNumber} (id={Id}, status={Status}, assignedTo={AssignedTo})",
            workOrder.WorkOrderNumber, workOrder.Id, workOrder.Status, workOrder.AssignedTo ?? "unassigned");

        return response.Resource;
    }
}
