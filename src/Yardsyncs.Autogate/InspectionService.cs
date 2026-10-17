using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Gate;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Autogate;

/// <summary>
/// Inspection at the gate: per container and passage, the seal against the one the company declared and the damage
/// seen, with photos — recorded by the lane cameras on an automatic lane and by clerks. Companies see their own
/// visits' inspections and can dispute one; terminal planners and admins accept or reject a dispute with a note.
/// Endpoints check terminal access; company users only ever see their own company's visits.
/// </summary>
public sealed class InspectionService(YardsyncsDbContext db, TimeProvider clock, ILogger<InspectionService> logger)
{
    // ================================================================ reading

    public async Task<AppointmentInspections?> ForAppointmentAsync(CurrentUser user, int terminalId, string appointmentNo, CancellationToken ct = default)
    {
        var a = await AppointmentAsync(user, terminalId, appointmentNo, ct);
        if (a is null)
            return null;
        var items = await db.AppointmentItems.AsNoTracking().Where(i => i.AppointmentId == a.AppointmentId).OrderBy(i => i.ItemId)
            .Select(i => new { i.ContainerNo, i.Category.Code, i.SealNo }).ToListAsync(ct);
        var inspections = await db.GateInspections.AsNoTracking().Include(i => i.Damage)
            .Where(i => i.AppointmentId == a.AppointmentId).OrderBy(i => i.Stage).ThenBy(i => i.ContainerNo).ToListAsync(ct);
        var views = await ViewsAsync(inspections, ct);
        var newAtOut = InspectionRules.NewAtGateOut(inspections.Select(i =>
            (i.ContainerNo, i.Stage, i.Damage.Select(d => (d.Side, d.Kind, d.Severity)))));
        var company = await db.Companies.AsNoTracking().Where(c => c.CompanyId == a.CompanyId).Select(c => c.Name).SingleAsync(ct);
        var plate = a.TruckId is null ? null : await db.Trucks.AsNoTracking().Where(t => t.TruckId == a.TruckId).Select(t => t.PlateNumber).SingleOrDefaultAsync(ct);
        return new AppointmentInspections(a.AppointmentNo, a.Status, company, plate,
            items.Select((i, n) => new InspectionItem(n + 1, i.ContainerNo, i.Code, i.SealNo)).ToList(), views, newAtOut);
    }

    public async Task<IReadOnlyList<InspectionRow>> ListAsync(
        CurrentUser user, int terminalId, DateTimeOffset from, DateTimeOffset to, bool damageOnly, bool disputedOnly, CancellationToken ct = default)
    {
        var companyId = user.IsCompanyUser ? user.CompanyId : null;
        var rows = await db.GateInspections.AsNoTracking()
            .Where(i => i.TerminalId == terminalId && i.InspectedAt >= from && i.InspectedAt < to)
            .Join(db.Appointments, i => i.AppointmentId, a => a.AppointmentId, (i, a) => new { i, a.AppointmentNo, a.CompanyId, Company = a.Company.Name })
            .Where(x => companyId == null || x.CompanyId == companyId)
            .Where(x => !damageOnly || x.i.DamageFound || x.i.SealStatus == SealStatus.Mismatch || x.i.SealStatus == SealStatus.Broken || x.i.SealStatus == SealStatus.Missing)
            .Select(x => new
            {
                x.i.InspectionId, x.AppointmentNo, x.Company, x.i.ContainerNo, x.i.Stage, x.i.InspectedAt, x.i.Source, x.i.SealStatus,
                Damage = x.i.Damage.Count,
                DisputeId = db.InspectionDisputes.Where(d => d.InspectionId == x.i.InspectionId).Select(d => (long?)d.DisputeId).FirstOrDefault(),
                DisputeStatus = db.InspectionDisputes.Where(d => d.InspectionId == x.i.InspectionId).Select(d => (DisputeStatus?)d.Status).FirstOrDefault(),
            })
            .Where(x => !disputedOnly || x.DisputeId != null)
            .OrderByDescending(x => x.InspectedAt)
            .Take(500)
            .ToListAsync(ct);
        return rows.Select(r => new InspectionRow(r.InspectionId, r.AppointmentNo, r.Company, r.ContainerNo.Length == 0 ? null : r.ContainerNo,
            r.Stage, r.InspectedAt, r.Source, r.SealStatus, r.Damage, r.DisputeStatus, r.DisputeId)).ToList();
    }

