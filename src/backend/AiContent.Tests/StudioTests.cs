using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiContent.Application;
using AiContent.Contracts;
using AiContent.Domain;
using AiContent.Infrastructure;
using AiContent.Infrastructure.Persistence;
using AiContent.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AiContent.Tests;

public class ChangeSetTests
{
    [Fact]
    public void CoverSelectionRejectsTimelineOps()
    {
        var change = new ProposedChangeSet("cover", true, true, "nope", [new EditOperation("delete_range", From: 1, To: 2)], "h", null, null, null);
        var ex = Assert.Throws<StudioException>(() => ChangeSetValidator.Validate(change, new SelectionDto("cover", null, null, null, null, null)));
        Assert.Equal("scope_violation", ex.Code);
    }

    [Fact]
    public void RangeOutsideSelectionIsRejected()
    {
        var change = new ProposedChangeSet("range", true, false, "cut", [new EditOperation("delete_range", From: 10, To: 12)], null, null, null, null);
        var ex = Assert.Throws<StudioException>(() => ChangeSetValidator.Validate(change, new SelectionDto("range", 1, 3, null, null, null)));
        Assert.Equal("scope_violation", ex.Code);
    }

    [Fact]
    public void RangeInsideSelectionIsAccepted()
    {
        var change = new ProposedChangeSet("range", true, false, "cut", [new EditOperation("delete_range", From: 1.2, To: 2.4)], null, null, null, null);
        ChangeSetValidator.Validate(change, new SelectionDto("range", 1, 3, null, null, null));
    }

    [Fact]
    public void UnknownOperationIsRejected()
    {
        var change = new ProposedChangeSet("range", true, false, "bad", [new EditOperation("shell", From: 1, To: 2)], null, null, null, null);
        Assert.Throws<StudioException>(() => ChangeSetValidator.Validate(change, new SelectionDto("range", 1, 2, null, null, null)));
    }

    [Fact]
    public async Task AllowlistMatchesVendoredEngine()
    {
        var root = RepoRoot();
        var psi = new ProcessStartInfo("node", "--input-type=module")
        {
            WorkingDirectory = Path.Combine(root, "video-engine/ai-edit-studio"),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        await process.StandardInput.WriteAsync("""
            import { agentTools, OPERATIONS } from './src/agent-tools.mjs';
            process.stdout.write(JSON.stringify({ tools: agentTools.map(t => t.name), operations: OPERATIONS }));
            """);
        process.StandardInput.Close();
        var json = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        using var doc = JsonDocument.Parse(json);
        var tools = doc.RootElement.GetProperty("tools").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
        var ops = doc.RootElement.GetProperty("operations").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
        Assert.Equal(tools, StudioOperations.ToolAllowlist.ToHashSet());
        Assert.Equal(ops, StudioOperations.All.ToHashSet());
        Assert.Equal(59, tools.Count);
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "video-engine", "UPSTREAM_PIN.json"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }
}

public class JobLeaseTests
{
    [Fact]
    public async Task ExpiredLeaseIsReclaimedThenExhausted()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var user = new AppUser { Id = Guid.NewGuid(), Email = "a@example.com", DisplayName = "A", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow };
        var project = new Project { Id = Guid.NewGuid(), OwnerId = user.Id, Title = "t", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Brief = new ProjectBrief() };
        db.Users.Add(user);
        db.Projects.Add(project);
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = JobKind.Generate,
            Status = JobStatus.Running,
            Stage = "render",
            Attempt = 1,
            MaxAttempts = 2,
            LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-5),
            LockedBy = "dead",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        var queue = new PostgresJobQueue(db);
        var claimed = await queue.ClaimAsync("worker-b", CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(2, claimed!.Attempt);
        Assert.Equal("worker-b", claimed.LockedBy);

        await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-1))
            .SetProperty(j => j.Status, JobStatus.Running));
        var again = await queue.ClaimAsync("worker-c", CancellationToken.None);
        Assert.Null(again);
        var stored = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Failed, stored.Status);
        Assert.Contains("retry budget", stored.Error);
    }
}

