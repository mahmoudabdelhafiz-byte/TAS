namespace Yardsyncs.Domain.Gate;

/// <summary>
/// A key for the terminal's own gate system (OCR portal, kiosk, gate operating system) to use the gate API, when the
/// terminal runs the truck appointment system without Yardsyncs Autogate. The key acts as its own gate clerk account
/// (no password, can't sign in). Only the key's SHA-256 is kept.
/// </summary>
public sealed class GateSystemKey
{
    public int KeyId { get; set; }
    public int TerminalId { get; set; }

    /// <summary>The gate clerk account the key acts as.</summary>
    public int UserId { get; set; }

    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";

    /// <summary>The key's last characters, to tell keys apart.</summary>
    public string KeyHint { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public int? CreatedBy { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public int? RevokedBy { get; set; }
}
