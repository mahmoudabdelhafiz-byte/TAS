using Tas.Domain;
using Tas.Domain.Booking;

namespace Tas.Application.Booking;

public sealed record ItemCheckState(string Label, ValidationStatus Status, string? FirstProblem);

/// <summary>
/// The driver-facing status of an appointment:
///   RED   — a check failed (hold, not available, unpaid…) or the arrival window has passed
///   GREEN — all checks pass and now is inside the slot ± grace window: proceed to the gate
///   AMBER — a warning needs attention
///   GREY  — fine, but not time to come yet (or the visit is finished/cancelled)
/// </summary>
public static class TrafficLightCalculator
{
    public static (TrafficLight Light, string Reason) Calculate(
        AppointmentStatus status,
        IReadOnlyList<ItemCheckState> items,
        DateTimeOffset slotStart,
        DateTimeOffset slotEnd,
        DateTimeOffset now,
        TerminalSettings settings,
        TimeZoneInfo terminalTimeZone)
    {
        switch (status)
        {
            case AppointmentStatus.Cancelled:
                return (TrafficLight.Grey, "Cancelled");
            case AppointmentStatus.NoShow:
                return (TrafficLight.Grey, "Missed — the truck did not arrive in its window");
            case AppointmentStatus.Completed:
                return (TrafficLight.Grey, "Visit completed");
            case AppointmentStatus.RejectedAtGate:
                return (TrafficLight.Red, "Rejected at gate — contact the terminal");
            case AppointmentStatus.GateIn:
                return (TrafficLight.Green, "In terminal — follow the yard instructions");
        }

        var failed = items.FirstOrDefault(i => i.Status == ValidationStatus.Fail);
        if (failed is not null)
            return (TrafficLight.Red, failed.FirstProblem ?? $"{failed.Label}: not cleared");

        var windowOpens = slotStart.AddMinutes(-settings.EarlyGraceMinutes);
        var windowCloses = slotEnd.AddMinutes(settings.LateGraceMinutes);

        if (now > windowCloses)
            return (TrafficLight.Red, "Arrival window has passed — reschedule the appointment");

        var warning = items.FirstOrDefault(i => i.Status == ValidationStatus.Warn);
        if (warning is not null)
            return (TrafficLight.Amber, warning.FirstProblem ?? $"{warning.Label}: needs attention");

        if (items.Any(i => i.Status == ValidationStatus.Pending))
            return (TrafficLight.Amber, "Checks are still running");

        if (now >= windowOpens)
            return (TrafficLight.Green, "Ready — proceed to the gate");

        var local = TimeZoneInfo.ConvertTime(slotStart, terminalTimeZone);
        var localOpens = TimeZoneInfo.ConvertTime(windowOpens, terminalTimeZone);
        return (TrafficLight.Grey, $"Appointment {local:ddd dd MMM HH:mm} — gate opens for you at {localOpens:HH:mm}");
    }

    /// <summary>Worst result across an item's checks.</summary>
    public static ValidationStatus Summarize(IEnumerable<CheckResult> results)
    {
        var list = results.ToList();
        if (list.Count == 0) return ValidationStatus.Pending;
        if (list.Contains(CheckResult.Fail)) return ValidationStatus.Fail;
        if (list.Contains(CheckResult.Warn)) return ValidationStatus.Warn;
        return ValidationStatus.Pass;
    }
}
