using System.Net;
using System.Net.Http.Json;
using Tas.Application.Booking;
using Tas.Infrastructure;

namespace Tas.Api.Tests;

/// <summary>Book by BL / several containers: the lookup that lists containers with their check results.</summary>
public sealed class ContainerLookupApiTests(TasApiFactory factory) : ApiTestBase(factory)
{
    private string Lookup => $"{Terminal}/containers/lookup";

    private static async Task<ContainerLookupResult> Data(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Read<AdminResponse<ContainerLookupResult>>(response)).Data;
    }

    [Fact]
    public async Task A_bill_of_lading_lists_its_containers_with_what_stops_each()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);
        var gulf = await As(DemoDataSeeder.OtherDispatcherEmail);

        // One container of the BL is already booked by Al Noor.
        Assert.Equal(HttpStatusCode.Created, (await alNoor.PostAsJsonAsync(Appointments, ImportBooking(container: "MSCU5523417"))).StatusCode);

        var result = await Data(await alNoor.PostAsJsonAsync(Lookup, new { category = "IMP_PICKUP", blNo = " medujd260001 " }));
        Assert.Equal("MEDUJD260001", result.BlNo);
        var byNo = result.Containers.ToDictionary(c => c.ContainerNo);
        Assert.Equal(5, byNo.Count);

        Assert.Equal(LookupStatus.Ready, byNo["MSCU1234566"].Status);
        Assert.Equal(20, byNo["MSCU1234566"].SizeFt);
        Assert.Equal(40, byNo["MEDU2601003"].SizeFt);
        Assert.Equal(LookupStatus.Blocked, byNo["MSCU7654329"].Status);
        Assert.Contains("Customs hold", Assert.Single(byNo["MSCU7654329"].Problems));
        Assert.Equal(LookupStatus.Warning, byNo["CMAU5100028"].Status);
        Assert.Single(byNo["CMAU5100028"].Warnings);
        Assert.Equal(LookupStatus.Booked, byNo["MSCU5523417"].Status);
        Assert.NotNull(byNo["MSCU5523417"].AppointmentNo);

        // Another company sees that it is taken, but not whose appointment it is.
        var forGulf = (await Data(await gulf.PostAsJsonAsync(Lookup, new { category = "IMP_PICKUP", blNo = "MEDUJD260001" })))
            .Containers.Single(c => c.ContainerNo == "MSCU5523417");
        Assert.Equal(LookupStatus.Booked, forGulf.Status);
        Assert.Null(forGulf.AppointmentNo);
        Assert.Equal("Already booked by another company", Assert.Single(forGulf.Problems));

        Assert.Equal(HttpStatusCode.NotFound, (await alNoor.PostAsJsonAsync(Lookup, new { category = "IMP_PICKUP", blNo = "NO-SUCH-BL" })).StatusCode);
    }

    [Fact]
    public async Task A_pasted_list_is_checked_container_by_container()
    {
        if (!Enabled) return;

        var alNoor = await As(DemoDataSeeder.DispatcherEmail);

        var result = await Data(await alNoor.PostAsJsonAsync(Lookup, new
        {
            category = "IMP_PICKUP",
            containers = new[] { "MSCU1234566\nmscu 765432-9", "MSCU1234567, ABC" },
        }));

        Assert.Null(result.BlNo);
        Assert.Equal(new[] { "MSCU1234566", "MSCU7654329", "MSCU1234567", "ABC" }, result.Containers.Select(c => c.ContainerNo).ToArray());
        Assert.Equal(LookupStatus.Ready, result.Containers[0].Status);
        Assert.Equal(LookupStatus.Blocked, result.Containers[1].Status);
        Assert.Contains("check digit", Assert.Single(result.Containers[2].Problems)); // wrong check digit
        Assert.Contains("not a container number", Assert.Single(result.Containers[3].Problems));

        // Cargo moves, empty input and unknown move types are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await alNoor.PostAsJsonAsync(Lookup, new { category = "BB_DELIVERY", containers = new[] { "MSCU1234566" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alNoor.PostAsJsonAsync(Lookup, new { category = "IMP_PICKUP", containers = new[] { " " } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alNoor.PostAsJsonAsync(Lookup, new { category = "NOPE", blNo = "MEDUJD260001" })).StatusCode);
    }
}
