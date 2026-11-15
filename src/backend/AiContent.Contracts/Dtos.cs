namespace AiContent.Contracts;

public sealed record UserDto(Guid Id, string Email, string DisplayName);
public sealed record AuthResponse(string Token, UserDto User);

public sealed record BriefDto(
    string Subject,
    string Goal,
    string Audience,
    string Tone,
    string Language,
    string Platform,
    int TargetDurationSeconds,
    string? CallToAction);

public sealed record CreateProjectRequest(
    string Title,
    string Subject,
    string Goal,
    string Audience,
    string Tone,
    string Language,
    string Platform,
    int TargetDurationSeconds,
    string? CallToAction,
    Guid? BrandKitId);

public sealed record BrandKitDto(Guid Id, string Name, string ThemeKey, string DefinitionJson, bool IsSystem);
public sealed record SaveBrandKitRequest(string Name, string ThemeKey, string DefinitionJson);

public sealed record AssetDto(
    Guid Id,
    string Kind,
    string FileName,
    string ContentType,
    long SizeBytes,
    double? DurationSeconds,
    int? Width,
    int? Height,
    string? DownloadUrl,
    DateTimeOffset CreatedAt);

public sealed record ProjectSummaryDto(
    Guid Id,
    string Title,
    string Status,
    string? StatusDetail,
    string Platform,
    string Language,
    DateTimeOffset UpdatedAt,
    int Revision);

public sealed record RevisionDto(
    int Number,
    int? ParentNumber,
    string State,
    string Summary,
    int EngineRevision,
    bool TouchesVideo,
    bool TouchesCover,
    Guid? VideoAssetId,
    string Author,
    DateTimeOffset CreatedAt,
    string? TimelineJson);

public sealed record CoverDto(Guid Id, Guid AssetId, string Source, string Headline, string? Prompt, bool Selected, string? DownloadUrl, int RevisionNumber);
public sealed record MessageDto(Guid Id, string Role, string Content, string? SelectionJson, string? ChangeSetJson, int? RevisionNumber, DateTimeOffset CreatedAt);
public sealed record JobDto(Guid Id, string Kind, string Status, string Stage, string? Message, int Attempt, bool CancelRequested, DateTimeOffset UpdatedAt);
public sealed record StageEventDto(long Id, Guid JobId, string Stage, string Message, DateTimeOffset CreatedAt);

public sealed record ProjectDetailDto(
    Guid Id,
    string Title,
    string Status,
    string? StatusDetail,
    int ConcurrencyToken,
    int CurrentRevisionNumber,
    string? EngineProjectId,
    Guid? CurrentVideoAssetId,
    Guid? SelectedCoverId,
    BriefDto Brief,
    BrandKitDto? Brand,
    string ThemeSnapshotJson,
    IReadOnlyList<string> Warnings,
    string? PublishPackJson,
    IReadOnlyList<AssetDto> Assets,
    IReadOnlyList<RevisionDto> Revisions,
    IReadOnlyList<CoverDto> Covers,
    IReadOnlyList<MessageDto> Messages,
    IReadOnlyList<JobDto> Jobs,
    object? Timeline,
    object? Transcript);

public sealed record SelectionDto(string Kind, double? Start, double? End, string? ClipId, int? Index, Guid? CoverId);
public sealed record CorrectionRequest(string Instruction, SelectionDto Selection, int BaseRevision);
public sealed record UploadStartRequest(string FileName, long Size, string ContentType, string Kind);
public sealed record UploadStartResponse(Guid Id, int ChunkSize, int ChunkCount);
public sealed record SystemInfoDto(
    string Provider,
    string Model,
    bool ProviderConfigured,
    bool Tools,
    bool ImageGenerationConfigured,
    string TranscriptionProvider,
    string StorageProvider,
    bool EngineConfigured,
    string? EngineVersion,
    IReadOnlyList<string> Notes);
