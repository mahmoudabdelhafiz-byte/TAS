using System.Security.Cryptography;
using System.Text;
using Yardsyncs.Application.Booking;
using Yardsyncs.Application.MasterData;
using Yardsyncs.Domain.Gate;

namespace Yardsyncs.Application.Gate;

// ================================================================ the device API (/api/devices, X-Device-Key)

/// <summary>
/// What a lane device read. Send only what the device produces: cameras and OCR send the plate, containers, seals,
/// damage and photos; RFID readers the tag; kiosks the appointment number or the scanned gate pass; weighbridges the weight.
/// </summary>
/// <param name="ReadAt">When the device read it (its own clock); now when left out.</param>
/// <param name="PlateConfidence">0–100, as the OCR reports it.</param>
/// <param name="Photos">References to pictures kept by the camera system (URLs or file keys), not the pictures themselves.</param>
public sealed record DeviceReadInput(
    DateTimeOffset? ReadAt = null,
    string? Plate = null,
    int? PlateConfidence = null,
    IReadOnlyList<ContainerReadInput>? Containers = null,
    IReadOnlyList<string>? Seals = null,
    IReadOnlyList<DamageInput>? Damage = null,
    IReadOnlyList<string>? Photos = null,
    string? Tag = null,
    string? AppointmentNo = null,
    string? QrToken = null,
    decimal? WeightKg = null);

/// <param name="Position">Where on the truck: FRONT, REAR, MIDDLE (free text, max 10).</param>
public sealed record ContainerReadInput(string Number, string? IsoCode = null, int? Confidence = null, string? Position = null);

/// <param name="Photo">Reference to the picture (URL or file key).</param>
/// <param name="Side">Which side of the container or truck (free text, max 20).</param>
public sealed record DamageInput(string? Photo = null, string? Note = null, string? Side = null);

/// <summary>The device's own report: alive and well, or a fault with what is wrong.</summary>
public sealed record DeviceStatusInput(DeviceHealth Status, string? Detail = null);

/// <summary>Carried out (Done = true) or not, with why.</summary>
public sealed record DeviceAckInput(bool Done, string? Detail = null);

/// <summary>Who is calling: the device the key belongs to.</summary>
public sealed record DeviceIdentity(int TerminalId, string TerminalCode, string LaneCode, LaneDirection Direction, string DeviceCode, LaneDeviceKind Kind);

public sealed record ContainerReadView(string Number, bool CheckDigitOk, string? IsoCode, int? Confidence, string? Position);

public sealed record DamageView(string? Photo, string? Note, string? Side);

/// <summary>A read as stored: normalized, with what looked wrong.</summary>
public sealed record DeviceReadView(
    long ReadId,
    string DeviceCode,
    DateTimeOffset ReadAt,
    DateTimeOffset ReceivedAt,
    string? Plate,
    int? PlateConfidence,
    IReadOnlyList<ContainerReadView> Containers,
    IReadOnlyList<string> Seals,
    IReadOnlyList<DamageView> Damage,
    IReadOnlyList<string> Photos,
    string? Tag,
    string? AppointmentNo,
    bool GatePassScanned,
    decimal? WeightKg,
    IReadOnlyList<string> Warnings);

