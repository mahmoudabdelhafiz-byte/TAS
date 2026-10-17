using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Gate;
using Yardsyncs.Application.Tos;
using Yardsyncs.Autogate.Endpoints;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Autogate;

/// <summary>
/// Plugging Yardsyncs Autogate into Yardsyncs. With Modules:Autogate=false (a truck appointment system sold on its own)
/// nothing is registered or mapped: the gate API stays for the terminal's own gate system. Per terminal, the setting
/// module_autogate switches it off (404 on its endpoints, devices refused, gate hooks idle).
/// </summary>
public static class AutogateModule
{
    public static IServiceCollection AddYardsyncsAutogate(this IServiceCollection services, IConfiguration config)
    {
        if (!config.GetValue("Modules:Autogate", true))
            return services;

        services.AddScoped<DeviceService>();
        services.AddScoped<LaneDeviceService>();
        services.AddScoped<ILaneAddOn>(sp => sp.GetRequiredService<LaneDeviceService>());
        services.AddScoped<GateTicketService>();
        services.AddScoped<InspectionService>();
        services.Configure<LaneControllerOptions>(config.GetSection("LaneController"));
        services.AddScoped<LaneControllerService>();
        services.AddHostedService<LaneControllerWorker>();
        services.AddScoped<N4GateWriter>();
        services.AddScoped<ITosGateWriter, AutogateTosGateWriter>();
        services.Configure<TosGateOptions>(config.GetSection("TosGate"));
        services.AddScoped<TosGateService>();
        services.AddHostedService<TosGateWorker>();
        services.AddScoped<IGateHook, AutogateGateHook>();
        services.AddScoped<IExitCheck, AutogateExitCheck>();
        return services;
    }

    public static WebApplication MapAutogate(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<Modules>().AutogateInstalled)
            return app;
        app.MapDevices();
        app.MapInspections();
        app.MapAutogateGate();
        return app;
    }

    /// <summary>A route group under a terminal that answers 404 when the terminal doesn't have Autogate.</summary>
    internal static RouteGroupBuilder TerminalGroup(WebApplication app, string prefix)
    {
        var group = app.MapGroup(prefix).RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.Request.RouteValues.TryGetValue("terminalId", out var raw) && int.TryParse(raw?.ToString(), out var terminalId)
                && !await http.RequestServices.GetRequiredService<Modules>().AutogateAtAsync(http.RequestServices.GetRequiredService<YardsyncsDbContext>(), terminalId, http.RequestAborted))
                return YardsyncsApi.Problem(StatusCodes.Status404NotFound, "This terminal doesn't have the Yardsyncs Autogate module.");
            return await next(context);
        });
        return group;
    }
}

/// <summary>
/// Autogate's part in every gate passage, however it happened (a clerk, the terminal's gate system, an automatic lane):
/// the gate record for the TOS, and — on the trouble desk — opening an automatic lane's barrier for a truck let in.
/// </summary>
public sealed class AutogateGateHook(YardsyncsDbContext db, Modules modules, IServiceProvider services) : IGateHook
{
    public async Task GateInAsync(GatePassage p, CancellationToken ct)
    {
        if (await modules.AutogateAtAsync(db, p.TerminalId, ct))
            await TosGateService.QueueAsync(db, p.TerminalId, p.AppointmentId, TosGateStage.GateIn, p.LaneCode, p.At, ct);
    }

    public async Task GateOutAsync(GatePassage p, CancellationToken ct)
    {
        if (await modules.AutogateAtAsync(db, p.TerminalId, ct))
            await TosGateService.QueueAsync(db, p.TerminalId, p.AppointmentId, TosGateStage.GateOut, p.LaneCode, p.At, ct);
    }

    public async Task TroubleResolvedAsync(int terminalId, long troubleId, TroubleResolution resolution, CancellationToken ct)
    {
        // The lane controller uses the gate itself, so it is fetched here rather than injected.
        if (await modules.AutogateAtAsync(db, terminalId, ct))
            await services.GetRequiredService<LaneControllerService>().OnTroubleResolvedAsync(troubleId, resolution, ct);
    }
}

/// <summary>
/// The exit check with the lane cameras: the containers seen on a leaving truck (sent by the gate, or read by the exit
/// lane's cameras around the truck's plate) against its visit; and the record of the exit with the truck's weights.
/// Only exit lanes with a camera reading container numbers can check.
/// </summary>
public sealed class AutogateExitCheck(YardsyncsDbContext db, Modules modules) : IExitCheck
{
    public async Task<ExitCheckResult> CheckAsync(ExitContext exit, CancellationToken ct)
    {
        var none = new ExitCheckResult(ExitCheckStatus.NotChecked, [], null);
        if (!await modules.AutogateAtAsync(db, exit.TerminalId, ct))
            return none;
        var readsContainers = (await db.LaneDevices.AsNoTracking().Where(d => d.LaneId == exit.Lane.LaneId && d.IsActive).Select(d => d.Reads).ToListAsync(ct))
            .Any(r => LaneRules.ParseReads(r).Contains(LaneRead.Container));
        if (!readsContainers)
            return none;
        var seen = exit.ContainerReads ?? await SeenAtExitAsync(exit.Lane, exit.TruckId, exit.PlateRead, exit.At, ct);
        return ExitRules.Check(exit.Items, seen);
    }

