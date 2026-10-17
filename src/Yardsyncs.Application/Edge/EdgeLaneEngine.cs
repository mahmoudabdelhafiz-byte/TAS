using Yardsyncs.Application.Gate;
using Yardsyncs.Domain.Booking;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Edge;

/// <summary>A command the agent hands to a device while working on its own (negative ids, so they never clash with the cloud's).</summary>
public sealed record EdgeCommand(long CommandId, string DeviceCode, LaneCommand Command, string? Text, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)
{
    public bool Done { get; set; }

    /// <summary>The passage the command belongs to (an opened barrier sends the truck through).</summary>
    public Guid? PassageId { get; init; }

    public DeviceCommandView View() => new(CommandId, Command, Text, CreatedAt, ExpiresAt);
}

/// <summary>
/// The automatic lanes without the cloud, from a snapshot: the same device API, the same matching and gate rules as the
/// cloud's lane controller (one transaction per truck, decided once the lane is quiet or at once for a kiosk entry or a
/// gate pass), the same commands. What it can't do offline: ask the TOS again (it uses the last light the cloud saw),
/// get a yard position, or call a clerk to the trouble desk — a referred truck is dealt with at the lane, and the
/// referral is recorded when the cloud is back. Not thread-safe: the host serialises calls.
/// </summary>
public sealed class EdgeLaneEngine
{
    private readonly Dictionary<string, LaneTransaction> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, EdgePassage> _passages = [];
    private readonly List<Guid> _order = [];
    private readonly List<EdgeCommand> _commands = [];
    private readonly Dictionary<string, AppointmentStatus> _overlay = new(StringComparer.Ordinal);
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeSpan _settle;
    private readonly TimeSpan _window;
    private long _nextCommandId = -1;
    private long _nextReadId = -1;
    private long _nextTransactionId = -1;

    /// <param name="recorded">Passages decided earlier and not yet sent (after a restart): trucks already let in stay in.</param>
    public EdgeLaneEngine(EdgeSnapshot snapshot, IEnumerable<EdgePassage>? recorded = null, double settleSeconds = 3, double windowSeconds = 30)
    {
        Snapshot = snapshot;
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(snapshot.TimeZone);
        _settle = TimeSpan.FromSeconds(settleSeconds);
        _window = TimeSpan.FromSeconds(windowSeconds);
        foreach (var p in recorded ?? [])
            Record(p);
    }

    public EdgeSnapshot Snapshot { get; }

    /// <summary>Everything decided offline, oldest first.</summary>
    public IReadOnlyList<EdgePassage> Passages => _order.Select(id => _passages[id]).ToList();

    /// <summary>The device a key belongs to, at a lane in use; null otherwise.</summary>
    public EdgeDevice? Authenticate(string? key)
    {
        if (!DeviceKeys.LooksLikeKey(key))
            return null;
        var hash = DeviceKeys.Hash(key!);
        var device = Snapshot.Devices.FirstOrDefault(d => d.KeyHash == hash);
        return device is not null && Lane(device.LaneCode) is { IsActive: true } ? device : null;
    }

    /// <summary>A read from a device: checked and normalized as the cloud does, filed under its truck, decided when ready.</summary>
    public (IReadOnlyList<string> Errors, DeviceReadView? Read) OnRead(EdgeDevice device, DeviceReadInput input, DateTimeOffset now)
    {
        var (errors, read) = DeviceRules.Check(device.Kind, input, now);
        if (read is null)
            return (errors, null);
        var view = new DeviceReadView(_nextReadId--, device.DeviceCode, read.ReadAt, now, read.Plate, read.PlateConfidence, read.Containers,
            read.Seals, read.Damage, read.Photos, read.Tag, read.AppointmentNo, read.QrToken is not null, read.WeightKg, read.Warnings);

        var lane = Lane(device.LaneCode);
        if (lane is not { Mode: LaneMode.Automatic, State: LaneState.Open, IsActive: true })
            return ([], view); // a clerk's lane, or not open: the read is all

        if (!_current.TryGetValue(lane.LaneCode, out var tx) || !LaneControllerRules.Belongs(tx, read, now, _window))
        {
            tx = new LaneTransaction { TransactionId = _nextTransactionId--, LaneId = 0, StartedAt = now, LastReadAt = now };
            _current[lane.LaneCode] = tx;
        }
        LaneControllerRules.Merge(tx, read, now);
        if (LaneControllerRules.Ready(tx, now, _settle))
            Decide(lane, tx, now);
        return ([], view);
    }

