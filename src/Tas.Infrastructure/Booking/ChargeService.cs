using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tas.Application.Auth;
using Tas.Application.Capacity;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure.Booking;

/// <summary>A terminal's fees, in the currency's smallest unit (e.g. 2500 = SAR 25.00 with 2 decimals).</summary>
public sealed record FeeSchedule(string Currency, int Decimals, long Booking, long NoShow, long LateCancel, int LateCancelHours);

public sealed record ChargeRow(
    long ChargeId, string AppointmentNo, int CompanyId, string CompanyName, ChargeKind Kind, long AmountMinor, string Currency,
    DateTimeOffset CreatedAt, DateTimeOffset? WaivedAt, string? WaiveReason);

public sealed record CompanyTotal(int CompanyId, string CompanyName, int Charges, long AmountMinor);

/// <param name="From">First local day (inclusive).</param>
/// <param name="To">Last local day (inclusive).</param>
public sealed record ChargeReport(
    DateOnly From, DateOnly To, string Currency, int Decimals, IReadOnlyList<ChargeRow> Charges, IReadOnlyList<CompanyTotal> Companies, long TotalMinor);

/// <summary>
/// Fees: each terminal sets its own booking, no-show and late-cancellation fees (terminal settings) in its country's
/// currency. A charge is recorded against the appointment when the fee applies, once per kind; terminal admins can
/// waive one with a reason. Yardsyncs reports the charges per company; the terminal bills them through its own system.
/// </summary>
public sealed class ChargeService(TasDbContext db, TerminalProfiles profiles, TimeProvider clock)
{
    public async Task<FeeSchedule> FeesAsync(int terminalId, CancellationToken ct = default)
    {
        var settings = TerminalSettings.From(await db.TerminalSettings.AsNoTracking().Where(s => s.TerminalId == terminalId).ToListAsync(ct));
        var country = profiles.CountryFor(await TerminalProfiles.CodeAsync(db, terminalId, ct));
        return new FeeSchedule(country.Currency, country.CurrencyDecimals, settings.FeeBooking, settings.FeeNoShow, settings.FeeLateCancel, settings.LateCancelHours);
    }

    /// <summary>Adds the fee of this kind to the appointment if the terminal charges one (saved with the caller's change).</summary>
    public async Task ChargeAsync(Appointment appointment, ChargeKind kind, CancellationToken ct = default)
    {
        var fees = await FeesAsync(appointment.TerminalId, ct);
        var amount = kind switch
        {
            ChargeKind.Booking => fees.Booking,
            ChargeKind.NoShow => fees.NoShow,
            _ => fees.LateCancel,
        };
        if (amount <= 0 || await db.AppointmentCharges.AnyAsync(c => c.AppointmentId == appointment.AppointmentId && c.Kind == kind, ct))
            return;
        db.AppointmentCharges.Add(new AppointmentCharge
        {
            TerminalId = appointment.TerminalId, AppointmentId = appointment.AppointmentId, CompanyId = appointment.CompanyId,
            Kind = kind, AmountMinor = amount, Currency = fees.Currency, CreatedAt = clock.GetUtcNow(),
        });
    }

    /// <summary>
    /// A cancellation: a company cancelling within the late-cancellation window pays that fee; one cancelling in time,
    /// or the terminal cancelling, gets the booking fee waived.
    /// </summary>
    public async Task CancelledAsync(Appointment appointment, DateTimeOffset slotStart, bool byTerminal, int userId, CancellationToken ct = default)
    {
        var fees = await FeesAsync(appointment.TerminalId, ct);
        var now = clock.GetUtcNow();
        var late = !byTerminal && fees.LateCancelHours > 0 && now > slotStart.AddHours(-fees.LateCancelHours);
        if (late)
        {
            await ChargeAsync(appointment, ChargeKind.LateCancel, ct);
            return;
        }
        var booking = await db.AppointmentCharges.SingleOrDefaultAsync(
            c => c.AppointmentId == appointment.AppointmentId && c.Kind == ChargeKind.Booking && c.WaivedAt == null, ct);
        if (booking is not null)
        {
            booking.WaivedAt = now;
            booking.WaivedBy = userId;
            booking.WaiveReason = byTerminal ? "Cancelled by the terminal" : "Cancelled in time";
        }
    }

