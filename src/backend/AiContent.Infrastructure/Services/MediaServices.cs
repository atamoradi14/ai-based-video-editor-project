using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiContent.Application;
using AiContent.Domain;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AiContent.Infrastructure.Services;

public sealed class LocalStorageService(StudioOptions options) : IStorageService
{
    public string Provider => "local";
    private string Root => Path.GetFullPath(options.StoragePath);

    public async Task<StoredObject> SaveAsync(Stream content, string contentType, string extension, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var key = $"{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}{ext}";
        var path = Full(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
        return new StoredObject(key, file.Length, contentType);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct) => Task.FromResult<Stream>(File.OpenRead(Full(key)));

    public Task<string> MaterializeAsync(string key, CancellationToken ct) => Task.FromResult(Full(key));

    public string CreateDownloadUrl(Guid assetId, string key, TimeSpan ttl)
    {
        var exp = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
        var sig = Sign(assetId, exp);
        var prefix = string.IsNullOrWhiteSpace(options.PublicApiUrl) ? "" : options.PublicApiUrl.TrimEnd('/');
        return $"{prefix}/api/v1/media/{assetId}?exp={exp}&sig={sig}";
    }

    public bool VerifyDownload(Guid assetId, long exp, string sig)
    {
        if (exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        var expected = Sign(assetId, exp);
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(sig ?? "");
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private string Sign(Guid assetId, long exp)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SigningKey()));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{assetId:N}.{exp}"))).ToLowerInvariant();
    }

    private string SigningKey() => string.IsNullOrWhiteSpace(options.JwtSigningKey) ? "dev-only-signing-key-change-me" : options.JwtSigningKey;

    private string Full(string key)
    {
        var path = Path.GetFullPath(Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && path != Root)
            throw new StudioException("invalid_path", "Storage key escaped the storage root.");
        return path;
    }
}

public sealed class S3StorageService(StudioOptions options) : IStorageService
{
    public string Provider => "s3";
    private IAmazonS3 Client => new AmazonS3Client(options.S3AccessKey, options.S3SecretKey, new AmazonS3Config
    {
        ServiceURL = string.IsNullOrWhiteSpace(options.S3Endpoint) ? null : options.S3Endpoint,
        ForcePathStyle = !string.IsNullOrWhiteSpace(options.S3Endpoint),
        AuthenticationRegion = options.S3Region,
        RegionEndpoint = string.IsNullOrWhiteSpace(options.S3Endpoint) ? RegionEndpoint.GetBySystemName(options.S3Region) : RegionEndpoint.USEast1,
    });

    public async Task<StoredObject> SaveAsync(Stream content, string contentType, string extension, CancellationToken ct)
    {
        Require();
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var key = $"{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}{ext}";
        await Client.PutObjectAsync(new PutObjectRequest { BucketName = options.S3Bucket, Key = key, InputStream = content, ContentType = contentType }, ct);
        return new StoredObject(key, content.CanSeek ? content.Length : 0, contentType);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        Require();
        var obj = await Client.GetObjectAsync(options.S3Bucket, key, ct);
        var ms = new MemoryStream();
        await obj.ResponseStream.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    public async Task<string> MaterializeAsync(string key, CancellationToken ct)
    {
        var stream = await OpenReadAsync(key, ct);
        var path = Path.Combine(Path.GetTempPath(), "aicontent", Guid.NewGuid().ToString("N") + Path.GetExtension(key));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await stream.CopyToAsync(file, ct);
        return path;
    }

    public string CreateDownloadUrl(Guid assetId, string key, TimeSpan ttl)
    {
        Require();
        return Client.GetPreSignedURL(new GetPreSignedUrlRequest { BucketName = options.S3Bucket, Key = key, Expires = DateTime.UtcNow.Add(ttl), Verb = HttpVerb.GET });
    }

    public bool VerifyDownload(Guid assetId, long exp, string sig) => false;

    private void Require()
    {
        if (string.IsNullOrWhiteSpace(options.S3Bucket) || string.IsNullOrWhiteSpace(options.S3AccessKey))
            throw new StudioException("storage_unconfigured", "S3 storage is selected but S3_BUCKET or S3_ACCESS_KEY is empty.");
    }
}

public sealed class AzureBlobStorageService(StudioOptions options) : IStorageService
{
    public string Provider => "azure";

    public async Task<StoredObject> SaveAsync(Stream content, string contentType, string extension, CancellationToken ct)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var key = $"{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}{ext}";
        var blob = Blob(key);
        await blob.UploadAsync(content, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);
        return new StoredObject(key, content.CanSeek ? content.Length : 0, contentType);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await Blob(key).DownloadToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    public async Task<string> MaterializeAsync(string key, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "aicontent", Guid.NewGuid().ToString("N") + Path.GetExtension(key));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await Blob(key).DownloadToAsync(path, ct);
        return path;
    }

    public string CreateDownloadUrl(Guid assetId, string key, TimeSpan ttl)
    {
        var blob = Blob(key);
        var uri = blob.GenerateSasUri(Azure.Storage.Sas.BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(ttl));
        return uri.ToString();
    }

    public bool VerifyDownload(Guid assetId, long exp, string sig) => false;

    private BlobClient Blob(string key)
    {
        if (string.IsNullOrWhiteSpace(options.AzureConnectionString))
            throw new StudioException("storage_unconfigured", "Azure Blob storage is selected but AZURE_STORAGE_CONNECTION_STRING is empty.");
        return new BlobContainerClient(options.AzureConnectionString, options.AzureContainer).GetBlobClient(key);
    }
}

