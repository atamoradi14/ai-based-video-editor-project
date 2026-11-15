namespace AiContent.Domain;

public enum ProjectStatus
{
    Draft,
    Uploading,
    Queued,
    Analyzing,
    Generating,
    Rendering,
    ReadyForReview,
    RevisionInProgress,
    Approved,
    Exported,
    Failed,
    Cancelled
}

public enum JobKind { Generate, Correct, Cover, Export }

public enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled }

public enum AssetKind
{
    SourceVideo,
    AdditionalClip,
    Photo,
    Logo,
    Music,
    TranscriptFile,
    ReferenceCover,
    Supporting,
    DerivedVideo,
    CoverImage,
    CaptionFile,
    PublishText,
    ExportManifest,
    Storyboard
}

public enum MessageRole { User, Assistant, System }

public enum RevisionState { Applied, Accepted, Rejected }

public sealed class AppUser
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<Project> Projects { get; set; } = [];
    public List<BrandKit> BrandKits { get; set; } = [];
}

public sealed class BrandKit
{
    public Guid Id { get; set; }
    public Guid? OwnerId { get; set; }
    public AppUser? Owner { get; set; }
    public string Name { get; set; } = "";
    public string ThemeKey { get; set; } = "custom";
    public string DefinitionJson { get; set; } = "{}";
    public bool IsSystem { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Project
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public AppUser? Owner { get; set; }
    public string Title { get; set; } = "";
    public ProjectStatus Status { get; set; } = ProjectStatus.Draft;
    public string? StatusDetail { get; set; }
    public int ConcurrencyToken { get; set; }
    public Guid? BrandKitId { get; set; }
    public BrandKit? BrandKit { get; set; }
    public string ThemeSnapshotJson { get; set; } = "{}";
    public string? EngineProjectId { get; set; }
    public int EngineRevision { get; set; }
    public int CurrentRevisionNumber { get; set; }
    public Guid? CurrentVideoAssetId { get; set; }
    public Guid? SelectedCoverAssetId { get; set; }
    public string WarningsJson { get; set; } = "[]";
    public string? PublishPackJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ProjectBrief? Brief { get; set; }
    public List<Asset> Assets { get; set; } = [];
    public List<TranscriptDocument> Transcripts { get; set; } = [];
    public List<TimelineRevision> Revisions { get; set; } = [];
    public List<CoverVariant> Covers { get; set; } = [];
    public List<ExportBundle> Exports { get; set; } = [];
    public List<ConversationMessage> Messages { get; set; } = [];
    public List<GenerationJob> Jobs { get; set; } = [];
    public List<ToolAudit> ToolAudits { get; set; } = [];
}

public sealed class ProjectBrief
{
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Subject { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Audience { get; set; } = "";
    public string Tone { get; set; } = "professional";
    public string Language { get; set; } = "auto";
    public string Platform { get; set; } = "reel";
    public int TargetDurationSeconds { get; set; } = 15;
    public string? CallToAction { get; set; }
}

public sealed class Asset
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public AssetKind Kind { get; set; }
    public string StorageKey { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public double? DurationSeconds { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? MetadataJson { get; set; }
    public bool Immutable { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TranscriptDocument
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? AssetId { get; set; }
    public string Language { get; set; } = "auto";
    public string Provider { get; set; } = "none";
    public string? Model { get; set; }
    public string WordTiming { get; set; } = "none";
    public string Text { get; set; } = "";
    public string WordsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TimelineRevision
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public int Number { get; set; }
    public int? ParentNumber { get; set; }
    public RevisionState State { get; set; } = RevisionState.Applied;
    public string Summary { get; set; } = "";
    public string TimelineJson { get; set; } = "{}";
    public string? DiffJson { get; set; }
    public int EngineRevision { get; set; }
    public Guid? VideoAssetId { get; set; }
    public string Author { get; set; } = "engine";
    public bool TouchesVideo { get; set; }
    public bool TouchesCover { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CoverVariant
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public int RevisionNumber { get; set; }
    public Guid AssetId { get; set; }
    public string Source { get; set; } = "template";
    public string? Prompt { get; set; }
    public string Headline { get; set; } = "";
    public bool Selected { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ExportBundle
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public int RevisionNumber { get; set; }
    public Guid? VideoAssetId { get; set; }
    public Guid? CoverAssetId { get; set; }
    public Guid? CaptionAssetId { get; set; }
    public string? SocialCaption { get; set; }
    public string HashtagsJson { get; set; } = "[]";
    public string ManifestJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ConversationMessage
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = "";
    public string? SelectionJson { get; set; }
    public string? ChangeSetJson { get; set; }
    public int? RevisionNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ToolAudit
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? JobId { get; set; }
    public string ToolName { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
    public string? ResultSummary { get; set; }
    public int DurationMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class GenerationJob
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public JobKind Kind { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string Stage { get; set; } = "queued";
    public string? Message { get; set; }
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LockedBy { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string? Error { get; set; }
    public string? IdempotencyKey { get; set; }
    public bool CancelRequested { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<JobStageEvent> Events { get; set; } = [];
}

public sealed class JobStageEvent
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public GenerationJob? Job { get; set; }
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class UploadSession
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid OwnerId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public AssetKind Kind { get; set; }
    public long SizeBytes { get; set; }
    public int ChunkSize { get; set; }
    public int ChunkCount { get; set; }
    public string ReceivedJson { get; set; } = "[]";
    public string TempDirectory { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Completed { get; set; }
}

public sealed class StudioException : Exception
{
    public StudioException(string code, string message, int status = 400) : base(message)
    {
        Code = code;
        Status = status;
    }

    public string Code { get; }
    public int Status { get; }
}
