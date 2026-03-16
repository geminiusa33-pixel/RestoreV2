namespace API.Services.Chatbot;

public class ChatbotSettings
{
    public bool Enabled { get; set; } = true;

    // Extra context (store policies, opening hours, returns, shipping, etc).
    // Keep it short; it is sent with every request.
    public string? SystemPrompt { get; set; }
}
