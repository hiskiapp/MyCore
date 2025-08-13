using System.Collections.Concurrent;

namespace MyCore.AI.Services;

public sealed class ConversationMemory
{
    public sealed record ConversationMessage(string Role, string Text, DateTimeOffset Timestamp);

    private readonly ConcurrentDictionary<string, List<ConversationMessage>> _conversationIdToMessages = new();

    public IReadOnlyList<ConversationMessage> GetMessages(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return [];
        if (_conversationIdToMessages.TryGetValue(conversationId, out var list))
        {
            lock (list)
            {
                return [.. list];
            }
        }
        return [];
    }

    public void AppendUserMessage(string conversationId, string text)
    {
        Append(conversationId, new ConversationMessage("user", text, DateTimeOffset.UtcNow));
    }

    public void AppendAssistantMessage(string conversationId, string text)
    {
        Append(conversationId, new ConversationMessage("assistant", text, DateTimeOffset.UtcNow));
    }

    private void Append(string conversationId, ConversationMessage message)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        var list = _conversationIdToMessages.GetOrAdd(conversationId, _ => new List<ConversationMessage>(capacity: 32));
        lock (list)
        {
            list.Add(message);
        }
    }
}
