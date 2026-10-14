using System.Net;
using System.Text;
using System.Xml.Linq;
using Tas.Application.Tos;
using Tas.Application.Tos.N4;
using Tas.Infrastructure.Tos.N4;

namespace Tas.Infrastructure.Tests;

/// <summary>The N4 SOAP client and payload handling, against a fake HTTP server (no N4 or database needed).</summary>
public class ArgoServiceClientTests
{
    private static readonly N4TerminalOptions Terminal = new()
    {
        Url = "http://n4.test/apex/services/argoservice",
        Username = "tas-ws",
        Password = "secret",
        OperatorId = "OPR",
        ComplexId = "CPX",
        FacilityId = "JED",
        YardId = "YRD",
        TimeoutSeconds = 1,
    };

    private const string OkResponse =
        """
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"><soapenv:Body>
          <ns1:genericInvokeResponse xmlns:ns1="http://www.navis.com/services/argoservice">
            <ns1:genericInvokeResponse>
              <ns2:commonResponse xmlns:ns2="http://types.webservice.argo.navis.com/v1.0"><ns2:Status>1</ns2:Status><ns2:StatusDescription>OK</ns2:StatusDescription></ns2:commonResponse>
              <ns1:responsePayLoad>{"units":[{"containerNo":"MSCU1234566","found":true,"transitState":"S40_YARD","holds":["A &amp; B"]}]}</ns1:responsePayLoad>
            </ns1:genericInvokeResponse>
          </ns1:genericInvokeResponse>
        </soapenv:Body></soapenv:Envelope>
        """;

    private sealed class FakeServer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await handle(request, ct);
        }
    }

    private static HttpResponseMessage Xml(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    [Fact]
    public async Task Sends_genericInvoke_with_scope_auth_and_escaped_document_and_returns_the_payload()
    {
        var server = new FakeServer((_, _) => Task.FromResult(Xml(HttpStatusCode.OK, OkResponse)));
        var client = new ArgoServiceClient(new HttpClient(server));
        var document = N4TosAdapter.LookupDocument("TasLookup", new N4LookupRequest(["MSCU1234566"], ["BKG1001"], []));

        var payload = await client.InvokeAsync(Terminal, document, CancellationToken.None);

        Assert.StartsWith("{\"units\":", payload);
        Assert.Contains("\"A & B\"", payload);

        Assert.Equal("Basic", server.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("tas-ws:secret", Encoding.UTF8.GetString(Convert.FromBase64String(server.LastRequest.Headers.Authorization.Parameter!)));
        Assert.True(server.LastRequest.Headers.Contains("SOAPAction"));

        var sent = XDocument.Parse(server.LastBody!);
        string Value(string local) => sent.Descendants().Single(e => e.Name.LocalName == local).Value;
        Assert.Equal("CPX", Value("complexId"));
        Assert.Equal("JED", Value("facilityId"));
        Assert.Equal(document, Value("xmlDoc"));

        var groovy = XElement.Parse(Value("xmlDoc"));
        Assert.Equal("TasLookup", groovy.Attribute("class-name")!.Value);
        Assert.Equal("""{"units":["MSCU1234566"],"orders":["BKG1001"],"bls":[]}""", groovy.Descendants("parameter").Single().Attribute("value")!.Value);
    }

    [Fact]
    public async Task Soap_faults_error_statuses_and_garbage_are_reported_as_unavailable()
    {
        const string fault = """
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"><soapenv:Body>
              <soapenv:Fault><faultcode>soapenv:Server</faultcode><faultstring>Authentication failed</faultstring></soapenv:Fault>
            </soapenv:Body></soapenv:Envelope>
            """;
        var faultClient = new ArgoServiceClient(new HttpClient(new FakeServer((_, _) => Task.FromResult(Xml(HttpStatusCode.InternalServerError, fault)))));
        Assert.Contains("Authentication failed", (await Assert.ThrowsAsync<TosUnavailableException>(() => faultClient.InvokeAsync(Terminal, "<groovy/>", CancellationToken.None))).Message);

        const string errors = """
            <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"><soapenv:Body>
              <genericInvokeResponse><genericInvokeResponse><commonResponse>
                <Status>3</Status><StatusDescription>Errors</StatusDescription>
                <MessageCollector><Messages Message="Code extension TasLookup not found"/></MessageCollector>
              </commonResponse></genericInvokeResponse></genericInvokeResponse>
            </soapenv:Body></soapenv:Envelope>
            """;
        var noPayload = Assert.Throws<TosUnavailableException>(() => ArgoServiceClient.ParseResponse(200, errors));
        Assert.Contains("Errors", noPayload.Message);
        Assert.Contains("TasLookup not found", noPayload.Message);

        Assert.Throws<TosUnavailableException>(() => ArgoServiceClient.ParseResponse(502, "<html>Bad gateway"));
    }

    [Fact]
    public async Task Timeouts_and_connection_failures_are_reported_as_unavailable()
    {
        var slow = new ArgoServiceClient(new HttpClient(new FakeServer(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return Xml(HttpStatusCode.OK, OkResponse);
        })));
        Assert.Contains("did not answer", (await Assert.ThrowsAsync<TosUnavailableException>(() => slow.InvokeAsync(Terminal, "<groovy/>", CancellationToken.None))).Message);

        var down = new ArgoServiceClient(new HttpClient(new FakeServer((_, _) => throw new HttpRequestException("Connection refused"))));
        Assert.Contains("Connection refused", (await Assert.ThrowsAsync<TosUnavailableException>(() => down.InvokeAsync(Terminal, "<groovy/>", CancellationToken.None))).Message);
    }

    [Fact]
    public void Payload_errors_from_the_code_extension_are_unavailable_and_good_payloads_parse()
    {
        Assert.Contains("NullPointerException",
            Assert.Throws<TosUnavailableException>(() => N4TosAdapter.ParsePayload("""{"error":"NullPointerException: x"}""")).Message);
        Assert.Throws<TosUnavailableException>(() => N4TosAdapter.ParsePayload("not json"));

        var result = N4TosAdapter.ParsePayload(
            """{"units":[{"containerNo":"MSCU1234566","found":true,"vgmKg":24100.5,"holds":["CUSTOMS HOLD"]}],"orders":[{"nbr":"BKG1001","found":true,"quantityRemaining":2,"cargoCutoff":"2026-10-21T18:00:00+03:00"}]}""");
        var unit = Assert.Single(result.Units!);
        Assert.Equal(24100.5m, unit.VgmKg);
        Assert.Equal("CUSTOMS HOLD", Assert.Single(unit.Holds!));
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 18, 0, 0, TimeSpan.FromHours(3)), Assert.Single(result.Orders!).CargoCutoff);
        Assert.Null(result.Bls);
    }
}
