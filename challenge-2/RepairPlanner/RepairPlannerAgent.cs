using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RepairPlannerAgent.Models;
using RepairPlannerAgent.Services;

namespace RepairPlannerAgent;

/// <summary>
/// Main agent class. Uses primary constructor — parameters auto-become fields
/// (like Python's __init__ assigning self.x = x).
/// </summary>
public sealed class RepairPlannerAgent(
    AIProjectClient projectClient,
    CosmosDbService cosmosDb,
    IFaultMappingService faultMapping,
    string modelDeploymentName,
    ILogger<RepairPlannerAgent> logger)
{
    private const string AgentName = "RepairPlannerAgent";

    private const string AgentInstructions = """
        You are a Repair Planner Agent for tire manufacturing equipment.
        Generate a repair plan with tasks, timeline, and resource allocation.
        Return the response as valid JSON matching the WorkOrder schema.

        Output JSON with these fields:
        - workOrderNumber, machineId, title, description
        - type: "corrective" | "preventive" | "emergency"
        - priority: "critical" | "high" | "medium" | "low"
        - status, assignedTo (technician id or null), notes
        - estimatedDuration: integer (minutes, e.g. 90 not "90 minutes")
        - partsUsed: [{ partId, partNumber, quantity }]
        - tasks: [{ sequence, title, description, estimatedDurationMinutes (integer), requiredSkills, safetyNotes }]

        IMPORTANT: All duration fields must be integers representing minutes (e.g. 90), not strings.

        Rules:
        - Assign the most qualified available technician
        - Include only relevant parts; empty array if none needed
        - Tasks must be ordered and actionable
        """;

    // LLMs sometimes return numbers as strings — this handles that gracefully
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Registers (or updates) the RepairPlannerAgent version in Azure AI Foundry.
    /// Safe to call on every startup — idempotent.
    /// </summary>
    public async Task EnsureAgentVersionAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Registering agent version '{AgentName}' with model '{Model}'...",
            AgentName, modelDeploymentName);

        var definition = new PromptAgentDefinition(model: modelDeploymentName)
        {
            Instructions = AgentInstructions
        };

        await projectClient.Agents.CreateAgentVersionAsync(
            AgentName,
            new AgentVersionCreationOptions(definition),
            ct);

        logger.LogInformation("Agent version registered successfully.");
    }

    /// <summary>
    /// End-to-end: fault → context lookup → agent prompt → WorkOrder saved to Cosmos DB.
    /// </summary>
    public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(
        DiagnosedFault fault,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Planning repair for machine '{MachineId}', fault '{FaultType}' (severity={Severity})...",
            fault.MachineId, fault.FaultType, fault.Severity);

        // ── Step 1: resolve required skills & part numbers from hardcoded mappings ──
        var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
        var requiredParts  = faultMapping.GetRequiredParts(fault.FaultType);

        logger.LogInformation("Required skills: [{Skills}]", string.Join(", ", requiredSkills));
        logger.LogInformation("Required parts:  [{Parts}]",  string.Join(", ", requiredParts));

        // ── Step 2: fetch available technicians and parts from Cosmos DB ──
        var technicians = await cosmosDb.GetAvailableTechniciansWithSkillsAsync(requiredSkills, ct);
        var parts       = await cosmosDb.GetPartsInventoryAsync(requiredParts, ct);
        logger.LogInformation("Found {TechCount} technician(s), {PartCount} part(s).",
            technicians.Count, parts.Count);

        // ── Step 3: build user prompt with full context ──
        var prompt = BuildPrompt(fault, technicians, parts, requiredSkills);
        logger.LogDebug("Prompt:\n{Prompt}", prompt);

        // ── Step 4: invoke the Foundry Agent with structured JSON output ──
        // Task 4 enhancement: use AIJsonUtilities.CreateJsonSchema + useJsonSchemaResponseFormat=true
        // so the model is constrained to emit valid WorkOrder JSON — no text parsing needed.
        var workOrder = await InvokeAgentStructuredAsync(prompt, fault, ct);

        // ── Step 5: apply sensible defaults for any fields the model left blank ──
        ApplyDefaults(workOrder, fault);

        // ── Step 6: persist to Cosmos DB ──
        var saved = await cosmosDb.CreateWorkOrderAsync(workOrder, ct);
        logger.LogInformation("Work order '{WorkOrderNumber}' saved (id={Id}).",
            saved.WorkOrderNumber, saved.Id);

        return saved;
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Task 4 enhancement: invoke agent with structured JSON output using
    /// AIJsonUtilities.CreateJsonSchema + useJsonSchemaResponseFormat=true.
    /// The model is forced to return a JSON object matching WorkOrder's schema,
    /// so RunAsync deserializes directly into WorkOrder — no text parsing needed.
    /// Falls back to text-based parsing if structured output fails.
    /// </summary>
    private async Task<WorkOrder> InvokeAgentStructuredAsync(
        string prompt, DiagnosedFault fault, CancellationToken ct)
    {
        logger.LogInformation("Invoking agent '{AgentName}' with structured JSON output...", AgentName);

        // AIJsonUtilities.CreateJsonSchema generates a JSON Schema from the WorkOrder type.
        // The model receives this schema and must produce a conforming JSON response.
        var schema = AIJsonUtilities.CreateJsonSchema(
            type: typeof(WorkOrder),
            serializerOptions: JsonOptions);

        var agent = projectClient.GetAIAgent(name: AgentName);

        try
        {
            // This overload constrains the model's output format to the WorkOrder JSON schema.
            // useJsonSchemaResponseFormat=true is equivalent to ChatResponseFormat.ForJsonSchema(schema).
            var structuredResponse = await agent.RunAsync<WorkOrder>(
                prompt,
                thread: null,
                serializerOptions: JsonOptions,
                options: null,
                useJsonSchemaResponseFormat: true,
                cancellationToken: ct);

            logger.LogInformation("Structured output received successfully.");
            logger.LogDebug("Schema used:\n{Schema}", schema);

            // structuredResponse.Result is the strongly-typed WorkOrder — no JSON parsing!
            return structuredResponse.Result
                ?? throw new InvalidOperationException("Agent returned null result.");
        }
        catch (Exception ex)
        {
            // Structured output may not be supported on all model deployments.
            // Fall back to text-based parsing if it fails.
            logger.LogWarning(ex, "Structured output failed — falling back to text parsing.");

            var fallbackResponse = await agent.RunAsync(prompt, thread: null, options: null);
            var raw = fallbackResponse.Text ?? string.Empty;
            logger.LogDebug("Raw fallback response:\n{Response}", raw);
            return ParseWorkOrder(raw, fault);
        }
    }

    private static string BuildPrompt(
        DiagnosedFault fault,
        IReadOnlyList<Technician> technicians,
        IReadOnlyList<Part> parts,
        IReadOnlyList<string> requiredSkills)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Diagnosed Fault");
        sb.AppendLine($"- Machine ID : {fault.MachineId}");
        sb.AppendLine($"- Fault Type : {fault.FaultType}");
        sb.AppendLine($"- Root Cause : {fault.RootCause}");
        sb.AppendLine($"- Severity   : {fault.Severity}");
        sb.AppendLine($"- Detected At: {fault.DetectedAt}");

        sb.AppendLine();
        sb.AppendLine("## Required Skills");
        foreach (var s in requiredSkills)
            sb.AppendLine($"- {s}");

        sb.AppendLine();
        sb.AppendLine("## Available Technicians");
        if (technicians.Count == 0)
        {
            sb.AppendLine("- (none available with matching skills)");
        }
        else
        {
            foreach (var t in technicians)
            {
                sb.AppendLine($"- id={t.Id} | name={t.Name} | department={t.Department} " +
                              $"| skills={string.Join(",", t.Skills)} | availability={t.Availability}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Available Parts");
        if (parts.Count == 0)
        {
            sb.AppendLine("- (no parts required or none in stock)");
        }
        else
        {
            foreach (var p in parts)
            {
                sb.AppendLine($"- id={p.Id} | partNumber={p.PartNumber} | name={p.Name} " +
                              $"| category={p.Category} | qty={p.QuantityInStock}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Based on the above, create a detailed repair plan and return it as valid JSON.");

        return sb.ToString();
    }

    /// <summary>
    /// Fallback: extract JSON from a text response and deserialize to WorkOrder.
    /// Handles models that wrap JSON in markdown code fences.
    /// </summary>
    private WorkOrder ParseWorkOrder(string raw, DiagnosedFault fault)
    {
        // Strip markdown code fences that some models add
        var start = raw.IndexOf('{');
        var end   = raw.LastIndexOf('}');
        var json  = (start >= 0 && end > start) ? raw[start..(end + 1)] : raw.Trim();

        WorkOrder? workOrder = null;
        try
        {
            workOrder = JsonSerializer.Deserialize<WorkOrder>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "JSON parse failed — building fallback work order.");
        }

        // Guarantee a non-null object even if parsing failed
        return workOrder ?? new WorkOrder
        {
            Title       = $"Repair: {fault.FaultType} on {fault.MachineId}",
            Description = fault.RootCause,
            Notes       = "Auto-generated fallback — agent response could not be parsed."
        };
    }

    /// <summary>
    /// Fills in any fields the model left empty, and stamps id/workOrderNumber/timestamps.
    /// Called after both structured and text-parsed responses.
    /// </summary>
    private void ApplyDefaults(WorkOrder workOrder, DiagnosedFault fault)
    {
        // Always overwrite id and number to guarantee uniqueness
        workOrder.Id              = Guid.NewGuid().ToString();
        workOrder.WorkOrderNumber = $"WO-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        workOrder.CreatedAt       = DateTime.UtcNow.ToString("o");

        workOrder.MachineId  = string.IsNullOrWhiteSpace(workOrder.MachineId) ? fault.MachineId : workOrder.MachineId;
        workOrder.Status     = string.IsNullOrWhiteSpace(workOrder.Status)    ? "new"           : workOrder.Status;
        workOrder.Type       = string.IsNullOrWhiteSpace(workOrder.Type)      ? "corrective"    : workOrder.Type;

        // Priority defaults based on fault severity (like Python's dict.get with default)
        workOrder.Priority = string.IsNullOrWhiteSpace(workOrder.Priority)
            ? fault.Severity?.ToLowerInvariant() switch
              {
                  "critical" => "critical",
                  "high"     => "high",
                  "low"      => "low",
                  _          => "medium"
              }
            : workOrder.Priority;

        workOrder.Tasks     ??= [];
        workOrder.PartsUsed ??= [];
    }
}