    /// <summary>A photo, for whoever may see its visit.</summary>
    public async Task<(string ContentType, byte[] Bytes)?> PhotoAsync(CurrentUser user, int terminalId, long photoId, CancellationToken ct = default)
    {
        var companyId = user.IsCompanyUser ? user.CompanyId : null;
        var photo = await db.InspectionPhotos.AsNoTracking()
            .Where(p => p.PhotoId == photoId)
            .Join(db.GateInspections, p => p.InspectionId, i => i.InspectionId, (p, i) => new { p, i.TerminalId, i.AppointmentId })
            .Join(db.Appointments, x => x.AppointmentId, a => a.AppointmentId, (x, a) => new { x.p, x.TerminalId, a.CompanyId })
            .Where(x => x.TerminalId == terminalId && (companyId == null || x.CompanyId == companyId))
            .Select(x => new { x.p.ContentType, x.p.Bytes })
            .SingleOrDefaultAsync(ct);
        return photo is null ? null : (photo.ContentType, photo.Bytes);
    }

    // ================================================================ recording

    /// <summary>The seal number the company declares for a container, until the truck is at the gate.</summary>
    public async Task<AdminResult<AppointmentInspections>> DeclareSealAsync(CurrentUser user, int terminalId, string appointmentNo, int itemIndex, SealInput input, CancellationToken ct = default)
    {
        var a = await AppointmentAsync(user, terminalId, appointmentNo, ct);
        if (a is null)
            return AdminResult<AppointmentInspections>.NotFound($"Appointment {appointmentNo} not found.");
        if (!Appointment.IsBeforeGate(a.Status) || a.Status == AppointmentStatus.Arrived)
            return AdminResult<AppointmentInspections>.Conflict("The truck is already at the gate; the seal can no longer be changed.");
        var seal = InspectionRules.NormalizeSeal(input.SealNo);
        if (seal?.Length > 20)
            return AdminResult<AppointmentInspections>.Invalid("A seal number is at most 20 characters.");
        var items = await db.AppointmentItems.Where(i => i.AppointmentId == a.AppointmentId).OrderBy(i => i.ItemId).ToListAsync(ct);
        if (itemIndex < 1 || itemIndex > items.Count || items[itemIndex - 1].ContainerNo is null)
            return AdminResult<AppointmentInspections>.Invalid($"Move {itemIndex} is not a container of this appointment.");
        items[itemIndex - 1].SealNo = seal;
        await db.SaveChangesAsync(ct);
        return AdminResult<AppointmentInspections>.Ok((await ForAppointmentAsync(user, terminalId, appointmentNo, ct))!);
    }

