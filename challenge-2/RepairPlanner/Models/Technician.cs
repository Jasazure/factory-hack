using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlannerAgent.Models;

/// <summary>
/// A maintenance technician read from the Cosmos DB Technicians container.
/// Partition key: /department
/// </summary>
public sealed class Technician
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("department")]
    [JsonProperty("department")]
    public string Department { get; set; } = string.Empty;

    // e.g. ["tire_curing_press", "temperature_control", ...]
    [JsonPropertyName("skills")]
    [JsonProperty("skills")]
    public List<string> Skills { get; set; } = [];

    // "available" | "on_assignment" | "off_duty"
    [JsonPropertyName("availability")]
    [JsonProperty("availability")]
    public string Availability { get; set; } = string.Empty;
}
