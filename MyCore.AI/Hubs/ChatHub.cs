using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using MyCore.AI.Services;

namespace MyCore.AI.Hubs;

public class ChatHub(
    FastTranscriber fastTranscriber,
    LlmOrchestrator llmOrchestrator,
    TtsStreamer ttsStreamer,
    ConversationMemory conversationMemory) : Hub
{
    // Maps SignalR connection IDs to per-session state
    private static readonly ConcurrentDictionary<string, SessionState> _connectionIdToSessionState = new();

    private readonly FastTranscriber _fastTranscriber = fastTranscriber;
    private readonly LlmOrchestrator _llmOrchestrator = llmOrchestrator;
    private readonly TtsStreamer _ttsStreamer = ttsStreamer;
    private readonly ConversationMemory _conversationMemory = conversationMemory;

  public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (_connectionIdToSessionState.TryRemove(Context.ConnectionId, out var sessionState))
        {
            sessionState.Cancel();
            sessionState.Dispose();
        }
        return base.OnDisconnectedAsync(exception);
    }

    public Task<string> Join(string? conversationId)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var sessionState = new SessionState(sessionId) { ConversationId = conversationId };
        _connectionIdToSessionState[Context.ConnectionId] = sessionState;

        // Notify client of session start
        return Clients.Caller.SendAsync("SessionStarted", new { sessionId, conversationId })
            .ContinueWith(_ => sessionId);
    }

    /// <summary>
    /// Streams LLM deltas to the client and yields them for downstream TTS.
    /// </summary>
    private async IAsyncEnumerable<string> StreamAndFanoutDeltas(
        SessionState sessionState,
        string segmentId,
        IAsyncEnumerable<string> llmDeltas,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var deltaIndex = 0;
        await foreach (var llmDelta in llmDeltas.WithCancellation(cancellationToken))
        {
            if (string.IsNullOrEmpty(llmDelta)) continue;
            // Send each LLM token delta to the client as soon as it's available
            await Clients.Caller.SendAsync("LLMDelta", new { segmentId, text = llmDelta, deltaIndex }, cancellationToken: cancellationToken);
            deltaIndex++;
            yield return llmDelta;
        }
        // Signal LLM completion to the client
        await Clients.Caller.SendAsync("LLMComplete", new { segmentId, usage = new { } }, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Handles a single audio input (base64-encoded WAV) from the client, runs STT, LLM, and TTS, and streams results back.
    /// </summary>
    public async Task AudioInput(string base64Wav)
    {
        var sessionState = GetSessionStateOrThrow();
        if (string.IsNullOrEmpty(base64Wav)) return;
        var wavBytes = Convert.FromBase64String(base64Wav);

        // Fast transcription path (HTTP). Process end-to-end in one go.
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(sessionState.Cancellation.Token);
        var segmentId = Guid.NewGuid().ToString("N");
        var language = "en-US"; // TODO: Make configurable per session

        // Speech-to-text (Azure Speech)
        var userText = await _fastTranscriber.TranscribeAsync(wavBytes, language, linkedCts.Token);
        if (!string.IsNullOrWhiteSpace(userText))
        {
            // Save user message to conversation memory if conversationId is set
            if (!string.IsNullOrWhiteSpace(sessionState.ConversationId))
            {
                _conversationMemory.AppendUserMessage(sessionState.ConversationId!, userText);
            }

            // Send final transcription to client
            await Clients.Caller.SendAsync("FinalTranscription", new { segmentId, text = userText, isFinal = true });

            var llmPrompt =
                """
                You are a voice assistant created by Kindred.
                Your interface with users will be voice.
                You should use short and concise responses, and avoid usage of unpronounceable punctuation.
                You were created as a demo to showcase the capabilities of Kindred's agents framework.
                """;

            // LLM: Stream response tokens
            var llmDeltas = _llmOrchestrator.StreamResponseAsync(
                sessionState.ConversationId,
                llmPrompt,
                userText,
                linkedCts.Token);

            // TTS: Stream audio from ElevenLabs as LLM tokens arrive
            var ttsAudioStream = _ttsStreamer.StreamFromPartialTextAsync(
                StreamAndFanoutDeltas(sessionState, segmentId, llmDeltas, linkedCts.Token),
                linkedCts.Token);

            // Stream TTS audio chunks to client as soon as they arrive
            await foreach (var (audioBase64, sequenceNumber) in ttsAudioStream.WithCancellation(linkedCts.Token))
            {
                await Clients.Caller.SendAsync("TTSChunk", audioBase64, new { segmentId, seq = sequenceNumber });
            }

            // Notify client that playback is complete
            await Clients.Caller.SendAsync("PlaybackComplete", new { segmentId });
        }
    }

    /// <summary>
    /// Retrieves the session state for the current connection or throws if not initialized.
    /// </summary>
    private SessionState GetSessionStateOrThrow()
    {
        if (!_connectionIdToSessionState.TryGetValue(Context.ConnectionId, out var sessionState))
        {
            throw new HubException("Session not initialized. Call Join first.");
        }
        return sessionState;
    }

    /// <summary>
    /// Holds per-session state for a SignalR connection.
    /// </summary>
    private sealed class SessionState(string sessionId) : IDisposable
    {
        public string SessionId { get; } = sessionId;
        public string? ConversationId { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken CancellationToken => Cancellation.Token;

        public void Cancel()
        {
            try { Cancellation.Cancel(); } catch { }
        }

        public void Dispose()
        {
            Cancel();
            Cancellation.Dispose();
        }
    }
}
