using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MyCore.AI.Services;

public class TtsStreamer(IConfiguration configuration)
{
    private readonly string _apiKey = configuration["ElevenLabs:ApiKey"] ?? throw new InvalidOperationException("ElevenLabs:ApiKey is required");
    private readonly string _voiceId = configuration["ElevenLabs:VoiceId"] ?? throw new InvalidOperationException("ElevenLabs:VoiceId is required");

  /// <summary>
  /// Streams audio chunks from ElevenLabs TTS as partial LLM text is received.
  /// </summary>
  public async IAsyncEnumerable<(string audioBase64, int sequenceNumber)>
        StreamFromPartialTextAsync(
            IAsyncEnumerable<string> partialTextStream,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var ttsWebSocketUri = new Uri($"wss://api.elevenlabs.io/v1/text-to-speech/{_voiceId}/stream-input?model_id=eleven_flash_v2_5&output_format=pcm_16000");

        using var webSocket = new ClientWebSocket();
        webSocket.Options.SetRequestHeader("xi-api-key", _apiKey);
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);

        await webSocket.ConnectAsync(ttsWebSocketUri, cancellationToken).ConfigureAwait(false);

        // Initial payload MUST be the first message with voice_settings (ElevenLabs requirement)
        var initialPayload = new
        {
            text = " ",
            voice_settings = new { stability = 0.75, similarity_boost = 1f, style = 0.0, speed = 1 }
        };
        var initialPayloadJson = JsonSerializer.Serialize(initialPayload);
        await webSocket.SendAsync(Encoding.UTF8.GetBytes(initialPayloadJson), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

        var receiveAudioStream = ReceiveAudioChunksAsync(webSocket, cancellationToken);
        var sendTextTask = SendPartialTextAndThenCloseAsync(webSocket, partialTextStream, cancellationToken);

        await foreach (var audioChunk in receiveAudioStream.WithCancellation(cancellationToken))
        {
            yield return audioChunk;
        }

        await sendTextTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Receives audio chunks from the ElevenLabs TTS WebSocket stream.
    /// </summary>
    private static async IAsyncEnumerable<(string audioBase64, int sequenceNumber)> ReceiveAudioChunksAsync(
        ClientWebSocket webSocket,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int sequenceNumber = 0;
        var receiveBuffer = new ArraySegment<byte>(new byte[32768]); // 32KB buffer for receiving data
        var memoryStream = new MemoryStream();

        // Main receive loop: keep reading as long as the WebSocket is open and not cancelled
        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            memoryStream.SetLength(0); // Reset the memory stream for the next message
            WebSocketReceiveResult receiveResult;

            // WebSocket messages may be fragmented, so we need to reassemble them until EndOfMessage is true
            do
            {
                receiveResult = await webSocket.ReceiveAsync(receiveBuffer, cancellationToken).ConfigureAwait(false);
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    // If the server initiates a close, exit the loop and stop yielding audio
                    yield break;
                }
                // Write the received bytes into the memory stream
                memoryStream.Write(receiveBuffer.Array!, receiveBuffer.Offset, receiveResult.Count);
            }
            while (!receiveResult.EndOfMessage);

            // Only process text messages (audio chunks are sent as base64 in JSON)
            if (receiveResult.MessageType == WebSocketMessageType.Text)
            {
                var jsonString = Encoding.UTF8.GetString(memoryStream.ToArray());

                bool isFinal = false;
                // Try to extract the "audio" base64 string from the JSON message
                if (TryExtractAudioBase64(jsonString, out var audioBase64))
                {
                    // Yield the audio chunk to the caller, incrementing the sequence number
                    yield return (audioBase64!, sequenceNumber++);
                }

                try
                {
                    // Parse the JSON to check if this is the final message (isFinal: true)
                    using var doc = JsonDocument.Parse(jsonString);
                    if (doc.RootElement.TryGetProperty("isFinal", out var isFinalProp) && isFinalProp.ValueKind == JsonValueKind.True)
                    {
                        isFinal = true;
                    }
                }
                catch { /* ignore parse issues */ }

                if (isFinal)
                {
                    // If this is the final message, break out of the loop and stop yielding audio
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Extracts the "audio" base64 string from a JSON message.
    /// </summary>
    private static bool TryExtractAudioBase64(string jsonString, out string? audioBase64)
    {
        audioBase64 = null;
        try
        {
            using var jsonDoc = JsonDocument.Parse(jsonString);
            if (jsonDoc.RootElement.TryGetProperty("audio", out var audioProperty) && audioProperty.ValueKind == JsonValueKind.String)
            {
                var audioString = audioProperty.GetString();
                if (!string.IsNullOrEmpty(audioString))
                {
                    audioBase64 = audioString;
                    return true;
                }
            }
        }
        catch
        {
            // Swallow parse errors; not all messages contain audio.
        }
        return false;
    }

    /// <summary>
    /// Sends partial LLM text to the ElevenLabs TTS WebSocket stream, chunking and triggering generation as needed.
    /// </summary>
    private static async Task SendPartialTextAndThenCloseAsync(
        ClientWebSocket webSocket,
        IAsyncEnumerable<string> partialTextStream,
        CancellationToken cancellationToken)
    {
        await foreach (var partialDelta in partialTextStream.WithCancellation(cancellationToken))
        {
            if (string.IsNullOrEmpty(partialDelta)) continue;
            var payloadJson = JsonSerializer.Serialize(new { text = partialDelta });
            var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
            await webSocket.SendAsync(payloadBytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }

        var endOfInputPayload = Encoding.UTF8.GetBytes("{\"text\": \"\"}");
        await webSocket.SendAsync(endOfInputPayload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }
}
