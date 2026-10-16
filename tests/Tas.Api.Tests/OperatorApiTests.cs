using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tas.Domain;
using Tas.Domain.Parties;
using Tas.Infrastructure;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.MasterData;
using Tas.Infrastructure.Persistence;

namespace Tas.Api.Tests;

/// <summary>
/// Operators on one installation: a system admin groups terminals under an operator; the operator's admins run every
/// terminal of the group, and nothing outside it.
/// </summary>
public sealed class OperatorApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private const string RootEmail = "root@yardsyncs.example";
    private const string OperatorAdminEmail = "ops@harbor-one.example";

    private sealed record Wrapped<T>(T Value);

    private sealed record TerminalRow(int TerminalId, string Code, int? OperatorId);

    private sealed record UserRow(int UserId, string Email, UserRole Role, int? OperatorId);

    [Fact]
    public async Task An_operator_admin_runs_the_operators_terminals_and_nothing_else()
    {
        if (!Enabled) return;

        int north, other;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            var northTerminal = new Terminal { Code = "NORTH", Name = "North terminal", TerminalType = TerminalType.Container };
            var otherTerminal = new Terminal { Code = "OTHER", Name = "Another operator's terminal", TerminalType = TerminalType.Container };
            db.Terminals.AddRange(northTerminal, otherTerminal);
            var root = new AppUser { Email = RootEmail, FullName = "System admin", Role = UserRole.SysAdmin };
            root.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(root, Password);
            db.AppUsers.Add(root);
            await db.SaveChangesAsync();
            (north, other) = (northTerminal.TerminalId, otherTerminal.TerminalId);
        }

        var sys = await As(RootEmail);
        var created = await sys.PostAsJsonAsync("/api/operators", new { code = "harbor1", name = "Harbor One" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var op = (await Read<Wrapped<OperatorView>>(created)).Value;
        Assert.Equal("HARBOR1", op.Code);
        Assert.Equal(HttpStatusCode.Conflict, (await sys.PostAsJsonAsync("/api/operators", new { code = "HARBOR1", name = "Again" })).StatusCode);

        foreach (var terminal in new[] { Demo.TerminalId, north })
            Assert.Equal(HttpStatusCode.OK, (await sys.PutAsJsonAsync($"/api/terminals/{terminal}/operator", new { operatorId = op.OperatorId })).StatusCode);

        var admin = await sys.PostAsJsonAsync("/api/users", new
        {
            email = OperatorAdminEmail, fullName = "Operations head", role = "OperatorAdmin", operatorId = op.OperatorId, password = Password,
        });
        Assert.Equal(HttpStatusCode.Created, admin.StatusCode);
        // An operator admin belongs to an operator, never to one terminal.
        Assert.Equal(HttpStatusCode.Forbidden, (await sys.PostAsJsonAsync("/api/users", new
        {
            email = "half@harbor-one.example", fullName = "Half", role = "OperatorAdmin", operatorId = op.OperatorId, terminalId = north, password = Password,
        })).StatusCode);

        var listed = await Read<List<OperatorView>>(await sys.GetAsync("/api/operators"));
        var one = Assert.Single(listed);
        Assert.Equal(new[] { "DEMO", "NORTH" }, one.Terminals.Select(t => t.Code).ToArray());
        Assert.Equal(1, one.Admins);
        Assert.Equal(OperatorAdminEmail, Assert.Single(await Read<List<UserRow>>(await sys.GetAsync($"/api/users?operatorId={op.OperatorId}"))).Email);

        // The operator admin signs in and works at both terminals of the operator.
        var tokens = await Login(OperatorAdminEmail);
        Assert.Equal(UserRole.OperatorAdmin, tokens.User.Role);
        Assert.Equal("Harbor One", tokens.User.OperatorName);
        var ops = await As(OperatorAdminEmail);
        Assert.Equal(new[] { "DEMO", "NORTH" }, (await Read<List<TerminalRow>>(await ops.GetAsync("/api/terminals"))).Select(t => t.Code).ToArray());

        Assert.Equal(HttpStatusCode.Created, (await ops.PostAsJsonAsync(Appointments, ImportBooking(companyId: Demo.CompanyId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ops.GetAsync($"/api/terminals/{north}/capacity/templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ops.PostAsJsonAsync("/api/users", new
        {
            email = "clerk@north.example", fullName = "North clerk", role = "GateClerk", terminalId = north, password = Password,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ops.PostAsJsonAsync("/api/users", new
        {
            email = "deputy@harbor-one.example", fullName = "Deputy", role = "OperatorAdmin", operatorId = op.OperatorId, password = Password,
        })).StatusCode);

        // ... and nowhere else.
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync($"/api/terminals/{other}/capacity/templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ops.GetAsync($"/api/terminals/{other}/availability?date={Tomorrow:yyyy-MM-dd}&category=IMP_PICKUP")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostAsJsonAsync("/api/users", new
        {
            email = "clerk@other.example", fullName = "Other clerk", role = "GateClerk", terminalId = other, password = Password,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostAsJsonAsync("/api/operators", new { code = "MINE", name = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.PutAsJsonAsync($"/api/terminals/{other}/operator", new { operatorId = op.OperatorId })).StatusCode);
        Assert.Equal("HARBOR1", Assert.Single(await Read<List<OperatorView>>(await ops.GetAsync("/api/operators"))).Code);
        var staff = await Read<List<UserRow>>(await ops.GetAsync("/api/users"));
        Assert.Contains(staff, u => u.Email == "clerk@north.example");
        Assert.Contains(staff, u => u.Email == DemoDataSeeder.TerminalAdminEmail);
        Assert.DoesNotContain(staff, u => u.Email == RootEmail);

        // A terminal admin can't make operator admins or see operators.
        var terminalAdmin = await As(DemoDataSeeder.TerminalAdminEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await terminalAdmin.PostAsJsonAsync("/api/users", new
        {
            email = "climber@demo-terminal.example", fullName = "Climber", role = "OperatorAdmin", operatorId = op.OperatorId, password = Password,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await terminalAdmin.GetAsync("/api/operators")).StatusCode);

        // Taking a terminal out counts at once, with the same sign-in.
        Assert.Equal(HttpStatusCode.OK, (await sys.PutAsJsonAsync($"/api/terminals/{north}/operator", new { operatorId = (int?)null })).StatusCode);
        Assert.Equal(new[] { "DEMO" }, (await Read<List<TerminalRow>>(await ops.GetAsync("/api/terminals"))).Select(t => t.Code).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync($"/api/terminals/{north}/capacity/templates")).StatusCode);

        // A stopped operator signs its admins out.
        Assert.Equal(HttpStatusCode.OK, (await sys.PutAsJsonAsync($"/api/operators/{op.OperatorId}", new { name = "Harbor One", isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ops.GetAsync("/api/terminals")).StatusCode);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            // In, in, out: every move between operators is in the audit log.
            Assert.Equal(3, await db.AuditLogs.CountAsync(a => a.EntityName == "terminal" && a.Reason != null && a.Reason.Contains("operator")));
        }
    }
}
