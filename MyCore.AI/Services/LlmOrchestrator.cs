using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace MyCore.AI.Services;

public class LlmOrchestrator(Kernel kernel, IChatCompletionService chat, ConversationMemory conversationMemory)
{
    private readonly Kernel _kernel = kernel;
    private readonly IChatCompletionService _chat = chat;
    private readonly ConversationMemory _conversationMemory = conversationMemory;

  public async IAsyncEnumerable<string> StreamResponseAsync(
        string? conversationId,
        string systemPrompt,
        string userInput,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chatHistory = new ChatHistory();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            chatHistory.AddSystemMessage(systemPrompt);
        }
        
        // Add existing conversation messages first (if any), then current user input
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            var priorMessages = _conversationMemory.GetMessages(conversationId);
            foreach (var message in priorMessages)
            {
                if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    chatHistory.AddUserMessage(message.Text);
                }
                else if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    chatHistory.AddAssistantMessage(message.Text);
                }
            }
        }
        chatHistory.AddUserMessage(userInput);

        var settings = new OpenAIPromptExecutionSettings
        {
            ReasoningEffort = "low"
        };

        var fullAssistantText = new System.Text.StringBuilder();
        await foreach (var delta in _chat.GetStreamingChatMessageContentsAsync(chatHistory, settings, _kernel, cancellationToken))
        {
            if (!string.IsNullOrEmpty(delta.Content))
            {
                yield return delta.Content;
                fullAssistantText.Append(delta.Content);
            }
        }

        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            _conversationMemory.AppendUserMessage(conversationId, userInput);
            _conversationMemory.AppendAssistantMessage(conversationId, fullAssistantText.ToString());
        }
    }
}
