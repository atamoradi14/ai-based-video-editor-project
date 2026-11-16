using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AiContent.Api;
using AiContent.Application;
using AiContent.Contracts;
using AiContent.Domain;
using AiContent.Infrastructure;
using AiContent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512L * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 512L * 1024 * 1024);
var studio = builder.Services.AddStudio(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = studio.JwtIssuer,
        ValidateAudience = false,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(PadKey(studio.JwtSigningKey))),
        NameClaimType = ClaimTypes.NameIdentifier,
    };
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            var access = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(access) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                ctx.Token = access;
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddSignalR();
var origins = (builder.Configuration["WEB_ORIGIN"] ?? "http://localhost:3410,http://127.0.0.1:3410,http://localhost:3000").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddOpenApi();
builder.Services.AddHostedService<RedisEventBridge>();

var app = builder.Build();
app.Use(async (ctx, next) =>
{
    var id = ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
    ctx.Response.Headers["X-Correlation-Id"] = id;
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["correlationId"] = id }))
    {
        try { await next(); }
        catch (StudioException ex)
        {
            ctx.Response.StatusCode = ex.Status;
            await ctx.Response.WriteAsJsonAsync(new { error = new { code = ex.Code, message = ex.Message } });
        }
    }
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/ready", async (AppDbContext db, CancellationToken ct) =>
{
    var ok = await db.Database.CanConnectAsync(ct);
    return ok ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
});
app.MapHub<StudioHub>("/hubs/studio");
app.MapStudioApi();
app.Run();

static string PadKey(string key) => key.Length >= 32 ? key : key.PadRight(32, 'x');

public partial class Program;

