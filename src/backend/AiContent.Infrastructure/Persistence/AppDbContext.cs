using AiContent.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiContent.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<BrandKit> BrandKits => Set<BrandKit>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectBrief> Briefs => Set<ProjectBrief>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<TranscriptDocument> Transcripts => Set<TranscriptDocument>();
    public DbSet<TimelineRevision> Revisions => Set<TimelineRevision>();
    public DbSet<CoverVariant> Covers => Set<CoverVariant>();
    public DbSet<ExportBundle> Exports => Set<ExportBundle>();
    public DbSet<ConversationMessage> Messages => Set<ConversationMessage>();
    public DbSet<ToolAudit> ToolAudits => Set<ToolAudit>();
    public DbSet<GenerationJob> Jobs => Set<GenerationJob>();
    public DbSet<JobStageEvent> StageEvents => Set<JobStageEvent>();
    public DbSet<UploadSession> Uploads => Set<UploadSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.DisplayName).HasMaxLength(120);
        });
        modelBuilder.Entity<BrandKit>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.ThemeKey).HasMaxLength(40);
            e.HasOne(x => x.Owner).WithMany(x => x.BrandKits).HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Project>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(160);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
            e.HasIndex(x => new { x.OwnerId, x.UpdatedAt });
            e.HasOne(x => x.Owner).WithMany(x => x.Projects).HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.BrandKit).WithMany().HasForeignKey(x => x.BrandKitId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
        });
        modelBuilder.Entity<ProjectBrief>(e =>
        {
            e.HasKey(x => x.ProjectId);
            e.Property(x => x.Tone).HasMaxLength(40);
            e.Property(x => x.Language).HasMaxLength(16);
            e.Property(x => x.Platform).HasMaxLength(24);
            e.HasOne(x => x.Project).WithOne(x => x.Brief).HasForeignKey<ProjectBrief>(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Asset>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.StorageKey).HasMaxLength(400);
            e.Property(x => x.OriginalFileName).HasMaxLength(240);
            e.HasIndex(x => new { x.ProjectId, x.Kind });
            e.HasOne(x => x.Project).WithMany(x => x.Assets).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<TranscriptDocument>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.Language).HasMaxLength(16);
            e.HasOne(x => x.Project).WithMany(x => x.Transcripts).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<TimelineRevision>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Number }).IsUnique();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Summary).HasMaxLength(400);
            e.HasOne(x => x.Project).WithMany(x => x.Revisions).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<CoverVariant>(e =>
        {
            e.Property(x => x.Source).HasMaxLength(40);
            e.HasOne(x => x.Project).WithMany(x => x.Covers).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ExportBundle>(e =>
        {
            e.HasOne(x => x.Project).WithMany(x => x.Exports).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ConversationMessage>(e =>
        {
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Project).WithMany(x => x.Messages).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ToolAudit>(e =>
        {
            e.Property(x => x.ToolName).HasMaxLength(80);
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            e.HasOne(x => x.Project).WithMany(x => x.ToolAudits).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<GenerationJob>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Stage).HasMaxLength(40);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.ProjectId, x.IdempotencyKey });
            e.HasOne(x => x.Project).WithMany(x => x.Jobs).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<JobStageEvent>(e =>
        {
            e.Property(x => x.Stage).HasMaxLength(40);
            e.HasIndex(x => new { x.JobId, x.Id });
            e.HasOne(x => x.Job).WithMany(x => x.Events).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<UploadSession>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.FileName).HasMaxLength(240);
        });
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            var offset = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
            var nullable = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(v => v == null ? null : v.Value.UtcTicks, v => v == null ? null : new DateTimeOffset(v.Value, TimeSpan.Zero));
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
            {
                if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(offset);
                else if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(nullable);
            }
        }
    }
}

public sealed class DesignTimeDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(cs))
            cs = "Host=localhost;Port=5432;Database=aicontent;Username=aicontent;Password=aicontent";
        if (cs.StartsWith("postgres://", StringComparison.Ordinal) || cs.StartsWith("postgresql://", StringComparison.Ordinal))
            cs = PostgresConnection.ToNpgsql(cs);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options;
        return new AppDbContext(options);
    }
}

public static class PostgresConnection
{
    public static string ToNpgsql(string url)
    {
        if (!url.Contains("://", StringComparison.Ordinal)) return url;
        var uri = new Uri(url);
        var user = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
        var pass = uri.UserInfo.Contains(':') ? Uri.UnescapeDataString(uri.UserInfo[(uri.UserInfo.IndexOf(':') + 1)..]) : "";
        var db = uri.AbsolutePath.Trim('/');
        var port = uri.Port > 0 ? uri.Port : 5432;
        return $"Host={uri.Host};Port={port};Database={db};Username={user};Password={pass}";
    }
}
