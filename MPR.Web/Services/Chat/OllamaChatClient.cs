using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MPR.Web.Services.Chat;

public record OllamaToolCall(string Id, string FunctionName, JsonElement Arguments);
public record OllamaChatResult(string? Content, List<OllamaToolCall> ToolCalls);
public record ChatTurn(string Role, string? Content, string? ToolCallId = null, string? ToolName = null);
public record ToolDefinition(string Name, string Description, object ParametersSchema);

/// <summary>
/// Thin client for a Chat Completions-style API with tool-calling. Works against
/// either a local Ollama instance (https://ollama.com, free, self-hosted, no API key)
/// or a hosted OpenAI-compatible provider such as Groq (https://groq.com - has a
/// genuinely free tier, unlike OpenAI/Anthropic's paid APIs) by setting apiKey and
/// pointing baseUrl at the provider's endpoint. Groq's free tier is the practical
/// choice when the app itself is hosted on a resource-capped platform like Render's
/// free tier, since it needs no local RAM/GPU for the model - the model runs on
/// Groq's infrastructure, not yours.
/// </summary>
public class OllamaChatClient
{
    private readonly HttpClient _http;
    private readonly string _model;

    public OllamaChatClient(HttpClient http, string baseUrl, string model, string? apiKey = null)
    {
        _http = http;
        _http.BaseAddress = new Uri(baseUrl);
        _model = model;
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<OllamaChatResult> ChatAsync(List<ChatTurn> history, List<ToolDefinition> tools, CancellationToken ct = default)
    {
        var payload = new
        {
            model = _model,
            messages = history.Select(h => (object)new { role = h.Role, content = h.Content, tool_call_id = h.ToolCallId, name = h.ToolName }),
            tools = tools.Select(t => (object)new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema }
            }),
            stream = false
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Ollama's native endpoint is /api/chat; Groq and other OpenAI-compatible
        // providers use /openai/v1/chat/completions (or /v1/chat/completions) with a
        // slightly different response envelope (choices[0].message vs message
        // directly). Detect by whether an API key was supplied, since that's the
        // signal this is a hosted OpenAI-compatible provider rather than local Ollama.
        bool isOpenAiCompatible = _http.DefaultRequestHeaders.Authorization is not null;
        string path = isOpenAiCompatible ? "/openai/v1/chat/completions" : "/api/chat";

        using var response = await _http.PostAsync(path, content, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var message = isOpenAiCompatible
            ? doc.RootElement.GetProperty("choices")[0].GetProperty("message")
            : doc.RootElement.GetProperty("message");

        string? textContent = message.TryGetProperty("content", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null;
        var toolCalls = new List<OllamaToolCall>();

        if (message.TryGetProperty("tool_calls", out var calls))
        {
            foreach (var call in calls.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                string name = fn.GetProperty("name").GetString()!;
                // OpenAI-compatible APIs (Groq included) return arguments as a JSON
                // *string* that must be parsed again; Ollama's native API returns it
                // as an already-parsed JSON object. Handle both.
                var argsProp = fn.GetProperty("arguments");
                JsonElement args = argsProp.ValueKind == JsonValueKind.String
                    ? JsonDocument.Parse(argsProp.GetString()!).RootElement
                    : argsProp;
                string id = call.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                toolCalls.Add(new OllamaToolCall(id, name, args));
            }
        }

        return new OllamaChatResult(textContent, toolCalls);
    }
}
