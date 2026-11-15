using System.Text.Json;
using AiContent.Contracts;

namespace AiContent.Application;

public interface IStorageService
{
    string Provider { get; }
    Task<StoredObject> SaveAsync(Stream content, string contentType, string extension, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    Task<string> MaterializeAsync(string key, CancellationToken ct);
    string CreateDownloadUrl(Guid assetId, string key, TimeSpan ttl);
    bool VerifyDownload(Guid assetId, long exp, string sig);
}

public sealed record StoredObject(string Key, long Size, string ContentType);

public interface IMediaProbe
{
    Task<ProbeResult> ProbeAsync(string path, CancellationToken ct);
}

public sealed record ProbeResult(double? Duration, int? Width, int? Height, string? VideoCodec, string? AudioCodec, bool HasAudio, string RawJson);

public interface IAiEditStudioClient
{
    bool IsConfigured { get; }
    Task<JsonElement> CallAsync(string tool, object? arguments, CancellationToken ct);
    Task<JsonElement> WaitUntilIdleAsync(string projectId, TimeSpan budget, CancellationToken ct);
    Task<string?> CopyExportAsync(JsonElement project, IStorageService storage, CancellationToken ct);
}

public interface IAgentOrchestrator
{
    string Provider { get; }
    string Model { get; }
    bool IsConfigured { get; }
    Task<EditorialPlan> PlanAsync(PlanRequest request, CancellationToken ct);
    Task<ProposedChangeSet> ProposeAsync(CorrectionPrompt request, CancellationToken ct);
}

public sealed record PlanRequest(string Title, string Subject, string Goal, string Audience, string Tone, string Language, string Platform, int DurationSeconds, string? CallToAction, string? TranscriptExcerpt);
public sealed record CorrectionPrompt(string Instruction, SelectionDto Selection, string TimelineJson, string TranscriptExcerpt, int Revision);

public interface ITranscriptionService
{
    string Provider { get; }
    Task<TranscriptDraft> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}

public sealed record TranscriptionRequest(string MediaPath, string? SidecarPath, string Language, string? EngineProjectId);
public sealed record TranscriptDraft(string Provider, string? Model, string Language, string WordTiming, IReadOnlyList<TimedWord> Words, string? Warning);

public interface IImageGenerationService
{
    bool IsConfigured { get; }
    Task<ImageAttempt> TryEditAsync(string sourcePngPath, string prompt, CancellationToken ct);
}

public sealed record ImageAttempt(bool Succeeded, string? OutputPath, string? Warning);

public interface ICoverComposer
{
    Task<string> ComposeAsync(CoverComposeRequest request, CancellationToken ct);
}

public sealed record CoverComposeRequest(
    string VideoPath,
    double AtSeconds,
    string Headline,
    string Primary,
    string Accent,
    string TextColor,
    string Font,
    bool RightToLeft,
    string OutputPath);

public interface IProgressPublisher
{
    Task PublishAsync(Guid userId, Guid projectId, Guid jobId, string stage, string message, CancellationToken ct);
}

public interface IJobQueue
{
    Task<Domain.GenerationJob?> ClaimAsync(string workerId, CancellationToken ct);
    Task HeartbeatAsync(Guid jobId, string workerId, CancellationToken ct);
    Task CompleteAsync(Guid jobId, Domain.JobStatus status, string? error, CancellationToken ct);
    Task RequeueAsync(Guid jobId, string reason, CancellationToken ct);
}

public interface IGenerationPipeline
{
    Task RunAsync(Guid jobId, string workerId, CancellationToken ct);
}

public interface IStudioUseCases
{
    Task<Domain.AppUser> RegisterAsync(string email, string displayName, string password, CancellationToken ct);
    Task<Domain.AppUser> LoginAsync(string email, string password, CancellationToken ct);
    Task<Domain.AppUser> GetUserAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<BrandKitDto>> ListBrandKitsAsync(Guid userId, CancellationToken ct);
    Task<BrandKitDto> SaveBrandKitAsync(Guid userId, SaveBrandKitRequest request, CancellationToken ct);
    Task<ProjectDetailDto> CreateProjectAsync(Guid userId, CreateProjectRequest request, CancellationToken ct);
    Task<IReadOnlyList<ProjectSummaryDto>> ListProjectsAsync(Guid userId, string? query, string? status, string? sort, CancellationToken ct);
    Task<ProjectDetailDto> GetProjectAsync(Guid userId, Guid projectId, CancellationToken ct);
    Task<ProjectDetailDto> UpdateBriefAsync(Guid userId, Guid projectId, BriefDto brief, CancellationToken ct);
    Task<ProjectDetailDto> UpdateThemeAsync(Guid userId, Guid projectId, Guid brandKitId, CancellationToken ct);
    Task<AssetDto> AddAssetAsync(Guid userId, Guid projectId, string kind, string fileName, string contentType, Stream content, CancellationToken ct);
    Task<UploadStartResponse> StartUploadAsync(Guid userId, Guid projectId, UploadStartRequest request, CancellationToken ct);
    Task ReceiveChunkAsync(Guid userId, Guid projectId, Guid uploadId, int index, Stream content, CancellationToken ct);
    Task<AssetDto> CompleteUploadAsync(Guid userId, Guid projectId, Guid uploadId, CancellationToken ct);
    Task<JobDto> EnqueueAsync(Guid userId, Guid projectId, Domain.JobKind kind, string payloadJson, string? idempotencyKey, CancellationToken ct);
    Task<JobDto> CancelAsync(Guid userId, Guid jobId, CancellationToken ct);
    Task<ProjectDetailDto> AcceptRevisionAsync(Guid userId, Guid projectId, int number, CancellationToken ct);
    Task<ProjectDetailDto> RejectRevisionAsync(Guid userId, Guid projectId, int number, CancellationToken ct);
    Task<ProjectDetailDto> RestoreRevisionAsync(Guid userId, Guid projectId, int number, CancellationToken ct);
    Task<ProjectDetailDto> SelectCoverAsync(Guid userId, Guid projectId, Guid coverId, CancellationToken ct);
    Task<IReadOnlyList<StageEventDto>> EventsAsync(Guid userId, Guid projectId, long after, CancellationToken ct);
    Task<(AssetDto Asset, Stream Stream)> OpenAssetAsync(Guid userId, Guid assetId, CancellationToken ct);
    Task<SystemInfoDto> SystemInfoAsync(CancellationToken ct);
}

public interface ITokenIssuer
{
    string Issue(Domain.AppUser user);
}
