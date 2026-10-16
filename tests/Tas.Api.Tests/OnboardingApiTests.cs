using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Tas.Domain;
using Tas.Domain.Parties;
using Tas.Infrastructure;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Onboarding;
using Tas.Infrastructure.Persistence;

namespace Tas.Api.Tests;

/// <summary>Adding a terminal and following its checklist until trucks can book.</summary>
public sealed class OnboardingApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private const string RootEmail = "root@yardsyncs.example";
    private const string OpsEmail = "ops@harbor-one.example";

    private sealed record TerminalRow(int TerminalId, string Code, int? OperatorId);

    [Fact]
    public async Task A_new_terminal_is_ready_to_book_once_its_checklist_is_done()
    {
        if (!Enabled) return;

        int operatorId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
            var op = new TerminalOperator { Code = "HARBOR1", Name = "Harbor One" };
            db.TerminalOperators.Add(op);
            await db.SaveChangesAsync();
            operatorId = op.OperatorId;
            var root = new AppUser { Email = RootEmail, FullName = "System admin", Role = UserRole.SysAdmin };
            var opsUser = new AppUser { Email = OpsEmail, FullName = "Operations head", Role = UserRole.OperatorAdmin, OperatorId = operatorId };
            root.PasswordHash = passwords.Hash(root, Password);
            opsUser.PasswordHash = passwords.Hash(opsUser, Password);
            db.AppUsers.AddRange(root, opsUser);
            await db.SaveChangesAsync();
        }

        var sys = await As(RootEmail);
        var created = await sys.PostAsJsonAsync("/api/terminals", new { code = "jed2", name = "Jeddah North", terminalType = "Container" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var terminal = (await Read<AdminResponse<NewTerminalView>>(created)).Data;
        Assert.Equal(("JED2", 4, 2), (terminal.Code, terminal.MoveTypes, terminal.Lanes));
        Assert.Equal(HttpStatusCode.Conflict, (await sys.PostAsJsonAsync("/api/terminals", new { code = "JED2", name = "Again", terminalType = "Container" })).StatusCode);
        var bad = await sys.PostAsJsonAsync("/api/terminals", new { code = "J", name = "", terminalType = "Container", timeZone = "Mars/Base", slotMinutes = 45 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Mars/Base", await bad.Content.ReadAsStringAsync());

        // The checklist: move types and lanes came with it; no weekly plan, so no slots yet.
        var setup = $"/api/terminals/{terminal.TerminalId}/setup";
        var status = await Read<SetupStatus>(await sys.GetAsync(setup));
        bool Done(SetupStatus s, string key) => s.Steps.Single(x => x.Key == key).Done;
        Assert.False(status.ReadyToBook);
        Assert.True(Done(status, "moveTypes") && Done(status, "lanes") && Done(status, "companies"));
        Assert.False(Done(status, "weeklyPlan") || Done(status, "slots") || Done(status, "tos") || Done(status, "staff") || Done(status, "map"));
        Assert.Contains("Terminals__JED2__Tos", status.Steps.Single(x => x.Key == "tos").Detail);

        // A weekly plan makes the slots: ready to book.
        var plan = await sys.PostAsJsonAsync($"/api/terminals/{terminal.TerminalId}/capacity/templates", new
        {
            name = "Standard week", isDefault = true, validFrom = (string?)null, validTo = (string?)null, priority = 0,
            windows = Enumerable.Range(0, 7).Select(d => new { day = d, start = "06:00", end = "22:00", totalCapacity = 20 }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, plan.StatusCode);
        status = await Read<SetupStatus>(await sys.GetAsync(setup));
        Assert.True(status.ReadyToBook);
        Assert.True(Done(status, "weeklyPlan") && Done(status, "slots"));

        Assert.Equal(0, (await Read<AdminResponse<int>>(await sys.PostAsync($"{setup}/standard-move-types", null))).Data);
        var test = (await Read<AdminResponse<TosTestResult>>(await sys.PostAsync($"{setup}/tos-test", null))).Data;
        Assert.Equal(("Fake", true), (test.Adapter, test.Reached));

        // An operator admin adds terminals to their own operator only; others can't add terminals or see the set-up.
        var ops = await As(OpsEmail);
        var theirs = (await Read<AdminResponse<NewTerminalView>>(await ops.PostAsJsonAsync("/api/terminals",
            new { code = "HB1", name = "Harbor Berth 1", terminalType = "Multipurpose", operatorId = 999 }))).Data;
        Assert.Equal(5, theirs.MoveTypes); // with breakbulk delivery
        var listed = Assert.Single(await Read<List<TerminalRow>>(await ops.GetAsync("/api/terminals")));
        Assert.Equal(("HB1", operatorId), (listed.Code, listed.OperatorId));
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync(setup)).StatusCode);

        var terminalAdmin = await As(DemoDataSeeder.TerminalAdminEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await terminalAdmin.PostAsJsonAsync("/api/terminals", new { code = "X9", name = "X", terminalType = "Container" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(DemoDataSeeder.DispatcherEmail)).GetAsync($"{Terminal}/setup")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await terminalAdmin.GetAsync($"{Terminal}/setup")).StatusCode);
    }
}
