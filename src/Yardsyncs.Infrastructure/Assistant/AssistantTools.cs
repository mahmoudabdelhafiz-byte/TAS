using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Infrastructure.Booking;
using Yardsyncs.Infrastructure.Capacity;
using Yardsyncs.Infrastructure.ControlRoom;
using Yardsyncs.Infrastructure.Gate;
using Yardsyncs.Infrastructure.Persistence;

namespace Yardsyncs.Infrastructure.Assistant;

/// <summary>
/// What the assistant may look up: read-only, always as the signed-in user with the same rules as the portal
/// (a company sees only its own appointments; terminal staff only their terminal). Nothing here changes data.
/// </summary>
public sealed class AssistantTools(
    YardsyncsDbContext db,
    AppointmentQueryService appointments,
    AvailabilityQueryService availability,
    ContainerTrackingService tracking,
    GateService gate,
    ControlRoomService controlRoom,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The tool definitions for this user (terminal staff get the terminal's figures too).</summary>
    public static JsonArray Definitions(CurrentUser user)
    {
        var tools = new JsonArray
        {
            Tool("list_move_types", "The move types (transaction categories) this terminal takes, with their codes. Use the code in get_free_hours.",
                new JsonObject()),
            Tool("get_appointment", "One appointment by its number: status, hour, truck, driver, moves with their checks, and the 'ready to go' light with its reason.",
                new JsonObject { ["appointment_no"] = Str("Appointment number, e.g. JED1-261020-7KQ4M") }, "appointment_no"),
            Tool("list_appointments", "The appointments of one local day (the user's company only for company users; at most 60, earliest first).",
                new JsonObject { ["date"] = Str("Local date, YYYY-MM-DD") }, "date"),
            Tool("get_free_hours", "Hours of one local day with the number of free places for a move type.",
                new JsonObject { ["date"] = Str("Local date, YYYY-MM-DD"), ["move_type"] = Str("Move type code from list_move_types, e.g. IMP_PICKUP") },
                "date", "move_type"),
            Tool("track_containers", "Where import containers stand at the terminal: ready to collect, not in the yard yet, on hold (and why), booked, collected. Give container numbers or a bill of lading.",
                new JsonObject
                {
                    ["containers"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Container numbers (at most 20)" },
                    ["bill_of_lading"] = Str("Bill of lading number"),
                }),
        };
        if (user.IsTerminalStaff)
            tools.Add(Tool("terminal_now", "Right now at this terminal: trucks on the way, at the gate, inside, at the trouble desk, completed today, average times.",
                new JsonObject()));
        return tools;
    }

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        },
    };

    /// <summary>Runs one tool call; returns the result as JSON text, and whether it is an error.</summary>
    public async Task<(string Content, bool IsError)> RunAsync(CurrentUser user, int terminalId, string name, JsonObject? input, CancellationToken ct)
    {
        if (!AccessRules.CanAccessTerminal(user, terminalId))
            return ("This user has no access to this terminal.", true);
        try
        {
            object result = name switch
            {
                "list_move_types" => await db.TransactionCategories.AsNoTracking()
                    .Where(c => c.TerminalId == terminalId && c.IsActive).OrderBy(c => c.SortOrder)
                    .Select(c => new { code = c.Code, english = c.NameEn, arabic = c.NameAr })
                    .ToListAsync(ct),
                "get_appointment" => await AppointmentAsync(user, terminalId, Text(input, "appointment_no"), ct),
                "list_appointments" => await ListAsync(user, terminalId, Date(input), ct),
                "get_free_hours" => await FreeHoursAsync(user, terminalId, Date(input), Text(input, "move_type"), ct),
                "track_containers" => await TrackAsync(user, terminalId, input, ct),
                "terminal_now" when user.IsTerminalStaff => await NowAsync(terminalId, ct),
                _ => throw new ToolInputException($"There is no tool '{name}'."),
            };
            return (JsonSerializer.Serialize(result, Json), false);
        }
        catch (ToolInputException ex)
        {
            return (ex.Message, true);
        }
        catch (KeyNotFoundException ex)
        {
            return (ex.Message, true);
        }
    }

    private sealed class ToolInputException(string message) : Exception(message);

    private static string Text(JsonObject? input, string key) =>
        input?[key]?.GetValueKind() == JsonValueKind.String && input[key]!.GetValue<string>().Trim() is { Length: > 0 } s
            ? s
            : throw new ToolInputException($"'{key}' is required.");

    private static DateOnly Date(JsonObject? input) =>
        DateOnly.TryParseExact(Text(input, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new ToolInputException("'date' must be YYYY-MM-DD.");

    private async Task<object> AppointmentAsync(CurrentUser user, int terminalId, string number, CancellationToken ct)
    {
        var a = await appointments.GetAsync(terminalId, number.ToUpperInvariant(), user.IsCompanyUser ? user.CompanyId : null, ct)
                ?? throw new ToolInputException($"Appointment {number} was not found for this user.");
        return new
        {
            a.AppointmentNo, a.Status, hour = a.SlotLocal, a.CompanyName, a.TruckPlate, a.DriverName, readyToGo = a.TrafficLight, reason = a.TrafficReason,
            eta = a.Eta, link = $"#/appointments?no={a.AppointmentNo}",
            moves = a.Items.Select(i => new
            {
                i.Category, i.ContainerNo, i.BookingRef, i.CargoRef, check = i.ValidationStatus,
                problems = i.Checks.Where(c => c.Result != Yardsyncs.Domain.Booking.CheckResult.Pass).Select(c => $"{c.Type}: {c.Message}"),
            }),
        };
    }

    private async Task<object> ListAsync(CurrentUser user, int terminalId, DateOnly date, CancellationToken ct)
    {
        var list = await appointments.ListAsync(terminalId, date, user.IsCompanyUser ? user.CompanyId : null, ct);
        return new
        {
            date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            total = list.Count,
            byStatus = list.GroupBy(a => a.Status).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            onHold = list.Count(a => a.TrafficLight == Yardsyncs.Domain.Booking.TrafficLight.Red),
            appointments = list.OrderBy(a => a.SlotStart).Take(60).Select(a => new
            {
                a.AppointmentNo, hour = a.SlotLocal, a.Status, readyToGo = a.TrafficLight, a.TruckPlate,
                company = user.IsCompanyUser ? null : a.CompanyName,
                moves = a.Items.Select(i => i.ContainerNo ?? i.BookingRef ?? i.CargoRef),
            }),
        };
    }

    private async Task<object> FreeHoursAsync(CurrentUser user, int terminalId, DateOnly date, string moveType, CancellationToken ct)
    {
        var slots = await availability.GetAsync(terminalId, date, moveType.ToUpperInvariant(), user.IsCompanyUser ? user.CompanyId : null, ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(await db.Terminals.Where(t => t.TerminalId == terminalId).Select(t => t.TimeZone).SingleAsync(ct));
        var now = clock.GetUtcNow();
        return new
        {
            date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            moveType = moveType.ToUpperInvariant(),
            hours = slots.Where(s => s.EndAt > now).Select(s => new
            {
                from = TimeZoneInfo.ConvertTime(s.StartAt, tz).ToString("HH:mm", CultureInfo.InvariantCulture),
                to = TimeZoneInfo.ConvertTime(s.EndAt, tz).ToString("HH:mm", CultureInfo.InvariantCulture),
                free = s.IsClosed ? 0 : s.Available,
                closed = s.IsClosed ? true : (bool?)null,
            }),
            book = "#/book",
        };
    }

    private async Task<object> TrackAsync(CurrentUser user, int terminalId, JsonObject? input, CancellationToken ct)
    {
        var bl = input?["bill_of_lading"]?.GetValueKind() == JsonValueKind.String ? input["bill_of_lading"]!.GetValue<string>() : null;
        var containers = input?["containers"] is JsonArray array
            ? array.Where(x => x?.GetValueKind() == JsonValueKind.String).Select(x => x!.GetValue<string>()).Take(20).ToList()
            : null;
        if (string.IsNullOrWhiteSpace(bl) && (containers is null || containers.Count == 0))
            throw new ToolInputException("Give container numbers or a bill of lading.");
        var result = await tracking.TrackAsync(user, terminalId, new TrackInput(bl, containers), ct);
        if (result.Status != AdminStatus.Ok)
            throw new ToolInputException(string.Join(" ", result.Errors));
        return new
        {
            billOfLading = result.Value!.BlNo,
            containers = result.Value.Containers.Select(c => new
            {
                c.ContainerNo, c.SizeFt, status = c.State, c.Problems, c.Warnings,
                appointment = c.Appointment?.AppointmentNo, bookedByAnotherCompany = c.BookedByOthers ? true : (bool?)null,
            }),
            track = "#/track",
        };
    }

    private async Task<object> NowAsync(int terminalId, CancellationToken ct)
    {
        var board = await gate.BoardAsync(terminalId, ct);
        var room = await controlRoom.BoardAsync(terminalId, ct);
        return new
        {
            atGate = board.AtGate.Count, inside = board.InTerminal.Count, troubleDesk = board.OpenTroubles,
            completedToday = board.CompletedToday, averageTurnMinutesToday = board.AverageTurnMinutesToday,
            onTheWay = room.Stats.OnTheWay, averageMinutesToBlock = room.Stats.AvgMinutesToBlock, averageMinutesAtBlock = room.Stats.AvgMinutesAtBlock,
            overdueInside = room.Trucks.Count(t => t.Overdue), noSignal = room.Trucks.Count(t => t.Silent),
            targetMinutesInside = room.TurnTargetMinutes,
        };
    }
}
