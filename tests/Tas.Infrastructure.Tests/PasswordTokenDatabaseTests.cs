using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tas.Application.Auth;
using Tas.Application.Capacity;
using Tas.Domain;
using Tas.Domain.Parties;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Capacity;
using Tas.Infrastructure.Notifications;
using Tas.Infrastructure.Persistence;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Tas.Infrastructure.Tests;

/// <summary>Forgotten password and invitations end to end: link email, set password, link rules.</summary>
public sealed class PasswordTokenDatabaseTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TAS_TEST_DB");
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.FromHours(3));
    private static readonly JwtOptions Jwt = new() { SigningKey = "test-only-signing-key-0123456789-abcdefghijkl" };
    private const string NewPassword = "Fresh-Start-2026";

    private DemoSeedResult _demo = null!;
    private readonly CapturingEmail _email = new();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private sealed class CapturingEmail : INotificationChannel
    {
        public ConcurrentQueue<OutgoingMessage> Sent { get; } = new();
        public NotificationChannel Channel => NotificationChannel.Email;

        public Task<DeliveryResult> SendAsync(OutgoingMessage message, CancellationToken ct)
        {
            Sent.Enqueue(message);
            return Task.FromResult(DeliveryResult.Sent(null));
        }

        /// <summary>The token from the last link sent to this address.</summary>
        public string LastToken(string to)
        {
            var body = Sent.Last(m => m.To == to).Message.Body;
            return Regex.Match(body, @"#token=([A-Za-z0-9_\-]+)").Groups[1].Value;
        }
    }

    private sealed class Services(TimeProvider clock, CapturingEmail email, string? portal = "https://tas.test/") : IAsyncDisposable
    {
        public TasDbContext Db { get; } = new(new DbContextOptionsBuilder<TasDbContext>().UseNpgsql(ConnectionString).Options);
        public PasswordService Passwords { get; } = new();
        public PayloadProtector Protector { get; } = new(MsOptions.Create(Jwt));
        public PasswordTokenOptions TokenOptions { get; } = new();

        public PasswordTokenService Tokens => new(Db, Passwords, Protector, MsOptions.Create(TokenOptions), clock, NullLogger<PasswordTokenService>.Instance);
        public AuthService Auth => new(Db, Passwords, new TokenService(MsOptions.Create(Jwt), clock), MsOptions.Create(Jwt), clock, NullLogger<AuthService>.Instance);
        public UserAdminService Users => new(Db, Passwords, Auth, Tokens, clock);

        public NotificationSender Sender => new(Db, [email], MsOptions.Create(new NotificationOptions { PortalUrl = portal }), Protector, clock, NullLogger<NotificationSender>.Instance);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Services At(DateTimeOffset now, string? portal = "https://tas.test/") => new(new FixedClock(now), _email, portal);

    public async Task InitializeAsync()
    {
        if (ConnectionString is null)
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("TAS_TEST_DB is not set in CI.");
            return;
        }

        await using var s = At(Start);
        await s.Db.Database.ExecuteSqlRawAsync("TRUNCATE terminal, company, app_user RESTART IDENTITY CASCADE");
        var clock = new FixedClock(Start);
        _demo = await new DemoDataSeeder(s.Db, new SlotGenerationService(s.Db, clock, NullLogger<SlotGenerationService>.Instance), s.Passwords, clock).SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Task<AppUser> UserAsync(Services s, string email) => s.Db.AppUsers.AsNoTracking().SingleAsync(u => u.Email == email);

    [Fact]
    public async Task Forgotten_password_link_sets_a_new_password_unlocks_and_signs_out_everywhere()
    {
        if (ConnectionString is null) return;
        const string email = DemoDataSeeder.DispatcherEmail;

        // Signed in on a phone, then locked out after wrong passwords.
        await using (var s = At(Start))
        {
            Assert.Equal(AuthStatus.Ok, (await s.Auth.LoginAsync(email, DemoDataSeeder.DemoPassword, "1.1.1.1")).Status);
            for (var i = 0; i < AuthService.MaxFailedLogins; i++)
                await s.Auth.LoginAsync(email, "wrong-password-1", "1.1.1.1");
            Assert.Equal(AuthStatus.LockedOut, (await s.Auth.LoginAsync(email, DemoDataSeeder.DemoPassword, "1.1.1.1")).Status);

            await s.Tokens.RequestResetAsync("  Dispatcher@AlNoor.example ", "1.1.1.1");
        }

        await using (var s = At(Start.AddMinutes(1)))
        {
            Assert.Equal(1, (await s.Sender.RunAsync()).Sent);
            var mail = _email.Sent.Single();
            Assert.Equal(email, mail.To);
            Assert.Contains("https://tas.test/reset-password#token=", mail.Message.Body);
            Assert.Contains("60 minutes", mail.Message.Body);

            var row = await s.Db.NotificationLogs.AsNoTracking().SingleAsync(n => n.TemplateCode == NotificationTemplates.PasswordReset);
            Assert.Null(row.Payload); // the secret is gone once sent
            Assert.Null(row.AppointmentId);
            Assert.Equal(NotificationStatus.Sent, row.Status);
        }

        var token = _email.LastToken(email);
        await using (var s = At(Start.AddMinutes(5)))
        {
            var info = await s.Tokens.CheckAsync(token);
            Assert.Equal((PasswordTokenPurpose.Reset, email), (info!.Purpose, info.Email));

            Assert.Equal((PasswordTokenStatus.Ok, (string?)null), await s.Tokens.CompleteAsync(token, NewPassword, "2.2.2.2"));

            var user = await UserAsync(s, email);
            Assert.NotEqual(PasswordVerificationResult.Failed, s.Passwords.Verify(user, NewPassword));
            Assert.Null(user.LockedUntil);
            Assert.False(await s.Db.RefreshTokens.AnyAsync(t => t.UserId == user.UserId && t.RevokedAt == null));
            Assert.Contains(await s.Db.AuditLogs.AsNoTracking().Where(a => a.EntityKey == user.UserId.ToString()).Select(a => a.Reason).ToListAsync(),
                r => r != null && r.Contains("reset by email link"));

            Assert.Equal(AuthStatus.Ok, (await s.Auth.LoginAsync(email, NewPassword, "2.2.2.2")).Status);

            // A link works once.
            Assert.Equal(PasswordTokenStatus.Invalid, (await s.Tokens.CompleteAsync(token, "Another-Pass-2026", null)).Status);
            Assert.Null(await s.Tokens.CheckAsync(token));
        }
    }

    [Fact]
    public async Task Unknown_or_deactivated_accounts_get_nothing_and_requests_are_limited_per_hour()
    {
        if (ConnectionString is null) return;

        await using (var s = At(Start))
        {
            await s.Tokens.RequestResetAsync("nobody@nowhere.example", null);
            await s.Tokens.RequestResetAsync("", null);

            var planner = await s.Db.AppUsers.SingleAsync(u => u.Email == DemoDataSeeder.PlannerEmail);
            planner.IsActive = false;
            await s.Db.SaveChangesAsync();
            await s.Tokens.RequestResetAsync(DemoDataSeeder.PlannerEmail, null);

            Assert.False(await s.Db.PasswordTokens.AnyAsync());
            Assert.False(await s.Db.NotificationLogs.AnyAsync());

            for (var i = 0; i < 5; i++)
                await s.Tokens.RequestResetAsync(DemoDataSeeder.DispatcherEmail, null);
            Assert.Equal(3, await s.Db.PasswordTokens.CountAsync());
            Assert.Equal(1, await s.Db.PasswordTokens.CountAsync(t => t.UsedAt == null)); // each new link cancels the previous one
        }

        await using (var s = At(Start.AddMinutes(61)))
        {
            await s.Tokens.RequestResetAsync(DemoDataSeeder.DispatcherEmail, null);
            Assert.Equal(4, await s.Db.PasswordTokens.CountAsync());

            // Only the newest link is emailed; the older ones are skipped, their secrets wiped.
            Assert.Equal(new NotificationRunResult(4, 1, 0, 0, 3), await s.Sender.RunAsync());
            Assert.All(await s.Db.NotificationLogs.AsNoTracking().ToListAsync(), n => Assert.Null(n.Payload));
            Assert.NotNull(await s.Tokens.CheckAsync(_email.LastToken(DemoDataSeeder.DispatcherEmail)));
        }
    }

    [Fact]
    public async Task A_weak_password_keeps_the_link_and_an_expired_link_is_refused()
    {
        if (ConnectionString is null) return;

        await using (var s = At(Start))
        {
            await s.Tokens.RequestResetAsync(DemoDataSeeder.DispatcherEmail, null);
            await s.Sender.RunAsync();
        }

        var token = _email.LastToken(DemoDataSeeder.DispatcherEmail);
        await using (var s = At(Start.AddMinutes(10)))
        {
            var (status, error) = await s.Tokens.CompleteAsync(token, "short", null);
            Assert.Equal(PasswordTokenStatus.Invalid, status);
            Assert.Contains("at least 10", error);
            Assert.NotNull(await s.Tokens.CheckAsync(token));
            Assert.Equal(PasswordTokenStatus.Invalid, (await s.Tokens.CompleteAsync("made-up-token", NewPassword, null)).Status);
        }

        await using (var s = At(Start.AddMinutes(61)))
        {
            Assert.Null(await s.Tokens.CheckAsync(token));
            Assert.Equal((PasswordTokenStatus.Invalid, PasswordTokenService.InvalidLink), await s.Tokens.CompleteAsync(token, NewPassword, null));
        }
    }

    [Fact]
    public async Task Changing_the_password_another_way_or_deactivating_cancels_open_links()
    {
        if (ConnectionString is null) return;

        await using (var s = At(Start))
        {
            await s.Tokens.RequestResetAsync(DemoDataSeeder.DispatcherEmail, null);
            await s.Sender.RunAsync();
        }

        var token = _email.LastToken(DemoDataSeeder.DispatcherEmail);
        await using (var s = At(Start.AddMinutes(1)))
        {
            Assert.Equal(AuthStatus.Ok, (await s.Auth.ChangePasswordAsync(_demo.DispatcherUserId, DemoDataSeeder.DemoPassword, NewPassword)).Status);
            Assert.Null(await s.Tokens.CheckAsync(token));
        }

        // Queued but not yet sent when the user is deactivated: skipped, never sent.
        await using (var s = At(Start.AddMinutes(2)))
        {
            await s.Tokens.RequestResetAsync(DemoDataSeeder.DispatcherEmail, null);
            var admin = new CurrentUser(_demo.TerminalAdminUserId, UserRole.TerminalAdmin, null, _demo.TerminalId);
            Assert.Equal(AdminStatus.Ok, (await s.Users.SetActiveAsync(admin, _demo.DispatcherUserId, false, "Left the company")).Status);
            Assert.Equal(1, (await s.Sender.RunAsync()).Skipped);
            Assert.False(await s.Db.PasswordTokens.AnyAsync(t => t.UsedAt == null));
        }

        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task Invited_users_set_their_own_password()
    {
        if (ConnectionString is null) return;
        const string email = "clerk@alnoor.example";
        var companyAdmin = new CurrentUser(_demo.DispatcherUserId, UserRole.CompanyAdmin, _demo.CompanyId, null);

        int userId;
        await using (var s = At(Start))
        {
            var created = await s.Users.CreateAsync(companyAdmin, new CreateUserRequest(email, "Sara", UserRole.CompanyUser, _demo.CompanyId, null, PreferredLanguage: "ar"));
            Assert.Equal(UserAdminStatus.Created, created.Status);
            Assert.True(created.Invited);
            userId = created.User!.UserId;

            Assert.Null((await UserAsync(s, email)).PasswordHash);
            Assert.Equal(AuthStatus.InvalidCredentials, (await s.Auth.LoginAsync(email, "", null)).Status);

            Assert.Equal(1, (await s.Sender.RunAsync()).Sent);
            var mail = _email.Sent.Single();
            Assert.Equal("ar", mail.Message.Language);
            Assert.Contains("Al Noor Transport", mail.Message.Subject);
            Assert.Contains("https://tas.test/accept-invite#token=", mail.Message.Body);
            Assert.Contains("7 أيام", mail.Message.Body);
        }

        var first = _email.LastToken(email);
        await using (var s = At(Start.AddDays(2)))
        {
            // The first email got lost: send a new invitation; the old link stops working.
            Assert.Equal(AdminStatus.Ok, (await s.Users.ResendInviteAsync(companyAdmin, userId)).Status);
            await s.Sender.RunAsync();
            var second = _email.LastToken(email);
            Assert.NotEqual(first, second);
            Assert.Null(await s.Tokens.CheckAsync(first));
            Assert.Equal(PasswordTokenPurpose.Invite, (await s.Tokens.CheckAsync(second))!.Purpose);

            Assert.Equal(PasswordTokenStatus.Ok, (await s.Tokens.CompleteAsync(second, NewPassword, null)).Status);
            Assert.Equal(AuthStatus.Ok, (await s.Auth.LoginAsync(email, NewPassword, null)).Status);

            // Has a password now: no more invitations.
            var again = await s.Users.ResendInviteAsync(companyAdmin, userId);
            Assert.Equal(AdminStatus.Invalid, again.Status);
        }

        // Invitations can be sent only within the admin's scope.
        await using (var s = At(Start.AddDays(2)))
        {
            var otherCompany = new CurrentUser(_demo.OtherDispatcherUserId, UserRole.CompanyAdmin, _demo.OtherCompanyId, null);
            Assert.Equal(AdminStatus.NotFound, (await s.Users.ResendInviteAsync(otherCompany, userId)).Status);
        }
    }

    [Fact]
    public async Task Without_a_portal_address_the_email_fails_visibly_and_the_secret_is_wiped()
    {
        if (ConnectionString is null) return;

        await using var s = At(Start, portal: null);
        await s.Tokens.RequestResetAsync(DemoDataSeeder.DispatcherEmail, null);
        Assert.Equal(1, (await s.Sender.RunAsync()).Failed);

        var row = await s.Db.NotificationLogs.AsNoTracking().SingleAsync();
        Assert.Equal(NotificationStatus.Failed, row.Status);
        Assert.Contains("PortalUrl", row.LastError);
        Assert.Null(row.Payload);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task The_same_link_used_twice_at_once_works_only_once()
    {
        if (ConnectionString is null) return;

        await using (var s = At(Start))
        {
            await s.Tokens.RequestResetAsync(DemoDataSeeder.DispatcherEmail, null);
            await s.Sender.RunAsync();
        }

        var token = _email.LastToken(DemoDataSeeder.DispatcherEmail);
        async Task<PasswordTokenStatus> UseAsync(string password)
        {
            await using var s = At(Start.AddMinutes(1));
            return (await s.Tokens.CompleteAsync(token, password, null)).Status;
        }

        var results = await Task.WhenAll(UseAsync("Parallel-One-2026"), UseAsync("Parallel-Two-2026"), UseAsync("Parallel-Six-2026"));
        Assert.Single(results, r => r == PasswordTokenStatus.Ok);
    }
}