namespace AiContent.Api
{
    public sealed class JwtTokenIssuer(StudioOptions options) : ITokenIssuer
    {
        public string Issue(AppUser user)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtSigningKey.Length >= 32 ? options.JwtSigningKey : options.JwtSigningKey.PadRight(32, 'x')));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(options.JwtIssuer, claims:
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.DisplayName),
            ], expires: DateTime.UtcNow.AddHours(options.JwtHours), signingCredentials: creds);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    public sealed class StudioHub : Microsoft.AspNetCore.SignalR.Hub
    {
        public override Task OnConnectedAsync()
        {
            var id = Context.UserIdentifier;
            return string.IsNullOrWhiteSpace(id) ? base.OnConnectedAsync() : Groups.AddToGroupAsync(Context.ConnectionId, "user:" + id);
        }
    }

    public sealed class RedisEventBridge(IConnectionMultiplexer? redis, Microsoft.AspNetCore.SignalR.IHubContext<StudioHub> hub, ILogger<RedisEventBridge> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (redis is null) return;
            var sub = redis.GetSubscriber();
            await sub.SubscribeAsync(RedisChannel.Literal("aicontent:events"), async (_, value) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(value.ToString());
                    var user = doc.RootElement.GetProperty("userId").GetGuid();
                    await hub.Clients.Group("user:" + user).SendAsync("project", doc.RootElement.GetRawText(), stoppingToken);
                }
                catch (Exception ex) { logger.LogDebug(ex, "Dropped a realtime event"); }
            });
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { /* shutdown */ }
        }
    }

    public static class StudioApiMap
    {
        public static void MapStudioApi(this WebApplication app)
        {
            var api = app.MapGroup("/api/v1");
            api.MapPost("/auth/register", async (AuthBody body, IStudioUseCases use, ITokenIssuer tokens, CancellationToken ct) =>
            {
                var user = await use.RegisterAsync(body.Email, body.DisplayName ?? body.Email, body.Password, ct);
                return Results.Ok(new AuthResponse(tokens.Issue(user), new UserDto(user.Id, user.Email, user.DisplayName)));
            });
            api.MapPost("/auth/login", async (AuthBody body, IStudioUseCases use, ITokenIssuer tokens, CancellationToken ct) =>
            {
                var user = await use.LoginAsync(body.Email, body.Password, ct);
                return Results.Ok(new AuthResponse(tokens.Issue(user), new UserDto(user.Id, user.Email, user.DisplayName)));
            });
            api.MapGet("/system", async (IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.SystemInfoAsync(ct)));

            var auth = api.MapGroup("").RequireAuthorization();
            auth.MapGet("/me", async (ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) =>
            {
                var user = await use.GetUserAsync(UserId(principal), ct);
                return Results.Ok(new UserDto(user.Id, user.Email, user.DisplayName));
            });
            auth.MapGet("/brand-kits", async (ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.ListBrandKitsAsync(UserId(principal), ct)));
            auth.MapPost("/brand-kits", async (SaveBrandKitRequest body, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.SaveBrandKitAsync(UserId(principal), body, ct)));
            auth.MapGet("/projects", async (string? q, string? status, string? sort, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.ListProjectsAsync(UserId(principal), q, status, sort, ct)));
            auth.MapPost("/projects", async (CreateProjectRequest body, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.CreateProjectAsync(UserId(principal), body, ct)));
            auth.MapGet("/projects/{id:guid}", async (Guid id, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.GetProjectAsync(UserId(principal), id, ct)));
            auth.MapPut("/projects/{id:guid}/brief", async (Guid id, BriefDto body, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.UpdateBriefAsync(UserId(principal), id, body, ct)));
            auth.MapPut("/projects/{id:guid}/theme", async (Guid id, ThemeBody body, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.UpdateThemeAsync(UserId(principal), id, body.BrandKitId, ct)));
            auth.MapPost("/projects/{id:guid}/assets", async (Guid id, HttpRequest request, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) =>
            {
                if (!request.HasFormContentType) throw new StudioException("invalid_upload", "Send multipart form data.");
                var form = await request.ReadFormAsync(ct);
                var file = form.Files.FirstOrDefault() ?? throw new StudioException("invalid_upload", "Choose a file.");
                await using var stream = file.OpenReadStream();
                return Results.Ok(await use.AddAssetAsync(UserId(principal), id, form["kind"].ToString(), file.FileName, file.ContentType, stream, ct));
            }).DisableAntiforgery();
            auth.MapPost("/projects/{id:guid}/uploads", async (Guid id, UploadStartRequest body, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.StartUploadAsync(UserId(principal), id, body, ct)));
            auth.MapPut("/projects/{id:guid}/uploads/{uploadId:guid}/chunks/{index:int}", async (Guid id, Guid uploadId, int index, HttpRequest request, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) =>
            {
                await use.ReceiveChunkAsync(UserId(principal), id, uploadId, index, request.Body, ct);
                return Results.NoContent();
            });
            auth.MapPost("/projects/{id:guid}/uploads/{uploadId:guid}/complete", async (Guid id, Guid uploadId, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.CompleteUploadAsync(UserId(principal), id, uploadId, ct)));
            auth.MapPost("/projects/{id:guid}/generate", async (Guid id, HttpRequest request, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) =>
            {
                var key = request.Headers["Idempotency-Key"].FirstOrDefault();
                return Results.Accepted($"/api/v1/projects/{id}", await use.EnqueueAsync(UserId(principal), id, JobKind.Generate, "{}", key, ct));
            });
            auth.MapPost("/projects/{id:guid}/corrections", async (Guid id, CorrectionRequest body, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) =>
                Results.Accepted($"/api/v1/projects/{id}", await use.EnqueueAsync(UserId(principal), id, body.Selection.Kind == "cover" ? JobKind.Cover : JobKind.Correct, JsonSerializer.Serialize(body), null, ct)));
            auth.MapPost("/projects/{id:guid}/export", async (Guid id, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) =>
                Results.Accepted($"/api/v1/projects/{id}", await use.EnqueueAsync(UserId(principal), id, JobKind.Export, "{}", null, ct)));
            auth.MapPost("/projects/{id:guid}/revisions/{number:int}/accept", async (Guid id, int number, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.AcceptRevisionAsync(UserId(principal), id, number, ct)));
            auth.MapPost("/projects/{id:guid}/revisions/{number:int}/reject", async (Guid id, int number, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.RejectRevisionAsync(UserId(principal), id, number, ct)));
            auth.MapPost("/projects/{id:guid}/revisions/{number:int}/restore", async (Guid id, int number, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.RestoreRevisionAsync(UserId(principal), id, number, ct)));
            auth.MapPost("/projects/{id:guid}/covers/{coverId:guid}/select", async (Guid id, Guid coverId, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.SelectCoverAsync(UserId(principal), id, coverId, ct)));
            auth.MapGet("/projects/{id:guid}/events", async (Guid id, long? after, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.EventsAsync(UserId(principal), id, after ?? 0, ct)));
            auth.MapPost("/jobs/{id:guid}/cancel", async (Guid id, ClaimsPrincipal principal, IStudioUseCases use, CancellationToken ct) => Results.Ok(await use.CancelAsync(UserId(principal), id, ct)));
            app.MapGet("/api/v1/media/{id:guid}", async (Guid id, long? exp, string? sig, ClaimsPrincipal principal, AppDbContext db, IStorageService storage, CancellationToken ct) =>
            {
                var asset = await db.Assets.Include(a => a.Project).FirstOrDefaultAsync(a => a.Id == id, ct);
                if (asset?.Project is null) return Results.NotFound();
                var signed = exp is not null && !string.IsNullOrWhiteSpace(sig) && storage.VerifyDownload(id, exp.Value, sig);
                var owner = principal.Identity?.IsAuthenticated == true && Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) && uid == asset.Project.OwnerId;
                if (!signed && !owner) return Results.Unauthorized();
                var stream = await storage.OpenReadAsync(asset.StorageKey, ct);
                return Results.File(stream, asset.ContentType, asset.OriginalFileName);
            });
        }

        private static Guid UserId(ClaimsPrincipal principal)
        {
            var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var id) ? id : throw new StudioException("unauthorized", "Sign in again.", 401);
        }
    }

    public sealed record AuthBody(string Email, string Password, string? DisplayName);
    public sealed record ThemeBody(Guid BrandKitId);
}
