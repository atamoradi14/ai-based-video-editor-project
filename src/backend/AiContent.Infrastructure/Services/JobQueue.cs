using AiContent.Application;
using AiContent.Domain;
using AiContent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace AiContent.Infrastructure.Services;

public sealed class PostgresJobQueue(AppDbContext db) : IJobQueue
{
    public async Task<Domain.GenerationJob?> ClaimAsync(string workerId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            var queued = await Next(JobStatus.Queued, expiredOnly: false, now, ct);
            var expired = await Next(JobStatus.Running, expiredOnly: true, now, ct);
            var candidate = queued is null ? expired : expired is null || queued.CreatedAt <= expired.CreatedAt ? queued : expired;
            if (candidate is null) return null;
            if (candidate.Status == JobStatus.Running && candidate.Attempt + 1 > candidate.MaxAttempts)
            {
                await db.Jobs.Where(j => j.Id == candidate.Id && j.Status == JobStatus.Running && j.LeaseUntil != null && j.LeaseUntil < now)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, JobStatus.Failed)
                        .SetProperty(j => j.Error, "Lease expired and the retry budget was exhausted.")
                        .SetProperty(j => j.Stage, "failed")
                        .SetProperty(j => j.UpdatedAt, now), ct);
                continue;
            }
            var until = now.AddSeconds(45);
            var query = candidate.Status == JobStatus.Queued
                ? db.Jobs.Where(j => j.Id == candidate.Id && j.Status == JobStatus.Queued)
                : db.Jobs.Where(j => j.Id == candidate.Id && j.Status == JobStatus.Running && j.LeaseUntil != null && j.LeaseUntil < now);
            var updated = await query.ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.Attempt, j => j.Attempt + 1)
                .SetProperty(j => j.LockedBy, workerId)
                .SetProperty(j => j.LeaseUntil, until)
                .SetProperty(j => j.UpdatedAt, now), ct);
            if (updated == 1)
                return await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == candidate.Id, ct);
        }
        return null;
    }

    private async Task<JobCandidate?> Next(JobStatus status, bool expiredOnly, DateTimeOffset now, CancellationToken ct)
    {
        var query = db.Jobs.AsNoTracking().Where(j => j.Status == status);
        if (expiredOnly)
            query = query.Where(j => j.LeaseUntil < now);
        var job = await query.OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        return job is null ? null : new JobCandidate(job.Id, job.Status, job.Attempt, job.MaxAttempts, job.CreatedAt);
    }

    private sealed record JobCandidate(Guid Id, JobStatus Status, int Attempt, int MaxAttempts, DateTimeOffset CreatedAt);

    public Task HeartbeatAsync(Guid jobId, string workerId, CancellationToken ct)
        => db.Jobs.Where(j => j.Id == jobId && j.LockedBy == workerId && j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(45)).SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);

    public async Task CompleteAsync(Guid jobId, JobStatus status, string? error, CancellationToken ct)
    {
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, status)
            .SetProperty(j => j.Error, error)
            .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(j => j.LockedBy, (string?)null)
            .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow)
            .SetProperty(j => j.Stage, status == JobStatus.Succeeded ? "done" : status == JobStatus.Cancelled ? "cancelled" : "failed"), ct);
    }

    public async Task RequeueAsync(Guid jobId, string reason, CancellationToken ct)
    {
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Queued)
            .SetProperty(j => j.Message, reason)
            .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(j => j.LockedBy, (string?)null)
            .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }
}

public sealed class ProgressPublisher(AppDbContext db, IConnectionMultiplexer? redis) : IProgressPublisher
{
    public async Task PublishAsync(Guid userId, Guid projectId, Guid jobId, string stage, string message, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        db.StageEvents.Add(new JobStageEvent { JobId = jobId, Stage = stage, Message = message, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Stage, stage)
            .SetProperty(j => j.Message, message)
            .SetProperty(j => j.UpdatedAt, now), ct);
        var status = stage switch
        {
            "probe" or "transcribe" or "analyze" => ProjectStatus.Analyzing,
            "plan" or "assemble" or "brand" => ProjectStatus.Generating,
            "render" or "cover" or "qc" or "export" => ProjectStatus.Rendering,
            "failed" => ProjectStatus.Failed,
            "cancelled" => ProjectStatus.Cancelled,
            _ => (ProjectStatus?)null
        };
        if (status is not null)
        {
            await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, status.Value)
                .SetProperty(p => p.StatusDetail, message)
                .SetProperty(p => p.UpdatedAt, now), ct);
        }
        if (redis is not null)
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new { userId, projectId, jobId, stage, message, at = now });
            await redis.GetSubscriber().PublishAsync(RedisChannel.Literal("aicontent:events"), payload);
        }
    }
}

public sealed class RedisConnector
{
    public IConnectionMultiplexer? Mux { get; }

    public RedisConnector(StudioOptions options, ILogger<RedisConnector> logger)
    {
        if (string.IsNullOrWhiteSpace(options.RedisUrl)) return;
        try
        {
            Mux = ConnectionMultiplexer.Connect(options.RedisUrl.Replace("redis://", "", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis is configured but could not be reached. Jobs remain durable in the database and the UI can poll.");
        }
    }
}
