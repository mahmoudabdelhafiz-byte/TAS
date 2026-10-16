using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Yardsyncs.Application.Auth;
using Yardsyncs.Application.Capacity;
using Yardsyncs.Domain;
using Yardsyncs.Infrastructure.Persistence;
using BrandingRow = Yardsyncs.Domain.Branding;

namespace Yardsyncs.Infrastructure.Brands;

/// <summary>The brand people see: name, main colour and logo (a logo address, or null for the Yardsyncs mark).</summary>
public sealed record BrandView(string Name, string Color, string? LogoUrl, int? OperatorId);

/// <summary>What an admin set for the installation (OperatorId null) or an operator; nulls fall back.</summary>
public sealed record BrandSettings(int? OperatorId, string? Name, string? Color, string? Domain, string? LogoUrl);

public sealed record BrandInput(string? Name, string? Color, string? Domain);

/// <summary>
/// Branding: the installation's and each operator's name, colour, logo and web address. An operator's shows for its
/// terminals, at its own web address and in its messages; whatever it leaves out comes from the installation's, then
/// from Yardsyncs. System admins set the installation's and every operator's; operator admins their own (not the
/// web address, which needs a DNS and certificate change).
/// </summary>
public sealed partial class BrandingService(YardsyncsDbContext db, TerminalProfiles profiles, TimeProvider clock)
{
    public const string DefaultColor = "#0e7c86";
    public const int MaxLogoBytes = 256 * 1024;

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex ColorPattern();

    [GeneratedRegex(@"^(?=.{4,200}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex HostPattern();

    // ------------------------------------------------------------------ what people see (cached: read on every page)

    private sealed record Row(int BrandingId, int? OperatorId, string? Name, string? Color, string? Domain, bool HasLogo, DateTimeOffset UpdatedAt);

    private sealed record Snapshot(DateTimeOffset At, IReadOnlyList<Row> Rows, IReadOnlyDictionary<int, int?> TerminalOperators);

    private static Snapshot? _snapshot;

    /// <summary>Forgets the cached branding (after a change; other app instances catch up within a minute).</summary>
    public static void Forget() => _snapshot = null;

    private async Task<Snapshot> SnapshotAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_snapshot is { } s && now - s.At < TimeSpan.FromMinutes(1))
            return s;
        var rows = await db.Brandings.AsNoTracking()
            .Select(b => new Row(b.BrandingId, b.OperatorId, b.Name, b.Color, b.Domain, b.Logo != null, b.UpdatedAt))
            .ToListAsync(ct);
        var terminals = await db.Terminals.AsNoTracking().ToDictionaryAsync(t => t.TerminalId, t => t.OperatorId, ct);
        return _snapshot = new Snapshot(now, rows, terminals);
    }

    private BrandView View(Snapshot s, int? operatorId)
    {
        var installation = s.Rows.FirstOrDefault(r => r.OperatorId is null);
        var own = operatorId is null ? null : s.Rows.FirstOrDefault(r => r.OperatorId == operatorId);
        var logo = own is { HasLogo: true } ? own : installation is { HasLogo: true } ? installation : null;
        return new BrandView(
            own?.Name ?? installation?.Name ?? profiles.BrandFor(null),
            own?.Color ?? installation?.Color ?? DefaultColor,
            logo is null ? null : LogoUrl(logo.BrandingId, logo.UpdatedAt),
            own is null ? null : operatorId);
    }

    private static string LogoUrl(int brandingId, DateTimeOffset updatedAt) =>
        $"/api/branding/{brandingId}/logo?v={updatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The brand for a web address: the operator whose address it is, else the installation's.</summary>
    public async Task<BrandView> ForHostAsync(string? host, CancellationToken ct = default)
    {
        var s = await SnapshotAsync(ct);
        var name = (host ?? "").Split(':')[0].Trim().TrimEnd('.').ToLowerInvariant();
        return View(s, s.Rows.FirstOrDefault(r => r.OperatorId is not null && r.Domain == name)?.OperatorId);
    }

