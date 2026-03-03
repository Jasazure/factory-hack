using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepairPlannerAgent.Models;
using RepairPlannerAgent.Services;
using Agent = RepairPlannerAgent.RepairPlannerAgent;

// ============================================================================
// Dependency Injection Setup
// Similar to Python's dependency injection frameworks, we register services
// in a container and resolve them later. "services" is the recipe book;
// "provider" (built later) is the actual factory.
// ============================================================================
var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.ClearProviders();
    builder.AddSimpleConsole(o =>
    {
        o.SingleLine      = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
    builder.SetMinimumLevel(LogLevel.Information);
});

// Azure AI Project client — uses DefaultAzureCredential (picks up az login token automatically)
var aiProjectEndpoint = GetRequiredEnvVar("AZURE_AI_PROJECT_ENDPOINT");
services.AddSingleton(_ => new AIProjectClient(new Uri(aiProjectEndpoint), new DefaultAzureCredential()));

// Cosmos DB options (endpoint + key + database name)
var cosmosOptions = new CosmosDbOptions
{
    Endpoint     = GetRequiredEnvVar("COSMOS_ENDPOINT"),
    Key          = GetRequiredEnvVar("COSMOS_KEY"),
    DatabaseName = GetRequiredEnvVar("COSMOS_DATABASE_NAME"),
};
services.AddSingleton(cosmosOptions);
services.AddSingleton<CosmosDbService>();

// Fault mapping service — hardcoded in-memory dictionaries
services.AddSingleton<IFaultMappingService, FaultMappingService>();

// Repair Planner Agent — wires together all dependencies
services.AddSingleton(sp => new Agent(
    sp.GetRequiredService<AIProjectClient>(),
    sp.GetRequiredService<CosmosDbService>(),
    sp.GetRequiredService<IFaultMappingService>(),
    GetRequiredEnvVar("MODEL_DEPLOYMENT_NAME"),
    sp.GetRequiredService<ILogger<Agent>>()));

// ============================================================================
// Run the workflow
// ============================================================================
// "await using" ensures the provider is disposed at the end — like Python's "async with"
await using var provider = services.BuildServiceProvider();
var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Program");

var planner = provider.GetRequiredService<Agent>();

// Register (or update) the agent version in Azure AI Foundry
await planner.EnsureAgentVersionAsync();

// Sample fault — as if received from the Fault Diagnosis Agent (Challenge 1)
var sampleFault = new DiagnosedFault
{
    MachineId  = "machine-001",
    FaultType  = "curing_temperature_excessive",
    Severity   = "high",
    RootCause  = "Heater element likely failing or thermocouple drift causing overshoot.",
    DetectedAt = DateTime.UtcNow.ToString("o"),
};

try
{
    var workOrder = await planner.PlanAndCreateWorkOrderAsync(sampleFault);

    // ?? means "if null, use the right-hand value" (like Python's "or")
    logger.LogInformation(
        "Saved work order {WorkOrderNumber} (id={Id}, status={Status}, assignedTo={AssignedTo})",
        workOrder.WorkOrderNumber,
        workOrder.Id,
        workOrder.Status,
        workOrder.AssignedTo ?? "<unassigned>");

    Console.WriteLine();
    Console.WriteLine(JsonSerializer.Serialize(workOrder, new JsonSerializerOptions { WriteIndented = true }));
}
catch (Exception ex)
{
    logger.LogError(ex, "Repair planning workflow failed.");
    Environment.ExitCode = 1;
}

// Helper to get required environment variables — throws clearly if one is missing
// (like Python's os.environ["KEY"] which raises KeyError if missing)
static string GetRequiredEnvVar(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing required environment variable: {name}");
