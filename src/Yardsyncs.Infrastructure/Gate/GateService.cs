using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Edge;
using Yardsyncs.Application.Gate;
using Yardsyncs.Application.MasterData;
using Yardsyncs.Application.Tos;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.Booking;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Gate;

/// <summary>
/// The gate: trucks arriving at entry lanes (matched by QR, appointment number, plate or container read),
/// re-checked against the TOS and admitted or referred to the trouble desk; gate-in; gate-out with turn time.
/// Endpoints check AccessRules.CanOperateGate. The lane controller calls arrival, gate-in and gate-out without a user
/// (the history then shows no user: the lane itself).
/// </summary>
public sealed class GateService(
    YardsyncsDbContext db,
    RevalidationService revalidation,
    BookingService booking,
    AppointmentQueryService queries,
    TimeProvider clock,
    ILogger<GateService> logger,
    Yardsyncs.Infrastructure.DriverApp.GatePassService? gatePasses = null,
    Yardsyncs.Infrastructure.DriverApp.YardGuidanceService? yard = null,
    Microsoft.Extensions.Configuration.IConfiguration? config = null,
    IEnumerable<IGateHook>? hooks = null,
    IExitCheck? exitCheck = null)
{
    // ================================================================ arrival

    /// <param name="record">
    /// False: only say what the gate would decide (the terminal's own gate system asking before it acts) — no arrival,
    /// no trouble-desk case, no gate event.
    /// </param>
    public async Task<AdminResult<ArrivalResult>> ArriveAsync(CurrentUser? clerk, int terminalId, ArrivalInput input, CancellationToken ct = default, bool record = true)
    {
        var lane = await LaneAsync(terminalId, input.LaneCode, LaneDirection.In, ct);
        if (lane is null)
            return AdminResult<ArrivalResult>.Invalid($"'{input.LaneCode}' is not an active entry lane at this terminal.");
        if (LaneRules.NotOpen(lane) is { } closed)
            return AdminResult<ArrivalResult>.Conflict(closed);

        var (_, settings, timeZone) = await TerminalAsync(terminalId, ct);
        var now = clock.GetUtcNow();
        var containerReads = (input.ContainerReads ?? Array.Empty<string>()).Select(ContainerNumber.Normalize).Where(c => c.Length > 0).Distinct().ToList();
        var reads = new GateReads(input.PlateRead, containerReads, input.QrToken, input.AppointmentNo);

        // Candidate appointments: by QR / number anywhere, by plate or container within a day either side.
        var plate = PlateNumber.Normalize(input.PlateRead);
        var qr = string.IsNullOrWhiteSpace(input.QrToken) ? null : input.QrToken.Trim().ToUpperInvariant();
        var number = string.IsNullOrWhiteSpace(input.AppointmentNo) ? null : input.AppointmentNo.Trim().ToUpperInvariant();

        // A rotating gate pass from the driver app: verify the live code, then match by its appointment number.
        string? passProblem = null;
        var passScanned = false;
        var passValid = false;
        if (gatePasses is not null && qr is not null && qr.StartsWith(GatePassCode.Prefix + ":", StringComparison.Ordinal))
        {
            passScanned = true;
            var pass = await gatePasses.VerifyAsync(terminalId, qr, ct);
            if (pass.Valid)
            {
                number = pass.AppointmentNo;
                passValid = true;
            }
            else
                passProblem = pass.Problem;
            qr = null;
            reads = reads with { QrToken = null, AppointmentNo = number };
        }
        var truckIds = plate.Length == 0
            ? new List<int>()
            : await db.Trucks.AsNoTracking().Where(t => t.PlateNormalized == plate).Select(t => t.TruckId).ToListAsync(ct);
        var from = now.AddHours(-24);
        var to = now.AddHours(24);

        var rows = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId
                        && ((qr != null && a.QrToken == qr)
                            || (number != null && a.AppointmentNo == number)
                            || (a.Slot.StartAt >= from && a.Slot.StartAt <= to
                                && ((a.TruckId != null && truckIds.Contains(a.TruckId.Value))
                                    || a.Items.Any(i => i.ContainerNo != null && containerReads.Contains(i.ContainerNo))))))
            .Select(a => new
            {
                a.AppointmentId,
                a.AppointmentNo,
                a.QrToken,
                a.Status,
                SlotStart = a.Slot.StartAt,
                SlotEnd = a.Slot.EndAt,
                Plate = a.Truck != null ? a.Truck.PlateNormalized : null,
                Containers = a.Items.Where(i => i.ContainerNo != null).Select(i => i.ContainerNo!).ToList(),
                TruckBlocked = a.Truck != null && a.Truck.IsBlocked,
                DriverBlocked = a.Driver != null && a.Driver.IsBlocked,
                HasDriver = a.DriverId != null,
            })
            .Take(50)
            .ToListAsync(ct);

        var candidates = rows
            .Select(r => new GateCandidate(r.AppointmentId, r.AppointmentNo, r.QrToken, r.Status, r.SlotStart, r.SlotEnd, r.Plate, r.Containers.ToHashSet(), r.HasDriver))
            .ToList();
        // A valid gate pass is the driver's own QR code: it always finds its appointment, whatever the number setting.
        var match = GateMatcher.Match(reads, candidates, now, passValid ? settings with { GateByNumber = true } : settings);
        if (passProblem is not null)
            match = match with { Warnings = match.Warnings.Append(passProblem).ToList() };
        var found = match.Appointment is null ? null : rows.Single(r => r.AppointmentId == match.Appointment.AppointmentId);

        // Re-check against the TOS at the gate (holds can appear minutes before arrival).
        var light = TrafficLight.Grey;
        var lightReason = "";
        var tosDown = false;
        if (match.Appointment is not null && Appointment.IsBeforeGate(match.Appointment.Status))
        {
            try
            {
                var check = await revalidation.RevalidateAsync(match.Appointment.AppointmentId, "gate", ct);
                light = check?.Light ?? TrafficLight.Grey;
                lightReason = check?.Reason ?? "";
            }
            catch (TosUnavailableException ex)
            {
                tosDown = true;
                logger.LogWarning(ex, "TOS unreachable at gate lane {Lane}", lane.LaneCode);
            }
        }
        db.ChangeTracker.Clear();

        var decision = GateDecider.Decide(
            match.Appointment, light, lightReason, found?.TruckBlocked ?? false, found?.DriverBlocked ?? false, now, settings, timeZone, passValid);

        // Never admit a truck we could not check: the clerk verifies in N4 and admits from the trouble desk.
        if (tosDown && decision.Kind == GateDecisionKind.Admit)
            decision = new GateDecision(GateDecisionKind.Refer, TroubleReason.NotCleared,
                "The terminal system could not be reached to re-check this truck — verify in the TOS and admit from the trouble desk.");

        // Seals read at the entry against the ones the company declared: held for the trouble desk where the terminal asks for it.
        if (decision.Kind == GateDecisionKind.Admit && settings.SealHold && input.SealReads is { Count: > 0 } sealReads)
        {
            var appointmentId = match.Appointment!.AppointmentId;
            var declared = await db.AppointmentItems.AsNoTracking().Where(i => i.AppointmentId == appointmentId)
                .OrderBy(i => i.ItemId).Select(i => new { i.ContainerNo, i.SealNo }).ToListAsync(ct);
            if (InspectionRules.SealProblem(declared.Select(i => (i.ContainerNo, i.SealNo)), sealReads) is { } sealProblem)
                decision = new GateDecision(GateDecisionKind.Refer, TroubleReason.SealMismatch, sealProblem);
        }

        if (!record)
        {
            var seen = match.Appointment is null ? null : await queries.GetAsync(terminalId, match.Appointment.AppointmentNo, ct);
            return AdminResult<ArrivalResult>.Ok(new ArrivalResult(
                decision.Kind, decision.Message, match.Appointment?.AppointmentNo, match.Method,
                match.Appointment is null ? null : seen?.TrafficLight ?? light,
                null, decision.Reason, match.Warnings, seen));
        }

        AddEvent(terminalId, lane.LaneId, null, string.IsNullOrEmpty(qr) && !passScanned ? GateEventType.OcrRead : GateEventType.PassScan,
            input.PlateRead, containerReads, number is null ? null : $"Appointment no. {number}", now);

        long? troubleId = null;
        if (decision.Kind == GateDecisionKind.Admit)
        {
            var appointmentId = match.Appointment!.AppointmentId;
            var method = match.Method switch
            {
                MatchMethod.QrCode => "APP_MANUAL",
                MatchMethod.AppointmentNo => "CLERK",
                _ => "OCR",
            };
            var arrived = await db.Appointments
                .Where(a => a.AppointmentId == appointmentId && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, AppointmentStatus.Arrived)
                    .SetProperty(a => a.ArrivedAt, now)
                    .SetProperty(a => a.CheckInMethod, method), ct);
            if (arrived > 0)
                AddHistory(appointmentId, match.Appointment.Status, AppointmentStatus.Arrived, clerk?.UserId,
                    $"Arrived at lane {lane.LaneCode} (matched by {match.Method})", now);
            AddEvent(terminalId, lane.LaneId, appointmentId, GateEventType.Matched, input.PlateRead, containerReads,
                string.Join(" ", match.Warnings), now);
        }
        else
        {
            var trouble = new GateTrouble
            {
                TerminalId = terminalId,
                LaneId = lane.LaneId,
                AppointmentId = match.Appointment?.AppointmentId,
                PlateRead = Truncate(input.PlateRead?.Trim(), 20),
                ContainerReads = Truncate(string.Join(",", containerReads), 60),
                ReasonCode = decision.Reason!.Value,
                ReasonText = Truncate(string.Join(" ", new[] { decision.Message }.Concat(match.Warnings)), 400)!,
                CreatedAt = now,
            };
            db.GateTroubles.Add(trouble);

            var eventType = decision.Reason switch
            {
                TroubleReason.NoAppointment => GateEventType.NoAppt,
                TroubleReason.Early => GateEventType.Early,
                TroubleReason.Late => GateEventType.Late,
                _ => GateEventType.Trouble,
            };
            AddEvent(terminalId, lane.LaneId, match.Appointment?.AppointmentId, eventType, input.PlateRead, containerReads, decision.Message, now);
            await db.SaveChangesAsync(ct);
            troubleId = trouble.TroubleId;
            logger.LogInformation("Gate {Lane}: referred to trouble desk ({Reason}) plate {Plate}", lane.LaneCode, decision.Reason, plate);
        }

        await db.SaveChangesAsync(ct);

        var appointment = match.Appointment is null ? null : await queries.GetAsync(terminalId, match.Appointment.AppointmentNo, ct);
        return AdminResult<ArrivalResult>.Ok(new ArrivalResult(
            decision.Kind, decision.Message, match.Appointment?.AppointmentNo, match.Method,
            match.Appointment is null ? null : appointment?.TrafficLight ?? light,
            troubleId, decision.Reason, match.Warnings, appointment));
    }

    // ================================================================ gate in / out

    /// <summary>The barrier opened for an arrived truck.</summary>
    public async Task<AdminResult<AppointmentDto>> GateInAsync(CurrentUser? clerk, int terminalId, GateInInput input, CancellationToken ct = default)
    {
        var number = input.AppointmentNo.Trim().ToUpperInvariant();
        var appointment = await db.Appointments.AsNoTracking().SingleOrDefaultAsync(a => a.TerminalId == terminalId && a.AppointmentNo == number, ct);
        if (appointment is null)
            return AdminResult<AppointmentDto>.NotFound($"Appointment {number} not found.");
        if (appointment.Status != AppointmentStatus.Arrived)
            return AdminResult<AppointmentDto>.Conflict($"Only an arrived truck can gate in (status is {appointment.Status}). Use the trouble desk to admit others.");

        int? laneId = null;
        if (!string.IsNullOrWhiteSpace(input.LaneCode))
            laneId = (await LaneAsync(terminalId, input.LaneCode, LaneDirection.In, ct))?.LaneId;

        var now = clock.GetUtcNow();
        var moved = await db.Appointments
            .Where(a => a.AppointmentId == appointment.AppointmentId && a.Status == AppointmentStatus.Arrived)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, AppointmentStatus.GateIn)
                .SetProperty(a => a.GateInAt, now), ct);
        if (moved == 0)
            return AdminResult<AppointmentDto>.Conflict("The appointment changed in the meantime; reload and try again.");

        AddHistory(appointment.AppointmentId, AppointmentStatus.Arrived, AppointmentStatus.GateIn, clerk?.UserId, "Gate in", now);
        AddEvent(terminalId, laneId, appointment.AppointmentId, GateEventType.GateIn, null, [], null, now);
        foreach (var hook in hooks ?? [])
            await hook.GateInAsync(new GatePassage(terminalId, appointment.AppointmentId, input.LaneCode?.Trim().ToUpperInvariant(), now, clerk?.UserId), ct);
        await db.SaveChangesAsync(ct);
        await AfterGateInAsync(appointment.AppointmentId, ct);
        return AdminResult<AppointmentDto>.Ok((await queries.GetAsync(terminalId, number, ct))!);
    }

    /// <summary>The truck leaves: the visit is completed and its turn time recorded.</summary>
    public async Task<AdminResult<GateOutResult>> GateOutAsync(CurrentUser? clerk, int terminalId, GateOutInput input, CancellationToken ct = default)
    {
        var lane = await LaneAsync(terminalId, input.LaneCode, LaneDirection.Out, ct);
        if (lane is null)
            return AdminResult<GateOutResult>.Invalid($"'{input.LaneCode}' is not an active exit lane at this terminal.");
        if (LaneRules.NotOpen(lane) is { } closed)
            return AdminResult<GateOutResult>.Conflict(closed);

        var query = db.Appointments.AsNoTracking().Where(a => a.TerminalId == terminalId && a.Status == AppointmentStatus.GateIn);
        if (!string.IsNullOrWhiteSpace(input.AppointmentNo))
        {
            var number = input.AppointmentNo.Trim().ToUpperInvariant();
            query = query.Where(a => a.AppointmentNo == number);
        }
        else
        {
            var plate = PlateNumber.Normalize(input.PlateRead);
            if (plate.Length == 0)
                return AdminResult<GateOutResult>.Invalid("Give the appointment number or the plate read.");
            query = query.Where(a => a.Truck != null && a.Truck.PlateNormalized == plate);
        }

        var appointment = await query.OrderByDescending(a => a.GateInAt).FirstOrDefaultAsync(ct);
        if (appointment is null)
            return AdminResult<GateOutResult>.NotFound("No truck inside the terminal matches.");

        // The exit check (an add-on such as Autogate): what is on the truck against what should leave.
        var now = clock.GetUtcNow();
        var items = await db.AppointmentItems.AsNoTracking().Where(i => i.AppointmentId == appointment.AppointmentId)
            .Select(i => new ExitItem(i.Category.Code, i.Category.Mode, i.Category.Flow, i.ContainerNo)).ToListAsync(ct);
        var exitContext = new ExitContext(terminalId, lane, appointment.AppointmentId, appointment.TruckId, input.PlateRead,
            input.ContainerReads, input.WeightKg, items, now);
        var check = exitCheck is null
            ? new ExitCheckResult(ExitCheckStatus.NotChecked, [], null)
            : await exitCheck.CheckAsync(exitContext, ct);
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        if (check.Status == ExitCheckStatus.Problem)
        {
            if (!input.LetOut)
                return new AdminResult<GateOutResult>(AdminStatus.Conflict,
                    new GateOutResult(appointment.AppointmentNo, appointment.ArrivedAt, appointment.GateInAt, now, null, null, check),
                    check.Problems.Select(p => p.Message).ToList());
            if (note is null || note.Length > 300)
                return AdminResult<GateOutResult>.Invalid("Say why the truck may leave anyway (at most 300 characters).");
        }
        var done = await db.Appointments
            .Where(a => a.AppointmentId == appointment.AppointmentId && a.Status == AppointmentStatus.GateIn)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, AppointmentStatus.Completed)
                .SetProperty(a => a.GateOutAt, now)
                .SetProperty(a => a.TrafficLight, TrafficLight.Grey)
                .SetProperty(a => a.TrafficReason, "Visit completed"), ct);
        if (done == 0)
            return AdminResult<GateOutResult>.Conflict("The appointment changed in the meantime; reload and try again.");

        AddHistory(appointment.AppointmentId, AppointmentStatus.GateIn, AppointmentStatus.Completed, clerk?.UserId, $"Gate out at lane {lane.LaneCode}", now);
        AddEvent(terminalId, lane.LaneId, appointment.AppointmentId, GateEventType.GateOut, input.PlateRead, check.Seen ?? [],
            check.Status == ExitCheckStatus.Problem ? $"Let out despite: {string.Join(" ", check.Problems.Select(p => p.Message))} Note: {note}" : null, now);
        foreach (var hook in hooks ?? [])
            await hook.GateOutAsync(new GatePassage(terminalId, appointment.AppointmentId, lane.LaneCode, now, clerk?.UserId), ct);
        var net = exitCheck is null
            ? null
            : await exitCheck.RecordAsync(exitContext, check, check.Status == ExitCheckStatus.Problem ? clerk?.UserId : null, note, ct);
        await db.SaveChangesAsync(ct);

        return AdminResult<GateOutResult>.Ok(new GateOutResult(
            appointment.AppointmentNo, appointment.ArrivedAt, appointment.GateInAt, now,
            appointment.ArrivedAt is { } arrived ? Math.Round((now - arrived).TotalMinutes, 1) : null,
            appointment.GateInAt is { } gateIn ? Math.Round((now - gateIn).TotalMinutes, 1) : null,
            check, net));
    }

    // ================================================================ trouble desk

    /// <summary>One trouble case, open or resolved.</summary>
    public async Task<TroubleView?> TroubleAsync(int terminalId, long troubleId, CancellationToken ct = default)
    {
        var trouble = await db.GateTroubles.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId && t.TroubleId == troubleId, ct);
        return trouble is null ? null : (await ViewsAsync([trouble], ct)).Single();
    }

    public async Task<IReadOnlyList<TroubleView>> ListTroublesAsync(int terminalId, bool openOnly, CancellationToken ct = default)
    {
        var since = clock.GetUtcNow().AddDays(-7);
        var troubles = await db.GateTroubles.AsNoTracking()
            .Where(t => t.TerminalId == terminalId && (t.Status == TroubleStatus.Open || (!openOnly && t.CreatedAt >= since)))
            .OrderBy(t => t.Status).ThenBy(t => t.CreatedAt)
            .Take(500)
            .ToListAsync(ct);
        return await ViewsAsync(troubles, ct);
    }

    public async Task<AdminResult<TroubleView>> ResolveAsync(
        CurrentUser clerk, int terminalId, long troubleId, ResolveTroubleInput input, CancellationToken ct = default)
    {
        var trouble = await db.GateTroubles.SingleOrDefaultAsync(t => t.TroubleId == troubleId && t.TerminalId == terminalId, ct);
        if (trouble is null)
            return AdminResult<TroubleView>.NotFound($"Trouble {troubleId} not found.");
        if (trouble.Status == TroubleStatus.Resolved)
            return AdminResult<TroubleView>.Conflict($"Trouble {troubleId} is already resolved ({trouble.Resolution}).");
        if (input.Action is TroubleAction.Admit or TroubleAction.Reject or TroubleAction.WalkIn && string.IsNullOrWhiteSpace(input.Note))
            return AdminResult<TroubleView>.Invalid("A note is required to admit, book or reject a truck.");

        var now = clock.GetUtcNow();
        var note = Truncate(input.Note?.Trim(), 300);
        TroubleResolution resolution;

        switch (input.Action)
        {
            case TroubleAction.Admit:
            {
                var appointmentId = await ResolveAppointmentIdAsync(terminalId, trouble.AppointmentId, input.AppointmentNo, ct);
                if (appointmentId is null)
                    return AdminResult<TroubleView>.Invalid("Which appointment? Give appointmentNo (or use WalkIn for a truck without one).");
                var error = await AdmitAsync(appointmentId.Value, clerk, trouble.LaneId, terminalId, $"Admitted by the trouble desk: {note}", now, ct);
                if (error is not null)
                    return AdminResult<TroubleView>.Conflict(error);
                trouble.AppointmentId = appointmentId;
                resolution = TroubleResolution.Admitted;
                break;
            }

            case TroubleAction.Reject:
            {
                if (trouble.AppointmentId is { } appointmentId)
                {
                    var appointment = await db.Appointments.AsNoTracking().Include(a => a.Items)
                        .SingleAsync(a => a.AppointmentId == appointmentId, ct);
                    if (Appointment.IsBeforeGate(appointment.Status))
                    {
                        await using var tx = await db.Database.BeginTransactionAsync(ct);
                        var rejected = await db.Appointments
                            .Where(a => a.AppointmentId == appointmentId
                                        && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute || a.Status == AppointmentStatus.Arrived))
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(a => a.Status, AppointmentStatus.RejectedAtGate)
                                .SetProperty(a => a.TrafficLight, TrafficLight.Red)
                                .SetProperty(a => a.TrafficReason, Truncate($"Rejected at gate: {note}", 200)), ct);
                        if (rejected > 0)
                        {
                            await booking.ReleaseCapacityAsync(appointment, ct);
                            AddHistory(appointmentId, appointment.Status, AppointmentStatus.RejectedAtGate, clerk.UserId, $"Rejected at gate: {note}", now);
                        }
                        await db.SaveChangesAsync(ct);
                        await tx.CommitAsync(ct);
                    }
                }
                resolution = TroubleResolution.Rejected;
                break;
            }

            case TroubleAction.WalkIn:
            {
                if (input.WalkIn is null || input.WalkIn.Items is null || input.WalkIn.Items.Count == 0)
                    return AdminResult<TroubleView>.Invalid("Give walkIn.companyId and walkIn.items for the truck.");

                var slotId = await db.Slots.AsNoTracking()
                    .Where(s => s.TerminalId == terminalId && s.StartAt <= now && s.EndAt > now && !s.IsClosed)
                    .Select(s => (long?)s.SlotId)
                    .FirstOrDefaultAsync(ct);
                if (slotId is null)
                    return AdminResult<TroubleView>.Conflict("No open slot right now for a walk-in.");

                var plate = PlateNumber.Normalize(trouble.PlateRead);
                var truckId = plate.Length == 0
                    ? null
                    : await db.Trucks.AsNoTracking()
                        .Where(t => t.CompanyId == input.WalkIn.CompanyId && t.PlateNormalized == plate)
                        .Select(t => (int?)t.TruckId)
                        .FirstOrDefaultAsync(ct);

                db.ChangeTracker.Clear();
                var booked = await booking.CreateAsync(terminalId, new BookingRequest(
                    input.WalkIn.CompanyId, clerk.UserId, slotId.Value, truckId, input.WalkIn.DriverId, input.WalkIn.Items, WalkIn: true), ct);
                if (booked.Kind != BookingOutcomeKind.Created)
                    return booked.Kind == BookingOutcomeKind.NoCapacity
                        ? AdminResult<TroubleView>.Conflict(string.Join(" ", booked.Errors))
                        : AdminResult<TroubleView>.Invalid(booked.Errors);

                var appointmentId = await db.Appointments.AsNoTracking()
                    .Where(a => a.AppointmentNo == booked.Appointment!.AppointmentNo)
                    .Select(a => a.AppointmentId)
                    .SingleAsync(ct);
                var error = await AdmitAsync(appointmentId, clerk, trouble.LaneId, terminalId, "Walk-in booked and admitted at the gate", now, ct);
                if (error is not null)
                    return AdminResult<TroubleView>.Conflict(error);

                trouble = await db.GateTroubles.SingleAsync(t => t.TroubleId == troubleId, ct);
                trouble.AppointmentId = appointmentId;
                resolution = TroubleResolution.WalkIn;
                break;
            }

            default:
                resolution = TroubleResolution.Dismissed;
                break;
        }

        trouble.Status = TroubleStatus.Resolved;
        trouble.Resolution = resolution;
        trouble.ResolutionNote = note;
        trouble.ResolvedBy = clerk.UserId;
        trouble.ResolvedAt = now;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Trouble {TroubleId} resolved as {Resolution} by user {UserId}", troubleId, resolution, clerk.UserId);
        foreach (var hook in hooks ?? [])
            await hook.TroubleResolvedAsync(terminalId, troubleId, resolution, ct);
        return AdminResult<TroubleView>.Ok((await ViewsAsync([trouble], ct)).Single());
    }

    // ================================================================ board (lanes: LaneService)

    public async Task<GateBoard> BoardAsync(int terminalId, CancellationToken ct = default)
    {
        var (_, _, timeZone) = await TerminalAsync(terminalId, ct);
        var now = clock.GetUtcNow();
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        var dayStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localToday.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), timeZone), TimeSpan.Zero);

        var active = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && (a.Status == AppointmentStatus.Arrived || a.Status == AppointmentStatus.GateIn))
            .Select(a => new { a.AppointmentNo, Company = a.Company.Name, Plate = a.Truck != null ? a.Truck.PlateNumber : null, a.Status, a.ArrivedAt, a.GateInAt })
            .ToListAsync(ct);

        var trucks = active
            .Select(a => new GateBoardTruck(a.AppointmentNo, a.Company, a.Plate, a.Status, a.ArrivedAt, a.GateInAt,
                (int)Math.Max(0, (now - (a.ArrivedAt ?? a.GateInAt ?? now)).TotalMinutes)))
            .OrderByDescending(t => t.MinutesSinceArrival)
            .ToList();

        var completed = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId && a.Status == AppointmentStatus.Completed && a.GateOutAt >= dayStart)
            .Select(a => new { a.ArrivedAt, a.GateOutAt })
            .ToListAsync(ct);
        var turns = completed.Where(c => c.ArrivedAt is not null).Select(c => (c.GateOutAt!.Value - c.ArrivedAt!.Value).TotalMinutes).ToList();

        var openTroubles = await db.GateTroubles.CountAsync(t => t.TerminalId == terminalId && t.Status == TroubleStatus.Open, ct);

        return new GateBoard(
            trucks.Where(t => t.Status == AppointmentStatus.Arrived).ToList(),
            trucks.Where(t => t.Status == AppointmentStatus.GateIn).ToList(),
            openTroubles,
            completed.Count,
            turns.Count == 0 ? null : Math.Round(turns.Average(), 1));
    }

    // ================================================================ helpers

    /// <summary>Moves a pre-gate appointment straight to GATE_IN (trouble desk / walk-in). Returns an error message or null.</summary>
    private async Task<string?> AdmitAsync(long appointmentId, CurrentUser clerk, int? laneId, int terminalId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var current = await db.Appointments.AsNoTracking().Where(a => a.AppointmentId == appointmentId).Select(a => a.Status).SingleAsync(ct);
        if (!Appointment.IsBeforeGate(current))
            return $"An appointment in status {current} cannot be admitted.";

        var admitted = await db.Appointments
            .Where(a => a.AppointmentId == appointmentId
                        && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute || a.Status == AppointmentStatus.Arrived))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, AppointmentStatus.GateIn)
                .SetProperty(a => a.ArrivedAt, a => a.ArrivedAt ?? now)
                .SetProperty(a => a.GateInAt, now)
                .SetProperty(a => a.CheckInMethod, "CLERK"), ct);
        if (admitted == 0)
            return "The appointment changed in the meantime; reload and try again.";

        AddHistory(appointmentId, current, AppointmentStatus.GateIn, clerk.UserId, reason, now);
        AddEvent(terminalId, laneId, appointmentId, GateEventType.GateIn, null, [], reason, now);
        var laneCode = laneId is null ? null : await db.GateLanes.AsNoTracking().Where(l => l.LaneId == laneId).Select(l => l.LaneCode).SingleOrDefaultAsync(ct);
        foreach (var hook in hooks ?? [])
            await hook.GateInAsync(new GatePassage(terminalId, appointmentId, laneCode, now, clerk.UserId), ct);
        await db.SaveChangesAsync(ct);
        await AfterGateInAsync(appointmentId, ct);
        return null;
    }

    /// <summary>
    /// The in-gate ticket: the TOS yard positions become the truck's stops, and the driver gets them by push
    /// (or SMS without the app). The truck is already in; a TOS or messaging problem must not undo that.
    /// </summary>
    private async Task AfterGateInAsync(long appointmentId, CancellationToken ct)
    {
        if (yard is null)
            return;
        try
        {
            var guidance = await yard.IssueAtGateInAsync(appointmentId, ct);
            var stops = guidance is null ? "" : Yardsyncs.Infrastructure.DriverApp.YardGuidanceService.StopsText(guidance.Stops);
            if (stops.Length == 0)
                return; // no position from the TOS: the driver follows the terminal's directions
            var values = new Dictionary<string, string>
            {
                [Yardsyncs.Application.Notifications.NotificationText.Keys.AppointmentNo] = guidance!.AppointmentNo,
                [Yardsyncs.Application.Notifications.NotificationText.Keys.Stops] = stops,
            };
            await Yardsyncs.Infrastructure.Notifications.NotificationQueue.ForAppointmentAsync(db, appointmentId, NotificationTemplates.GateIn, values,
                await Yardsyncs.Infrastructure.Notifications.NotificationQueue.DriverChannelAsync(db, config,
                    await db.Appointments.AsNoTracking().Where(a => a.AppointmentId == appointmentId).Select(a => a.TerminalId).SingleAsync(ct), ct),
                clock.GetUtcNow(), ct,
                includeBooker: false, pushFirst: Yardsyncs.Infrastructure.Notifications.NotificationQueue.PushFirst(config));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "In-gate ticket for appointment {AppointmentId} could not be issued", appointmentId);
        }
    }

    // ================================================================ decided at the terminal without the cloud (edge mode)

    /// <summary>
    /// Records a passage the lane agent decided while the cloud couldn't be reached, at the times it happened: an entry
    /// that went through is gated in (one let in whose barrier never confirmed is only arrived), an exit is gated out,
    /// a referral is logged. The TOS gets the gate transactions and the driver the yard position as for any passage.
    /// Sending the same passage again records nothing.
    /// </summary>
    public async Task<EdgePassageResult> RecordOfflineAsync(CurrentUser agent, int terminalId, EdgePassage p, CancellationToken ct = default)
    {
        EdgePassageResult Result(EdgePassageOutcome outcome, string? detail = null) => new(p.PassageId, outcome, detail);
        var tag = $"Edge passage {p.PassageId:N}.";
        if (await db.GateEvents.AsNoTracking().AnyAsync(e => e.TerminalId == terminalId && e.Details != null && e.Details.StartsWith(tag), ct))
            return Result(EdgePassageOutcome.Duplicate);

        var laneCode = (p.LaneCode ?? "").Trim().ToUpperInvariant();
        var lane = await db.GateLanes.AsNoTracking().SingleOrDefaultAsync(l => l.TerminalId == terminalId && l.LaneCode == laneCode, ct);
        if (lane is null || lane.Direction != p.Direction)
            return Result(EdgePassageOutcome.Failed, $"Lane {laneCode} is not an {(p.Direction == LaneDirection.In ? "entry" : "exit")} lane of this terminal.");
        var containers = p.Containers ?? [];
        var number = p.AppointmentNo?.Trim().ToUpperInvariant();
        var offline = $"{tag} Decided at the terminal without the cloud at lane {lane.LaneCode}.";

        if (!p.Admitted || number is null)
        {
            AddEvent(terminalId, lane.LaneId, null, GateEventType.Trouble, p.Plate, [], $"{offline} Referred{(p.Reason is null ? "" : $" ({p.Reason})")}: {p.Message}", p.DecidedAt);
            await db.SaveChangesAsync(ct);
            return Result(EdgePassageOutcome.Applied);
        }

        var appointment = await db.Appointments.AsNoTracking().SingleOrDefaultAsync(a => a.TerminalId == terminalId && a.AppointmentNo == number, ct);
        if (appointment is null)
            return Result(EdgePassageOutcome.Failed, $"Appointment {number} not found.");

        if (p.Direction == LaneDirection.In)
        {
            if (appointment.Status is AppointmentStatus.GateIn or AppointmentStatus.Completed
                || (appointment.Status == AppointmentStatus.Arrived && p.PassedAt is null))
                return Result(EdgePassageOutcome.Duplicate, $"Appointment {number} is already {appointment.Status}.");
            if (!Appointment.IsBeforeGate(appointment.Status))
            {
                AddEvent(terminalId, lane.LaneId, appointment.AppointmentId, GateEventType.Trouble, p.Plate, [],
                    $"{offline} Let in while the visit was {appointment.Status}: check this truck.", p.PassedAt ?? p.DecidedAt);
                await db.SaveChangesAsync(ct);
                return Result(EdgePassageOutcome.Failed, $"Appointment {number} was {appointment.Status} when the lane let the truck in: logged for the gate staff.");
            }

            var arrivedAt = appointment.ArrivedAt ?? p.DecidedAt;
            await db.Appointments.Where(a => a.AppointmentId == appointment.AppointmentId)
                .ExecuteUpdateAsync(x => x
                    .SetProperty(a => a.Status, p.PassedAt is null ? AppointmentStatus.Arrived : AppointmentStatus.GateIn)
                    .SetProperty(a => a.ArrivedAt, arrivedAt)
                    .SetProperty(a => a.GateInAt, p.PassedAt)
                    .SetProperty(a => a.CheckInMethod, appointment.CheckInMethod ?? "OCR"), ct);
            if (appointment.Status != AppointmentStatus.Arrived)
                AddHistory(appointment.AppointmentId, appointment.Status, AppointmentStatus.Arrived, agent.UserId, $"Arrived at lane {lane.LaneCode} (decided at the terminal without the cloud)", p.DecidedAt);
            AddEvent(terminalId, lane.LaneId, appointment.AppointmentId, GateEventType.Matched, p.Plate, [], $"{offline} {p.Message}", p.DecidedAt);
            if (p.PassedAt is { } passed)
            {
                AddHistory(appointment.AppointmentId, AppointmentStatus.Arrived, AppointmentStatus.GateIn, agent.UserId, "Gate in (decided at the terminal without the cloud)", passed);
                AddEvent(terminalId, lane.LaneId, appointment.AppointmentId, GateEventType.GateIn, p.Plate, containers, null, passed);
                foreach (var hook in hooks ?? [])
                    await hook.GateInAsync(new GatePassage(terminalId, appointment.AppointmentId, lane.LaneCode, passed, agent.UserId), ct);
            }
            await db.SaveChangesAsync(ct);
            if (p.PassedAt is not null)
                await AfterGateInAsync(appointment.AppointmentId, ct);
            return Result(EdgePassageOutcome.Applied);
        }

        if (appointment.Status == AppointmentStatus.Completed)
            return Result(EdgePassageOutcome.Duplicate, $"Appointment {number} is already completed.");
        if (appointment.Status != AppointmentStatus.GateIn)
            return Result(EdgePassageOutcome.Failed, $"Appointment {number} was not inside (status {appointment.Status}).");
        var outAt = p.PassedAt ?? p.DecidedAt;
        await db.Appointments.Where(a => a.AppointmentId == appointment.AppointmentId && a.Status == AppointmentStatus.GateIn)
            .ExecuteUpdateAsync(x => x
                .SetProperty(a => a.Status, AppointmentStatus.Completed)
                .SetProperty(a => a.GateOutAt, outAt)
                .SetProperty(a => a.TrafficLight, TrafficLight.Grey)
                .SetProperty(a => a.TrafficReason, "Visit completed"), ct);
        AddHistory(appointment.AppointmentId, AppointmentStatus.GateIn, AppointmentStatus.Completed, agent.UserId,
            $"Gate out at lane {lane.LaneCode} (decided at the terminal without the cloud; exit not checked)", outAt);
        AddEvent(terminalId, lane.LaneId, appointment.AppointmentId, GateEventType.GateOut, p.Plate, [], $"{offline} {p.Message}", outAt);
        foreach (var hook in hooks ?? [])
            await hook.GateOutAsync(new GatePassage(terminalId, appointment.AppointmentId, lane.LaneCode, outAt, agent.UserId), ct);
        await db.SaveChangesAsync(ct);
        return Result(EdgePassageOutcome.Applied);
    }

    private async Task<long?> ResolveAppointmentIdAsync(int terminalId, long? fromTrouble, string? appointmentNo, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(appointmentNo))
        {
            var number = appointmentNo.Trim().ToUpperInvariant();
            return await db.Appointments.AsNoTracking()
                .Where(a => a.TerminalId == terminalId && a.AppointmentNo == number)
                .Select(a => (long?)a.AppointmentId)
                .SingleOrDefaultAsync(ct);
        }
        return fromTrouble;
    }

    private async Task<IReadOnlyList<TroubleView>> ViewsAsync(IReadOnlyList<GateTrouble> troubles, CancellationToken ct)
    {
        var appointmentIds = troubles.Where(t => t.AppointmentId is not null).Select(t => t.AppointmentId!.Value).Distinct().ToList();
        var numbers = await db.Appointments.AsNoTracking().Where(a => appointmentIds.Contains(a.AppointmentId))
            .ToDictionaryAsync(a => a.AppointmentId, a => a.AppointmentNo, ct);
        var laneIds = troubles.Where(t => t.LaneId is not null).Select(t => t.LaneId!.Value).Distinct().ToList();
        var lanes = await db.GateLanes.AsNoTracking().Where(l => laneIds.Contains(l.LaneId)).ToDictionaryAsync(l => l.LaneId, l => l.LaneCode, ct);

        return troubles.Select(t => new TroubleView(
            t.TroubleId,
            t.LaneId is { } laneId ? lanes.GetValueOrDefault(laneId) : null,
            t.AppointmentId is { } id ? numbers.GetValueOrDefault(id) : null,
            t.PlateRead, t.ContainerReads, t.ReasonCode, t.ReasonText, t.Status, t.Resolution, t.ResolutionNote,
            t.CreatedAt, t.ResolvedAt, t.ResolvedBy)).ToList();
    }

    private Task<GateLane?> LaneAsync(int terminalId, string? laneCode, LaneDirection direction, CancellationToken ct)
    {
        var code = laneCode?.Trim().ToUpperInvariant() ?? "";
        return db.GateLanes.AsNoTracking()
            .SingleOrDefaultAsync(l => l.TerminalId == terminalId && l.LaneCode == code && l.Direction == direction && l.IsActive, ct);
    }

    private async Task<(Terminal Terminal, TerminalSettings Settings, TimeZoneInfo TimeZone)> TerminalAsync(int terminalId, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct)
                       ?? throw new KeyNotFoundException($"Terminal {terminalId} not found.");
        var settings = TerminalSettings.From(await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
        return (terminal, settings, TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone));
    }

    private void AddEvent(int terminalId, int? laneId, long? appointmentId, GateEventType type, string? plateRead,
        IReadOnlyList<string> containerReads, string? details, DateTimeOffset now) =>
        db.GateEvents.Add(new GateEvent
        {
            TerminalId = terminalId,
            LaneId = laneId,
            AppointmentId = appointmentId,
            EventType = type,
            PlateRead = Truncate(plateRead?.Trim(), 20),
            ContainerRead = containerReads.FirstOrDefault(),
            Details = Truncate(string.Join(" ", new[] { containerReads.Count > 1 ? $"Containers: {string.Join(",", containerReads)}." : null, details }
                .Where(x => !string.IsNullOrWhiteSpace(x))), 400) is { Length: > 0 } d ? d : null,
            OccurredAt = now,
        });

    private void AddHistory(long appointmentId, AppointmentStatus from, AppointmentStatus to, int? userId, string reason, DateTimeOffset now) =>
        db.AppointmentStatusHistory.Add(new AppointmentStatusHistory
        {
            AppointmentId = appointmentId,
            FromStatus = from,
            ToStatus = to,
            ChangedBy = userId,
            Reason = Truncate(reason, 300),
            ChangedAt = now,
        });

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
