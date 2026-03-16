namespace API.Services.Chatbot;

public class AnthropicSettings
{
    public string Model { get; set; } = "claude-3-haiku-20240307";
    public int MaxTokens { get; set; } = 256;
    public double Temperature { get; set; } = 0.2;

    // API key must be provided via env var / secret store:
    // - ANTHROPIC_API_KEY
    // or config: Anthropic:ApiKey (not recommended for source-controlled files)
    public string? ApiKey { get; set; }
}
