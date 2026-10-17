using Yardsyncs.Application.Languages;
using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Application.Tos;
using Yardsyncs.Domain;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Booking;

/// <summary>
/// "Book by BL" and "book several containers": finds the containers, runs the booking checks on each and says
/// which are already booked. Books nothing — the portal then books one appointment per truck.
/// </summary>
public sealed class ContainerLookupService(YardsyncsDbContext db, ITosAdapter tos, TimeProvider clock)
{
    public const int MaxContainers = 60;

    public const string TosDown = "The terminal system could not be reached. Try again in a few minutes.";

    public async Task<AdminResult<ContainerLookupResult>> LookupAsync(
        CurrentUser user, int terminalId, ContainerLookupInput input, CancellationToken ct = default)
    {
        var code = input.Category?.Trim().ToUpperInvariant() ?? "";
        var category = await db.TransactionCategories.AsNoTracking()
            .SingleOrDefaultAsync(c => c.TerminalId == terminalId && c.Code == code && c.IsActive, ct);
        if (category is null)
            return AdminResult<ContainerLookupResult>.Invalid($"Unknown move type '{input.Category}'.");
        if (category.Mode != CategoryMode.Container)
            return AdminResult<ContainerLookupResult>.Invalid($"{category.Code} is a cargo move; book it by cargo reference.");

        var blNo = string.IsNullOrWhiteSpace(input.BlNo) ? null : input.BlNo.Trim().ToUpperInvariant();
        List<(string ContainerNo, string? Iso)> containers;

        if (blNo is not null)
        {
            IReadOnlyList<TosBlUnit>? units;
            try
            {
                units = await tos.FindBlContainersAsync(terminalId, blNo, ct);
            }
            catch (TosUnavailableException)
            {
                return AdminResult<ContainerLookupResult>.Conflict(TosDown);
            }

            if (units is null)
                return AdminResult<ContainerLookupResult>.NotFound($"Bill of lading {blNo} was not found at this terminal.");
            containers = units.Select(u => (ContainerNumber.Normalize(u.ContainerNo), u.IsoType)).DistinctBy(c => c.Item1).ToList();
        }
        else
        {
            containers = IsoSizeType.SplitContainers(input.Containers ?? Array.Empty<string>()).Select(c => (c, (string?)null)).ToList();
            if (containers.Count == 0)
                return AdminResult<ContainerLookupResult>.Invalid("Enter a bill of lading or at least one container number.");
        }

        if (containers.Count > MaxContainers)
            return AdminResult<ContainerLookupResult>.Invalid(
                $"{containers.Count} containers is more than {MaxContainers} at a time; use the bulk upload for larger lists.");

        var bookingRef = string.IsNullOrWhiteSpace(input.BookingRef) ? null : input.BookingRef.Trim().ToUpperInvariant();
        var wellFormed = containers.Select((c, i) => (c.ContainerNo, Index: i)).Where(c => ContainerNumber.HasValidFormat(c.ContainerNo)).ToList();

        List<ItemCheck> checks;
        try
        {
            checks = wellFormed.Count == 0
                ? new List<ItemCheck>()
                : await ItemChecks.RunAsync(
                    tos, terminalId,
                    wellFormed.Select(c => new ItemToCheck(c.Index, category.Code, category.Mode, c.ContainerNo, bookingRef, null, null)).ToList(),
                    clock.GetUtcNow(), ct);
        }
        catch (TosUnavailableException)
        {
            return AdminResult<ContainerLookupResult>.Conflict(TosDown);
        }

        var numbers = containers.Select(c => c.ContainerNo).ToList();
        var live = await db.Appointments.AsNoTracking()
            .Where(a => a.TerminalId == terminalId
                        && (a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.EnRoute
                            || a.Status == AppointmentStatus.Arrived || a.Status == AppointmentStatus.GateIn))
            .SelectMany(a => a.Items.Where(i => i.ContainerNo != null && numbers.Contains(i.ContainerNo)),
                (a, i) => new { i.ContainerNo, a.AppointmentNo, a.CompanyId })
            .ToListAsync(ct);

        var result = containers.Select((c, index) =>
        {
            var size = IsoSizeType.LengthFeet(c.Iso);
            var booked = live.FirstOrDefault(l => l.ContainerNo == c.ContainerNo);
            if (booked is not null)
            {
                var visible = !user.IsCompanyUser || booked.CompanyId == user.CompanyId;
                return new LookupContainer(c.ContainerNo, c.Iso, size, LookupStatus.Booked,
                    new[] { visible ? $"Already booked on {booked.AppointmentNo}" : "Already booked by another company" },
                    Array.Empty<string>(), visible ? booked.AppointmentNo : null);
            }

            if (!ContainerNumber.HasValidFormat(c.ContainerNo))
                return new LookupContainer(c.ContainerNo, c.Iso, size, LookupStatus.Blocked,
                    new[] { $"'{c.ContainerNo}' is not a container number (4 letters + 7 digits)" }, Array.Empty<string>(), null);

            var mine = checks.Where(k => k.Index == index).ToList();
            var problems = mine.Where(k => k.Result == CheckResult.Fail).Select(k => k.Message ?? k.Type.ToString()).ToList();
            var warnings = mine.Where(k => k.Result == CheckResult.Warn).Select(k => k.Message ?? k.Type.ToString()).ToList();
            var status = problems.Count > 0 ? LookupStatus.Blocked : warnings.Count > 0 ? LookupStatus.Warning : LookupStatus.Ready;
            return new LookupContainer(c.ContainerNo, c.Iso, size, status, ServerText.T(problems), ServerText.T(warnings), null);
        }).ToList();

        return AdminResult<ContainerLookupResult>.Ok(new ContainerLookupResult(category.Code, blNo, result));
    }
}
