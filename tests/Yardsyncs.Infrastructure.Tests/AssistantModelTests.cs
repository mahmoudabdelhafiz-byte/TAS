using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Yardsyncs.Infrastructure.Assistant;

namespace Yardsyncs.Infrastructure.Tests;

/// <summary>The Anthropic Messages API client against a fake server, and the guides built into the server.</summary>
public class AssistantModelTests
{
    private sealed class FakeServer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Sent = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static AnthropicModel Model(FakeServer server) =>
        new(new HttpClient(server), Options.Create(new AssistantOptions { ApiKey = "sk-test", BaseUrl = "https://gateway.example/" }));

    [Fact]
    public async Task Sends_the_messages_request_with_the_key_and_version()
    {
        var server = new FakeServer(HttpStatusCode.OK, """{"stop_reason":"end_turn","content":[{"type":"text","text":"Hi"}]}""");
        var answer = await Model(server).SendAsync(new JsonObject { ["model"] = "claude-sonnet-5-5", ["messages"] = new JsonArray() }, default);

        Assert.Equal("Hi", (string?)answer["content"]![0]!["text"]);
        Assert.Equal("https://gateway.example/v1/messages", server.Request!.RequestUri!.ToString());
        Assert.Equal("sk-test", server.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", server.Request.Headers.GetValues("anthropic-version").Single());
        Assert.Contains("claude-sonnet-5-5", server.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused the API key")]
    [InlineData(HttpStatusCode.TooManyRequests, "busy")]
    [InlineData((HttpStatusCode)529, "busy")]
    [InlineData(HttpStatusCode.InternalServerError, "answered 500")]
    public async Task Provider_errors_become_unavailable(HttpStatusCode status, string expected)
    {
        var server = new FakeServer(status, """{"type":"error","error":{"type":"x","message":"nope"}}""");
        var error = await Assert.ThrowsAsync<AssistantUnavailableException>(() => Model(server).SendAsync(new JsonObject(), default));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void The_guides_are_built_into_the_server()
    {
        Assert.Equal(9 * 2 + 7, AssistantKnowledge.PageCount); // guides in two languages, technical pages
        var company = AssistantKnowledge.For("en", staff: false);
        Assert.Contains("<guide id=\"booking\">", company);
        Assert.DoesNotContain("<technical-page", company);
        Assert.Contains("<technical-page id=\"api\">", AssistantKnowledge.For("en", staff: true));
        Assert.Contains("حجز شاحنة", AssistantKnowledge.For("ar", staff: false));
        Assert.False(new AssistantOptions().Enabled); // off until a key is set
    }
}
