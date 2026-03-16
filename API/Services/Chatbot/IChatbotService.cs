namespace API.Services.Chatbot;

public interface IChatbotService
{
    Task<string> GetReplyAsync(string userMessage, CancellationToken cancellationToken = default);
}