public class ProviderTests
{
    [Fact]
    public async Task UnavailableModelDoesNotCallAnotherHost()
    {
        var hits = 0;
        var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:0/");
        // HttpListener needs a concrete port.
        listener.Close();
        var port = FreePort();
        listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            Interlocked.Increment(ref hits);
            var body = Encoding.UTF8.GetBytes("""{"error":{"code":"model_not_found","message":"no such model"}}""");
            ctx.Response.StatusCode = 404;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        });
        var options = new StudioOptions { Provider = "openai", Model = "gpt-6.1-luna", ApiKey = "test-key", BaseUrl = $"http://127.0.0.1:{port}/v1", MaxRetries = 0 };
        var client = new OpenAiCompatibleChatClient(options, new HttpClient(), NullLogger.Instance);
        var ex = await Assert.ThrowsAsync<StudioException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: CancellationToken.None));
        await serve;
        listener.Close();
        Assert.Equal("model_unavailable", ex.Code);
        Assert.Equal(1, hits);
        Assert.Contains("gpt-6.1-luna", ex.Message);
    }

    [Fact]
    public async Task AgentFrameworkRecordsTheTypedPlan()
    {
        var options = new StudioOptions { Provider = "openai", Model = "scripted", ApiKey = "test-key", MaxToolCalls = 4 };
        var orchestrator = new MicrosoftAgentOrchestrator(options, new HttpClientFactoryStub(), NullLogger<MicrosoftAgentOrchestrator>.Instance);
        orchestrator.UseChatClient(new ScriptedChatClient());
        var plan = await orchestrator.PlanAsync(new PlanRequest("Title", "موضوع", "Explain growth", "founders", "educational", "fa", "reel", 15, "Follow", "رشد"), CancellationToken.None);
        Assert.Equal("رشد ۳۵٪", plan.Hook);
        Assert.True(plan.FromModel);
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private int _calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
            {
                var json = """{"hook":"رشد ۳۵٪","message":"growth","title":"Title","caption":"caption","hashtags":["#رشد"],"coverHeadline":"رشد","look":"cinema","captionStyle":"bold","captionAnimation":"highlight","rationale":"scripted","fromModel":false}""";
                var call = new FunctionCallContent("c1", "SubmitPlan", new Dictionary<string, object?> { ["planJson"] = json });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "recorded")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text ?? "");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

public class PersianRenderTests
{
    [Fact]
    public async Task LibassRendersPersianAndMixedText()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, "base.mp4");
        var plain = Path.Combine(dir, "plain.png");
        var burned = Path.Combine(dir, "burned.png");
        await Run("ffmpeg", ["-y", "-f", "lavfi", "-i", "color=c=0x1C1917:s=1080x1920:d=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", video]);
        var ass = Path.Combine(dir, "fa.ass");
        await File.WriteAllTextAsync(ass, """
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 1080
            PlayResY: 1920
            WrapStyle: 0

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Fa,Noto Naskh Arabic,72,&H00E6EFF4,&H000000FF,&H0019191C,&H64000000,-1,0,0,0,100,100,0,0,1,3,0,2,80,80,240,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:01.80,Fa,,0,0,0,,رشد ۳۵٪ در فروش Growth
            """);
        var fonts = Directory.Exists("/usr/share/fonts/truetype/noto") ? "/usr/share/fonts/truetype/noto" : "/usr/share/fonts";
        var filter = $"subtitles={ass}:fontsdir={fonts}";
        await Run("ffmpeg", ["-y", "-ss", "0.4", "-i", video, "-frames:v", "1", "-update", "1", plain]);
        var burnedLog = await Run("ffmpeg", ["-y", "-ss", "0.4", "-i", video, "-frames:v", "1", "-update", "1", "-vf", filter, burned]);
        var a = await File.ReadAllBytesAsync(plain);
        var b = await File.ReadAllBytesAsync(burned);
        Assert.True(!SHA256.HashData(a).SequenceEqual(SHA256.HashData(b)), "Persian glyphs did not change the frame.\n" + filter + "\n" + burnedLog);
        Assert.Contains("رشد", await File.ReadAllTextAsync(ass));
    }

    private static async Task<string> Run(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var err = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, err);
        return err;
    }
}

public class ApiIsolationTests : IClassFixture<StudioApp>
{
    private readonly StudioApp _app;
    public ApiIsolationTests(StudioApp app) => _app = app;

    [Fact]
    public async Task ProjectsAreIsolatedAndWizardPersistsTheme()
    {
        var a = await _app.Login("ada@example.com", "Ada Editor");
        var b = await _app.Login("behzad@example.com", "Behzad");
        var kits = await a.GetFromJsonAsync<List<BrandKitDto>>("/api/v1/brand-kits");
        Assert.NotNull(kits);
        Assert.Contains(kits!, k => k.ThemeKey == "cinematic");
        var kit = kits!.First(k => k.ThemeKey == "cinematic");
        var created = await a.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("رشد فروش", "رشد فروش", "Show the 35 percent story", "founders", "cinematic", "fa", "reel", 12, "دنبال کنید", kit.Id));
        var project = await created.Content.ReadFromJsonAsync<ProjectDetailDto>();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal("fa", project!.Brief.Language);
        Assert.Contains("Cinematic", project.Brand?.Name);
        var denied = await b.GetAsync($"/api/v1/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var own = await a.GetAsync($"/api/v1/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }
}