public sealed class FfprobeMediaProbe : IMediaProbe
{
    public async Task<ProbeResult> ProbeAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add(path);
        using var process = Process.Start(psi) ?? throw new StudioException("ffprobe_missing", "ffprobe is not on PATH.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new StudioException("probe_failed", string.IsNullOrWhiteSpace(stderr) ? "ffprobe failed." : stderr.Trim());
        using var doc = JsonDocument.Parse(stdout);
        double? duration = null;
        if (doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            duration = parsed;
        int? width = null, height = null;
        string? video = null, audio = null;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "video" && width is null)
                {
                    video = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                    width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : null;
                    height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : null;
                }
                if (type == "audio" && audio is null)
                    audio = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
            }
        }
        return new ProbeResult(duration, width, height, video, audio, audio is not null, stdout);
    }
}

public sealed class FfmpegCoverComposer : ICoverComposer
{
    private static readonly HashSet<string> Fonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Noto Naskh Arabic", "Noto Sans Arabic", "Noto Sans", "DejaVu Sans"
    };

    public async Task<string> ComposeAsync(CoverComposeRequest request, CancellationToken ct)
    {
        var font = Fonts.Contains(request.Font) ? request.Font : request.RightToLeft ? "Noto Naskh Arabic" : "Noto Sans";
        var assPath = Path.Combine(Path.GetDirectoryName(request.OutputPath)!, Path.GetFileNameWithoutExtension(request.OutputPath) + ".ass");
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
        await File.WriteAllTextAsync(assPath, Ass(request.Headline, font, request.TextColor, request.Primary), ct);
        var fonts = Directory.Exists("/usr/share/fonts/truetype/noto") ? "/usr/share/fonts/truetype/noto" : "/usr/share/fonts";
        var filter = $"scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,drawbox=x=0:y=1180:w=1080:h=740:color={Hex(request.Primary)}@0.78:t=fill,subtitles={Escape(assPath)}:fontsdir={Escape(fonts)}";
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-y", "-ss", request.AtSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", request.VideoPath, "-frames:v", "1", "-vf", filter, request.OutputPath })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new StudioException("ffmpeg_missing", "ffmpeg is not on PATH.");
        var err = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 || !File.Exists(request.OutputPath))
            throw new StudioException("cover_failed", "Cover composition failed. " + Trim(err));
        return request.OutputPath;
    }

    private static string Ass(string headline, string font, string text, string outline)
    {
        var primary = AssColor(text);
        var back = AssColor(outline);
        var safe = headline.Replace("\\", " ").Replace("{", "(").Replace("}", ")").Replace("\n", "\\N");
        return $"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 1080
            PlayResY: 1920
            WrapStyle: 0
            ScaledBorderAndShadow: yes

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Cover,{font},78,{primary},&H000000FF,{back},&H64000000,-1,0,0,0,100,100,0,0,1,4,0,2,90,90,280,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:05.00,Cover,,0,0,0,,{safe}
            """;
    }

    private static string AssColor(string hex)
    {
        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6) h = "F4EFE6";
        return $"&H00{h[4..6]}{h[2..4]}{h[0..2]}".ToUpperInvariant();
    }

    private static string Hex(string hex) => "0x" + (hex.Trim().TrimStart('#').Length == 6 ? hex.Trim().TrimStart('#') : "1C1917");
    private static string Escape(string path) => path.Replace("\\", "\\\\").Replace(":", "\\:").Replace(",", "\\,");
    private static string Trim(string err) => err.Length > 500 ? err[^500..] : err;
}

public sealed class NoImageGenerationService : IImageGenerationService
{
    public bool IsConfigured => false;
    public Task<ImageAttempt> TryEditAsync(string sourcePngPath, string prompt, CancellationToken ct)
        => Task.FromResult(new ImageAttempt(false, null, null));
}

public sealed class OpenAiImageService(StudioOptions options, IHttpClientFactory http) : IImageGenerationService
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ImageProvider) && !string.IsNullOrWhiteSpace(options.ImageModel) && !string.IsNullOrWhiteSpace(options.ImageApiKey);

    public async Task<ImageAttempt> TryEditAsync(string sourcePngPath, string prompt, CancellationToken ct)
    {
        if (!IsConfigured) return new ImageAttempt(false, null, null);
        var client = http.CreateClient("ai");
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(options.ImageModel), "model");
        form.Add(new StringContent(prompt), "prompt");
        await using var file = File.OpenRead(sourcePngPath);
        form.Add(new StreamContent(file), "image", Path.GetFileName(sourcePngPath));
        using var req = new HttpRequestMessage(HttpMethod.Post, options.ImageBaseUrl.TrimEnd('/') + "/images/edits") { Content = form };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ImageApiKey);
        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            return new ImageAttempt(false, null, $"Image provider '{options.ImageProvider}' rejected the edit ({(int)res.StatusCode}). The frame template was kept. {Truncate(body)}");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var b64 = doc.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString();
            if (string.IsNullOrWhiteSpace(b64)) return new ImageAttempt(false, null, "Image provider returned no image. The frame template was kept.");
            var path = Path.Combine(Path.GetTempPath(), "aicontent", Guid.NewGuid().ToString("N") + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(b64), ct);
            return new ImageAttempt(true, path, null);
        }
        catch (Exception ex)
        {
            return new ImageAttempt(false, null, "Image provider response could not be read. The frame template was kept. " + ex.Message);
        }
    }

    private static string Truncate(string s) => s.Length > 240 ? s[..240] : s;
}