/// <summary>A command for the device: carry it out before <see cref="ExpiresAt"/>, then confirm it.</summary>
public sealed record DeviceCommandView(long CommandId, LaneCommand Command, string? Text, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

// ================================================================ staff (/api/terminals/{id}/gate/lanes/{laneId}/…)

/// <summary>
/// Send a command to a lane: Open / Close go to its barriers, Red / Green / Off to its traffic lights, Show / Clear to
/// its displays and kiosks, Print to its printers, Call / Hangup to its intercoms. Name a device to reach only that one.
/// </summary>
public sealed record LaneCommandInput(LaneCommand Command, string? Text = null, string? DeviceCode = null);

/// <summary>A command as the gate sees it, with how far it got.</summary>
public sealed record LaneCommandView(
    long CommandId,
    string DeviceCode,
    LaneCommand Command,
    string? Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? DoneAt,
    CommandResult? Result,
    string? ResultDetail,
    CommandProgress Progress);

public enum CommandProgress
{
    Waiting,
    Delivered,
    Done,
    Failed,

    /// <summary>Not carried out in time.</summary>
    Expired,
}

/// <summary>A lane's latest reads and commands.</summary>
public sealed record LaneActivity(IReadOnlyList<DeviceReadView> Reads, IReadOnlyList<LaneCommandView> Commands);

/// <summary>A newly issued device key. Shown once: Yardsyncs keeps only its fingerprint.</summary>
public sealed record DeviceKeyView(string DeviceCode, string Key);

/// <summary>Whether a device is talking to Yardsyncs.</summary>
public enum DeviceConnection
{
    /// <summary>No key issued, or it never called.</summary>
    NotConnected,

    Online,

    /// <summary>Calling, but reports a fault.</summary>
    Fault,

    /// <summary>Silent for longer than <see cref="DeviceRules.OfflineAfter"/>.</summary>
    Offline,
}

/// <summary>The device API's rules, free of the database.</summary>
public static class DeviceRules
{
    /// <summary>A device that hasn't called for this long is offline. Devices report their status every 30 seconds.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(2);

    /// <summary>Barrier and light commands must be carried out within seconds, or not at all.</summary>
    public static readonly TimeSpan ActuationLifetime = TimeSpan.FromSeconds(30);

    /// <summary>Display texts and tickets can wait a little longer.</summary>
    public static readonly TimeSpan MessageLifetime = TimeSpan.FromMinutes(2);

    public const int MaxTextLength = 400;

    public static DeviceConnection Connection(GateLaneDevice device, DateTimeOffset now) =>
        device.KeyHash is null || device.LastSeenAt is null ? DeviceConnection.NotConnected
        : now - device.LastSeenAt.Value > OfflineAfter ? DeviceConnection.Offline
        : device.ReportedStatus == DeviceHealth.Fault ? DeviceConnection.Fault
        : DeviceConnection.Online;

    /// <summary>The kinds of device that carry out a command.</summary>
    public static IReadOnlySet<LaneDeviceKind> TakenBy(LaneCommand command) => command switch
    {
        LaneCommand.Open or LaneCommand.Close => new HashSet<LaneDeviceKind> { LaneDeviceKind.Barrier },
        LaneCommand.Red or LaneCommand.Green or LaneCommand.Off => new HashSet<LaneDeviceKind> { LaneDeviceKind.TrafficLight },
        LaneCommand.Show or LaneCommand.Clear => new HashSet<LaneDeviceKind> { LaneDeviceKind.Display, LaneDeviceKind.Kiosk },
        LaneCommand.Print => new HashSet<LaneDeviceKind> { LaneDeviceKind.Printer },
        LaneCommand.Call or LaneCommand.Hangup => new HashSet<LaneDeviceKind> { LaneDeviceKind.Intercom },
        _ => new HashSet<LaneDeviceKind>(),
    };

    public static TimeSpan Lifetime(LaneCommand command) =>
        command is LaneCommand.Show or LaneCommand.Clear or LaneCommand.Print ? MessageLifetime : ActuationLifetime;

    /// <summary>Show and Print need a text; the others take none.</summary>
    public static string? CommandProblem(LaneCommand command, string? text)
    {
        if (!Enum.IsDefined(command))
            return "Unknown command.";
        var needsText = command is LaneCommand.Show or LaneCommand.Print;
        if (needsText && string.IsNullOrWhiteSpace(text))
            return $"{command} needs a text.";
        if (!needsText && !string.IsNullOrWhiteSpace(text))
            return $"{command} takes no text.";
        if (text?.Trim().Length > MaxTextLength)
            return $"The text is at most {MaxTextLength} characters.";
        return null;
    }

    public static CommandProgress Progress(LaneDeviceCommand c, DateTimeOffset now) =>
        c.Result == CommandResult.Done ? CommandProgress.Done
        : c.Result == CommandResult.Failed ? CommandProgress.Failed
        : now > c.ExpiresAt ? CommandProgress.Expired
        : c.DeliveredAt is not null ? CommandProgress.Delivered
        : CommandProgress.Waiting;

    /// <summary>
    /// Checks a read against what the kind of device produces, and normalizes it: plates as the gate matches them,
    /// container numbers upper-case with the ISO 6346 check digit tested, ISO codes and seals upper-case.
    /// Returns the problems (the read is refused) or the normalized read with warnings.
    /// </summary>
    public static (IReadOnlyList<string> Errors, NormalizedRead? Read) Check(LaneDeviceKind kind, DeviceReadInput input, DateTimeOffset now)
    {
        var errors = new List<string>();
        var containers = input.Containers ?? [];
        var seals = input.Seals ?? [];
        var damage = input.Damage ?? [];
        var photos = input.Photos ?? [];
        var camera = kind is LaneDeviceKind.Camera or LaneDeviceKind.Ocr;

        void Only(bool sent, bool allowed, string what)
        {
            if (sent && !allowed) errors.Add($"A {KindWords(kind)} does not send {what}.");
        }
        Only(!string.IsNullOrWhiteSpace(input.Plate) || input.PlateConfidence is not null, camera, "plates");
        Only(containers.Count > 0, camera, "containers");
        Only(seals.Count > 0, camera, "seals");
        Only(damage.Count > 0, camera, "damage");
        Only(photos.Count > 0, camera, "photos");
        Only(!string.IsNullOrWhiteSpace(input.Tag), kind == LaneDeviceKind.RfidReader, "tags");
        Only(!string.IsNullOrWhiteSpace(input.AppointmentNo) || !string.IsNullOrWhiteSpace(input.QrToken), kind == LaneDeviceKind.Kiosk, "appointment numbers or gate passes");
        Only(input.WeightKg is not null, kind == LaneDeviceKind.Weighbridge, "weights");
        if (!camera && kind is not (LaneDeviceKind.RfidReader or LaneDeviceKind.Kiosk or LaneDeviceKind.Weighbridge))
            errors.Add($"A {KindWords(kind)} sends no reads.");

        if (input.PlateConfidence is < 0 or > 100 || containers.Any(c => c.Confidence is < 0 or > 100))
            errors.Add("Confidence is 0–100.");
        if (containers.Count > 4) errors.Add("At most 4 containers per read.");
        if (seals.Count > 8) errors.Add("At most 8 seals per read.");
        if (damage.Count > 20 || photos.Count > 20) errors.Add("At most 20 damage entries and 20 photos per read.");
        if (photos.Concat(damage.Select(d => d.Photo ?? "")).Any(p => p.Length > 400)) errors.Add("Photo references are at most 400 characters.");
        if (damage.Any(d => d.Note?.Length > 200 || d.Side?.Length > 20)) errors.Add("Damage notes are at most 200 characters, sides 20.");
        if (containers.Any(c => c.Position?.Length > 10)) errors.Add("Container positions are at most 10 characters.");
        if (input.Tag?.Trim().Length > 64 || input.AppointmentNo?.Trim().Length > 30 || input.QrToken?.Trim().Length > 200 || seals.Any(s => s.Trim().Length > 20))
            errors.Add("Tag (64), appointment number (30), gate pass (200) or seal (20) too long.");
        if (input.WeightKg is <= 0 or > 200_000) errors.Add("The weight is 1–200,000 kg.");
        if (input.ReadAt is { } at && (at > now.AddMinutes(5) || at < now.AddDays(-1)))
            errors.Add("readAt is more than 5 minutes ahead or a day behind: check the device clock.");

        var warnings = new List<string>();
        string? plate = null;
        if (!string.IsNullOrWhiteSpace(input.Plate))
        {
            plate = PlateNumber.Normalize(input.Plate);
            if (!PlateNumber.IsValid(plate))
            {
                warnings.Add($"'{input.Plate.Trim()}' does not look like a plate.");
                plate = plate.Length is 0 or > 20 ? null : plate;
            }
        }

        var normalizedContainers = new List<ContainerReadView>();
        foreach (var c in containers)
        {
            var number = ContainerNumber.Normalize(c.Number ?? "");
            if (number.Length == 0) { errors.Add("A container read has no number."); continue; }
            if (number.Length > 11) { errors.Add($"'{c.Number}' is not a container number."); continue; }
            var ok = ContainerNumber.IsValid(number);
            if (!ok) warnings.Add($"{number}: the check digit does not match (misread?).");
            var iso = string.IsNullOrWhiteSpace(c.IsoCode) ? null : c.IsoCode.Trim().ToUpperInvariant();
            if (iso is not null && (iso.Length != 4 || !iso.All(char.IsAsciiLetterOrDigit)))
            {
                warnings.Add($"{number}: '{c.IsoCode}' is not an ISO size-type code.");
                iso = null;
            }
            normalizedContainers.Add(new ContainerReadView(number, ok, iso, c.Confidence, Clean(c.Position)?.ToUpperInvariant()));
        }

        if (errors.Count > 0)
            return (errors, null);

        var qr = Clean(input.QrToken);
        return ([], new NormalizedRead(
            input.ReadAt ?? now,
            plate,
            input.PlateConfidence,
            normalizedContainers,
            seals.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().ToList(),
            damage.Select(d => new DamageView(Clean(d.Photo), Clean(d.Note), Clean(d.Side))).ToList(),
            photos.Select(p => p.Trim()).Where(p => p.Length > 0).ToList(),
            Clean(input.Tag)?.ToUpperInvariant(),
            Clean(input.AppointmentNo)?.ToUpperInvariant(),
            qr,
            input.WeightKg,
            warnings));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string KindWords(LaneDeviceKind kind) => kind switch
    {
        LaneDeviceKind.Ocr => "OCR engine",
        LaneDeviceKind.RfidReader => "RFID reader",
        LaneDeviceKind.TrafficLight => "traffic light",
        _ => kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>A read after <see cref="DeviceRules.Check"/>. The gate pass itself is kept only for matching, never shown.</summary>
public sealed record NormalizedRead(
    DateTimeOffset ReadAt,
    string? Plate,
    int? PlateConfidence,
    IReadOnlyList<ContainerReadView> Containers,
    IReadOnlyList<string> Seals,
    IReadOnlyList<DamageView> Damage,
    IReadOnlyList<string> Photos,
    string? Tag,
    string? AppointmentNo,
    string? QrToken,
    decimal? WeightKg,
    IReadOnlyList<string> Warnings);

/// <summary>Device keys: "ysd_" and 43 random characters. Only the SHA-256 fingerprint is stored.</summary>
public static class DeviceKeys
{
    public const string Prefix = "ysd_";

    public static string New() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));

    public static bool LooksLikeKey(string? key) => key is { Length: > 20 and < 100 } && key.StartsWith(Prefix, StringComparison.Ordinal);
}
