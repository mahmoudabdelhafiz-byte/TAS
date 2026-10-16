using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.Gate;

namespace Yardsyncs.Api;

/// <summary>
/// The device API under /api/devices, for lane devices and the vendor adapters speaking for them. Each device signs in
/// with its own key in the X-Device-Key header (issued by a terminal admin on the Lanes screen). See the technical page
/// "Device adapters".
/// </summary>
public static class DeviceEndpoints
{
    public const string KeyHeader = "X-Device-Key";
    private const string DeviceItem = "yardsyncs.device";

    public static WebApplication MapDevices(this WebApplication app)
    {
        var devices = app.MapGroup("/api/devices")
            .AllowAnonymous()
            .RequireRateLimiting("device")
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                var service = http.RequestServices.GetRequiredService<DeviceService>();
                var device = await service.AuthenticateAsync(http.Request.Headers[KeyHeader].FirstOrDefault(), http.RequestAborted);
                if (device is null)
                    return YardsyncsApi.Problem(StatusCodes.Status401Unauthorized,
                        $"Send the device's key in {KeyHeader}. The key is unknown, was replaced, or the device or its lane is no longer in use.");
                http.Items[DeviceItem] = device;
                return await next(context);
            });

        // Who am I: to check the key while setting up an adapter.
        devices.MapGet("/me", async (HttpContext http, DeviceService service, CancellationToken ct) =>
            Results.Ok(await service.IdentityAsync(Device(http), ct)));

        // Alive and well (or not). Every 30 seconds; any call counts as contact.
        devices.MapPost("/status", async (DeviceStatusInput body, HttpContext http, DeviceService service, CancellationToken ct) =>
            YardsyncsApi.Admin(await service.StatusAsync(Device(http), body, ct)));

        // A read is filed under its truck's transaction; at an automatic lane the lane controller decides from there.
        devices.MapPost("/reads", async (DeviceReadInput body, HttpContext http, DeviceService service, LaneControllerService controller, CancellationToken ct) =>
        {
            var result = await service.ReadAsync(Device(http), body, ct);
            if (result.Value is { } read)
                await controller.OnReadAsync(read.ReadId, ct);
            return YardsyncsApi.Admin(result, r => $"/api/devices/reads/{r.ReadId}");
        });

        // Commands to carry out now (each handed out until confirmed or expired).
        devices.MapGet("/commands", async (HttpContext http, DeviceService service, CancellationToken ct) =>
            Results.Ok(await service.PendingAsync(Device(http), ct)));

        // A confirmed barrier opening gates the admitted truck in.
        devices.MapPost("/commands/{commandId:long}/ack", async (
            long commandId, DeviceAckInput body, HttpContext http, DeviceService service, LaneControllerService controller, CancellationToken ct) =>
        {
            var result = await service.AckAsync(Device(http), commandId, body, ct);
            if (result.Value is not null)
                await controller.OnCommandConfirmedAsync(commandId, ct);
            return YardsyncsApi.Admin(result);
        });

        return app;
    }

    private static GateLaneDevice Device(HttpContext http) => (GateLaneDevice)http.Items[DeviceItem]!;
}