    /// <summary>The brand of a terminal: its operator's, else the installation's.</summary>
    public async Task<BrandView> ForTerminalAsync(int terminalId, CancellationToken ct = default)
    {
        var s = await SnapshotAsync(ct);
        return View(s, s.TerminalOperators.GetValueOrDefault(terminalId));
    }

    /// <summary>
    /// The name messages are signed with: the terminal's own setting (Terminals:&lt;code&gt;:Brand), else the brand of
    /// the terminal (its operator's, the installation's, the Brand setting, Yardsyncs). Without a terminal (drivers'
    /// sign-in codes): the installation's.
    /// </summary>
    public async Task<string> MessageNameAsync(int? terminalId, CancellationToken ct = default)
    {
        if (terminalId is not { } id)
            return View(await SnapshotAsync(ct), null).Name;
        var code = await TerminalProfiles.CodeAsync(db, id, ct);
        return profiles.OwnBrand(code) ?? (await ForTerminalAsync(id, ct)).Name;
    }

    public async Task<(byte[] Logo, string Type, DateTimeOffset UpdatedAt)?> LogoAsync(int brandingId, CancellationToken ct = default)
    {
        var row = await db.Brandings.AsNoTracking().Where(b => b.BrandingId == brandingId && b.Logo != null)
            .Select(b => new { b.Logo, b.LogoType, b.UpdatedAt }).SingleOrDefaultAsync(ct);
        return row is null ? null : (row.Logo!, row.LogoType ?? "image/png", row.UpdatedAt);
    }

    // ------------------------------------------------------------------ settings

    private static bool CanEdit(CurrentUser user, int? operatorId) =>
        user.IsSysAdmin || (operatorId is not null && user.IsOperatorAdmin && user.OperatorId == operatorId);

    public async Task<AdminResult<BrandSettings>> GetAsync(CurrentUser user, int? operatorId, CancellationToken ct = default)
    {
        if (!CanEdit(user, operatorId))
            return AdminResult<BrandSettings>.Forbidden("You cannot change this branding.");
        if (operatorId is { } id && !await db.TerminalOperators.AnyAsync(o => o.OperatorId == id, ct))
            return AdminResult<BrandSettings>.NotFound($"Operator {id} not found.");
        var row = await db.Brandings.AsNoTracking().SingleOrDefaultAsync(b => b.OperatorId == operatorId, ct);
        return AdminResult<BrandSettings>.Ok(Settings(operatorId, row));
    }

    private static BrandSettings Settings(int? operatorId, BrandingRow? row) =>
        new(operatorId, row?.Name, row?.Color, row?.Domain, row?.Logo is null ? null : LogoUrl(row.BrandingId, row.UpdatedAt));

    public async Task<AdminResult<BrandSettings>> UpdateAsync(CurrentUser user, int? operatorId, BrandInput input, CancellationToken ct = default)
    {
        var (row, denied) = await LoadAsync(user, operatorId, ct);
        if (denied is not null)
            return denied;

        var errors = new List<string>();
        var name = string.IsNullOrWhiteSpace(input.Name) ? null : input.Name.Trim();
        if (name is { Length: > 80 })
            errors.Add("Name: at most 80 characters.");

        var color = string.IsNullOrWhiteSpace(input.Color) ? null : input.Color.Trim().ToLowerInvariant();
        if (color is not null && !ColorPattern().IsMatch(color))
            errors.Add("Colour: a hex colour such as #0e7c86.");
        else if (color is not null && ContrastWithWhite(color) < 4.5)
            errors.Add($"Colour {color} is too light: buttons show white text on it (contrast {ContrastWithWhite(color):0.0}:1, at least 4.5:1 is needed). Choose a darker shade.");

        var domain = string.IsNullOrWhiteSpace(input.Domain) ? null : input.Domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (operatorId is null && domain is not null)
            errors.Add("Web address: only operators have their own; the installation's is its own address.");
        else if (domain is not null && !HostPattern().IsMatch(domain))
            errors.Add("Web address: a host name such as tas.harbor-one.example (no https://, no path).");
        if (domain != row!.Domain && !user.IsSysAdmin)
            errors.Add("Web address: only a system admin can change it (it needs a DNS name and certificate).");
        if (errors.Count > 0)
            return AdminResult<BrandSettings>.Invalid(errors);

        var before = new { row.Name, row.Color, row.Domain };
        (row.Name, row.Color, row.Domain, row.UpdatedAt) = (name, color, domain, clock.GetUtcNow());
        return await SaveAsync(user, operatorId, row, before, null, ct);
    }