    /// <summary>A clerk records or corrects the inspection of a container (or the cargo truck) at gate-in or gate-out.</summary>
    public async Task<AdminResult<InspectionView>> SaveAsync(CurrentUser clerk, int terminalId, string appointmentNo, InspectionInput input, CancellationToken ct = default)
    {
        if (InspectionRules.Validate(input) is { Count: > 0 } errors)
            return AdminResult<InspectionView>.Invalid(errors);
        var a = await AppointmentAsync(clerk, terminalId, appointmentNo, ct);
        if (a is null)
            return AdminResult<InspectionView>.NotFound($"Appointment {appointmentNo} not found.");
        var reached = input.Stage == InspectionStage.In
            ? a.Status is AppointmentStatus.Arrived or AppointmentStatus.GateIn or AppointmentStatus.Completed
            : a.Status is AppointmentStatus.GateIn or AppointmentStatus.Completed;
        if (!reached)
            return AdminResult<InspectionView>.Conflict(input.Stage == InspectionStage.In
                ? "The truck has not reached the gate yet."
                : "The truck is not inside the terminal.");

        var container = ContainerNumber.Normalize(input.ContainerNo ?? "");
        var items = await db.AppointmentItems.AsNoTracking().Where(i => i.AppointmentId == a.AppointmentId).Select(i => new { i.ContainerNo, i.SealNo }).ToListAsync(ct);
        var item = items.FirstOrDefault(i => (i.ContainerNo ?? "") == container);
        if (item is null && container.Length > 0)
            return AdminResult<InspectionView>.Invalid($"{container} is not a container of this appointment.");

        var photoIds = (input.Damage ?? []).Where(d => d.PhotoId is not null).Select(d => d.PhotoId!.Value).Distinct().ToList();
        var now = clock.GetUtcNow();
        var inspection = await db.GateInspections.Include(i => i.Damage)
            .SingleOrDefaultAsync(i => i.AppointmentId == a.AppointmentId && i.Stage == input.Stage && i.ContainerNo == container, ct);
        if (photoIds.Count > 0)
        {
            var owned = inspection is null ? 0 : await db.InspectionPhotos.CountAsync(p => p.InspectionId == inspection.InspectionId && photoIds.Contains(p.PhotoId), ct);
            if (owned != photoIds.Count)
                return AdminResult<InspectionView>.Invalid("A damage photo must be one uploaded to this inspection.");
        }
        if (inspection is null)
        {
            inspection = new GateInspection { TerminalId = terminalId, AppointmentId = a.AppointmentId, Stage = input.Stage, ContainerNo = container };
            db.GateInspections.Add(inspection);
        }
        else
            db.InspectionDamages.RemoveRange(inspection.Damage);

        inspection.SealExpected = item?.SealNo;
        inspection.SealSeen = InspectionRules.NormalizeSeal(input.SealSeen);
        inspection.SealStatus = InspectionRules.Seal(item?.SealNo, input.SealSeen, input.SealBroken, input.SealMissing);
        inspection.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        inspection.Source = InspectionSource.Clerk;
        inspection.InspectedAt = now;
        inspection.InspectedBy = clerk.UserId;
        inspection.Damage = (input.Damage ?? []).Select(d => new InspectionDamage
        {
            Side = d.Side, Kind = d.Kind, Severity = d.Severity,
            Note = string.IsNullOrWhiteSpace(d.Note) ? null : d.Note.Trim(),
            PhotoRef = string.IsNullOrWhiteSpace(d.PhotoRef) ? null : d.PhotoRef.Trim(),
            PhotoId = d.PhotoId,
        }).ToList();
        inspection.DamageFound = inspection.Damage.Count > 0;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Inspection of {No} {Container} at {Stage}: seal {Seal}, {Damage} damage, by user {UserId}",
            a.AppointmentNo, container, input.Stage, inspection.SealStatus, inspection.Damage.Count, clerk.UserId);
        return AdminResult<InspectionView>.Ok((await ViewsAsync([inspection], ct)).Single());
    }

    /// <summary>A photo taken at the gate, added to an inspection (JPEG, PNG or WebP, at most 3 MB, eight per inspection).</summary>
    public async Task<AdminResult<long>> AddPhotoAsync(CurrentUser clerk, int terminalId, long inspectionId, string contentType, byte[] bytes, CancellationToken ct = default)
    {
        if (!InspectionRules.PhotoTypes.Contains(contentType))
            return AdminResult<long>.Invalid("Photos are JPEG, PNG or WebP.");
        if (bytes.Length is 0 or > InspectionRules.MaxPhotoBytes)
            return AdminResult<long>.Invalid("A photo is at most 3 MB.");
        if (!await db.GateInspections.AnyAsync(i => i.InspectionId == inspectionId && i.TerminalId == terminalId, ct))
            return AdminResult<long>.NotFound($"Inspection {inspectionId} not found.");
        if (await db.InspectionPhotos.CountAsync(p => p.InspectionId == inspectionId, ct) >= InspectionRules.MaxPhotosPerInspection)
            return AdminResult<long>.Conflict($"An inspection keeps at most {InspectionRules.MaxPhotosPerInspection} photos.");
        var photo = new InspectionPhoto
        {
            InspectionId = inspectionId, ContentType = contentType.ToLowerInvariant(), Bytes = bytes, SizeBytes = bytes.Length,
            UploadedAt = clock.GetUtcNow(), UploadedBy = clerk.UserId,
        };
        db.InspectionPhotos.Add(photo);
        await db.SaveChangesAsync(ct);
        return AdminResult<long>.Created(photo.PhotoId);
    }

    /// <summary>
    /// What the lane cameras saw, as an inspection per container (when nothing was recorded for it yet): the seals read,
    /// checked against the declared ones, and the damage the OCR reported with its pictures.
    /// </summary>
    public async Task FromLaneAsync(long transactionId, CancellationToken ct = default)
    {
        var tx = await db.LaneTransactions.AsNoTracking().SingleOrDefaultAsync(t => t.TransactionId == transactionId, ct);
        if (tx?.AppointmentId is not { } appointmentId)
            return;
        var direction = await db.GateLanes.AsNoTracking().Where(l => l.LaneId == tx.LaneId).Select(l => l.Direction).SingleAsync(ct);
        var stage = direction == LaneDirection.In ? InspectionStage.In : InspectionStage.Out;
        var reads = (await db.LaneDeviceReads.AsNoTracking().Where(r => r.TransactionId == transactionId).ToListAsync(ct))
            .Select(DeviceService.Normalized).ToList();
        var seals = reads.SelectMany(r => r.Seals).Select(InspectionRules.NormalizeSeal).Where(s => s is not null).Select(s => s!).Distinct().ToList();
        var damage = reads.SelectMany(r => r.Damage).ToList();
        if (seals.Count == 0 && damage.Count == 0)
            return;

        var items = await db.AppointmentItems.AsNoTracking().Where(i => i.AppointmentId == appointmentId).OrderBy(i => i.ItemId)
            .Select(i => new { i.ContainerNo, i.SealNo }).ToListAsync(ct);
        var containers = items.Where(i => i.ContainerNo is not null).ToList();
        var now = clock.GetUtcNow();
        List<(string Container, string? Declared)> targets = containers.Count == 0 ? [("", null)] : containers.Select(c => (c.ContainerNo!, c.SealNo)).ToList();
        foreach (var (container, declared) in targets)
        {
            if (await db.GateInspections.AnyAsync(i => i.AppointmentId == appointmentId && i.Stage == stage && i.ContainerNo == container, ct))
                continue;
            // A declared seal among those read is this container's; otherwise the first unclaimed one.
            var seen = declared is not null && seals.Contains(InspectionRules.NormalizeSeal(declared)!) ? InspectionRules.NormalizeSeal(declared) : seals.FirstOrDefault();
            if (seen is not null) seals.Remove(seen);
            var first = container == targets[0].Container;
            List<DamageView> found = first ? damage : [];
            if (seen is null && found.Count == 0)
                continue;
            db.GateInspections.Add(new GateInspection
            {
                TerminalId = tx.TerminalId, AppointmentId = appointmentId, Stage = stage, ContainerNo = container,
                SealExpected = declared, SealSeen = seen, SealStatus = InspectionRules.Seal(declared, seen),
                Source = InspectionSource.Camera, InspectedAt = now,
                Damage = found.Select(d => new InspectionDamage
                {
                    Side = InspectionRules.SideFrom(d.Side), Kind = DamageKind.Other, Severity = DamageSeverity.Minor,
                    Note = d.Note is null ? null : (d.Note.Length > 200 ? d.Note[..200] : d.Note),
                    PhotoRef = d.Photo,
                }).ToList(),
                DamageFound = found.Count > 0,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    // ================================================================ disputes

    public async Task<AdminResult<InspectionView>> OpenDisputeAsync(CurrentUser user, int terminalId, long inspectionId, DisputeInput input, CancellationToken ct = default)
    {
        if (!user.IsCompanyUser || user.CompanyId is not { } companyId)
            return AdminResult<InspectionView>.Forbidden("Disputes are opened by the trucking company.");
        var reason = input.Reason?.Trim() ?? "";
        if (reason.Length is < 5 or > 600)
            return AdminResult<InspectionView>.Invalid("Say what is wrong with this inspection (5–600 characters).");
        var inspection = await db.GateInspections.Include(i => i.Damage)
            .Where(i => i.InspectionId == inspectionId && i.TerminalId == terminalId)
            .Where(i => db.Appointments.Any(a => a.AppointmentId == i.AppointmentId && a.CompanyId == companyId))
            .SingleOrDefaultAsync(ct);
        if (inspection is null)
            return AdminResult<InspectionView>.NotFound($"Inspection {inspectionId} not found.");
        if (await db.InspectionDisputes.AnyAsync(d => d.InspectionId == inspectionId, ct))
            return AdminResult<InspectionView>.Conflict("This inspection is already disputed.");
        db.InspectionDisputes.Add(new InspectionDispute
        {
            InspectionId = inspectionId, CompanyId = companyId, Reason = reason, OpenedBy = user.UserId, OpenedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Inspection {Id} disputed by company {CompanyId}", inspectionId, companyId);
        return AdminResult<InspectionView>.Created((await ViewsAsync([inspection], ct)).Single());
    }

    public async Task<AdminResult<InspectionView>> ResolveDisputeAsync(CurrentUser staff, int terminalId, long disputeId, ResolveDisputeInput input, CancellationToken ct = default)
    {
        var note = input.Note?.Trim() ?? "";
        if (note.Length is 0 or > 600)
            return AdminResult<InspectionView>.Invalid("A note for the company is required (at most 600 characters).");
        var dispute = await db.InspectionDisputes.SingleOrDefaultAsync(d => d.DisputeId == disputeId, ct);
        var inspection = dispute is null ? null : await db.GateInspections.Include(i => i.Damage)
            .SingleOrDefaultAsync(i => i.InspectionId == dispute.InspectionId && i.TerminalId == terminalId, ct);
        if (dispute is null || inspection is null)
            return AdminResult<InspectionView>.NotFound($"Dispute {disputeId} not found.");
        if (dispute.Status != DisputeStatus.Open)
            return AdminResult<InspectionView>.Conflict($"This dispute was already {dispute.Status.ToString().ToLowerInvariant()}.");
        var now = clock.GetUtcNow();
        dispute.Status = input.Accept ? DisputeStatus.Accepted : DisputeStatus.Rejected;
        dispute.ResolutionNote = note;
        dispute.ResolvedBy = staff.UserId;
        dispute.ResolvedAt = now;
        AuditWriter.Add(db, terminalId, "inspection_dispute", dispute.DisputeId, AuditAction.Update, null,
            new { dispute.Status, dispute.ResolutionNote }, note, staff.UserId, now);
        await db.SaveChangesAsync(ct);
        return AdminResult<InspectionView>.Ok((await ViewsAsync([inspection], ct)).Single());
    }

    // ================================================================ helpers

    /// <summary>The appointment, if this user may see it (company users only their own company's).</summary>
    private async Task<Appointment?> AppointmentAsync(CurrentUser user, int terminalId, string appointmentNo, CancellationToken ct)
    {
        var number = appointmentNo.Trim().ToUpperInvariant();
        var companyId = user.IsCompanyUser ? user.CompanyId : null;
        return await db.Appointments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.TerminalId == terminalId && a.AppointmentNo == number && (companyId == null || a.CompanyId == companyId), ct);
    }

    private async Task<IReadOnlyList<InspectionView>> ViewsAsync(IReadOnlyList<GateInspection> inspections, CancellationToken ct)
    {
        var ids = inspections.Select(i => i.InspectionId).ToList();
        var photos = (await db.InspectionPhotos.AsNoTracking().Where(p => ids.Contains(p.InspectionId)).OrderBy(p => p.PhotoId)
            .Select(p => new { p.InspectionId, p.PhotoId }).ToListAsync(ct)).ToLookup(p => p.InspectionId, p => p.PhotoId);
        var disputes = await db.InspectionDisputes.AsNoTracking().Where(d => ids.Contains(d.InspectionId)).ToDictionaryAsync(d => d.InspectionId, ct);
        return inspections.Select(i => new InspectionView(
            i.InspectionId, i.Stage, i.ContainerNo.Length == 0 ? null : i.ContainerNo, i.SealExpected, i.SealSeen, i.SealStatus, i.DamageFound, i.Notes,
            i.Source, i.InspectedAt,
            i.Damage.OrderBy(d => d.DamageId).Select(d => new DamageEntryView(d.DamageId, d.Side, d.Kind, d.Severity, d.Note, d.PhotoRef, d.PhotoId)).ToList(),
            photos[i.InspectionId].ToList(),
            disputes.TryGetValue(i.InspectionId, out var dispute)
                ? new DisputeView(dispute.DisputeId, dispute.Status, dispute.Reason, dispute.OpenedAt, dispute.ResolutionNote, dispute.ResolvedAt)
                : null))
            .ToList();
    }
}