public class EnginePipelineTests
{
    [Fact]
    public async Task UploadGenerateCorrectCoverAndExport()
    {
        var root = ChangeSetTests.RepoRoot();
        var work = Path.Combine(Path.GetTempPath(), "e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var clip = Path.Combine(work, "clip.mp4");
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var arg in new[] { "-y", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=25:duration=8", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=8", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", clip })
            psi.ArgumentList.Add(arg);
        using (var ffmpeg = Process.Start(psi)!)
        {
            await ffmpeg.WaitForExitAsync();
            Assert.Equal(0, ffmpeg.ExitCode);
        }
        var words = Path.Combine(work, "words.json");
        await File.WriteAllTextAsync(words, """[{"start":0.4,"end":1.4,"text":"رشد"},{"start":1.4,"end":2.6,"text":"Growth"},{"start":2.6,"end":3.6,"text":"۳۵٪"}]""");
        var data = Path.Combine(work, "engine");
        var port = FreePort();
        var start = new ProcessStartInfo("node", "server.mjs")
        {
            WorkingDirectory = Path.Combine(root, "video-engine/ai-edit-studio"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["GAME_TRAILER_STUDIO_DATA_ROOT"] = data;
        start.Environment["GAME_TRAILER_STUDIO_PORT"] = port.ToString();
        var engine = Process.Start(start)!;
        using var app = new StudioApp(new Dictionary<string, string?>
        {
            ["ENGINE_BASE_URL"] = $"http://127.0.0.1:{port}",
            ["ENGINE_DATA_ROOT"] = data,
            ["RUN_WORKER_IN_API"] = "true",
            ["AI_PROVIDER"] = "fixture",
            ["AI_TRANSCRIPTION_PROVIDER"] = "fixture",
        });
        try
        {
            await WaitHealth(port);
            var client = await app.Login("editor@example.com", "Editor");
            var kits = await client.GetFromJsonAsync<List<BrandKitDto>>("/api/v1/brand-kits");
            var kit = kits!.First(k => k.ThemeKey == "educational");
            var created = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Growth story", "رشد فروش", "Explain a 35 percent increase", "founders", "educational", "fa", "reel", 8, "دنبال کنید", kit.Id));
            var project = await created.Content.ReadFromJsonAsync<ProjectDetailDto>();
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            await Upload(client, project!.Id, clip, "SourceVideo", "video/mp4");
            await Upload(client, project.Id, words, "TranscriptFile", "application/json");
            var generate = await client.PostAsync($"/api/v1/projects/{project.Id}/generate", new StringContent(""));
            Assert.Equal(HttpStatusCode.Accepted, generate.StatusCode);
            project = await WaitStatus(client, project.Id, ["ReadyForReview", "Failed"], TimeSpan.FromMinutes(3));
            Assert.Equal("ReadyForReview", project.Status);
            var video = project.Assets.Last(a => a.Kind == "DerivedVideo");
            var before = await Sha(client, video.DownloadUrl!);
            var probe = await Probe(await Download(client, video.DownloadUrl!));
            Assert.Equal("h264", probe.Video);
            Assert.Equal("aac", probe.Audio);
            Assert.True(probe.Width > 0 && probe.Height > probe.Width);

            var cover = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/corrections", new CorrectionRequest(
                "Make only the cover more professional; keep the video unchanged.",
                new SelectionDto("cover", null, null, null, null, project.SelectedCoverId),
                project.CurrentRevisionNumber));
            Assert.Equal(HttpStatusCode.Accepted, cover.StatusCode);
            project = await WaitStatus(client, project.Id, ["ReadyForReview", "Failed"], TimeSpan.FromMinutes(2));
            Assert.Equal("ReadyForReview", project.Status);
            var same = project.Assets.Last(a => a.Kind == "DerivedVideo");
            Assert.Equal(video.Id, same.Id);
            Assert.Equal(before, await Sha(client, same.DownloadUrl!));
            Assert.Contains(project.Covers, c => c.Selected);

            var fix = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/corrections", new CorrectionRequest(
                "Remove the pause at 00:01-00:02.",
                new SelectionDto("range", 1, 2, null, null, null),
                project.CurrentRevisionNumber));
            Assert.Equal(HttpStatusCode.Accepted, fix.StatusCode);
            var revised = await WaitStatus(client, project.Id, ["ReadyForReview", "Failed"], TimeSpan.FromMinutes(3));
            Assert.Equal("ReadyForReview", revised.Status);
            Assert.True(revised.CurrentRevisionNumber > project.CurrentRevisionNumber);
            var nextVideo = revised.Assets.Last(a => a.Kind == "DerivedVideo");
            Assert.NotEqual(video.Id, nextVideo.Id);

            var export = await client.PostAsync($"/api/v1/projects/{revised.Id}/export", new StringContent(""));
            Assert.Equal(HttpStatusCode.Accepted, export.StatusCode);
            var exported = await WaitStatus(client, revised.Id, ["Exported", "Failed"], TimeSpan.FromMinutes(2));
            Assert.Equal("Exported", exported.Status);
            Assert.Contains(exported.Assets, a => a.Kind == "CaptionFile");
            Assert.Contains(exported.Assets, a => a.Kind == "CoverImage");
            Assert.Contains(exported.Assets, a => a.Kind == "ExportManifest");
        }
        finally
        {
            if (!engine.HasExited) engine.Kill(entireProcessTree: true);
        }
    }

