using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticSdlc.Core.Model;

namespace AgenticSdlc.Core.Observability;

/// <summary>One immutable audit record. Each record carries the hash of its predecessor, making the log tamper-evident.</summary>
public sealed class AuditEvent
{
    public long Seq { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required string RunId { get; init; }
    public string? NodeId { get; init; }
    public int? Attempt { get; init; }
    public required string Type { get; init; }
    public required Actor Actor { get; init; }
    public string? ActorName { get; init; }
    public required string Message { get; init; }
    public Dictionary<string, object?> Data { get; init; } = new();
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

/// <summary>
/// Append-only JSON-lines audit log with a SHA-256 hash chain. Every orchestration decision, agent call,
/// gate result, approval, retry, rollback and policy verdict is recorded here.
/// </summary>
public sealed class AuditLog : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonSerializerOptions SerializerOptions => Options;

    private readonly string _path;
    private readonly object _lock = new();
    private long _seq;
    private string _lastHash = "GENESIS";
    private readonly List<Action<AuditEvent>> _sinks = new();

    public AuditLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var ev = JsonSerializer.Deserialize<AuditEvent>(line, Options)!;
                _seq = ev.Seq;
                _lastHash = ev.Hash;
            }
        }
    }

    public string Path_ => _path;

    /// <summary>Attach an additional sink (console, dashboard stream, etc.).</summary>
    public void AddSink(Action<AuditEvent> sink) => _sinks.Add(sink);

    public AuditEvent Append(string runId, string type, Actor actor, string message,
        string? nodeId = null, int? attempt = null, Dictionary<string, object?>? data = null, string? actorName = null)
    {
        lock (_lock)
        {
            var ev = new AuditEvent
            {
                Seq = ++_seq,
                RunId = runId,
                NodeId = nodeId,
                Attempt = attempt,
                Type = type,
                Actor = actor,
                ActorName = actorName,
                Message = message,
                Data = data ?? new(),
                PrevHash = _lastHash
            };
            ev.Hash = ComputeHash(ev);
            _lastHash = ev.Hash;
            File.AppendAllText(_path, JsonSerializer.Serialize(ev, Options) + Environment.NewLine);
            foreach (var sink in _sinks) sink(ev);
            return ev;
        }
    }

    public static string ComputeHash(AuditEvent ev)
    {
        var payload = $"{ev.Seq}|{ev.Timestamp:O}|{ev.RunId}|{ev.NodeId}|{ev.Attempt}|{ev.Type}|{ev.Actor}|{ev.Message}|{JsonSerializer.Serialize(ev.Data, Options)}|{ev.PrevHash}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static IReadOnlyList<AuditEvent> Read(string path)
    {
        if (!File.Exists(path)) return Array.Empty<AuditEvent>();
        return File.ReadLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<AuditEvent>(l, Options)!)
            .ToList();
    }

    /// <summary>Verifies the hash chain. Returns (ok, firstBrokenSeq).</summary>
    public static (bool Ok, long? BrokenAtSeq) Verify(string path)
    {
        var prev = "GENESIS";
        foreach (var ev in Read(path))
        {
            if (ev.PrevHash != prev || ComputeHash(ev) != ev.Hash) return (false, ev.Seq);
            prev = ev.Hash;
        }
        return (true, null);
    }

    public void Dispose() { }
}

/// <summary>Well-known audit event types, kept as constants so dashboards and tests can filter reliably.</summary>
public static class AuditTypes
{
    public const string RunCreated = "RunCreated";
    public const string RunStatusChanged = "RunStatusChanged";
    public const string WorkflowPlanned = "WorkflowPlanned";
    public const string WorkflowReplanned = "WorkflowReplanned";
    public const string NodeReady = "NodeReady";
    public const string NodeStarted = "NodeStarted";
    public const string NodeSucceeded = "NodeSucceeded";
    public const string NodeFailed = "NodeFailed";
    public const string NodeInvalidated = "NodeInvalidated";
    public const string EntryGate = "EntryGate";
    public const string ExitGate = "ExitGate";
    public const string LlmCall = "LlmCall";
    public const string PolicyCheck = "PolicyCheck";
    public const string PolicyViolation = "PolicyViolation";
    public const string ApprovalRequested = "ApprovalRequested";
    public const string ApprovalDecided = "ApprovalDecided";
    public const string ClarificationRequested = "ClarificationRequested";
    public const string ClarificationAnswered = "ClarificationAnswered";
    public const string Retry = "Retry";
    public const string RepairCycle = "RepairCycle";
    public const string Fallback = "Fallback";
    public const string Snapshot = "Snapshot";
    public const string Rollback = "Rollback";
    public const string SafeStop = "SafeStop";
    public const string WorkspaceWrite = "WorkspaceWrite";
    public const string Metrics = "Metrics";
    public const string Warning = "Warning";
}
