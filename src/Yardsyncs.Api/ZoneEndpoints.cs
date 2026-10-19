using Yardsyncs.Infrastructure.Zone;

namespace Yardsyncs.Api;

/// <summary>
/// The zone system around the terminals (Nafith) telling Yardsyncs about trucks in its yard and clearance changes.
/// No user: the zone system sends its key (Nafith__InboundKey) in X-Zone-Key. See docs/nafith.md.
/// </summary>
public static class ZoneEndpoints
{
    public static WebApplication MapZone(this WebApplication app)
    {
        app.MapPost("/api/zone/events", async (ZoneEventInput body, HttpContext http, ZoneEventService service, CancellationToken ct) =>
        {
            if (!service.KeyMatches(http.Request.Headers["X-Zone-Key"].FirstOrDefault()))
                return Results.Unauthorized();
            return YardsyncsApi.Admin(await service.ApplyAsync(body, ct));
        }).AllowAnonymous().RequireRateLimiting("device"); // a busy yard sends many events a minute
        return app;
    }
}