    /// <summary>Decide the trucks whose lane has been quiet long enough (the host calls this every second).</summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var (laneCode, tx) in _current.ToList())
        {
            if (LaneControllerRules.Ready(tx, now, _settle) && Lane(laneCode) is { } lane)
                Decide(lane, tx, now);
        }
    }

    /// <summary>The device's commands still to carry out.</summary>
    public IReadOnlyList<DeviceCommandView> Pending(EdgeDevice device, DateTimeOffset now) =>
        _commands.Where(c => c.DeviceCode == device.DeviceCode && !c.Done && c.ExpiresAt > now).Select(c => c.View()).ToList();

    /// <summary>
    /// A device confirmed a command. An opened barrier sends the truck through; at an entry lane the gate ticket follows
    /// (without a yard position: that comes from the TOS). False: not this device's command.
    /// </summary>
    public bool Ack(EdgeDevice device, long commandId, bool done, DateTimeOffset now)
    {
        var command = _commands.FirstOrDefault(c => c.CommandId == commandId && c.DeviceCode == device.DeviceCode);
        if (command is null)
            return false;
        command.Done = true;
        if (!done || command.Command != LaneCommand.Open || command.PassageId is not { } id || !_passages.TryGetValue(id, out var passage) || passage.PassedAt is not null)
            return true;

        _passages[id] = passage with { PassedAt = now };
        if (passage.Direction == LaneDirection.In && Lane(passage.LaneCode) is { } lane)
        {
            var local = TimeZoneInfo.ConvertTime(now, _timeZone).ToString("yyyy-MM-dd HH:mm");
            Queue(lane, [new PlannedCommand(LaneCommand.Print,
                LaneControllerRules.Display.Ticket(Snapshot.TerminalName, lane.LaneCode, passage.AppointmentNo, passage.Plate, passage.Containers, local))], id, now);
        }
        return true;
    }

    /// <summary>Passages sent to the cloud and recorded there (or refused for good): no longer kept.</summary>
    public void Forget(IEnumerable<Guid> passageIds)
    {
        foreach (var id in passageIds)
        {
            if (_passages.Remove(id))
                _order.Remove(id);
        }
    }

    // ================================================================ deciding

    private void Decide(EdgeLane lane, LaneTransaction tx, DateTimeOffset now)
    {
        _current.Remove(lane.LaneCode);
        var containers = LaneControllerRules.ContainersOf(tx);
        var (admitted, number, reason, message) = lane.Direction == LaneDirection.In
            ? Entry(tx, containers, now)
            : Exit(tx, now);

        var passage = new EdgePassage(Guid.NewGuid(), lane.LaneCode, lane.Direction, number, tx.Plate, containers, now, admitted, reason, message, null);
        Record(passage);
        Queue(lane, LaneControllerRules.Plan(lane.Direction, admitted, number, message), passage.PassageId, now);
    }

    private (bool Admitted, string? Number, string? Reason, string Message) Entry(LaneTransaction tx, IReadOnlyList<string> containers, DateTimeOffset now)
    {
        var settings = Snapshot.Settings;
        var number = tx.AppointmentRead?.Trim().ToUpperInvariant();
        var qr = tx.QrToken?.Trim().ToUpperInvariant();
        var warnings = new List<string> { "Decided at the terminal without the cloud: the TOS was not asked again." };

        // The driver's rotating gate pass, checked against the pass secret and the appointment's driver.
        var passValid = false;
        if (qr is not null && qr.StartsWith(GatePassCode.Prefix + ":", StringComparison.Ordinal))
        {
            if (GatePassCode.TryParse(qr, out var passId, out var passNumber, out var code)
                && Snapshot.Passes.FirstOrDefault(p => p.PassId == passId) is { } pass
                && GatePassCode.Verify(Convert.FromBase64String(pass.Secret), code, now))
            {
                if (Snapshot.Appointments.Any(a => a.AppointmentNo == passNumber && a.DriverMobile == pass.Mobile))
                {
                    number = passNumber;
                    passValid = true;
                }
                else
                    warnings.Add($"The gate pass is valid, but appointment {passNumber} is not this driver's.");
            }
            else
                warnings.Add("The gate pass could not be checked: expired, or not known at the terminal.");
            qr = null;
        }

        var from = now.AddHours(-24);
        var to = now.AddHours(24);
        var found = Snapshot.Appointments
            .Where(a => (qr != null && a.QrToken == qr) || (number != null && a.AppointmentNo == number)
                        || (a.SlotStart >= from && a.SlotStart <= to
                            && ((tx.Plate != null && a.Plate == tx.Plate) || a.Containers.Any(containers.Contains))))
            .ToList();
        var candidates = found.Select(a => new GateCandidate(a.AppointmentId, a.AppointmentNo, a.QrToken, StatusOf(a), a.SlotStart, a.SlotEnd,
            a.Plate, a.Containers.ToHashSet(), a.HasDriver)).ToList();

        var match = GateMatcher.Match(new GateReads(tx.Plate, containers, qr, number), candidates, now,
            passValid ? settings with { GateByNumber = true } : settings);
        var appointment = match.Appointment is null ? null : found.Single(a => a.AppointmentId == match.Appointment.AppointmentId);
        var decision = GateDecider.Decide(match.Appointment, appointment?.Light ?? TrafficLight.Grey, appointment?.LightReason ?? "",
            appointment?.TruckBlocked ?? false, appointment?.DriverBlocked ?? false, now, settings, _timeZone, passValid);

        if (decision.Kind == GateDecisionKind.Admit)
            _overlay[match.Appointment!.AppointmentNo] = AppointmentStatus.GateIn;
        var message = string.Join(" ", new[] { decision.Message }.Concat(match.Warnings).Concat(warnings));
        return (decision.Kind == GateDecisionKind.Admit, match.Appointment?.AppointmentNo,
            decision.Reason is { } r ? Upper(r.ToString()) : null, message);
    }

    private (bool Admitted, string? Number, string? Reason, string Message) Exit(LaneTransaction tx, DateTimeOffset now)
    {
        var number = tx.AppointmentRead?.Trim().ToUpperInvariant();
        var inside = Snapshot.Appointments.Where(a => StatusOf(a) == AppointmentStatus.GateIn);
        var appointment = number is not null
            ? inside.FirstOrDefault(a => a.AppointmentNo == number)
            : tx.Plate is null ? null : inside.FirstOrDefault(a => a.Plate == tx.Plate);
        if (appointment is null)
            return (false, number, "EXIT", "No truck inside matches. Check it and let it out by hand; the cloud records it later.");
        _overlay[appointment.AppointmentNo] = AppointmentStatus.Completed;
        return (true, appointment.AppointmentNo, null, "Gated out at the terminal without the cloud (exit not checked).");
    }

    // ================================================================ helpers

    private void Record(EdgePassage passage)
    {
        if (_passages.ContainsKey(passage.PassageId))
            return;
        _passages[passage.PassageId] = passage;
        _order.Add(passage.PassageId);
        if (passage.Admitted && passage.AppointmentNo is { } no)
            _overlay[no] = passage.Direction == LaneDirection.In ? AppointmentStatus.GateIn : AppointmentStatus.Completed;
    }

    private void Queue(EdgeLane lane, IEnumerable<PlannedCommand> plan, Guid passageId, DateTimeOffset now)
    {
        var devices = Snapshot.Devices.Where(d => d.LaneCode == lane.LaneCode).ToList();
        foreach (var step in plan)
        {
            foreach (var device in devices.Where(d => DeviceRules.TakenBy(step.Command).Contains(d.Kind)))
                _commands.Add(new EdgeCommand(_nextCommandId--, device.DeviceCode, step.Command, step.Text, now, now + DeviceRules.Lifetime(step.Command))
                {
                    PassageId = passageId,
                });
        }
        _commands.RemoveAll(c => c.Done || c.ExpiresAt < now.AddMinutes(-10));
    }

    private AppointmentStatus StatusOf(EdgeAppointment a) => _overlay.TryGetValue(a.AppointmentNo, out var s) ? s : a.Status;

    private EdgeLane? Lane(string laneCode) => Snapshot.Lanes.FirstOrDefault(l => l.LaneCode == laneCode);

    /// <summary>"NoAppointment" → "NO_APPOINTMENT", as the cloud stores reasons.</summary>
    private static string Upper(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : char.ToUpperInvariant(c).ToString()));
}
