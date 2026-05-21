// SPDX-FileCopyrightText: REBUS-ORBIT
// Hand-maintained C# mirror of shared/contracts/agent-protocol.json + .ts.
// Edit all three in the same commit and let CI's `npm run validate:contracts`
// catch any drift in the MessageType union.

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace PRISM.Contracts;

public static class Protocol
{
    public const int Version = 1;
}

[JsonConverter(typeof(StringEnumConverter), typeof(CamelCaseNamingStrategy))]
public enum MessageType
{
    Hello,
    Welcome,
    ServerPing,
    Heartbeat,
    Assign,
    Ack,
    Progress,
    Log,
    Complete,
    Fail,
    Cancel,
    PollLayers,
    Layers,
}

[JsonConverter(typeof(StringEnumConverter), typeof(CamelCaseNamingStrategy))]
public enum AgentRole
{
    Conversion,
    Layering,
    Receive,
}

[JsonConverter(typeof(StringEnumConverter), typeof(CamelCaseNamingStrategy))]
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public sealed class Envelope<TData>
{
    [JsonProperty("v")]    public int Version { get; set; } = Protocol.Version;
    [JsonProperty("type")] public MessageType Type { get; set; }
    [JsonProperty("id", NullValueHandling = NullValueHandling.Ignore)]
    public string? Id { get; set; }
    [JsonProperty("ts", NullValueHandling = NullValueHandling.Ignore)]
    public string? Timestamp { get; set; }
    [JsonProperty("data")] public TData Data { get; set; } = default!;

    public static Envelope<TData> New(MessageType type, TData data, string? id = null) => new()
    {
        Version = Protocol.Version,
        Type = type,
        Id = id,
        Timestamp = DateTime.UtcNow.ToString("o"),
        Data = data,
    };
}

/* -------------------------------------------------------------------------- */
/* Agent -> server                                                            */
/* -------------------------------------------------------------------------- */

public sealed class HelloData
{
    [JsonProperty("machineId")]    public string MachineId    { get; set; } = "";
    [JsonProperty("nodeName")]     public string NodeName     { get; set; } = "";
    [JsonProperty("slots")]        public int    Slots        { get; set; }
    [JsonProperty("formats")]      public string[] Formats    { get; set; } = Array.Empty<string>();
    [JsonProperty("roles")]        public AgentRole[] Roles   { get; set; } = Array.Empty<AgentRole>();
    [JsonProperty("agentVersion")] public string AgentVersion { get; set; } = "";
    [JsonProperty("rhinoVersion", NullValueHandling = NullValueHandling.Ignore)]
    public string? RhinoVersion { get; set; }
}

public sealed class HeartbeatData
{
    [JsonProperty("slotsBusy")] public int  SlotsBusy { get; set; }
    [JsonProperty("cpuPct",    NullValueHandling = NullValueHandling.Ignore)] public double? CpuPct    { get; set; }
    [JsonProperty("memUsedMb", NullValueHandling = NullValueHandling.Ignore)] public double? MemUsedMb { get; set; }
}

public sealed class AckData
{
    [JsonProperty("jobId")]    public string JobId    { get; set; } = "";
    [JsonProperty("accepted")] public bool   Accepted { get; set; }
    [JsonProperty("reason", NullValueHandling = NullValueHandling.Ignore)]
    public string? Reason { get; set; }
}

public sealed class ProgressData
{
    [JsonProperty("jobId")]   public string  JobId   { get; set; } = "";
    [JsonProperty("stage")]   public string  Stage   { get; set; } = "";
    [JsonProperty("percent", NullValueHandling = NullValueHandling.Ignore)] public double? Percent { get; set; }
    [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)] public string? Message { get; set; }
}

public sealed class LogData
{
    [JsonProperty("jobId")]   public string   JobId   { get; set; } = "";
    [JsonProperty("level")]   public LogLevel Level   { get; set; }
    [JsonProperty("message")] public string   Message { get; set; } = "";
}

