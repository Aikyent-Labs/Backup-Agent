using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>Sent once, immediately after the WebSocket connection is established.</summary>
public sealed class HelloMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = ProtocolMessageTypes.Hello;

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("serverId")]
    public required string ServerId { get; init; }

    [JsonPropertyName("machineId")]
    public required string MachineId { get; init; }

    [JsonPropertyName("machineName")]
    public required string MachineName { get; init; }

    [JsonPropertyName("agentVersion")]
    public required string AgentVersion { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Agent protocol version. Absent from older agents (treated as "1" by the backend).
    /// Version negotiation per CO-DEV-BKP-001 §3 "Agent Gateway ... version negotiation".
    /// </summary>
    [JsonPropertyName("protocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtocolVersion { get; init; }

    /// <summary>
    /// Capability profile advertised by this build (e.g. "backup.v1"), so the backend only sends
    /// typed commands this agent can execute. Absent from older agents.
    /// </summary>
    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>Installed backup engines and versions, e.g. {"kopia":"0.21.1"}. Absent if none.</summary>
    [JsonPropertyName("engines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Engines { get; init; }

    /// <summary>"backup" (standalone CloudOrc Backup Agent) or "control" (Server Agent). Absent from older agents.</summary>
    [JsonPropertyName("agentKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentKind { get; init; }

    /// <summary>"windows", "linux" or "macos".</summary>
    [JsonPropertyName("osFamily")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OsFamily { get; init; }

    /// <summary>Operating system description, e.g. "Ubuntu 24.04.1 LTS" or "Microsoft Windows 10.0.20348".</summary>
    [JsonPropertyName("osVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OsVersion { get; init; }

    /// <summary>"x64" or "arm64".</summary>
    [JsonPropertyName("architecture")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Architecture { get; init; }
}
