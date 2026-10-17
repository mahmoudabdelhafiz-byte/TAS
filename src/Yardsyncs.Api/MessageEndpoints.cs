using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Languages;
using Yardsyncs.Application.Messages;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure.Auth;
using Yardsyncs.Infrastructure.Messages;

namespace Yardsyncs.Api;

/// <summary>
/// Messages between a visit's driver and the terminal's control room: the driver app under /api/driver, the control
/// room (any terminal staff) under /api/terminals/{terminalId}/messages.
/// </summary>
public static class MessageEndpoints
{
    public static WebApplication MapMessages(this WebApplication app)
    {
        var driver = app.MapGroup("/api/driver").RequireAuthorization(DriverAuth.Policy);

        // Ready-made messages the driver can send, in the app's language.
        driver.MapGet("/message-templates", () => Results.Ok(MessageRules.TemplatesFor(MessageDirection.FromDriver, ServerText.Language)));

        // The visit's messages; reading marks the control room's messages read.
        driver.MapGet("/appointments/{appointmentNo}/messages", async (string appointmentNo, HttpContext http, MessageService service, CancellationToken ct) =>
            YardsyncsApi.Admin(await service.ForDriverAsync(Driver(http), appointmentNo, ct)));

        driver.MapPost("/appointments/{appointmentNo}/messages", async (
            string appointmentNo, MessageInput body, HttpContext http, MessageService service, CancellationToken ct) =>
            YardsyncsApi.Admin(await service.FromDriverAsync(Driver(http), appointmentNo, body, ct)));

        var room = app.MapGroup("/api/terminals/{terminalId:int}/messages").RequireAuthorization();

        // Visits with messages, unread first.
        room.MapGet("/", async (int terminalId, HttpContext http, MessageService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? Results.Ok(await service.ConversationsAsync(terminalId, ct)));

        room.MapGet("/templates", (int terminalId, HttpContext http) =>
            Denied(http, terminalId) ?? Results.Ok(MessageRules.TemplatesFor(MessageDirection.ToDriver, ServerText.Language)));

        // One visit's messages; reading marks the driver's messages read.
        room.MapGet("/{appointmentNo}", async (int terminalId, string appointmentNo, HttpContext http, MessageService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(await service.ForControlRoomAsync(terminalId, appointmentNo, ct)));

        // To the driver: kept here and sent to their phone (push, else SMS / WhatsApp).
        room.MapPost("/{appointmentNo}", async (
            int terminalId, string appointmentNo, MessageInput body, HttpContext http, MessageService service, CancellationToken ct) =>
            Denied(http, terminalId) ?? YardsyncsApi.Admin(await service.ToDriverAsync(YardsyncsApi.User(http)!, terminalId, appointmentNo, body, ct)));

        return app;
    }

    private static CurrentDriver Driver(HttpContext http) => CurrentDriver.FromClaims(http.User)!;

    private static IResult? Denied(HttpContext http, int terminalId) =>
        AccessRules.CanOperateGate(YardsyncsApi.User(http)!, terminalId)
            ? null
            : YardsyncsApi.Forbidden("Only terminal staff of this terminal can read and write drivers' messages.");
}