public sealed class CompleteData
{
    [JsonProperty("jobId")]      public string JobId      { get; set; } = "";
    [JsonProperty("versionUrl",   NullValueHandling = NullValueHandling.Ignore)] public string? VersionUrl   { get; set; }
    [JsonProperty("rootObjectId", NullValueHandling = NullValueHandling.Ignore)] public string? RootObjectId { get; set; }
    [JsonProperty("versionId",    NullValueHandling = NullValueHandling.Ignore)] public string? VersionId    { get; set; }
    [JsonProperty("outputs",      NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, string>? Outputs { get; set; }
    [JsonProperty("stats",        NullValueHandling = NullValueHandling.Ignore)] public CompleteStats? Stats { get; set; }
}

public sealed class CompleteStats
{
    [JsonProperty("objects",     NullValueHandling = NullValueHandling.Ignore)] public int?  Objects     { get; set; }
    [JsonProperty("blobs",       NullValueHandling = NullValueHandling.Ignore)] public int?  Blobs       { get; set; }
    [JsonProperty("uploadBytes", NullValueHandling = NullValueHandling.Ignore)] public long? UploadBytes { get; set; }
    [JsonProperty("elapsedMs",   NullValueHandling = NullValueHandling.Ignore)] public long? ElapsedMs   { get; set; }
}

public sealed class FailData
{
    [JsonProperty("jobId")] public string JobId { get; set; } = "";
    [JsonProperty("error")] public string Error { get; set; } = "";
    [JsonProperty("stack",     NullValueHandling = NullValueHandling.Ignore)] public string? Stack     { get; set; }
    [JsonProperty("retryable", NullValueHandling = NullValueHandling.Ignore)] public bool?   Retryable { get; set; }
}

public sealed class LayerNode
{
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("fullPath", NullValueHandling = NullValueHandling.Ignore)] public string?     FullPath { get; set; }
    [JsonProperty("color",    NullValueHandling = NullValueHandling.Ignore)] public string?     Color    { get; set; }
    [JsonProperty("visible",  NullValueHandling = NullValueHandling.Ignore)] public bool?       Visible  { get; set; }
    [JsonProperty("children", NullValueHandling = NullValueHandling.Ignore)] public LayerNode[]? Children { get; set; }
}

public sealed class LayersData
{
    [JsonProperty("jobId")]  public string      JobId  { get; set; } = "";
    [JsonProperty("layers")] public LayerNode[] Layers { get; set; } = Array.Empty<LayerNode>();
}

/* -------------------------------------------------------------------------- */
/* Server -> agent                                                            */
/* -------------------------------------------------------------------------- */

public sealed class WelcomeData
{
    [JsonProperty("sessionId")]  public string SessionId  { get; set; } = "";
    [JsonProperty("serverTime")] public string ServerTime { get; set; } = "";
    [JsonProperty("heartbeatSeconds", NullValueHandling = NullValueHandling.Ignore)]
    public int? HeartbeatSeconds { get; set; }
}

public sealed class AssignOptions
{
    [JsonProperty("swapYZ",                  NullValueHandling = NullValueHandling.Ignore)] public bool?     SwapYZ                  { get; set; }
    [JsonProperty("quality",                 NullValueHandling = NullValueHandling.Ignore)] public string?   Quality                 { get; set; }
    [JsonProperty("includedLayers",          NullValueHandling = NullValueHandling.Ignore)] public string[]? IncludedLayers          { get; set; }
    [JsonProperty("includeLayerDescendants", NullValueHandling = NullValueHandling.Ignore)] public bool?     IncludeLayerDescendants { get; set; }
}

public sealed class AssignData
{
    [JsonProperty("jobId")]          public string JobId          { get; set; } = "";
    [JsonProperty("jobType", NullValueHandling = NullValueHandling.Ignore)] public string? JobType { get; set; }
    [JsonProperty("slot")]           public int    Slot           { get; set; }
    [JsonProperty("format")]         public string Format         { get; set; } = "";
    [JsonProperty("fileUrl",  NullValueHandling = NullValueHandling.Ignore)] public string? FileUrl  { get; set; }
    [JsonProperty("fileName", NullValueHandling = NullValueHandling.Ignore)] public string? FileName { get; set; }
    [JsonProperty("orbitServerUrl")] public string OrbitServerUrl { get; set; } = "";
    [JsonProperty("orbitToken")]     public string OrbitToken     { get; set; } = "";
    [JsonProperty("projectId")]      public string ProjectId      { get; set; } = "";
    [JsonProperty("modelId")]        public string ModelId        { get; set; } = "";
    [JsonProperty("modelName",         NullValueHandling = NullValueHandling.Ignore)] public string?   ModelName        { get; set; }
    [JsonProperty("receiveVersionId",  NullValueHandling = NullValueHandling.Ignore)] public string?   ReceiveVersionId { get; set; }
    [JsonProperty("outputFormats",     NullValueHandling = NullValueHandling.Ignore)] public string[]? OutputFormats    { get; set; }
    [JsonProperty("outputUploadUrl",   NullValueHandling = NullValueHandling.Ignore)] public string?   OutputUploadUrl  { get; set; }
    [JsonProperty("options",           NullValueHandling = NullValueHandling.Ignore)] public AssignOptions? Options     { get; set; }
}

public sealed class CancelData
{
    [JsonProperty("jobId")] public string JobId { get; set; } = "";
    [JsonProperty("reason", NullValueHandling = NullValueHandling.Ignore)]
    public string? Reason { get; set; }
}

public sealed class PollLayersData
{
    [JsonProperty("jobId")]   public string JobId   { get; set; } = "";
    [JsonProperty("fileUrl")] public string FileUrl { get; set; } = "";
    [JsonProperty("format")]  public string Format  { get; set; } = "";
}