    public async Task<AdminResult<ChargeReport>> ReportAsync(
        CurrentUser user, int terminalId, DateOnly from, DateOnly to, int? companyId, CancellationToken ct = default)
    {
        if (!AccessRules.CanAccessTerminal(user, terminalId))
            return AdminResult<ChargeReport>.Forbidden("You cannot see this terminal.");
        if (user.IsCompanyUser)
            companyId = user.CompanyId; // a company sees its own charges only
        else if (!AccessRules.CanViewCapacity(user, terminalId))
            return AdminResult<ChargeReport>.Forbidden("Only the terminal's staff see its charges.");
        if (to < from || to.DayNumber - from.DayNumber > 92)
            return AdminResult<ChargeReport>.Invalid("Choose up to three months, 'from' on or before 'to'.");

        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.TerminalId == terminalId, ct);
        if (terminal is null)
            return AdminResult<ChargeReport>.NotFound($"Terminal {terminalId} not found.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(terminal.TimeZone);
        DateTimeOffset Start(DateOnly d) => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(d.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var (start, end) = (Start(from), Start(to.AddDays(1)));

        var rows = await db.AppointmentCharges.AsNoTracking()
            .Where(c => c.TerminalId == terminalId && c.CreatedAt >= start && c.CreatedAt < end && (companyId == null || c.CompanyId == companyId))
            .OrderBy(c => c.CreatedAt)
            .Select(c => new ChargeRow(
                c.ChargeId,
                db.Appointments.Where(a => a.AppointmentId == c.AppointmentId).Select(a => a.AppointmentNo).First(),
                c.CompanyId,
                db.Companies.Where(x => x.CompanyId == c.CompanyId).Select(x => x.Name).First(),
                c.Kind, c.AmountMinor, c.Currency, c.CreatedAt, c.WaivedAt, c.WaiveReason))
            .Take(20_000)
            .ToListAsync(ct);

        var fees = await FeesAsync(terminalId, ct);
        var totals = rows.Where(r => r.WaivedAt is null)
            .GroupBy(r => (r.CompanyId, r.CompanyName))
            .Select(g => new CompanyTotal(g.Key.CompanyId, g.Key.CompanyName, g.Count(), g.Sum(r => r.AmountMinor)))
            .OrderByDescending(t => t.AmountMinor).ToList();
        return AdminResult<ChargeReport>.Ok(new ChargeReport(from, to, fees.Currency, fees.Decimals, rows, totals, totals.Sum(t => t.AmountMinor)));
    }

    public async Task<AdminResult<ChargeRow>> WaiveAsync(CurrentUser user, int terminalId, long chargeId, string? reason, CancellationToken ct = default)
    {
        if (!(user.IsSysAdmin || user.AdministersTerminal(terminalId)))
            return AdminResult<ChargeRow>.Forbidden("Only the terminal's admins waive charges.");
        if (string.IsNullOrWhiteSpace(reason))
            return AdminResult<ChargeRow>.Invalid("A reason is required to waive a charge.");
        var charge = await db.AppointmentCharges.SingleOrDefaultAsync(c => c.ChargeId == chargeId && c.TerminalId == terminalId, ct);
        if (charge is null)
            return AdminResult<ChargeRow>.NotFound($"Charge {chargeId} not found.");
        if (charge.WaivedAt is not null)
            return AdminResult<ChargeRow>.Conflict("This charge is already waived.");

        charge.WaivedAt = clock.GetUtcNow();
        charge.WaivedBy = user.UserId;
        charge.WaiveReason = reason.Trim()[..Math.Min(reason.Trim().Length, 300)];
        AuditWriter.Add(db, terminalId, "appointment_charge", charge.ChargeId, AuditAction.Update, null,
            new { charge.Kind, charge.AmountMinor, charge.Currency, Waived = true }, charge.WaiveReason, user.UserId, charge.WaivedAt.Value);
        await db.SaveChangesAsync(ct);

        var no = await db.Appointments.AsNoTracking().Where(a => a.AppointmentId == charge.AppointmentId).Select(a => a.AppointmentNo).SingleAsync(ct);
        var company = await db.Companies.AsNoTracking().Where(c => c.CompanyId == charge.CompanyId).Select(c => c.Name).SingleAsync(ct);
        return AdminResult<ChargeRow>.Ok(new ChargeRow(charge.ChargeId, no, charge.CompanyId, company, charge.Kind, charge.AmountMinor, charge.Currency,
            charge.CreatedAt, charge.WaivedAt, charge.WaiveReason));
    }

    /// <summary>The report as CSV for the terminal's billing system (amounts in major units, invariant format).</summary>
    public static string Csv(ChargeReport report)
    {
        string Amount(long minor) => (minor / Math.Pow(10, report.Decimals)).ToString("F" + report.Decimals, CultureInfo.InvariantCulture);
        static string Cell(string? s) => s is null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 || s.StartsWith('=') || s.StartsWith('+') || s.StartsWith('-') || s.StartsWith('@')
            ? "\"" + (s.StartsWith('=') || s.StartsWith('+') || s.StartsWith('-') || s.StartsWith('@') ? "'" : "") + s.Replace("\"", "\"\"") + "\""
            : s;
        var csv = new StringBuilder("charge_id,appointment_no,company_id,company,kind,amount,currency,charged_at_utc,waived_at_utc,waive_reason\r\n");
        foreach (var r in report.Charges)
            csv.Append(CultureInfo.InvariantCulture, $"{r.ChargeId},{Cell(r.AppointmentNo)},{r.CompanyId},{Cell(r.CompanyName)},{r.Kind},{Amount(r.AmountMinor)},{r.Currency},")
               .Append(CultureInfo.InvariantCulture, $"{r.CreatedAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{r.WaivedAt?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)},{Cell(r.WaiveReason)}\r\n");
        return csv.ToString();
    }
}
