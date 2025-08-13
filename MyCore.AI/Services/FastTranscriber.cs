using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MyCore.AI.Services;

public class FastTranscriber(IConfiguration configuration, IHttpClientFactory httpClientFactory)
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly string _region = configuration["AzureSpeech:Region"] ?? throw new InvalidOperationException("AzureSpeech:Region is required");
    private readonly string _key = configuration["AzureSpeech:Key"] ?? throw new InvalidOperationException("AzureSpeech:Key is required");
    private readonly string _defaultLocale = configuration["AzureSpeech:Language"] ?? "en-US";

  public async Task<string> TranscribeAsync(byte[] wavMono16k, string? locale, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"https://{_region}.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe?api-version=2024-11-15";

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavMono16k);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
        form.Add(fileContent, "audio", "audio.wav");

        var defObj = new { locales = new[] { string.IsNullOrWhiteSpace(locale) ? _defaultLocale : locale } };
        var defJson = JsonSerializer.Serialize(defObj);
        var defContent = new StringContent(defJson, Encoding.UTF8, "application/json");
        form.Add(defContent, "definition");

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Ocp-Apim-Subscription-Key", _key);
        req.Content = form;

        using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        
        // Prefer combinedPhrases[0].text if available
        if (root.TryGetProperty("combinedPhrases", out var combined) && combined.ValueKind == JsonValueKind.Array && combined.GetArrayLength() > 0)
        {
            var first = combined[0];
            if (first.TryGetProperty("text", out var textEl))
            {
                return textEl.GetString() ?? string.Empty;
            }
        }

        // Fallback to concat of phrases[].text
        if (root.TryGetProperty("phrases", out var phrases) && phrases.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var p in phrases.EnumerateArray())
            {
                if (p.TryGetProperty("text", out var t))
                {
                    var s = t.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(s);
                    }
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }
}
