namespace Tas.Domain.Parties;

public enum PasswordTokenPurpose
{
    /// <summary>Forgotten password: the user asked for a link to set a new password.</summary>
    Reset,

    /// <summary>New user invited by an admin: sets their own first password.</summary>
    Invite,
}

/// <summary>
/// A one-time link to set a password. Only the SHA-256 hash of the token is stored; the token itself reaches
/// the user by email (encrypted in the notification outbox until it is sent).
/// </summary>
public sealed class PasswordToken
{
    public long PasswordTokenId { get; set; }
    public int UserId { get; set; }
    public PasswordTokenPurpose Purpose { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when the password was set with it, or when it was cancelled by a newer token or another password change.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    public string? CreatedIp { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;
}
