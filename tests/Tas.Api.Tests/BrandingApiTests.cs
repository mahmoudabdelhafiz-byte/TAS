using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Tas.Domain;
using Tas.Domain.Parties;
using Tas.Infrastructure;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Brands;
using Tas.Infrastructure.Persistence;

namespace Tas.Api.Tests;

/// <summary>Branding: the installation's and an operator's name, colour, logo and web address.</summary>
public sealed class BrandingApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private const string RootEmail = "root@yardsyncs.example";
    private const string OpsEmail = "ops@harbor-one.example";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    private static MultipartFormDataContent File(byte[] bytes, string name, string type)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new MultipartFormDataContent { { content, "file", name } };
    }

    [Fact]
    public async Task The_installation_and_an_operator_have_their_own_brand()
    {
        if (!Enabled) return;

        var anonymous = Factory.CreateClient();
        Assert.Equal(new BrandView("Yardsyncs", "#0e7c86", null, null), await Read<BrandView>(await anonymous.GetAsync("/api/branding")));

        int operatorId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasDbContext>();
            var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
            var op = new TerminalOperator { Code = "HARBOR1", Name = "Harbor One" };
            db.TerminalOperators.Add(op);
            await db.SaveChangesAsync();
            operatorId = op.OperatorId;
            (await db.Terminals.FindAsync(Demo.TerminalId))!.OperatorId = operatorId;
            var root = new AppUser { Email = RootEmail, FullName = "System admin", Role = UserRole.SysAdmin };
            var ops = new AppUser { Email = OpsEmail, FullName = "Operations head", Role = UserRole.OperatorAdmin, OperatorId = operatorId };
            root.PasswordHash = passwords.Hash(root, Password);
            ops.PasswordHash = passwords.Hash(ops, Password);
            db.AppUsers.AddRange(root, ops);
            await db.SaveChangesAsync();
        }
        BrandingService.Forget();

        // The installation's: system admins only; the colour must carry white text; no web address of its own.
        var sys = await As(RootEmail);
        Assert.Equal(HttpStatusCode.OK, (await sys.PutAsJsonAsync("/api/branding/installation", new { name = "PortOps", color = "#1F4E79" })).StatusCode);
        var light = await sys.PutAsJsonAsync("/api/branding/installation", new { name = "PortOps", color = "#ff9900" });
        Assert.Equal(HttpStatusCode.BadRequest, light.StatusCode);
        Assert.Contains("too light", await light.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await sys.PutAsJsonAsync("/api/branding/installation", new { domain = "tas.portops.example" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(DemoDataSeeder.TerminalAdminEmail)).PutAsJsonAsync("/api/branding/installation", new { name = "Mine" })).StatusCode);
        Assert.Equal(new BrandView("PortOps", "#1f4e79", null, null), await Read<BrandView>(await anonymous.GetAsync("/api/branding")));

        // The operator's: a name, a colour, a logo and its own web address.
        var own = $"/api/operators/{operatorId}/branding";
        Assert.Equal(HttpStatusCode.OK, (await sys.PutAsJsonAsync(own, new { name = "Harbor One", color = "#7a1f3d", domain = "TAS.Harbor-One.example" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sys.PostAsync($"{own}/logo", File(Png, "logo.png", "image/png"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await sys.PostAsync($"{own}/logo", File("<svg><script>alert(1)</script></svg>"u8.ToArray(), "logo.png", "image/png"))).StatusCode);

        // Its people signing in at its address see its brand; everyone else the installation's.
        var atHarbor = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://tas.harbor-one.example") });
        var harbor = await Read<BrandView>(await atHarbor.GetAsync("/api/branding"));
        Assert.Equal(("Harbor One", "#7a1f3d", operatorId), (harbor.Name, harbor.Color, harbor.OperatorId));
        Assert.NotNull(harbor.LogoUrl);
        Assert.Equal("PortOps", (await Read<BrandView>(await anonymous.GetAsync("/api/branding"))).Name);

        var logo = await anonymous.GetAsync(harbor.LogoUrl);
        Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await logo.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", logo.Headers.GetValues("X-Content-Type-Options").Single());

        // Signed in, the operator's terminals show its brand.
        var dispatcher = await As(DemoDataSeeder.DispatcherEmail);
        Assert.Equal("Harbor One", (await Read<BrandView>(await dispatcher.GetAsync($"{Terminal}/branding"))).Name);

        // Operator admins change their own look, not the web address, not another operator's.
        var ops = await As(OpsEmail);
        Assert.Equal(HttpStatusCode.OK, (await ops.PutAsJsonAsync(own, new { name = "Harbor One Terminals", color = "#7a1f3d", domain = "tas.harbor-one.example" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ops.PutAsJsonAsync(own, new { name = "Harbor One", domain = "tas.mine.example" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.PutAsJsonAsync("/api/branding/installation", new { name = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync($"/api/operators/{operatorId + 1}/branding")).StatusCode);
        var settings = await Read<BrandSettings>(await ops.GetAsync(own));
        Assert.Equal(("Harbor One Terminals", "tas.harbor-one.example"), (settings.Name, settings.Domain));

        // Messages are signed with the terminal's brand; drivers' sign-in codes with the installation's.
        using (var scope = Factory.Services.CreateScope())
        {
            var branding = scope.ServiceProvider.GetRequiredService<BrandingService>();
            Assert.Equal("Harbor One Terminals", await branding.MessageNameAsync(Demo.TerminalId));
            Assert.Equal("PortOps", await branding.MessageNameAsync(null));
        }

        // Without a logo of its own, the installation's (none here) shows: the Yardsyncs mark.
        Assert.Equal(HttpStatusCode.OK, (await ops.DeleteAsync($"{own}/logo")).StatusCode);
        Assert.Null((await Read<BrandView>(await dispatcher.GetAsync($"{Terminal}/branding"))).LogoUrl);
    }
}
