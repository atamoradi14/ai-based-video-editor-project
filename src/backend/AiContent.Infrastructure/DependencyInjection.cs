using AiContent.Application;
using AiContent.Domain;
using AiContent.Infrastructure.Persistence;
using AiContent.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AiContent.Infrastructure;

public static class DependencyInjection
{
    public static StudioOptions AddStudio(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var options = StudioOptions.From(config);
        if (string.IsNullOrWhiteSpace(options.JwtSigningKey))
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException("JWT_SIGNING_KEY is required outside Development.");
            var dir = Path.GetFullPath(options.StoragePath);
            Directory.CreateDirectory(dir);
            var keyPath = Path.Combine(dir, "dev-jwt.key");
            if (!File.Exists(keyPath))
                File.WriteAllText(keyPath, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)));
            options.JwtSigningKey = File.ReadAllText(keyPath).Trim();
        }
        services.AddSingleton(options);
        services.AddDbContext<AppDbContext>(o =>
        {
            if (options.DatabaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                var path = Path.GetFullPath(options.SqlitePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                o.UseSqlite($"Data Source={path}");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(options.DatabaseUrl))
                    throw new InvalidOperationException("DATABASE_URL is required when DATABASE_PROVIDER is Postgres.");
                var cs = options.DatabaseUrl.Contains("://", StringComparison.Ordinal) ? PostgresConnection.ToNpgsql(options.DatabaseUrl) : options.DatabaseUrl;
                o.UseNpgsql(cs);
            }
        });
        services.AddHttpClient("engine");
        services.AddHttpClient("ai");
        services.AddSingleton<RedisConnector>();
        services.AddSingleton<IConnectionMultiplexer?>(sp => sp.GetRequiredService<RedisConnector>().Mux);
        services.AddScoped<IStorageService>(sp => options.StorageProvider.ToLowerInvariant() switch
        {
            "s3" => new S3StorageService(options),
            "azure" => new AzureBlobStorageService(options),
            _ => new LocalStorageService(options),
        });
        services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
        services.AddSingleton<ICoverComposer, FfmpegCoverComposer>();
        services.AddScoped<IAiEditStudioClient, AiEditStudioClient>();
        services.AddScoped<ITranscriptionService, TranscriptionRouter>();
        services.AddScoped<IImageGenerationService>(sp => string.IsNullOrWhiteSpace(options.ImageProvider)
            ? new NoImageGenerationService()
            : new OpenAiImageService(options, sp.GetRequiredService<IHttpClientFactory>()));
        services.AddScoped<IAgentOrchestrator>(sp => options.Provider.Equals("fixture", StringComparison.OrdinalIgnoreCase)
            ? new FixtureAgentOrchestrator()
            : new MicrosoftAgentOrchestrator(options, sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<MicrosoftAgentOrchestrator>>()));
        services.AddScoped<IJobQueue, PostgresJobQueue>();
        services.AddScoped<IProgressPublisher, ProgressPublisher>();
        services.AddScoped<IGenerationPipeline, GenerationPipeline>();
        services.AddScoped<IStudioUseCases, StudioUseCases>();
        services.AddHostedService<DatabaseInitializer>();
        if (string.Equals(config["RUN_WORKER_IN_API"], "true", StringComparison.OrdinalIgnoreCase) || (string.IsNullOrWhiteSpace(config["RUN_WORKER_IN_API"]) && env.IsDevelopment()))
            services.AddHostedService<GenerationWorker>();
        return options;
    }
}

public sealed class DatabaseInitializer(IServiceProvider services, IHostEnvironment env, ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<StudioOptions>();
        if (options.DatabaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            await db.Database.EnsureCreatedAsync(cancellationToken);
        else
            await db.Database.MigrateAsync(cancellationToken);
        if (!await db.BrandKits.AnyAsync(k => k.IsSystem, cancellationToken))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (key, name) in new (string, string)[]
            {
                ("minimal", "Minimal"), ("professional", "Professional"), ("modern", "Modern"), ("cinematic", "Cinematic"),
                ("educational", "Educational"), ("promotional", "Promotional"), ("luxury", "Luxury"), ("energetic", "Energetic"),
            })
            {
                db.BrandKits.Add(new BrandKit
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    ThemeKey = key,
                    DefinitionJson = StudioUseCases.DefaultTheme(key),
                    IsSystem = true,
                    CreatedAt = now,
                });
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        if (options.SeedDemoUser && env.IsDevelopment() && !await db.Users.AnyAsync(u => u.Email == options.DemoEmail, cancellationToken))
        {
            var use = scope.ServiceProvider.GetRequiredService<IStudioUseCases>();
            await use.RegisterAsync(options.DemoEmail, "Demo editor", options.DemoPassword, cancellationToken);
            logger.LogInformation("Seeded demo user {Email}", options.DemoEmail);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class GenerationWorker(IServiceProvider services, ILogger<GenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = Environment.MachineName + ":" + Environment.ProcessId;
        logger.LogInformation("Generation worker {WorkerId} started", workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
                var pipeline = scope.ServiceProvider.GetRequiredService<IGenerationPipeline>();
                var job = await queue.ClaimAsync(workerId, stoppingToken);
                if (job is null)
                {
                    await Task.Delay(700, stoppingToken);
                    continue;
                }
                await pipeline.RunAsync(job.Id, workerId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker loop error");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
