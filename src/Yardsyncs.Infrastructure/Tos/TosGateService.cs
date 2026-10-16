using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Tos;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.Gate;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Tos;

public sealed class TosGateOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 10;
}

/// <summary>A gate record as staff see it.</summary>
public sealed record TosGateMessageView(
    long MessageId,
    string AppointmentNo,
    TosGateStage Stage,
    string? LaneCode,
    DateTimeOffset OccurredAt,
    TosMessageStatus Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? SentAt,
    string? TosReference,
    string? LastError);

/// <summary>
/// Gate passages recorded in the TOS (terminals with tos_gate_write on). The gate queues each gate-in and gate-out in
/// the same save as the passage itself, so none is lost; the worker sends them, gate-out only after its gate-in,
/// retrying while the TOS can't be reached (1, 2, 4… minutes) and giving up after ten tries or when the TOS refuses —
/// those wait for a person (the Lanes screen), who can send them again.
/// </summary>
public sealed class TosGateService(
    YardsyncsDbContext db,
    ITosGateWriter writer,
    TimeProvider clock,
    ILogger<TosGateService> logger)
{
    /// <summary>
    /// Queues a gate passage when the terminal records them in the TOS; called by the gate before it saves. A passage is
    /// queued once (a second gate-in of the same appointment is ignored).
    /// </summary>
    public static async Task QueueAsync(YardsyncsDbContext db, int terminalId, long appointmentId, TosGateStage stage, string? laneCode, DateTimeOffset now, CancellationToken ct)
    {
        var on = await db.TerminalSettings.AsNoTracking()
            .AnyAsync(s => s.TerminalId == terminalId && s.SettingKey == TerminalSettings.TosGateWriteKey && s.SettingValue == "1", ct);
        if (!on)
            return;
        var code = stage == TosGateStage.GateIn ? TosGateStageCode.GateIn : TosGateStageCode.GateOut;
        if (await db.TosGateMessages.AnyAsync(m => m.AppointmentId == appointmentId && m.Stage == code, ct)
            || db.TosGateMessages.Local.Any(m => m.AppointmentId == appointmentId && m.Stage == code))
            return;
        db.TosGateMessages.Add(new TosGateMessage
        {
            TerminalId = terminalId,
            AppointmentId = appointmentId,
            Stage = code,
            LaneCode = laneCode,
            OccurredAt = now,
            NextAttemptAt = now,
            CreatedAt = now,
        });
    }

    /// <summary>Sends what is due (one terminal, or all). Returns how many were tried.</summary>
    public async Task<int> SendDueAsync(int? terminalId = null, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = await db.TosGateMessages
            .Where(m => m.Status == TosMessageStatus.Queued && m.NextAttemptAt <= now && (terminalId == null || m.TerminalId == terminalId))
            .OrderBy(m => m.OccurredAt).ThenBy(m => m.Stage).ThenBy(m => m.MessageId)
            .Take(50).ToListAsync(ct);
        var tried = 0;
        foreach (var message in due)
        {
            string? visit = null;
            if (message.Stage == TosGateStageCode.GateOut)
            {
                var gateIn = await db.TosGateMessages.AsNoTracking()
                    .SingleOrDefaultAsync(m => m.AppointmentId == message.AppointmentId && m.Stage == TosGateStageCode.GateIn, ct);
                if (gateIn is { Status: TosMessageStatus.Queued })
                    continue; // the gate-in goes first
                if (gateIn is { Status: TosMessageStatus.Failed })
                {
                    Fail(message, "Not sent: the gate-in of this visit was not recorded in the TOS.", now);
                    continue;
                }
                visit = gateIn?.TosReference;
            }

            tried++;
            message.Attempts++;
            TosGateResult result;
            try
            {
                result = await writer.RecordAsync(message.TerminalId, await RequestAsync(message, visit, ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = TosGateResult.Unreachable(ex.Message);
            }

            if (result.Ok)
            {
                message.Status = TosMessageStatus.Sent;
                message.SentAt = now;
                message.TosReference = Clip(result.VisitReference ?? visit, 60);
                message.LastError = null;
            }
            else if (!result.Retry || message.Attempts >= TosGateRetry.MaxAttempts)
                Fail(message, result.Message ?? "The TOS did not take the record.", now);
            else
            {
                message.LastError = Clip(result.Message, 400);
                message.NextAttemptAt = now + TosGateRetry.Delay(message.Attempts);
            }
            await db.SaveChangesAsync(ct); // a gate-out further down sees its gate-in as sent
        }
        await db.SaveChangesAsync(ct);
        return tried;
    }

    public async Task<IReadOnlyList<TosGateMessageView>> ListAsync(int terminalId, bool problemsOnly, CancellationToken ct = default)
    {
        var rows = await db.TosGateMessages.AsNoTracking()
            .Where(m => m.TerminalId == terminalId && (!problemsOnly || m.Status != TosMessageStatus.Sent))
            .OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.MessageId)
            .Take(100)
            .Join(db.Appointments, m => m.AppointmentId, a => a.AppointmentId, (m, a) => new { m, a.AppointmentNo })
            .ToListAsync(ct);
        return rows.Select(x => View(x.m, x.AppointmentNo)).ToList();
    }

    /// <summary>Send a record that was given up again, now.</summary>
    public async Task<AdminResult<TosGateMessageView>> RetryAsync(int terminalId, long messageId, CancellationToken ct = default)
    {
        var message = await db.TosGateMessages.SingleOrDefaultAsync(m => m.TerminalId == terminalId && m.MessageId == messageId, ct);
        if (message is null)
            return AdminResult<TosGateMessageView>.NotFound($"Gate record {messageId} not found.");
        if (message.Status == TosMessageStatus.Sent)
            return AdminResult<TosGateMessageView>.Conflict("This record is already in the TOS.");
        message.Status = TosMessageStatus.Queued;
        message.Attempts = 0;
        message.NextAttemptAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await SendDueAsync(terminalId, ct);
        var number = await db.Appointments.AsNoTracking().Where(a => a.AppointmentId == message.AppointmentId).Select(a => a.AppointmentNo).SingleAsync(ct);
        return AdminResult<TosGateMessageView>.Ok(View(message, number));
    }

    /// <summary>The TOS's visit key for an appointment, once its gate-in is recorded.</summary>
    public Task<string?> VisitReferenceAsync(long appointmentId, CancellationToken ct = default) =>
        db.TosGateMessages.AsNoTracking()
            .Where(m => m.AppointmentId == appointmentId && m.Stage == TosGateStageCode.GateIn && m.Status == TosMessageStatus.Sent)
            .Select(m => m.TosReference).SingleOrDefaultAsync(ct);

    private async Task<TosGateRequest> RequestAsync(TosGateMessage message, string? visit, CancellationToken ct)
    {
        var a = await db.Appointments.AsNoTracking()
            .Where(x => x.AppointmentId == message.AppointmentId)
            .Select(x => new
            {
                x.AppointmentNo,
                Company = x.Company.Name,
                Plate = x.Truck != null ? x.Truck.PlateNumber : null,
                Driver = x.Driver != null ? x.Driver.FullName : null,
                Items = x.Items.OrderBy(i => i.ItemId).Select(i => new
                {
                    i.Category.Code, i.Category.Mode, i.ContainerNo, i.IsoType, i.BookingRef, i.EdoRef,
                    CargoRef = i.CargoAllocation != null ? i.CargoAllocation.CargoRef : null,
                }).ToList(),
            })
            .SingleAsync(ct);
        var gateCode = message.LaneCode is null ? null : await db.GateLanes.AsNoTracking()
            .Where(l => l.TerminalId == message.TerminalId && l.LaneCode == message.LaneCode).Select(l => l.GateCode).FirstOrDefaultAsync(ct);
        var seals = message.Stage == TosGateStageCode.GateIn ? await SealsAsync(message.AppointmentId, ct) : [];

        return new TosGateRequest(
            message.Stage == TosGateStageCode.GateIn ? TosGateStage.GateIn : TosGateStage.GateOut,
            a.AppointmentNo, a.Plate, a.Driver, a.Company, gateCode, message.LaneCode, message.OccurredAt, visit,
            a.Items.Select((i, n) => new TosGateMove(n + 1, i.Code, i.Mode.ToString(), i.ContainerNo, i.IsoType, i.BookingRef, i.EdoRef, i.CargoRef,
                i.ContainerNo is null ? [] : seals)).ToList());
    }

    /// <summary>Seals seen at gate-in for this appointment: the clerk's inspection first, else what the lane cameras read.</summary>
    private async Task<IReadOnlyList<string>> SealsAsync(long appointmentId, CancellationToken ct)
    {
        var inspected = await db.GateInspections.AsNoTracking()
            .Where(i => i.AppointmentId == appointmentId && i.Stage == InspectionStage.In && i.SealSeen != null)
            .Select(i => i.SealSeen!).ToListAsync(ct);
        if (inspected.Count > 0)
            return inspected.Distinct().Take(4).ToList();
        var reads = await db.LaneDeviceReads.AsNoTracking()
            .Where(r => r.TransactionId != null && db.LaneTransactions.Any(t => t.TransactionId == r.TransactionId && t.AppointmentId == appointmentId))
            .ToListAsync(ct);
        return reads.SelectMany(r => DeviceService.Normalized(r).Seals).Distinct().Take(4).ToList();
    }

    private void Fail(TosGateMessage message, string error, DateTimeOffset now)
    {
        message.Status = TosMessageStatus.Failed;
        message.LastError = Clip(error, 400);
        message.NextAttemptAt = now;
        logger.LogWarning("Gate record {Id} ({Stage}) not recorded in the TOS: {Error}", message.MessageId, message.Stage, error);
    }

    private static TosGateMessageView View(TosGateMessage m, string appointmentNo) => new(
        m.MessageId, appointmentNo, m.Stage == TosGateStageCode.GateIn ? TosGateStage.GateIn : TosGateStage.GateOut, m.LaneCode,
        m.OccurredAt, m.Status, m.Attempts, m.NextAttemptAt, m.SentAt, m.TosReference, m.LastError);

    private static string? Clip(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>Sends queued gate records to the TOS every TosGate:IntervalSeconds (default 10) when TosGate:Enabled.</summary>
public sealed class TosGateWorker(IServiceScopeFactory scopes, IOptions<TosGateOptions> options, ILogger<TosGateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(2, options.Value.IntervalSeconds)));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<TosGateService>().SendDueAsync(null, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Sending gate records to the TOS failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
