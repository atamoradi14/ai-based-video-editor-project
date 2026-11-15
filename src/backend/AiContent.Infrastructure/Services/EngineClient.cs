using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiContent.Application;
using AiContent.Domain;

namespace AiContent.Infrastructure.Services;

public sealed class AiEditStudioClient(StudioOptions options, IHttpClientFactory http, ILogger<AiEditStudioClient> logger) : IAiEditStudioClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.EngineBaseUrl);

    public async Task<JsonElement> CallAsync(string tool, object? arguments, CancellationToken ct)
    {
        if (!StudioOperations.ToolAllowlist.Contains(tool))
            throw new StudioException("tool_not_allowed", $"'{tool}' is not in the AI Edit Studio allowlist.");
        if (!IsConfigured)
            throw new StudioException("engine_unconfigured", "ENGINE_BASE_URL is empty, so the video engine cannot be called.");
        var client = http.CreateClient("engine");
        client.Timeout = TimeSpan.FromMinutes(10);
        var payload = JsonSerializer.Serialize(arguments ?? new { }, Json);
        using var req = new HttpRequestMessage(HttpMethod.Post, options.EngineBaseUrl.TrimEnd('/') + "/api/agent/tools/" + tool)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("X-Studio-Client", "ai-content");
        if (!string.IsNullOrWhiteSpace(options.EngineToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.EngineToken);
        var sw = Stopwatch.StartNew();
        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        logger.LogInformation("engine tool {Tool} status {Status} in {Ms}ms", tool, (int)res.StatusCode, sw.ElapsedMilliseconds);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
        catch { throw new StudioException("engine_invalid", $"{tool} returned non-JSON (HTTP {(int)res.StatusCode})."); }
        using (doc)
        {
            if (!res.IsSuccessStatusCode || (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False))
            {
                var code = "engine_error";
                var message = $"{tool} failed with HTTP {(int)res.StatusCode}.";
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.TryGetProperty("code", out var c)) code = c.GetString() ?? code;
                    if (error.TryGetProperty("message", out var m)) message = m.GetString() ?? message;
                }
                throw new StudioException(code.ToLowerInvariant(), message);
            }
            if (doc.RootElement.TryGetProperty("result", out var result))
                return result.Clone();
            return doc.RootElement.Clone();
        }
    }

    public async Task<JsonElement> WaitUntilIdleAsync(string projectId, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(budget);
        JsonElement last = default;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var waited = await CallAsync("studio_wait", new { projectId, until = "idle", timeoutSeconds = 25 }, ct);
            last = ProjectOf(waited);
            var status = Text(last, "status");
            if (status is "ready" or "failed" or "draft") return last;
            if (status is not ("analyzing" or "rendering" or "previewing")) return last;
        }
        throw new StudioException("engine_timeout", "The video engine did not finish before the job budget elapsed.");
    }

    public async Task<string?> CopyExportAsync(JsonElement project, IStorageService storage, CancellationToken ct)
    {
        if (!project.TryGetProperty("exports", out var exports) || exports.ValueKind != JsonValueKind.Array)
            return null;
        JsonElement? chosen = null;
        foreach (var item in exports.EnumerateArray())
        {
            if (item.TryGetProperty("output", out var output) && output.GetString()?.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) == true)
                chosen = item;
        }
        if (chosen is null) return null;
        var path = chosen.Value.GetProperty("output").GetString()!;
        EnsureUnderDataRoot(path);
        await using var stream = File.OpenRead(path);
        var stored = await storage.SaveAsync(stream, "video/mp4", ".mp4", ct);
        return stored.Key;
    }

    private void EnsureUnderDataRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.IsNullOrWhiteSpace(options.EngineDataRoot))
        {
            var root = Path.GetFullPath(options.EngineDataRoot);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new StudioException("engine_path", "The engine returned a file outside ENGINE_DATA_ROOT.");
        }
        if (!File.Exists(full)) throw new StudioException("export_missing", "The engine export path does not exist on this machine.");
    }

    public static JsonElement ProjectOf(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("project", out var project)) return project;
        return result;
    }

    public static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static int IntAt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : 0;
}
