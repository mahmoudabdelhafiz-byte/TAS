using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Gate;

/// <summary>
/// The Autogate lane controller: one transaction per truck at an automatic lane. Reads from the lane's devices are
/// gathered into the truck's transaction; once the lane has something to identify the truck by and has been quiet for
/// a moment (at once for a kiosk entry or gate pass), the gate decides exactly as it does for a clerk — match, re-check
/// with the TOS, admit or refer. Admitted: green, barrier open, a welcome and the gate ticket; the barrier's confirmation
/// gates the truck in. Referred: red, the reason on the display, and the case on the trouble desk. At an exit lane the
/// truck is gated out and the barrier opened, or a clerk is called. Manual lanes are left to the clerk.
/// It runs inside Yardsyncs, next to the lanes when Yardsyncs is installed at the terminal.
/// </summary>
public sealed class LaneControllerService(
    YardsyncsDbContext db,
    GateService gate,
    DeviceService devices,
    InspectionService inspections,
    TimeProvider clock,
    IOptions<LaneControllerOptions> options,
    ILogger<LaneControllerService> logger)
{
    private LaneControllerOptions O => options.Value;

    /// <summary>A read has arrived: file it under its truck's transaction and decide when there is enough.</summary>
    public async Task<LaneTransactionView?> OnReadAsync(long readId, CancellationToken ct = default)
    {
        var row = await db.LaneDeviceReads.SingleOrDefaultAsync(r => r.ReadId == readId, ct);
        if (row is null)
            return null;
        var lane = await db.GateLanes.AsNoTracking().SingleAsync(l => l.LaneId == row.LaneId, ct);
        if (lane.Mode != LaneMode.Automatic || lane.State != LaneState.Open || !lane.IsActive)
            return null;

        var now = clock.GetUtcNow();
        var read = DeviceService.Normalized(row);
        var latest = await db.LaneTransactions
            .Where(t => t.LaneId == lane.LaneId)
            .OrderByDescending(t => t.LastReadAt).ThenByDescending(t => t.TransactionId)
            .FirstOrDefaultAsync(ct);
        var tx = latest is not null && LaneControllerRules.Belongs(latest, read, now, TimeSpan.FromSeconds(O.WindowSeconds)) ? latest : null;
        if (tx is null)
        {
            tx = new LaneTransaction { TerminalId = lane.TerminalId, LaneId = lane.LaneId, StartedAt = now, LastReadAt = now };
            db.LaneTransactions.Add(tx);
        }
        LaneControllerRules.Merge(tx, read, now);
        await db.SaveChangesAsync(ct);
        row.TransactionId = tx.TransactionId;
        await db.SaveChangesAsync(ct);

        if (LaneControllerRules.Ready(tx, now, TimeSpan.FromSeconds(O.SettleSeconds)))
            return await DecideAsync(tx.TransactionId, ct);
        return LaneTransactionView.From(tx, lane.LaneCode);
    }

    /// <summary>Decide the transactions whose lane has been quiet long enough; give up on those with nothing to go on.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var open = await db.LaneTransactions.AsNoTracking().Where(t => t.Status == LaneTransactionStatus.Open).ToListAsync(ct);
        var done = 0;
        foreach (var t in open)
        {
            if (LaneControllerRules.Ready(t, now, TimeSpan.FromSeconds(O.SettleSeconds)))
            {
                await DecideAsync(t.TransactionId, ct);
                done++;
            }
            else if (LaneControllerRules.Abandon(t, now, TimeSpan.FromSeconds(O.AbandonSeconds)))
            {
                await db.LaneTransactions.Where(x => x.TransactionId == t.TransactionId && x.Status == LaneTransactionStatus.Open)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Status, LaneTransactionStatus.Abandoned)
                        .SetProperty(x => x.ClosedAt, now)
                        .SetProperty(x => x.Message, "Nothing to identify the truck by."), ct);
                done++;
            }
        }
        return done;
    }

    /// <summary>A device confirmed a command: an opened barrier gates the admitted truck in; a barrier that failed calls a clerk.</summary>
    public async Task OnCommandConfirmedAsync(long commandId, CancellationToken ct = default)
    {
        var command = await db.LaneDeviceCommands.AsNoTracking().SingleOrDefaultAsync(c => c.CommandId == commandId, ct);
        if (command is not { TransactionId: { } txId, Command: LaneCommand.Open })
            return;
        var tx = await db.LaneTransactions.SingleOrDefaultAsync(t => t.TransactionId == txId, ct);
        if (tx is null || tx.Status != LaneTransactionStatus.Admitted)
            return;
        var lane = await db.GateLanes.AsNoTracking().SingleAsync(l => l.LaneId == tx.LaneId, ct);
        var now = clock.GetUtcNow();

        if (command.Result == CommandResult.Failed)
        {
            tx.Status = LaneTransactionStatus.Referred;
            tx.Reason = "BARRIER";
            tx.Message = Clip($"The barrier did not open{(command.ResultDetail is null ? "" : $": {command.ResultDetail}")}. Let the truck through by hand.");
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Lane {Lane}: barrier failed for transaction {Tx}", lane.LaneCode, tx.TransactionId);
            return;
        }

        if (lane.Direction == LaneDirection.In && tx.AppointmentNo is not null)
        {
            // A truck a clerk let in is already gated in (Conflict): nothing more to do.
            var result = await gate.GateInAsync(null, tx.TerminalId, new GateInInput(tx.AppointmentNo, lane.LaneCode), ct);
            if (result.Status is not (Yardsyncs.Application.Capacity.AdminStatus.Ok or Yardsyncs.Application.Capacity.AdminStatus.Conflict))
                logger.LogWarning("Lane {Lane}: gate-in of {No} after the barrier opened failed: {Errors}", lane.LaneCode, tx.AppointmentNo, string.Join(" ", result.Errors));
        }
        await db.LaneTransactions.Where(t => t.TransactionId == txId && t.Status == LaneTransactionStatus.Admitted)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, LaneTransactionStatus.Passed).SetProperty(t => t.ClosedAt, now), ct);

        // What the cameras saw (seals, damage) becomes the passage's inspection.
        await inspections.FromLaneAsync(txId, ct);
    }

    /// <summary>
    /// A clerk resolved a truck the lane referred: let in (or booked as a walk-in) opens that lane's barrier; turned away
    /// tells the driver on the display. Only while the truck can still be at the lane (10 minutes).
    /// </summary>
    public async Task OnTroubleResolvedAsync(long troubleId, TroubleResolution resolution, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var tx = await db.LaneTransactions.SingleOrDefaultAsync(t => t.TroubleId == troubleId && t.Status == LaneTransactionStatus.Referred, ct);
        if (tx is null || now - tx.LastReadAt > TimeSpan.FromMinutes(10))
            return;
        var lane = await db.GateLanes.AsNoTracking().SingleAsync(l => l.LaneId == tx.LaneId, ct);
        if (lane.Mode != LaneMode.Automatic)
            return;

        IReadOnlyList<PlannedCommand> plan;
        if (resolution is TroubleResolution.Admitted or TroubleResolution.WalkIn)
        {
            var trouble = await db.GateTroubles.AsNoTracking().SingleAsync(t => t.TroubleId == troubleId, ct);
            var number = trouble.AppointmentId is { } id
                ? await db.Appointments.AsNoTracking().Where(a => a.AppointmentId == id).Select(a => a.AppointmentNo).SingleOrDefaultAsync(ct)
                : tx.AppointmentNo;
            tx.Status = LaneTransactionStatus.Admitted;
            tx.AppointmentId = trouble.AppointmentId ?? tx.AppointmentId;
            tx.AppointmentNo = number ?? tx.AppointmentNo;
            tx.Message = "Let in by a clerk.";
            plan = LaneControllerRules.Plan(lane.Direction, true, tx.AppointmentNo, null, null);
        }
        else if (resolution == TroubleResolution.Rejected)
        {
            tx.Message = "Turned away by a clerk.";
            plan = [new(LaneCommand.Red), new(LaneCommand.Show, LaneControllerRules.Display.TurnedAway)];
        }
        else
            return;
        await db.SaveChangesAsync(ct);
        await devices.QueueAsync(lane.TerminalId, lane.LaneId, tx.TransactionId, plan, ct);
    }

    /// <summary>
    /// A clerk lets out a truck the exit lane held: gated out with the clerk's note despite the exit check (or, with no
    /// visit to close, simply let through), then green and the barrier.
    /// </summary>
    public async Task<AdminResult<LaneTransactionView>> LetOutAsync(
        Yardsyncs.Application.Auth.CurrentUser clerk, int terminalId, long transactionId, string? note, CancellationToken ct = default)
    {
        var tx = await db.LaneTransactions.SingleOrDefaultAsync(t => t.TerminalId == terminalId && t.TransactionId == transactionId, ct);
        if (tx is null)
            return AdminResult<LaneTransactionView>.NotFound($"Truck transaction {transactionId} not found.");
        var lane = await db.GateLanes.AsNoTracking().SingleAsync(l => l.LaneId == tx.LaneId, ct);
        if (lane.Direction != LaneDirection.Out || tx.Status != LaneTransactionStatus.Referred)
            return AdminResult<LaneTransactionView>.Conflict("Only a truck held at an exit lane can be let out here.");
        var why = note?.Trim() ?? "";
        if (why.Length is 0 or > 300)
            return AdminResult<LaneTransactionView>.Invalid("Say why the truck may leave (at most 300 characters).");

        if (tx.Reason == "EXIT_CHECK")
        {
            var result = await gate.GateOutAsync(clerk, terminalId,
                new GateOutInput(lane.LaneCode, tx.AppointmentNo, tx.Plate, LaneControllerRules.ContainersOf(tx), tx.WeightKg, LetOut: true, Note: why), ct);
            if (result.Status != AdminStatus.Ok)
                return new AdminResult<LaneTransactionView>(result.Status, null, result.Errors);
            db.ChangeTracker.Clear();
            tx = await db.LaneTransactions.SingleAsync(t => t.TransactionId == transactionId, ct);
        }
        tx.Status = LaneTransactionStatus.Admitted;
        tx.Message = Clip($"Let out by a clerk: {why}");
        await db.SaveChangesAsync(ct);
        await devices.QueueAsync(terminalId, lane.LaneId, transactionId, LaneControllerRules.Plan(LaneDirection.Out, true, tx.AppointmentNo, null, null), ct);
        logger.LogInformation("Lane {Lane}: transaction {Tx} let out by user {UserId}", lane.LaneCode, transactionId, clerk.UserId);
        return AdminResult<LaneTransactionView>.Ok(LaneTransactionView.From(tx, lane.LaneCode));
    }

    /// <summary>Everything about one truck at a lane, for the remote clerk.</summary>
    public async Task<LaneTransactionDetail?> DetailAsync(int terminalId, long transactionId, CancellationToken ct = default)
    {
        var tx = await db.LaneTransactions.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId && t.TransactionId == transactionId, ct);
        if (tx is null)
            return null;
        var lane = await db.GateLanes.AsNoTracking().SingleAsync(l => l.LaneId == tx.LaneId, ct);
        var laneDevices = await db.LaneDevices.AsNoTracking().Where(d => d.LaneId == tx.LaneId).ToListAsync(ct);
        var codes = laneDevices.ToDictionary(d => d.DeviceId, d => d.DeviceCode);
        var reads = await db.LaneDeviceReads.AsNoTracking().Where(r => r.TransactionId == transactionId)
            .OrderBy(r => r.ReceivedAt).ThenBy(r => r.ReadId).Take(50).ToListAsync(ct);
        var commands = await db.LaneDeviceCommands.AsNoTracking().Where(c => c.TransactionId == transactionId)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.CommandId).Take(50).ToListAsync(ct);
        var now = clock.GetUtcNow();
        return new LaneTransactionDetail(
            LaneTransactionView.From(tx, lane.LaneCode),
            reads.Select(r => DeviceService.View(r, codes.GetValueOrDefault(r.DeviceId, "?"))).ToList(),
            commands.Select(c => DeviceService.View(c, codes.GetValueOrDefault(c.DeviceId, "?"), now)).ToList(),
            tx.TroubleId is { } troubleId ? await gate.TroubleAsync(terminalId, troubleId, ct) : null,
            laneDevices.OrderBy(d => d.Kind).ThenBy(d => d.DeviceCode).Select(d => LaneDeviceView.From(d, now)).ToList());
    }

    /// <summary>
    /// The clerk corrected what the cameras misread (or typed the appointment number the driver gave over the intercom):
    /// the referred truck's trouble case is closed and the lane checks the truck again — let in with the barrier opening,
    /// or referred anew with a fresh case.
    /// </summary>
    public async Task<AdminResult<LaneTransactionView>> RecheckAsync(
        Yardsyncs.Application.Auth.CurrentUser clerk, int terminalId, long transactionId, TransactionCorrectionInput input, CancellationToken ct = default)
    {
        var tx = await db.LaneTransactions.SingleOrDefaultAsync(t => t.TerminalId == terminalId && t.TransactionId == transactionId, ct);
        if (tx is null)
            return AdminResult<LaneTransactionView>.NotFound($"Truck transaction {transactionId} not found.");
        var now = clock.GetUtcNow();
        if (tx.Status != LaneTransactionStatus.Referred)
            return AdminResult<LaneTransactionView>.Conflict($"Only a truck waiting for a clerk can be checked again (this one is {tx.Status}).");
        if (now - tx.LastReadAt > RecheckWindow)
            return AdminResult<LaneTransactionView>.Conflict("This truck was read too long ago to still be at the lane; check it in from the gate screen.");

        var errors = new List<string>();
        string? plate = null;
        if (input.Plate is not null)
        {
            plate = Yardsyncs.Application.MasterData.PlateNumber.Normalize(input.Plate);
            if (plate.Length > 0 && !Yardsyncs.Application.MasterData.PlateNumber.IsValid(plate))
                errors.Add($"'{input.Plate.Trim()}' is not a plate.");
        }
        List<string>? containers = null;
        if (input.Containers is not null)
        {
            containers = input.Containers.Select(Yardsyncs.Application.Booking.ContainerNumber.Normalize).Where(c => c.Length > 0).Distinct().ToList();
            errors.AddRange(containers.Where(c => !Yardsyncs.Application.Booking.ContainerNumber.IsValid(c)).Select(c => $"{c} is not a valid container number."));
            if (containers.Count > LaneControllerRules.MaxContainers)
                errors.Add($"At most {LaneControllerRules.MaxContainers} containers.");
        }
        var number = string.IsNullOrWhiteSpace(input.AppointmentNo) ? null : input.AppointmentNo.Trim().ToUpperInvariant();
        if (number?.Length > 30)
            errors.Add("The appointment number is too long.");
        if (errors.Count > 0)
            return AdminResult<LaneTransactionView>.Invalid(errors);

        if (plate is not null && plate != (tx.Plate ?? ""))
        {
            tx.Plate = plate.Length == 0 ? null : plate;
            tx.PlateConfidence = null;
            tx.PlateCorrected = true;
        }
        if (containers is not null && string.Join(",", containers) != (tx.Containers ?? ""))
        {
            tx.Containers = containers.Count == 0 ? null : string.Join(",", containers);
            tx.ContainersCorrected = true;
        }
        if (number is not null)
            tx.AppointmentRead = number;
        if (!LaneControllerRules.CanIdentify(tx))
            return AdminResult<LaneTransactionView>.Invalid("Give a plate, a container number or the appointment number.");
        tx.CorrectedBy = clerk.UserId;
        tx.CorrectedAt = now;

        // The old case is settled by this check.
        if (tx.TroubleId is { } troubleId)
            await db.GateTroubles.Where(t => t.TroubleId == troubleId && t.Status == TroubleStatus.Open)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TroubleStatus.Resolved)
                    .SetProperty(t => t.Resolution, TroubleResolution.Dismissed)
                    .SetProperty(t => t.ResolutionNote, "Checked again with corrected reads")
                    .SetProperty(t => t.ResolvedBy, clerk.UserId)
                    .SetProperty(t => t.ResolvedAt, now), ct);
        tx.Status = LaneTransactionStatus.Open;
        tx.DecidedAt = null;
        tx.TroubleId = null;
        tx.Reason = null;
        tx.Message = null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Lane transaction {Tx} checked again by user {UserId} (plate corrected: {Plate}, containers corrected: {Containers})",
            transactionId, clerk.UserId, tx.PlateCorrected, tx.ContainersCorrected);
        var decided = await DecideAsync(transactionId, ct, clerk);
        return decided is null
            ? AdminResult<LaneTransactionView>.Conflict("The lane is already deciding this truck.")
            : AdminResult<LaneTransactionView>.Ok(decided);
    }

    /// <summary>How long after its last read a referred truck can be checked again from the station.</summary>
    public static readonly TimeSpan RecheckWindow = TimeSpan.FromMinutes(15);

    /// <summary>The lane's recent transactions (all lanes when laneId is null), newest first.</summary>
    public async Task<IReadOnlyList<LaneTransactionView>> ListAsync(int terminalId, int? laneId, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var lanes = await db.GateLanes.AsNoTracking().Where(l => l.TerminalId == terminalId).ToDictionaryAsync(l => l.LaneId, l => l.LaneCode, ct);
        var rows = await db.LaneTransactions.AsNoTracking()
            .Where(t => t.TerminalId == terminalId && (laneId == null || t.LaneId == laneId))
            .OrderByDescending(t => t.LastReadAt).ThenByDescending(t => t.TransactionId)
            .Take(limit).ToListAsync(ct);
        return rows.Select(t => LaneTransactionView.From(t, lanes.GetValueOrDefault(t.LaneId, "?"))).ToList();
    }

    // ================================================================ deciding

    private async Task<LaneTransactionView?> DecideAsync(long transactionId, CancellationToken ct, Yardsyncs.Application.Auth.CurrentUser? clerk = null)
    {
        // Claim the transaction, so the worker and a read arriving at the same moment don't both decide it.
        var now = clock.GetUtcNow();
        var claimed = await db.LaneTransactions
            .Where(t => t.TransactionId == transactionId && t.Status == LaneTransactionStatus.Open && t.DecidedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DecidedAt, now), ct);
        if (claimed == 0)
            return null;

        db.ChangeTracker.Clear();
        var tx = await db.LaneTransactions.AsNoTracking().SingleAsync(t => t.TransactionId == transactionId, ct);
        var lane = await db.GateLanes.AsNoTracking().SingleAsync(l => l.LaneId == tx.LaneId, ct);
        var containers = LaneControllerRules.ContainersOf(tx);

        LaneTransactionStatus status;
        string? appointmentNo = null, reason = null, message;
        long? appointmentId = null, troubleId = null;

        if (lane.Direction == LaneDirection.In)
        {
            var result = await gate.ArriveAsync(clerk, tx.TerminalId,
                new ArrivalInput(lane.LaneCode, tx.Plate, containers, tx.QrToken, tx.AppointmentRead), ct);
            if (result.Value is { } arrival)
            {
                status = arrival.Decision == GateDecisionKind.Admit ? LaneTransactionStatus.Admitted : LaneTransactionStatus.Referred;
                appointmentNo = arrival.AppointmentNo;
                troubleId = arrival.TroubleId;
                reason = arrival.Reason is { } r ? EnumNames.ToDb(r) : null;
                message = arrival.Message;
            }
            else
            {
                status = LaneTransactionStatus.Referred;
                reason = "LANE";
                message = string.Join(" ", result.Errors);
            }
        }
        else
        {
            var result = await gate.GateOutAsync(clerk, tx.TerminalId,
                new GateOutInput(lane.LaneCode, tx.AppointmentRead, tx.Plate, containers, tx.WeightKg), ct);
            if (result.Status == AdminStatus.Ok && result.Value is { } exit)
            {
                status = LaneTransactionStatus.Admitted;
                appointmentNo = exit.AppointmentNo;
                message = exit.TurnMinutes is { } m ? $"Gated out after {Math.Round(m)} min." : "Gated out.";
            }
            else if (result.Status == AdminStatus.Conflict && result.Value is { } held)
            {
                // The exit check found a problem: the truck waits on red for a clerk.
                status = LaneTransactionStatus.Referred;
                appointmentNo = held.AppointmentNo;
                reason = "EXIT_CHECK";
                message = string.Join(" ", result.Errors);
            }
            else
            {
                status = LaneTransactionStatus.Referred;
                reason = "EXIT";
                message = string.Join(" ", result.Errors);
            }
        }

        db.ChangeTracker.Clear();
        if (appointmentNo is not null)
            appointmentId = await db.Appointments.AsNoTracking()
                .Where(a => a.TerminalId == tx.TerminalId && a.AppointmentNo == appointmentNo)
                .Select(a => (long?)a.AppointmentId).SingleOrDefaultAsync(ct);
        var row = await db.LaneTransactions.SingleAsync(t => t.TransactionId == transactionId, ct);
        row.Status = status;
        row.AppointmentNo = appointmentNo;
        row.AppointmentId = appointmentId;
        row.TroubleId = troubleId;
        row.Reason = reason;
        row.Message = Clip(message);
        row.QrToken = null; // a gate pass is only needed to decide
        await db.SaveChangesAsync(ct);

        var admitted = status == LaneTransactionStatus.Admitted;
        string? ticket = null;
        if (admitted && lane.Direction == LaneDirection.In)
        {
            var terminal = await db.Terminals.AsNoTracking().SingleAsync(t => t.TerminalId == lane.TerminalId, ct);
            var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone));
            ticket = LaneControllerRules.Display.Ticket(terminal.Name, lane.LaneCode, appointmentNo, row.Plate, containers, local.ToString("yyyy-MM-dd HH:mm"));
        }
        await devices.QueueAsync(lane.TerminalId, lane.LaneId, transactionId,
            LaneControllerRules.Plan(lane.Direction, admitted, appointmentNo, message, ticket), ct);

        logger.LogInformation("Lane {Lane}: transaction {Tx} {Status} ({No}{Reason})",
            lane.LaneCode, transactionId, status, appointmentNo ?? "no appointment", reason is null ? "" : $", {reason}");
        return LaneTransactionView.From(row, lane.LaneCode);
    }

    private static string? Clip(string? text) => text is null ? null : text.Length <= 400 ? text : text[..399] + "…";
}

/// <summary>Runs the lane controller every second (LaneController:IntervalMilliseconds) when LaneController:Enabled.</summary>
public sealed class LaneControllerWorker(IServiceScopeFactory scopes, IOptions<LaneControllerOptions> options, ILogger<LaneControllerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(200, options.Value.IntervalMilliseconds)));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<LaneControllerService>().ProcessDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Lane controller run failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
