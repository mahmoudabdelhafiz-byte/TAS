using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Yardsyncs.Infrastructure.Assistant;

/// <summary>Configuration section "Assistant". Off until an API key is set.</summary>
public sealed class AssistantOptions
{
    /// <summary>Anthropic API key (environment variable Assistant__ApiKey). Empty = the assistant is off.</summary>
    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "claude-sonnet-5-5";

    /// <summary>The Anthropic API, or a gateway that speaks the same API.</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>Allow read-only look-ups of the user's data (appointments, free hours, containers, today's figures).</summary>
    public bool LiveData { get; set; } = true;

    public int DailyQuestionsPerUser { get; set; } = 60;
    public int MaxTokens { get; set; } = 1500;

    /// <summary>Look-ups the model may make for one question.</summary>
    public int MaxToolRounds { get; set; } = 5;

    public int TimeoutSeconds { get; set; } = 60;

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>The model could not answer (provider down, overloaded, misconfigured).</summary>
public sealed class AssistantUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One call to the model: a Messages API request body in, the response body out.</summary>
public interface IAssistantModel
{
    Task<JsonObject> SendAsync(JsonObject request, CancellationToken ct);
}

/// <summary>Anthropic's Messages API (POST /v1/messages).</summary>
public sealed class AnthropicModel(HttpClient http, IOptions<AssistantOptions> options) : IAssistantModel
{
    public async Task<JsonObject> SendAsync(JsonObject request, CancellationToken ct)
    {
        var o = options.Value;
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("x-api-key", o.ApiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds)));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new AssistantUnavailableException("The AI service could not be reached.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = TryError(body);
                throw response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        new AssistantUnavailableException($"The AI service refused the API key ({(int)response.StatusCode}): {detail}"),
                    HttpStatusCode.TooManyRequests or (HttpStatusCode)529 =>
                        new AssistantUnavailableException($"The AI service is busy ({(int)response.StatusCode})."),
                    _ => new AssistantUnavailableException($"The AI service answered {(int)response.StatusCode}: {detail}"),
                };
            }
            return JsonNode.Parse(body) as JsonObject ?? throw new AssistantUnavailableException("The AI service sent an empty answer.");
        }
    }

    private static string TryError(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body[..Math.Min(body.Length, 200)];
        }
        catch
        {
            return body[..Math.Min(body.Length, 200)];
        }
    }
}
