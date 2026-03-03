using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlannerAgent.Models;

/// <summary>
/// An individual repair step inside a WorkOrder.
/// </summary>
public sealed class RepairTask
{
    // Order in which this task should be performed
    [JsonPropertyName("sequence")]
    [JsonProperty("sequence")]
    public int Sequence { get; set; }

    [JsonPropertyName("title")]
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    // Integer minutes — LLMs sometimes return strings, handled via NumberHandling
    [JsonPropertyName("estimatedDurationMinutes")]
    [JsonProperty("estimatedDurationMinutes")]
    public int EstimatedDurationMinutes { get; set; }

    [JsonPropertyName("requiredSkills")]
    [JsonProperty("requiredSkills")]
    public List<string> RequiredSkills { get; set; } = [];

    [JsonPropertyName("safetyNotes")]
    [JsonProperty("safetyNotes")]
    public string SafetyNotes { get; set; } = string.Empty;
}
