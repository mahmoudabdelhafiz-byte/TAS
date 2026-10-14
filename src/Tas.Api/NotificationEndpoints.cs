using Tas.Application.Auth;
using Tas.Domain;
using Tas.Infrastructure.Notifications;

namespace Tas.Api;

/// <summary>
/// The notification outbox under /api/terminals/{terminalId}/notifications.
/// Viewing: terminal staff of the terminal. Sending a failed message again: planners and terminal admins.
/// </summary>
public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotifications(this RouteGroupBuilder terminal)
    {
        var notifications = terminal.MapGroup("/notifications");

        notifications.MapGet("/", async (
            int terminalId, NotificationStatus? status, string? appointmentNo, int? limit,
            HttpContext http, NotificationQueryService service, CancellationToken ct) =>
            !AccessRules.CanOperateGate(TasApi.User(http)!, terminalId)
                ? TasApi.Forbidden("Only terminal staff of this terminal can see the notification log.")
                : Results.Ok(await service.ListAsync(terminalId, status, appointmentNo, limit, ct)));

        notifications.MapPost("/{notificationId:long}/retry", async (
            int terminalId, long notificationId, HttpContext http, NotificationQueryService service, CancellationToken ct) =>
        {
            if (!AccessRules.CanManageCapacity(TasApi.User(http)!, terminalId))
                return TasApi.Forbidden("Only planners and terminal admins of this terminal can re-send notifications.");

            var (status, error) = await service.RetryAsync(terminalId, notificationId, ct);
            return status switch
            {
                RetryStatus.Ok => Results.Accepted(),
                RetryStatus.NotFound => TasApi.Problem(StatusCodes.Status404NotFound, error!),
                _ => TasApi.Problem(StatusCodes.Status409Conflict, error!),
            };
        });

        return terminal;
    }
}