    public async Task<decimal?> RecordAsync(ExitContext exit, ExitCheckResult result, int? letOutBy, string? note, CancellationToken ct)
    {
        if (!await modules.AutogateAtAsync(db, exit.TerminalId, ct))
            return null;
        var weightIn = await db.LaneTransactions.AsNoTracking()
            .Where(t => t.AppointmentId == exit.AppointmentId && t.WeightKg != null
                        && db.GateLanes.Any(l => l.LaneId == t.LaneId && l.Direction == LaneDirection.In))
            .OrderByDescending(t => t.LastReadAt).Select(t => t.WeightKg).FirstOrDefaultAsync(ct);
        var net = ExitRules.NetKg(weightIn, exit.WeightKg);

        var row = await db.GateExitChecks.SingleOrDefaultAsync(e => e.AppointmentId == exit.AppointmentId, ct);
        if (row is null)
        {
            row = new GateExitCheck { AppointmentId = exit.AppointmentId, TerminalId = exit.TerminalId };
            db.GateExitChecks.Add(row);
        }
        row.CheckedAt = exit.At;
        row.LaneCode = exit.Lane.LaneCode;
        row.ContainersSeen = result.Seen is null ? null : Clip(string.Join(",", result.Seen), 60);
        row.Status = result.Status;
        row.Problems = result.Problems.Count == 0 ? null : Clip(string.Join(" ", result.Problems.Select(p => p.Message)), 600);
        row.LetOutBy = letOutBy;
        row.Note = note;
        row.WeightInKg = weightIn;
        row.WeightOutKg = exit.WeightKg;

        // A single cargo move weighed in and out: its tonnage is the difference.
        if (net is { } kg && exit.Items.Count == 1 && exit.Items[0].Mode == CategoryMode.Cargo)
            await db.AppointmentItems.Where(i => i.AppointmentId == exit.AppointmentId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.ActualTonnage, Math.Round(kg / 1000m, 2)), ct);
        return net;
    }

    /// <summary>The containers the exit lane's cameras last saw on this truck (by plate, in the last 3 minutes), or null.</summary>
    private async Task<IReadOnlyList<string>?> SeenAtExitAsync(GateLane lane, int? truckId, string? plateRead, DateTimeOffset now, CancellationToken ct)
    {
        var plate = Yardsyncs.Application.MasterData.PlateNumber.Normalize(plateRead);
        if (plate.Length == 0 && truckId is not null)
            plate = await db.Trucks.AsNoTracking().Where(t => t.TruckId == truckId).Select(t => t.PlateNormalized).SingleOrDefaultAsync(ct) ?? "";
        if (plate.Length == 0)
            return null;
        // The truck's plate read, then every read at the lane within a minute of it (the rear camera reads the containers).
        var since = now.AddMinutes(-3);
        var at = await db.LaneDeviceReads.AsNoTracking()
            .Where(r => r.LaneId == lane.LaneId && r.ReceivedAt >= since && r.Plate == plate)
            .OrderByDescending(r => r.ReceivedAt).Select(r => (DateTimeOffset?)r.ReceivedAt).FirstOrDefaultAsync(ct);
        if (at is not { } t)
            return null;
        var reads = await db.LaneDeviceReads.AsNoTracking()
            .Where(r => r.LaneId == lane.LaneId && r.ReceivedAt >= t.AddMinutes(-1) && r.ReceivedAt <= t.AddMinutes(1))
            .ToListAsync(ct);
        return reads.SelectMany(r => DeviceService.Normalized(r).Containers.Select(c => c.Number)).Distinct().ToList();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>Gate records go to the terminal's TOS: N4's gate web service, or the demo TOS.</summary>
public sealed class AutogateTosGateWriter(IServiceProvider services, TerminalProfiles profiles, YardsyncsDbContext db) : ITosGateWriter
{
    public async Task<TosGateResult> RecordAsync(int terminalId, TosGateRequest request, CancellationToken ct = default) =>
        await (profiles.TosFor(await TerminalProfiles.CodeAsync(db, terminalId, ct)) switch
        {
            "N4" => (ITosGateWriter)services.GetRequiredService<N4GateWriter>(),
            _ => services.GetRequiredService<FakeTosAdapter>(),
        }).RecordAsync(terminalId, request, ct);
}