    /// <summary>A PNG, JPEG or WebP logo up to 256 KB (read from the file's own bytes, not its name). SVG is refused: it can carry scripts.</summary>
    public async Task<AdminResult<BrandSettings>> SetLogoAsync(CurrentUser user, int? operatorId, byte[]? logo, CancellationToken ct = default)
    {
        var (row, denied) = await LoadAsync(user, operatorId, ct);
        if (denied is not null)
            return denied;
        string? type = null;
        if (logo is not null)
        {
            if (logo.Length is 0 or > MaxLogoBytes)
                return AdminResult<BrandSettings>.Invalid($"The logo must be a file of at most {MaxLogoBytes / 1024} KB.");
            type = ImageType(logo);
            if (type is null)
                return AdminResult<BrandSettings>.Invalid("The logo must be a PNG, JPEG or WebP image.");
        }

        var before = new { HadLogo = row!.Logo is not null };
        (row.Logo, row.LogoType, row.UpdatedAt) = (logo, type, clock.GetUtcNow());
        return await SaveAsync(user, operatorId, row, before, logo is null ? "Logo removed" : "Logo changed", ct);
    }

    private async Task<(BrandingRow? Row, AdminResult<BrandSettings>? Denied)> LoadAsync(CurrentUser user, int? operatorId, CancellationToken ct)
    {
        if (!CanEdit(user, operatorId))
            return (null, AdminResult<BrandSettings>.Forbidden("You cannot change this branding."));
        if (operatorId is { } id && !await db.TerminalOperators.AnyAsync(o => o.OperatorId == id, ct))
            return (null, AdminResult<BrandSettings>.NotFound($"Operator {id} not found."));
        var row = await db.Brandings.SingleOrDefaultAsync(b => b.OperatorId == operatorId, ct);
        if (row is null)
        {
            row = new BrandingRow { OperatorId = operatorId, UpdatedAt = clock.GetUtcNow() };
            db.Brandings.Add(row);
        }
        return (row, null);
    }

    private async Task<AdminResult<BrandSettings>> SaveAsync(CurrentUser user, int? operatorId, BrandingRow row, object before, string? reason, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AdminResult<BrandSettings>.Conflict($"The web address {row.Domain} is already another operator's.");
        }
        AuditWriter.Add(db, null, "branding", row.BrandingId, AuditAction.Update, before, new { row.Name, row.Color, row.Domain, HasLogo = row.Logo is not null },
            reason, user.UserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        Forget();
        return AdminResult<BrandSettings>.Ok(Settings(operatorId, row));
    }

    public static string? ImageType(byte[] b) => b switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
        _ => null,
    };

    /// <summary>WCAG contrast ratio of a "#rrggbb" colour against white.</summary>
    public static double ContrastWithWhite(string hex)
    {
        static double Channel(string h)
        {
            var c = int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        var l = 0.2126 * Channel(hex[1..3]) + 0.7152 * Channel(hex[3..5]) + 0.0722 * Channel(hex[5..7]);
        return 1.05 / (l + 0.05);
    }
}
