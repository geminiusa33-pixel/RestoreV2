# Restore Chatbot Setup

## 🔒 Security First

**⚠️ IMPORTANT:** The API key is a sensitive secret. It should NEVER be hardcoded in source code.

### Setup for Local Development

1. **Get your Anthropic API Key**
   - Visit https://console.anthropic.com/account/keys
   - Create a new API key
   - Copy the key

2. **Set up environment variable**
   - Copy `.env.local` template to `.env` (or set in your system)
   - Add your key: `ANTHROPIC_API_KEY=sk-ant-api03-xxxxx`
   - The app reads this automatically in Development mode

3. **Test the chatbot**
   ```powershell
   $env:ANTHROPIC_API_KEY = "your-key-here"
   dotnet run --project .\API\API.csproj
   ```

### Setup for Azure Deployment

1. **Add to Azure App Service**
   - Go to Azure Portal → App Service → Configuration
   - Click "New application setting"
   - Name: `ANTHROPIC_API_KEY`
   - Value: Your Anthropic API key
   - Click Save (auto-restart)

2. **Alternative: Use Azure Key Vault**
   - Store key in Key Vault
   - Use Managed Identity to access it
   - More secure for production

## 📋 Important Files

- **AnthropicChatbotService.cs**: Core chatbot logic
  - Reads from `ANTHROPIC_API_KEY` environment variable first
  - Falls back to `Anthropic:ApiKey` from appsettings
  - NO hardcoded secrets anywhere

- **appsettings.Development.json**: Development config
  - `Chatbot.Enabled`: Enable/disable chatbot
  - `Chatbot.SystemPrompt`: Bot's instructions (Portuguese)
  - `Anthropic.Model`: Currently `claude-opus-4-6`

- **ChatController.cs**: `/api/chat` endpoint
  - Rate limited: 30 requests per 10 minutes
  - Returns `{reply: "..."}`

## ✅ Checklist Before Pushing to Git

- [ ] Remove ALL hardcoded API keys from code
- [ ] Use `.env` or environment variables only
- [ ] Never commit `.env` file (already in .gitignore)
- [ ] Use `dotnet user-secrets` for development
- [ ] Test with GitHub Push Protection enabled

## 🚀 How the Chatbot Works

```
User sends: "What shirt do you recommend?"
                 ↓
ChatController validates message
                 ↓
AnthropicChatbotService loads:
  - API Key (from env var)
  - System Prompt (Portuguese, Restore context)
  - Temperature: 0.2 (deterministic)
                 ↓
Sends to Anthropic API: /v1/messages
                 ↓
Claude responds with text
                 ↓
Returns to frontend widget
```

## 🐛 Troubleshooting

**401 Unauthorized**: 
- Check `ANTHROPIC_API_KEY` is set
- Verify key is correct and active
- Try: `echo $env:ANTHROPIC_API_KEY`

**400 Bad Request**:
- Check message is not empty
- Message max length: 2000 characters

**Model not found**:
- Verify `claude-opus-4-6` is available in your Anthropic account
- Check API version header: `2023-06-01`
