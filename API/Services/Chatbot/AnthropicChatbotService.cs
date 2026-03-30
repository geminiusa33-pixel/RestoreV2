using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace API.Services.Chatbot;

public class AnthropicChatbotService(
    IHttpClientFactory httpClientFactory,
    IOptions<AnthropicSettings> anthropicOptions,
    IOptions<ChatbotSettings> chatbotOptions,
    IHostEnvironment env,
    ILogger<AnthropicChatbotService> logger) : IChatbotService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> GetReplyAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        var settings = anthropicOptions.Value;
        var chatbot = chatbotOptions.Value;

        if (!chatbot.Enabled)
        {
            return "O chat está desativado de momento.";
        }

        // TEMPORARY: Hardcode key for testing
        
        /*
        var apiKey =
            Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? settings.ApiKey
            ?? config["Anthropic:ApiKey"]
            ?? config["ANTHROPIC_API_KEY"];
        */

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? settings.ApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("Anthropic API key is not configured (missing ANTHROPIC_API_KEY or Anthropic:ApiKey)");
            return "O chat ainda não está configurado. Por favor, contacte-nos por outros meios.";
        }

        // Debug: log key health (first and last char, length)
        logger.LogInformation("[DEBUG] API key loaded: length={Length}, starts={Start}, ends={End}", apiKey.Length, apiKey.Substring(0, Math.Min(10, apiKey.Length)), apiKey.Substring(Math.Max(0, apiKey.Length - 10)));

        var trimmed = (userMessage ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "Diga-me como posso ajudar.";
        }

        if (trimmed.Length > 2000)
        {
            trimmed = trimmed[..2000];
        }

        var systemPrompt = string.IsNullOrWhiteSpace(chatbot.SystemPrompt)
            ? "És um assistente de apoio ao cliente de uma loja online chamada Restore. Responde em português de Portugal, de forma curta e útil. Se não souberes a resposta, pede mais detalhes ou sugere contactar o suporte. Não inventes políticas, preços ou prazos." 
            : chatbot.SystemPrompt;

        var requestBody = new AnthropicMessagesRequest
        {
            Model = "claude-opus-4-6",
            MaxTokens = settings.MaxTokens,
            Temperature = settings.Temperature,
            System = systemPrompt,
            Messages =
            [
                new AnthropicMessage { Role = "user", Content = trimmed }
            ]
        };

        var httpClient = httpClientFactory.CreateClient("anthropic");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        logger.LogInformation("[DEBUG] Sending Anthropic request: {JsonLength} bytes", json.Length);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            logger.LogWarning("Anthropic error {StatusCode}: {Body}", status, responseText);

            if (env.IsDevelopment())
            {
                var detail = TryGetAnthropicErrorMessage(responseText);
                return $"Erro do serviço de chat (Anthropic HTTP {status}). {detail}";
            }

            return "Não consegui responder agora. Tente novamente daqui a pouco.";
        }

        AnthropicMessagesResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AnthropicMessagesResponse>(responseText, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse Anthropic response: {Body}", responseText);
            return "Não consegui responder agora. Tente novamente.";
        }

        var textParts = parsed?.Content
            ?.Where(c => string.Equals(c.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();

        var reply = textParts is { Length: > 0 }
            ? string.Join("\n", textParts).Trim()
            : string.Empty;

        if (string.IsNullOrWhiteSpace(reply))
        {
            return "Não consegui gerar uma resposta. Pode reformular a pergunta?";
        }

        // Keep the UI tidy / cost bounded.
        if (reply.Length > 1500)
        {
            reply = reply[..1500];
        }

        return reply;
    }

    private class AnthropicMessagesRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; set; }

        [JsonPropertyName("max_tokens")]
        public required int MaxTokens { get; set; }

        [JsonPropertyName("temperature")]
        public required double Temperature { get; set; }

        [JsonPropertyName("system")]
        public required string System { get; set; }

        [JsonPropertyName("messages")]
        public required AnthropicMessage[] Messages { get; set; }
    }

    private class AnthropicMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; set; }

        [JsonPropertyName("content")]
        public required string Content { get; set; }
    }

    private class AnthropicContentBlock
    {
        [JsonPropertyName("type")]
        public required string Type { get; set; }

        [JsonPropertyName("text")]
        public required string Text { get; set; }
    }

    private class AnthropicMessagesResponse
    {
        [JsonPropertyName("content")]
        public AnthropicContentPart[]? Content { get; set; }
    }

    private class AnthropicContentPart
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private static string TryGetAnthropicErrorMessage(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (!doc.RootElement.TryGetProperty("error", out var error))
            {
                return "";
            }

            if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // ignore parse errors
        }

        return "";
    }
}
