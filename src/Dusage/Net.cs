using System.Net;
using System.Text.Json;

namespace Dusage;

static class Net
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dusage/" + AppInfo.Version);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    public static async Task<JsonDocument> GetJsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new UsageException("Can't reach the server — offline?", UsageException.Recheck);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status == 429)
            {
                var wait = RetryAfter(response) ?? TimeSpan.FromMinutes(10);
                throw new UsageException($"Rate limited — next try in {Fmt.Span(wait)}.", wait, status);
            }
            if (!response.IsSuccessStatusCode)
                throw new UsageException($"Server answered HTTP {status}.", null, status);

            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                throw new UsageException("Server sent something that isn't JSON.");
            }
        }
    }

    static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return wait is { } w ? TimeSpan.FromSeconds(Math.Clamp(w.TotalSeconds, 60, 3600)) : null;
    }

    /// <summary>Reads a small JSON file that its owner (Claude Code, Codex) may be rewriting at this very moment.</summary>
    public static JsonDocument? ReadJsonFile(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonDocument.Parse(file);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception e) when (attempt < 2 && e is IOException or UnauthorizedAccessException or JsonException)
            {
                Thread.Sleep(200);
            }
        }
    }

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double? Num(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static bool? Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
