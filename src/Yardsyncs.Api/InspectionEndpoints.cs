using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Gate;
using Yardsyncs.Infrastructure.Brands;
using Yardsyncs.Infrastructure.Gate;

namespace Yardsyncs.Api;

/// <summary>
/// Inspection at the gate: seals and damage per container and passage, photos, and disputes. Gate staff inspect;
/// companies see their own visits, declare seals and dispute; planners and admins resolve disputes.
/// </summary>
public static class InspectionEndpoints
{
    public static WebApplication MapInspections(this WebApplication app)
    {
        var terminal = app.MapGroup("/api/terminals/{terminalId:int}").RequireAuthorization();

        terminal.MapGet("/appointments/{appointmentNo}/inspections", async (int terminalId, string appointmentNo, HttpContext http, InspectionService service, CancellationToken ct) =>
            NoAccess(http, terminalId) ?? (await service.ForAppointmentAsync(YardsyncsApi.User(http)!, terminalId, appointmentNo, ct) is { } found
                ? Results.Ok(found)
                : YardsyncsApi.Problem(StatusCodes.Status404NotFound, $"Appointment {appointmentNo} not found.")));

        // The company declares the seal of a container (move number 1, 2 …) until the truck is at the gate.
        terminal.MapPut("/appointments/{appointmentNo}/items/{index:int}/seal", async (
            int terminalId, string appointmentNo, int index, SealInput body, HttpContext http, InspectionService service, CancellationToken ct) =>
            NoAccess(http, terminalId) ?? YardsyncsApi.Admin(await service.DeclareSealAsync(YardsyncsApi.User(http)!, terminalId, appointmentNo, index, body, ct)));

        terminal.MapPost("/appointments/{appointmentNo}/inspections", async (
            int terminalId, string appointmentNo, InspectionInput body, HttpContext http, InspectionService service, CancellationToken ct) =>
            NotGate(http, terminalId) ?? YardsyncsApi.Admin(await service.SaveAsync(YardsyncsApi.User(http)!, terminalId, appointmentNo, body, ct)));

        terminal.MapPost("/inspections/{inspectionId:long}/photos", async (
            int terminalId, long inspectionId, IFormFile? file, HttpContext http, InspectionService service, CancellationToken ct) =>
        {
            if (NotGate(http, terminalId) is { } denied)
                return denied;
            if (file is null || file.Length is 0 or > InspectionRules.MaxPhotoBytes)
                return YardsyncsApi.Problem(StatusCodes.Status400BadRequest, "Send the photo as the form field 'file' (JPEG, PNG or WebP, at most 3 MB).");
            using var stream = new MemoryStream();
            await file.CopyToAsync(stream, ct);
            var bytes = stream.ToArray();
            var type = BrandingService.ImageType(bytes) ?? "";
            return YardsyncsApi.Admin(await service.AddPhotoAsync(YardsyncsApi.User(http)!, terminalId, inspectionId, type, bytes, ct),
                id => $"/api/terminals/{terminalId}/inspections/photos/{id}");
        }).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(InspectionRules.MaxPhotoBytes + 16 * 1024));

        terminal.MapGet("/inspections/photos/{photoId:long}", async (int terminalId, long photoId, HttpContext http, InspectionService service, CancellationToken ct) =>
        {
            if (NoAccess(http, terminalId) is { } denied)
                return denied;
            var photo = await service.PhotoAsync(YardsyncsApi.User(http)!, terminalId, photoId, ct);
            if (photo is null)
                return YardsyncsApi.Problem(StatusCodes.Status404NotFound, $"Photo {photoId} not found.");
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(photo.Value.Bytes, photo.Value.ContentType);
        });

        terminal.MapGet("/inspections", async (
            int terminalId, DateOnly? from, DateOnly? to, bool? damageOnly, bool? disputedOnly, HttpContext http, InspectionService service, TimeProvider clock, CancellationToken ct) =>
        {
            if (NoAccess(http, terminalId) is { } denied)
                return denied;
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var start = from ?? today.AddDays(-7);
            var end = (to ?? today).AddDays(1);
            if (end.DayNumber - start.DayNumber > 92)
                return YardsyncsApi.Problem(StatusCodes.Status400BadRequest, "At most 92 days at a time.");
            return Results.Ok(await service.ListAsync(YardsyncsApi.User(http)!, terminalId,
                new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(-14),
                new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(14),
                damageOnly ?? false, disputedOnly ?? false, ct));
        });

        terminal.MapPost("/inspections/{inspectionId:long}/dispute", async (
            int terminalId, long inspectionId, DisputeInput body, HttpContext http, InspectionService service, CancellationToken ct) =>
            NoAccess(http, terminalId) ?? YardsyncsApi.Admin(await service.OpenDisputeAsync(YardsyncsApi.User(http)!, terminalId, inspectionId, body, ct)));

        terminal.MapPost("/disputes/{disputeId:long}/resolve", async (
            int terminalId, long disputeId, ResolveDisputeInput body, HttpContext http, InspectionService service, CancellationToken ct) =>
            !AccessRules.CanManageCapacity(YardsyncsApi.User(http)!, terminalId)
                ? YardsyncsApi.Forbidden("Disputes are settled by the terminal's planners and admins.")
                : YardsyncsApi.Admin(await service.ResolveDisputeAsync(YardsyncsApi.User(http)!, terminalId, disputeId, body, ct)));

        return app;
    }

    private static IResult? NoAccess(HttpContext http, int terminalId) =>
        AccessRules.CanAccessTerminal(YardsyncsApi.User(http)!, terminalId) ? null : YardsyncsApi.Forbidden("You have no access to this terminal.");

    private static IResult? NotGate(HttpContext http, int terminalId) =>
        AccessRules.CanOperateGate(YardsyncsApi.User(http)!, terminalId) ? null : YardsyncsApi.Forbidden("Only terminal staff of this terminal inspect at the gate.");
}
