using Tas.Application.Booking;
using Tas.Application.MasterData;
using Tas.Domain;
using Tas.Domain.Booking;
using Tas.Domain.Gate;

namespace Tas.Application.Gate;

/// <summary>What the lane saw: any of plate (OCR), container numbers (OCR), the QR code from the driver's app, or a typed appointment number.</summary>
public sealed record GateReads(string? PlateRead, IReadOnlyList<string> ContainerReads, string? QrToken, string? AppointmentNo);

/// <summary>An appointment the truck might be coming for.</summary>
public sealed record GateCandidate(
    long AppointmentId,
    string AppointmentNo,
    string QrToken,
    AppointmentStatus Status,
    DateTimeOffset SlotStart,
    DateTimeOffset SlotEnd,
    string? TruckPlateNormalized,
    IReadOnlySet<string> Containers);

public enum MatchMethod
{
    None,
    QrCode,
    AppointmentNo,
    Plate,
    Container,
}

public sealed record GateMatch(GateCandidate? Appointment, MatchMethod Method, IReadOnlyList<string> Warnings);

public enum GateDecisionKind
{
    Admit,
    Refer,
}

public sealed record GateDecision(GateDecisionKind Kind, TroubleReason? Reason, string Message);

public static class GateMatcher
{
    /// <summary>
    /// QR code or appointment number win outright. Otherwise the plate (Arabic or Latin) picks the truck's
    /// appointment closest to now; failing that, a container read does. Reads that don't fit the chosen
    /// appointment are reported as warnings for the clerk.
    /// </summary>
    public static GateMatch Match(GateReads reads, IReadOnlyList<GateCandidate> candidates, DateTimeOffset now)
    {
        var warnings = new List<string>();
        var containers = reads.ContainerReads
            .Select(ContainerNumber.Normalize)
            .Where(c => c.Length > 0)
            .Distinct()
            .ToList();

        GateMatch Found(GateCandidate c, MatchMethod method)
        {
            foreach (var container in containers.Where(x => !c.Containers.Contains(x)))
                warnings.Add($"Container {container} was read but is not on appointment {c.AppointmentNo}.");

            var plate = PlateNumber.Normalize(reads.PlateRead);
            if (plate.Length > 0 && c.TruckPlateNormalized is not null && plate != c.TruckPlateNormalized)
                warnings.Add($"Plate {plate} was read but the appointment is for truck {c.TruckPlateNormalized}.");

            return new GateMatch(c, method, warnings);
        }

        if (!string.IsNullOrWhiteSpace(reads.QrToken))
        {
            var byQr = candidates.FirstOrDefault(c => string.Equals(c.QrToken, reads.QrToken.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byQr is not null)
                return Found(byQr, MatchMethod.QrCode);
            warnings.Add("The QR code does not belong to any appointment at this terminal.");
        }

        if (!string.IsNullOrWhiteSpace(reads.AppointmentNo))
        {
            var byNumber = candidates.FirstOrDefault(c => string.Equals(c.AppointmentNo, reads.AppointmentNo.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byNumber is not null)
                return Found(byNumber, MatchMethod.AppointmentNo);
            warnings.Add($"Appointment {reads.AppointmentNo.Trim().ToUpperInvariant()} was not found.");
        }

        var plateRead = PlateNumber.Normalize(reads.PlateRead);
        if (plateRead.Length > 0)
        {
            var byPlate = Closest(candidates.Where(c => c.TruckPlateNormalized == plateRead), now);
            if (byPlate is not null)
                return Found(byPlate, MatchMethod.Plate);
        }

        if (containers.Count > 0)
        {
            var byContainer = Closest(candidates.Where(c => containers.Any(c.Containers.Contains)), now);
            if (byContainer is not null)
                return Found(byContainer, MatchMethod.Container);
        }

        return new GateMatch(null, MatchMethod.None, warnings);
    }

    // Live appointments are preferred over finished ones; then the slot nearest to now.
    private static GateCandidate? Closest(IEnumerable<GateCandidate> candidates, DateTimeOffset now) =>
        candidates
            .OrderBy(c => Appointment.IsLive(c.Status) ? 0 : 1)
            .ThenBy(c => Math.Abs((c.SlotStart - now).TotalMinutes))
            .FirstOrDefault();
}

public static class GateDecider
{
    /// <summary>
    /// Admit only a live appointment whose truck and driver aren't blocked, inside its window, with nothing red.
    /// Everything else goes to the trouble desk with the reason — the clerk decides.
    /// </summary>
    public static GateDecision Decide(
        GateCandidate? appointment,
        TrafficLight light,
        string lightReason,
        bool truckBlocked,
        bool driverBlocked,
        DateTimeOffset now,
        TerminalSettings settings,
        TimeZoneInfo timeZone)
    {
        string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, timeZone).ToString("HH:mm");

        if (appointment is null)
            return Refer(TroubleReason.NoAppointment, "No appointment found for this truck.");

        switch (appointment.Status)
        {
            case AppointmentStatus.GateIn:
                return Refer(TroubleReason.AlreadyIn, $"Appointment {appointment.AppointmentNo} is already inside the terminal.");
            case AppointmentStatus.Completed:
                return Refer(TroubleReason.AlreadyIn, $"Appointment {appointment.AppointmentNo} was already completed.");
            case AppointmentStatus.Cancelled or AppointmentStatus.NoShow or AppointmentStatus.RejectedAtGate or AppointmentStatus.Draft:
                return Refer(TroubleReason.Cancelled, $"Appointment {appointment.AppointmentNo} is {appointment.Status}.");
        }

        if (truckBlocked)
            return Refer(TroubleReason.Blocked, "This truck is blocked at the terminal.");
        if (driverBlocked)
            return Refer(TroubleReason.Blocked, "This driver is blocked at the terminal.");

        if (now > appointment.SlotEnd.AddMinutes(settings.LateGraceMinutes))
            return Refer(TroubleReason.Late, $"Late: the arrival window closed at {Local(appointment.SlotEnd.AddMinutes(settings.LateGraceMinutes))}.");
        if (light == TrafficLight.Red)
            return Refer(TroubleReason.NotCleared, lightReason);
        if (now < appointment.SlotStart.AddMinutes(-settings.EarlyGraceMinutes))
            return Refer(TroubleReason.Early, $"Early: the arrival window opens at {Local(appointment.SlotStart.AddMinutes(-settings.EarlyGraceMinutes))}.");

        return new GateDecision(GateDecisionKind.Admit, null,
            light == TrafficLight.Amber ? $"Admit — note: {lightReason}" : "Admit");
    }

    private static GateDecision Refer(TroubleReason reason, string message) => new(GateDecisionKind.Refer, reason, message);
}
