using System.Text.Json;
using System.Text.Json.Serialization;
using Yardsyncs.Domain;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure;

/// <summary>Adds audit entries to the context; they are saved with the change they describe.</summary>
public static class AuditWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Add(
        YardsyncsDbContext db, int? terminalId, string entity, object key, AuditAction action,
        object? before, object? after, string? reason, int userId, DateTimeOffset now)
    {
        var trimmed = reason?.Trim();
        db.AuditLogs.Add(new AuditLog
        {
            TerminalId = terminalId,
            EntityName = entity,
            EntityKey = key.ToString() ?? "",
            Action = action,
            OldValues = before is null ? null : JsonSerializer.Serialize(before, Json),
            NewValues = after is null ? null : JsonSerializer.Serialize(after, Json),
            Reason = string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, 300)],
            ChangedBy = userId,
            ChangedAt = now,
        });
    }
}
