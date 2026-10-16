using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Yardsyncs.Application.Tos;

namespace Yardsyncs.Infrastructure.Tos.N4;

/// <summary>Sends one request document to N4 and returns the answer payload.</summary>
public interface IN4Client
{
    /// <exception cref="TosUnavailableException">N4 could not be reached or answered with an error.</exception>
    Task<string> InvokeAsync(N4TerminalOptions terminal, string xmlDoc, CancellationToken ct);
}

/// <summary>
/// N4 ArgoService <c>genericInvoke</c> over SOAP 1.1 with basic authentication. The xmlDoc runs a Groovy code
/// extension inside N4 and its return value comes back in <c>responsePayLoad</c>.
/// Every failure — timeout, HTTP error, SOAP fault, missing payload — becomes a <see cref="TosUnavailableException"/>.
/// </summary>
public sealed class ArgoServiceClient(HttpClient http) : IN4Client
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";

    public async Task<string> InvokeAsync(N4TerminalOptions terminal, string xmlDoc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(terminal.Url))
            throw new TosUnavailableException("N4 URL is not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, terminal.Url)
        {
            Content = new StringContent(Envelope(terminal, xmlDoc), Encoding.UTF8, "text/xml"),
        };
        request.Headers.Add("SOAPAction", "\"\"");
        if (!string.IsNullOrEmpty(terminal.Username))
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{terminal.Username}:{terminal.Password}")));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, terminal.TimeoutSeconds)));

        string body;
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token);
            body = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TosUnavailableException($"N4 did not answer within {terminal.TimeoutSeconds} s.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new TosUnavailableException($"N4 could not be reached: {ex.Message}", ex);
        }

        using (response)
            return ParseResponse((int)response.StatusCode, body);
    }

    /// <summary>The SOAP request; the xmlDoc is sent as escaped text, as the WSDL defines it as a string.</summary>
    public static string Envelope(N4TerminalOptions terminal, string xmlDoc)
    {
        XNamespace arg = terminal.ArgoNamespace;
        XNamespace v1 = terminal.TypesNamespace;

        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soapenv", Soap),
            new XAttribute(XNamespace.Xmlns + "arg", arg),
            new XAttribute(XNamespace.Xmlns + "v1", v1),
            new XElement(Soap + "Header"),
            new XElement(Soap + "Body",
                new XElement(arg + "genericInvoke",
                    new XElement(arg + "scopeCoordinateIdsWsType",
                        new XElement(v1 + "operatorId", terminal.OperatorId),
                        new XElement(v1 + "complexId", terminal.ComplexId),
                        new XElement(v1 + "facilityId", terminal.FacilityId),
                        new XElement(v1 + "yardId", terminal.YardId)),
                    new XElement(arg + "xmlDoc", xmlDoc))));

        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Reads the payload, or turns a fault / error status into a <see cref="TosUnavailableException"/>.</summary>
    public static string ParseResponse(int httpStatus, string body)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(body);
        }
        catch (XmlException)
        {
            throw new TosUnavailableException($"N4 answered HTTP {httpStatus} with a non-XML body.");
        }

        var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault is not null)
        {
            var reason = fault.Descendants().FirstOrDefault(e => e.Name.LocalName is "faultstring" or "Text")?.Value;
            throw new TosUnavailableException($"N4 returned a SOAP fault: {reason ?? "no reason given"}");
        }

        var payload = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("responsePayLoad", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(payload))
            return payload.Trim();

        var status = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "StatusDescription")?.Value;
        var messages = doc.Descendants()
            .Where(e => e.Name.LocalName is "Message" or "message" or "Messages")
            .Select(e => e.Attribute("Message")?.Value ?? e.Value)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Take(3)
            .ToList();
        throw new TosUnavailableException(
            $"N4 answered HTTP {httpStatus} without a payload. {status} {string.Join("; ", messages)}".Trim());
    }
}
