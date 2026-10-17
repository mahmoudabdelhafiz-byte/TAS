using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Infrastructure.Auth;

namespace Yardsyncs.Api;

// ---------------------------------------------------------------- request / response bodies

public sealed record LoginBody(string Email, string Password);

public sealed record RefreshBody(string RefreshToken);

public sealed record ChangePasswordBody(string CurrentPassword, string NewPassword);

public sealed record ForgotPasswordBody(string? Email);

public sealed record FollowBody(IReadOnlyList<string>? Containers);

public sealed record SetPasswordBody(string? Token, string? NewPassword);

/// <param name="CompanyId">Ignored for company users (taken from their account); required for terminal staff booking on behalf of a company.</param>
public sealed record CreateAppointmentBody(
    long SlotId,
    IReadOnlyList<BookingItemRequest>? Items,
    int? CompanyId = null,
    int? TruckId = null,
    int? DriverId = null);

public sealed record CancelBody(string? Reason);

public sealed record EraseBody(string? Reason);

public sealed record RescheduleBody(long SlotId);

public sealed record AssignBody(int? TruckId, int? DriverId);

public sealed record NotificationView(
    string Template,
    Yardsyncs.Domain.NotificationChannel Channel,
    string Language,
    Yardsyncs.Domain.NotificationStatus Status,
    string? Recipient,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserProfile User)
{
    public static TokenResponse From(AuthResult result) =>
        new(result.Tokens!.AccessToken, result.Tokens.AccessTokenExpiresAt, result.Tokens.RefreshToken, result.Tokens.RefreshTokenExpiresAt, result.User!);
}
