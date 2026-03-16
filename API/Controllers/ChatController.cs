using API.DTOs;
using API.Services.Chatbot;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController(IChatbotService chatbot) : ControllerBase
{
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("chat")]
    public async Task<ActionResult<ChatResponseDto>> Chat([FromBody] ChatRequestDto dto, CancellationToken cancellationToken)
    {
        var message = dto.Message?.Trim() ?? string.Empty;

        if (message.Length == 0)
        {
            return BadRequest("Mensagem em falta.");
        }

        var reply = await chatbot.GetReplyAsync(message, cancellationToken);
        return Ok(new ChatResponseDto { Reply = reply });
    }
}
