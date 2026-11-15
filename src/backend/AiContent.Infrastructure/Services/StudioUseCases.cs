using System.Text.Json;
using AiContent.Application;
using AiContent.Contracts;
using AiContent.Domain;
using AiContent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AiContent.Infrastructure.Services;

public sealed class StudioUseCases(AppDbContext db, IStorageService storage, IAiEditStudioClient engine, StudioOptions options) : IStudioUseCases
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly PasswordHasher<AppUser> _passwords = new();

    public async Task<AppUser> RegisterAsync(string email, string displayName, string password, CancellationToken ct)
    {
        email = NormalizeEmail(email);
        if (password.Length < 10 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new StudioException("weak_password", "Use at least 10 characters with a letter and a digit.");
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new StudioException("email_taken", "An account with that email already exists.", 409);
        var user = new AppUser { Id = Guid.NewGuid(), Email = email, DisplayName = displayName.Trim(), CreatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = _passwords.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<AppUser> LoginAsync(string email, string password, CancellationToken ct)
    {
        email = NormalizeEmail(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null || _passwords.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
            throw new StudioException("invalid_login", "Email or password is wrong.", 401);
        return user;
    }

    public async Task<AppUser> GetUserAsync(Guid id, CancellationToken ct)
        => await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new StudioException("not_found", "User not found.", 404);

    public async Task<IReadOnlyList<BrandKitDto>> ListBrandKitsAsync(Guid userId, CancellationToken ct)
        => await db.BrandKits.Where(k => k.IsSystem || k.OwnerId == userId).OrderByDescending(k => k.IsSystem).ThenBy(k => k.Name)
            .Select(k => new BrandKitDto(k.Id, k.Name, k.ThemeKey, k.DefinitionJson, k.IsSystem)).ToListAsync(ct);

    public async Task<BrandKitDto> SaveBrandKitAsync(Guid userId, SaveBrandKitRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new StudioException("invalid_brand", "A brand kit needs a name.");
        var kit = new BrandKit
        {
            Id = Guid.NewGuid(),
            OwnerId = userId,
            Name = request.Name.Trim(),
            ThemeKey = string.IsNullOrWhiteSpace(request.ThemeKey) ? "custom" : request.ThemeKey.Trim(),
            DefinitionJson = string.IsNullOrWhiteSpace(request.DefinitionJson) ? "{}" : request.DefinitionJson,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.BrandKits.Add(kit);
        await db.SaveChangesAsync(ct);
        return new BrandKitDto(kit.Id, kit.Name, kit.ThemeKey, kit.DefinitionJson, false);
    }

    public async Task<ProjectDetailDto> CreateProjectAsync(Guid userId, CreateProjectRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) throw new StudioException("invalid_project", "A project needs a title.");
        var kit = await ResolveKit(userId, request.BrandKitId, ct);
        var now = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            OwnerId = userId,
            Title = request.Title.Trim(),
            Status = ProjectStatus.Draft,
            BrandKitId = kit?.Id,
            ThemeSnapshotJson = kit?.DefinitionJson ?? DefaultTheme("minimal"),
            CreatedAt = now,
            UpdatedAt = now,
            Brief = new ProjectBrief
            {
                Subject = request.Subject.Trim(),
                Goal = request.Goal.Trim(),
                Audience = request.Audience.Trim(),
                Tone = string.IsNullOrWhiteSpace(request.Tone) ? "professional" : request.Tone.Trim(),
                Language = string.IsNullOrWhiteSpace(request.Language) ? "auto" : request.Language.Trim(),
                Platform = NormalizePlatform(request.Platform),
                TargetDurationSeconds = request.TargetDurationSeconds <= 0 ? 15 : request.TargetDurationSeconds,
                CallToAction = request.CallToAction,
            },
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return await GetProjectAsync(userId, project.Id, ct);
    }

    public async Task<IReadOnlyList<ProjectSummaryDto>> ListProjectsAsync(Guid userId, string? query, string? status, string? sort, CancellationToken ct)
    {
        var q = db.Projects.AsNoTracking().Where(p => p.OwnerId == userId);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLowerInvariant();
            q = q.Where(p => p.Title.ToLower().Contains(term) || (p.Brief != null && p.Brief.Subject.ToLower().Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ProjectStatus>(status, true, out var parsed))
            q = q.Where(p => p.Status == parsed);
        q = sort == "title" ? q.OrderBy(p => p.Title) : q.OrderByDescending(p => p.UpdatedAt);
        return await q.Select(p => new ProjectSummaryDto(p.Id, p.Title, p.Status.ToString(), p.StatusDetail, p.Brief!.Platform, p.Brief.Language, p.UpdatedAt, p.CurrentRevisionNumber)).ToListAsync(ct);
    }

    public async Task<ProjectDetailDto> GetProjectAsync(Guid userId, Guid projectId, CancellationToken ct)
    {
        var project = await Load(userId, projectId, ct);
        return Map(project);
    }

    public async Task<ProjectDetailDto> UpdateBriefAsync(Guid userId, Guid projectId, BriefDto brief, CancellationToken ct)
    {
        var project = await Load(userId, projectId, ct);
        project.Brief ??= new ProjectBrief { ProjectId = project.Id };
        project.Brief.Subject = brief.Subject.Trim();
        project.Brief.Goal = brief.Goal.Trim();
        project.Brief.Audience = brief.Audience.Trim();
        project.Brief.Tone = brief.Tone.Trim();
        project.Brief.Language = brief.Language.Trim();
        project.Brief.Platform = NormalizePlatform(brief.Platform);
        project.Brief.TargetDurationSeconds = brief.TargetDurationSeconds;
        project.Brief.CallToAction = brief.CallToAction;
        Touch(project);
        await db.SaveChangesAsync(ct);
        return Map(project);
    }

    public async Task<ProjectDetailDto> UpdateThemeAsync(Guid userId, Guid projectId, Guid brandKitId, CancellationToken ct)
    {
        var project = await Load(userId, projectId, ct);
        var kit = await ResolveKit(userId, brandKitId, ct) ?? throw new StudioException("not_found", "Brand kit not found.", 404);
        project.BrandKitId = kit.Id;
        project.ThemeSnapshotJson = kit.DefinitionJson;
        Touch(project);
        await db.SaveChangesAsync(ct);
        return await GetProjectAsync(userId, projectId, ct);
    }

    public async Task<AssetDto> AddAssetAsync(Guid userId, Guid projectId, string kind, string fileName, string contentType, Stream content, CancellationToken ct)
    {
        var project = await Load(userId, projectId, ct);
        var assetKind = ParseKind(kind);
        var safeName = Path.GetFileName(fileName);
        await using var buffer = new MemoryStream();
        var total = 0L;
        var chunk = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(chunk, ct);
            if (read == 0) break;
            total += read;
            if (total > options.UploadMaxBytes) throw new StudioException("file_too_large", "The file exceeds the upload limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        var bytes = buffer.ToArray();
        ValidatePayload(assetKind, safeName, contentType, bytes);
        buffer.Position = 0;
        var stored = await storage.SaveAsync(buffer, contentType, Path.GetExtension(safeName), ct);
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = assetKind,
            StorageKey = stored.Key,
            OriginalFileName = safeName,
            ContentType = contentType,
            SizeBytes = bytes.Length,
            Immutable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Assets.Add(asset);
        if (project.Status == ProjectStatus.Draft) project.Status = ProjectStatus.Uploading;
        Touch(project);
        await db.SaveChangesAsync(ct);
        return MapAsset(asset);
    }

    public async Task<UploadStartResponse> StartUploadAsync(Guid userId, Guid projectId, UploadStartRequest request, CancellationToken ct)
    {
        var project = await Load(userId, projectId, ct);
        if (request.Size <= 0 || request.Size > options.UploadMaxBytes)
            throw new StudioException("file_too_large", "The file exceeds the upload limit.");
        var kind = ParseKind(request.Kind);
        ValidateName(kind, request.FileName);
        var chunk = 5 * 1024 * 1024;
        var session = new UploadSession
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OwnerId = userId,
            FileName = Path.GetFileName(request.FileName),
            ContentType = request.ContentType,
            Kind = kind,
            SizeBytes = request.Size,
            ChunkSize = chunk,
            ChunkCount = (int)Math.Ceiling(request.Size / (double)chunk),
            TempDirectory = Path.Combine(Path.GetFullPath(options.StoragePath), "uploads", Guid.NewGuid().ToString("N")),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(12),
        };
        Directory.CreateDirectory(session.TempDirectory);
        db.Uploads.Add(session);
        await db.SaveChangesAsync(ct);
        return new UploadStartResponse(session.Id, session.ChunkSize, session.ChunkCount);
    }

    public async Task ReceiveChunkAsync(Guid userId, Guid projectId, Guid uploadId, int index, Stream content, CancellationToken ct)
    {
        var session = await db.Uploads.FirstOrDefaultAsync(u => u.Id == uploadId && u.ProjectId == projectId && u.OwnerId == userId, ct)
            ?? throw new StudioException("not_found", "Upload not found.", 404);
        if (session.Completed || session.ExpiresAt < DateTimeOffset.UtcNow) throw new StudioException("upload_closed", "This upload is closed.");
        if (index < 0 || index >= session.ChunkCount) throw new StudioException("invalid_chunk", "Chunk index is out of range.");
        var path = Path.Combine(session.TempDirectory, index.ToString("D6"));
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
        var got = JsonSerializer.Deserialize<List<int>>(session.ReceivedJson) ?? [];
        if (!got.Contains(index)) got.Add(index);
        session.ReceivedJson = JsonSerializer.Serialize(got);
        await db.SaveChangesAsync(ct);
    }

    public async Task<AssetDto> CompleteUploadAsync(Guid userId, Guid projectId, Guid uploadId, CancellationToken ct)
    {
        var session = await db.Uploads.FirstOrDefaultAsync(u => u.Id == uploadId && u.ProjectId == projectId && u.OwnerId == userId, ct)
            ?? throw new StudioException("not_found", "Upload not found.", 404);
        var got = JsonSerializer.Deserialize<List<int>>(session.ReceivedJson) ?? [];
        if (got.Count != session.ChunkCount) throw new StudioException("upload_incomplete", "Some chunks have not arrived.");
        var combined = Path.Combine(session.TempDirectory, "combined");
        await using (var output = File.Create(combined))
        {
            for (var i = 0; i < session.ChunkCount; i++)
            {
                await using var part = File.OpenRead(Path.Combine(session.TempDirectory, i.ToString("D6")));
                await part.CopyToAsync(output, ct);
            }
        }
        await using var read = File.OpenRead(combined);
        var asset = await AddAssetAsync(userId, projectId, session.Kind.ToString(), session.FileName, session.ContentType, read, ct);
        session.Completed = true;
        await db.SaveChangesAsync(ct);
        return asset;
    }

    public async Task<JobDto> EnqueueAsync(Guid userId, Guid projectId, JobKind kind, string payloadJson, string? idempotencyKey, CancellationToken ct)
    {
        var project = await Load(userId, projectId, ct);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await db.Jobs.FirstOrDefaultAsync(j => j.ProjectId == project.Id && j.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null) return MapJob(existing);
        }
        if (await db.Jobs.AnyAsync(j => j.ProjectId == project.Id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
            throw new StudioException("job_active", "This project already has a job in progress.", 409);
        if (kind == JobKind.Generate && !project.Assets.Any(a => a.Kind == AssetKind.SourceVideo))
            throw new StudioException("video_required", "Upload at least one video.");
        var now = DateTimeOffset.UtcNow;
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = kind,
            Status = JobStatus.Queued,
            Stage = "queued",
            PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            UpdatedAt = now,
        };
        project.Status = kind == JobKind.Correct || kind == JobKind.Cover ? ProjectStatus.RevisionInProgress : ProjectStatus.Queued;
        project.StatusDetail = "Queued.";
        Touch(project);
        db.Jobs.Add(job);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new StudioException("conflict", "The project was updated by another request.", 409); }
        return MapJob(job);
    }

    public async Task<JobDto> CancelAsync(Guid userId, Guid jobId, CancellationToken ct)
    {
        var job = await db.Jobs.Include(j => j.Project).FirstOrDefaultAsync(j => j.Id == jobId && j.Project!.OwnerId == userId, ct)
            ?? throw new StudioException("not_found", "Job not found.", 404);
        if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled) return MapJob(job);
        job.CancelRequested = true;
        if (job.Status == JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            job.Stage = "cancelled";
            job.Project!.Status = ProjectStatus.Cancelled;
        }
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return MapJob(job);
    }

    public async Task<ProjectDetailDto> AcceptRevisionAsync(Guid userId, Guid projectId, int number, CancellationToken ct)
    {
        var project = await Load(userId, projectId, ct);
        var revision = project.Revisions.FirstOrDefault(r => r.Number == number) ?? throw new StudioException("not_found", "Revision not found.", 404);
        revision.State = RevisionState.Accepted;
        project.Status = ProjectStatus.Approved;
        project.StatusDetail = $"Revision {number} accepted.";
        Touch(project);
        await db.SaveChangesAsync(ct);
        return Map(project);
    }

    public async Task<ProjectDetailDto> RejectRevisionAsync(Guid userId, Guid projectId, int number, CancellationToken ct)
    {
        var project = await Load(userId, projectId, ct);
        var revision = project.Revisions.FirstOrDefault(r => r.Number == number) ?? throw new StudioException("not_found", "Revision not found.", 404);
        revision.State = RevisionState.Rejected;
        var parent = revision.ParentNumber is null ? null : project.Revisions.FirstOrDefault(r => r.Number == revision.ParentNumber);
        if (parent?.VideoAssetId is not null) project.CurrentVideoAssetId = parent.VideoAssetId;
        if (!string.IsNullOrWhiteSpace(project.EngineProjectId) && parent is not null && engine.IsConfigured)
        {
            try
            {
                await engine.CallAsync("studio_restore", new { projectId = project.EngineProjectId, revision = project.EngineRevision, toRevision = parent.EngineRevision, author = "aicontent" }, ct);
            }
            catch (StudioException ex)
            {
                project.WarningsJson = AppendWarning(project.WarningsJson, "Engine restore: " + ex.Message);
            }
        }
        project.Status = ProjectStatus.ReadyForReview;
        project.StatusDetail = $"Revision {number} rejected. The previous video file is current again.";
        Touch(project);
        await db.SaveChangesAsync(ct);
        return await GetProjectAsync(userId, projectId, ct);
    }

    public async Task<ProjectDetailDto> RestoreRevisionAsync(Guid userId, Guid projectId, int number, CancellationToken ct)
    {
        var project = await Load(userId, projectId, ct);
        var source = project.Revisions.FirstOrDefault(r => r.Number == number) ?? throw new StudioException("not_found", "Revision not found.", 404);
        if (!string.IsNullOrWhiteSpace(project.EngineProjectId) && engine.IsConfigured)
        {
            try
            {
                var restored = await engine.CallAsync("studio_restore", new { projectId = project.EngineProjectId, revision = project.EngineRevision, toRevision = source.EngineRevision, author = "aicontent" }, ct);
                project.EngineRevision = AiEditStudioClient.IntAt(AiEditStudioClient.ProjectOf(restored), "revision");
            }
            catch (StudioException ex)
            {
                project.WarningsJson = AppendWarning(project.WarningsJson, "Engine restore: " + ex.Message);
            }
        }
        var copy = new TimelineRevision
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Number = project.CurrentRevisionNumber + 1,
            ParentNumber = project.CurrentRevisionNumber,
            State = RevisionState.Applied,
            Summary = $"Restored revision {number}.",
            TimelineJson = source.TimelineJson,
            EngineRevision = project.EngineRevision,
            VideoAssetId = source.VideoAssetId,
            Author = "editor",
            TouchesVideo = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Revisions.Add(copy);
        project.CurrentRevisionNumber = copy.Number;
        if (source.VideoAssetId is not null) project.CurrentVideoAssetId = source.VideoAssetId;
        project.Status = ProjectStatus.ReadyForReview;
        project.StatusDetail = copy.Summary + " The stored video for that revision is current. A new render was not started.";
        Touch(project);
        await db.SaveChangesAsync(ct);
        return await GetProjectAsync(userId, projectId, ct);
    }

    public async Task<ProjectDetailDto> SelectCoverAsync(Guid userId, Guid projectId, Guid coverId, CancellationToken ct)
    {
        var project = await Load(userId, projectId, ct);
        var cover = project.Covers.FirstOrDefault(c => c.Id == coverId) ?? throw new StudioException("not_found", "Cover not found.", 404);
        foreach (var item in project.Covers) item.Selected = item.Id == cover.Id;
        project.SelectedCoverAssetId = cover.Id;
        Touch(project);
        await db.SaveChangesAsync(ct);
        return Map(project);
    }

    public async Task<IReadOnlyList<StageEventDto>> EventsAsync(Guid userId, Guid projectId, long after, CancellationToken ct)
    {
        await Load(userId, projectId, ct);
        return await db.StageEvents.Where(e => e.Job!.ProjectId == projectId && e.Id > after).OrderBy(e => e.Id)
            .Select(e => new StageEventDto(e.Id, e.JobId, e.Stage, e.Message, e.CreatedAt)).ToListAsync(ct);
    }

    public async Task<(AssetDto Asset, Stream Stream)> OpenAssetAsync(Guid userId, Guid assetId, CancellationToken ct)
    {
        var asset = await db.Assets.Include(a => a.Project).FirstOrDefaultAsync(a => a.Id == assetId && a.Project!.OwnerId == userId, ct)
            ?? throw new StudioException("not_found", "Asset not found.", 404);
        return (MapAsset(asset), await storage.OpenReadAsync(asset.StorageKey, ct));
    }

    public async Task<SystemInfoDto> SystemInfoAsync(CancellationToken ct)
    {
        string? version = null;
        var notes = new List<string>();
        if (engine.IsConfigured)
        {
            try
            {
                var schema = await engine.CallAsync("studio_schema", new { }, ct);
                version = schema.TryGetProperty("version", out var v) ? v.GetString() : null;
            }
            catch (Exception ex) { notes.Add("Video engine is configured but did not respond: " + ex.Message); }
        }
        else notes.Add("ENGINE_BASE_URL is empty. Generation jobs cannot render until a video worker is configured.");
        if (options.Provider.Equals("fixture", StringComparison.OrdinalIgnoreCase))
            notes.Add("Editorial plans use the offline fixture planner. Set AI_PROVIDER, AI_MODEL and AI_API_KEY to call a live model. gpt-6.1-luna is not assumed to exist.");
        if (string.IsNullOrWhiteSpace(options.ImageProvider))
            notes.Add("Covers are composed from source frames and brand type. Set AI_IMAGE_PROVIDER only when that endpoint supports image edits.");
        return new SystemInfoDto(options.Provider, string.IsNullOrWhiteSpace(options.Model) ? (options.Provider == "fixture" ? "fixture-planner" : "") : options.Model,
            options.Provider == "fixture" || !string.IsNullOrWhiteSpace(options.ApiKey),
            true, !string.IsNullOrWhiteSpace(options.ImageProvider) && !string.IsNullOrWhiteSpace(options.ImageModel),
            options.TranscriptionProvider, storage.Provider, engine.IsConfigured, version, notes);
    }

    private async Task<Project> Load(Guid userId, Guid projectId, CancellationToken ct)
        => await db.Projects.Include(p => p.Brief).Include(p => p.Assets).Include(p => p.Revisions).Include(p => p.Covers)
            .Include(p => p.Messages).Include(p => p.Jobs).Include(p => p.Transcripts).Include(p => p.BrandKit)
            .FirstOrDefaultAsync(p => p.Id == projectId && p.OwnerId == userId, ct)
            ?? throw new StudioException("not_found", "Project not found.", 404);

    private async Task<BrandKit?> ResolveKit(Guid userId, Guid? id, CancellationToken ct)
    {
        if (id is null) return await db.BrandKits.FirstOrDefaultAsync(k => k.IsSystem && k.ThemeKey == "minimal", ct);
        return await db.BrandKits.FirstOrDefaultAsync(k => k.Id == id && (k.IsSystem || k.OwnerId == userId), ct);
    }

    private ProjectDetailDto Map(Project project)
    {
        object? timeline = null;
        var latest = project.Revisions.OrderByDescending(r => r.Number).FirstOrDefault();
        if (latest is not null && !string.IsNullOrWhiteSpace(latest.TimelineJson))
        {
            try { timeline = JsonSerializer.Deserialize<JsonElement>(latest.TimelineJson); } catch { timeline = null; }
        }
        object? transcript = project.Transcripts.OrderByDescending(t => t.CreatedAt).Select(t => new { t.Language, t.Provider, t.WordTiming, t.Text, words = t.WordsJson }).FirstOrDefault();
        var warnings = JsonSerializer.Deserialize<List<string>>(project.WarningsJson) ?? [];
        return new ProjectDetailDto(project.Id, project.Title, project.Status.ToString(), project.StatusDetail, project.ConcurrencyToken, project.CurrentRevisionNumber,
            project.EngineProjectId, project.CurrentVideoAssetId, project.SelectedCoverAssetId,
            new BriefDto(project.Brief?.Subject ?? "", project.Brief?.Goal ?? "", project.Brief?.Audience ?? "", project.Brief?.Tone ?? "", project.Brief?.Language ?? "auto", project.Brief?.Platform ?? "reel", project.Brief?.TargetDurationSeconds ?? 15, project.Brief?.CallToAction),
            project.BrandKit is null ? null : new BrandKitDto(project.BrandKit.Id, project.BrandKit.Name, project.BrandKit.ThemeKey, project.BrandKit.DefinitionJson, project.BrandKit.IsSystem),
            project.ThemeSnapshotJson, warnings, project.PublishPackJson,
            project.Assets.OrderBy(a => a.CreatedAt).Select(MapAsset).ToList(),
            project.Revisions.OrderBy(r => r.Number).Select(r => new RevisionDto(r.Number, r.ParentNumber, r.State.ToString(), r.Summary, r.EngineRevision, r.TouchesVideo, r.TouchesCover, r.VideoAssetId, r.Author, r.CreatedAt, r.Number == project.CurrentRevisionNumber ? r.TimelineJson : null)).ToList(),
            project.Covers.OrderByDescending(c => c.CreatedAt).Select(c => new CoverDto(c.Id, c.AssetId, c.Source, c.Headline, c.Prompt, c.Selected, Url(project.Assets.FirstOrDefault(a => a.Id == c.AssetId)), c.RevisionNumber)).ToList(),
            project.Messages.OrderBy(m => m.CreatedAt).Select(m => new MessageDto(m.Id, m.Role.ToString(), m.Content, m.SelectionJson, m.ChangeSetJson, m.RevisionNumber, m.CreatedAt)).ToList(),
            project.Jobs.OrderByDescending(j => j.CreatedAt).Take(20).Select(MapJob).ToList(),
            timeline, transcript);
    }

    private AssetDto MapAsset(Asset asset) => new(asset.Id, asset.Kind.ToString(), asset.OriginalFileName, asset.ContentType, asset.SizeBytes, asset.DurationSeconds, asset.Width, asset.Height, storage.CreateDownloadUrl(asset.Id, asset.StorageKey, TimeSpan.FromHours(2)), asset.CreatedAt);
    private static JobDto MapJob(GenerationJob job) => new(job.Id, job.Kind.ToString(), job.Status.ToString(), job.Stage, job.Message, job.Attempt, job.CancelRequested, job.UpdatedAt);
    private string? Url(Asset? asset) => asset is null ? null : storage.CreateDownloadUrl(asset.Id, asset.StorageKey, TimeSpan.FromHours(2));

    private static void Touch(Project project) { project.UpdatedAt = DateTimeOffset.UtcNow; project.ConcurrencyToken++; }
    private static string NormalizeEmail(string email) => (email ?? "").Trim().ToLowerInvariant();
    private static string NormalizePlatform(string platform) => platform.ToLowerInvariant() switch { "story" => "story", "post" => "post", _ => "reel" };
    private static string AppendWarning(string json, string message)
    {
        var list = JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? [];
        list.Add(message);
        return JsonSerializer.Serialize(list);
    }

    private static AssetKind ParseKind(string kind) => Enum.TryParse<AssetKind>(kind, true, out var parsed) ? parsed : throw new StudioException("invalid_asset", "Unknown asset kind.");

    private static void ValidateName(AssetKind kind, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var allowed = kind switch
        {
            AssetKind.SourceVideo or AssetKind.AdditionalClip => new[] { ".mp4", ".mov", ".webm", ".mkv" },
            AssetKind.Photo or AssetKind.Logo or AssetKind.ReferenceCover => new[] { ".png", ".jpg", ".jpeg", ".webp" },
            AssetKind.Music => new[] { ".mp3", ".wav", ".m4a", ".ogg" },
            AssetKind.TranscriptFile => new[] { ".json", ".srt", ".vtt", ".txt" },
            _ => new[] { ".png", ".jpg", ".jpeg", ".mp4", ".json", ".txt" }
        };
        if (!allowed.Contains(ext)) throw new StudioException("invalid_asset", $"Extension {ext} is not allowed for {kind}.");
    }

    private static void ValidatePayload(AssetKind kind, string fileName, string contentType, byte[] bytes)
    {
        ValidateName(kind, fileName);
        if (bytes.Length < 12) throw new StudioException("invalid_asset", "The file is too small to be valid media.");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".mp4" or ".mov" && !(bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p'))
            throw new StudioException("invalid_asset", "The file does not look like an MP4 or MOV.");
        if (ext == ".png" && bytes[0] != 0x89) throw new StudioException("invalid_asset", "The file does not look like a PNG.");
        if (ext is ".jpg" or ".jpeg" && bytes[0] != 0xFF) throw new StudioException("invalid_asset", "The file does not look like a JPEG.");
        if (ext == ".webm" && bytes[0] != 0x1A) throw new StudioException("invalid_asset", "The file does not look like WebM or MKV.");
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase)) throw new StudioException("invalid_asset", "HTML uploads are not accepted.");
    }

    public static string DefaultTheme(string key) => key switch
    {
        "professional" => Theme("#102033", "#D6A15A", "#F7F4EF", "neutral", "minimal"),
        "modern" => Theme("#111111", "#3DDC97", "#F4F7F5", "vivid", "bold"),
        "cinematic" => Theme("#140E0A", "#E7A04A", "#F6EFE4", "cinema", "bold"),
        "educational" => Theme("#10261F", "#F2C14E", "#F4F7F2", "neutral", "box"),
        "promotional" => Theme("#2A0E16", "#FF5A36", "#FFF6F1", "vivid", "bold"),
        "luxury" => Theme("#16130F", "#C6A15B", "#F8F1E3", "noir", "minimal"),
        "energetic" => Theme("#140818", "#FF3D8A", "#FFF0F6", "vivid", "bold"),
        _ => Theme("#1C1917", "#C4552A", "#F4EFE6", "cinema", "bold"),
    };

    private static string Theme(string primary, string accent, string text, string look, string caption) =>
        $$"""{"primary":"{{primary}}","accent":"{{accent}}","text":"{{text}}","font":"Noto Naskh Arabic","captionStyle":"{{caption}}","captionPosition":"low","captionAnimation":"highlight","look":"{{look}}","logoPosition":"top-right","musicStyle":"cinematic"}""";
}