    private static async Task WaitHealth(int port)
    {
        using var http = new HttpClient();
        for (var i = 0; i < 40; i++)
        {
            try
            {
                var res = await http.GetAsync($"http://127.0.0.1:{port}/api/health");
                if (res.IsSuccessStatusCode) return;
            }
            catch { /* engine still binding */ }
            await Task.Delay(250);
        }
        throw new TimeoutException("Engine health check failed.");
    }

    private static async Task<ProjectDetailDto> WaitStatus(HttpClient client, Guid id, string[] states, TimeSpan budget)
    {
        var deadline = DateTimeOffset.UtcNow.Add(budget);
        ProjectDetailDto? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await client.GetFromJsonAsync<ProjectDetailDto>($"/api/v1/projects/{id}");
            if (last is not null && states.Contains(last.Status))
            {
                if (last.Status == "Failed") throw new Xunit.Sdk.XunitException(last.StatusDetail + " " + string.Join(" | ", last.Warnings));
                return last;
            }
            await Task.Delay(1000);
        }
        throw new TimeoutException(last?.StatusDetail ?? "timed out");
    }

    private static async Task Upload(HttpClient client, Guid id, string path, string kind, string type)
    {
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(path));
        bytes.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(bytes, "file", Path.GetFileName(path));
        form.Add(new StringContent(kind), "kind");
        var res = await client.PostAsync($"/api/v1/projects/{id}/assets", form);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    private static async Task<string> Download(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        res.EnsureSuccessStatusCode();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4");
        await File.WriteAllBytesAsync(path, await res.Content.ReadAsByteArrayAsync());
        return path;
    }

    private static async Task<string> Sha(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        res.EnsureSuccessStatusCode();
        return Convert.ToHexString(SHA256.HashData(await res.Content.ReadAsByteArrayAsync()));
    }

    private static async Task<(string? Video, string? Audio, int Width, int Height)> Probe(string path)
    {
        var psi = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error"); psi.ArgumentList.Add("-print_format"); psi.ArgumentList.Add("json"); psi.ArgumentList.Add("-show_streams"); psi.ArgumentList.Add(path);
        using var process = Process.Start(psi)!;
        var json = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        using var doc = JsonDocument.Parse(json);
        string? video = null, audio = null;
        var width = 0; var height = 0;
        foreach (var stream in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            var kind = stream.GetProperty("codec_type").GetString();
            if (kind == "video") { video = stream.GetProperty("codec_name").GetString(); width = stream.GetProperty("width").GetInt32(); height = stream.GetProperty("height").GetInt32(); }
            if (kind == "audio") audio = stream.GetProperty("codec_name").GetString();
        }
        return (video, audio, width, height);
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public sealed class StudioApp : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _extra;
    public StudioApp() : this(new Dictionary<string, string?>()) { }
    internal StudioApp(Dictionary<string, string?> extra) => _extra = extra;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        builder.UseSetting("DATABASE_PROVIDER", "Sqlite");
        builder.UseSetting("SQLITE_PATH", Path.Combine(root, "app.db"));
        builder.UseSetting("STORAGE_PROVIDER", "local");
        builder.UseSetting("STORAGE_PATH", Path.Combine(root, "storage"));
        builder.UseSetting("JWT_SIGNING_KEY", "test-signing-key-32-characters-minimum");
        builder.UseSetting("AI_PROVIDER", "fixture");
        builder.UseSetting("SEED_DEMO_USER", "false");
        builder.UseSetting("RUN_WORKER_IN_API", "false");
        builder.UseSetting("ENGINE_BASE_URL", "");
        foreach (var (key, value) in _extra) builder.UseSetting(key, value);
        builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
    }

    public async Task<HttpClient> Login(string email, string name)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Studio-pass-1", displayName = name });
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }
}
