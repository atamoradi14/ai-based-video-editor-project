namespace AiContent.Infrastructure;

public sealed class StudioOptions
{
    public string Provider { get; set; } = "fixture";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public int TimeoutSeconds { get; set; } = 180;
    public int MaxOutputTokens { get; set; } = 4096;
    public int MaxToolCalls { get; set; } = 8;
    public int MaxRetries { get; set; } = 3;
    public string VisionModel { get; set; } = "";
    public string ImageProvider { get; set; } = "";
    public string ImageModel { get; set; } = "";
    public string ImageApiKey { get; set; } = "";
    public string ImageBaseUrl { get; set; } = "";
    public string TranscriptionProvider { get; set; } = "local";
    public string TranscriptionModel { get; set; } = "";
    public string DatabaseProvider { get; set; } = "Postgres";
    public string DatabaseUrl { get; set; } = "";
    public string SqlitePath { get; set; } = "data/dev.db";
    public string RedisUrl { get; set; } = "";
    public string StorageProvider { get; set; } = "local";
    public string StoragePath { get; set; } = "data/storage";
    public string AzureConnectionString { get; set; } = "";
    public string AzureContainer { get; set; } = "ai-content";
    public string S3Endpoint { get; set; } = "";
    public string S3Bucket { get; set; } = "";
    public string S3AccessKey { get; set; } = "";
    public string S3SecretKey { get; set; } = "";
    public string S3Region { get; set; } = "us-east-1";
    public string EngineBaseUrl { get; set; } = "";
    public string EngineToken { get; set; } = "";
    public string EngineDataRoot { get; set; } = "";
    public string JwtSigningKey { get; set; } = "";
    public string JwtIssuer { get; set; } = "ai-content";
    public int JwtHours { get; set; } = 12;
    public long UploadMaxBytes { get; set; } = 512L * 1024 * 1024;
    public bool SeedDemoUser { get; set; }
    public string DemoEmail { get; set; } = "demo@lumen.studio";
    public string DemoPassword { get; set; } = "Demo-Studio-1";
    public string PublicApiUrl { get; set; } = "";
    public bool ComposeMusic { get; set; }

    public static StudioOptions From(IConfiguration config)
    {
        string S(string key, string fallback = "") => config[key] ?? fallback;
        int I(string key, int fallback) => int.TryParse(S(key), out var n) ? n : fallback;
        bool B(string key) => string.Equals(S(key), "true", StringComparison.OrdinalIgnoreCase) || S(key) == "1";
        long L(string key, long fallback) => long.TryParse(S(key), out var n) ? n : fallback;
        return new StudioOptions
        {
            Provider = S("AI_PROVIDER", "fixture"),
            Model = S("AI_MODEL"),
            ApiKey = S("AI_API_KEY"),
            BaseUrl = S("AI_BASE_URL", "https://api.openai.com/v1"),
            TimeoutSeconds = I("AI_TIMEOUT_SECONDS", 180),
            MaxOutputTokens = I("AI_MAX_OUTPUT_TOKENS", 4096),
            MaxToolCalls = I("AI_MAX_TOOL_CALLS", 8),
            MaxRetries = I("AI_MAX_RETRIES", 3),
            VisionModel = S("AI_VISION_MODEL"),
            ImageProvider = S("AI_IMAGE_PROVIDER"),
            ImageModel = S("AI_IMAGE_MODEL"),
            ImageApiKey = S("AI_IMAGE_API_KEY", S("AI_API_KEY")),
            ImageBaseUrl = S("AI_IMAGE_BASE_URL", S("AI_BASE_URL", "https://api.openai.com/v1")),
            TranscriptionProvider = S("AI_TRANSCRIPTION_PROVIDER", "local"),
            TranscriptionModel = S("AI_TRANSCRIPTION_MODEL"),
            DatabaseProvider = S("DATABASE_PROVIDER", string.IsNullOrWhiteSpace(S("DATABASE_URL")) ? "Sqlite" : "Postgres"),
            DatabaseUrl = S("DATABASE_URL"),
            SqlitePath = S("SQLITE_PATH", "data/dev.db"),
            RedisUrl = S("REDIS_URL"),
            StorageProvider = S("STORAGE_PROVIDER", "local"),
            StoragePath = S("STORAGE_PATH", "data/storage"),
            AzureConnectionString = S("AZURE_STORAGE_CONNECTION_STRING"),
            AzureContainer = S("AZURE_STORAGE_CONTAINER", "ai-content"),
            S3Endpoint = S("S3_ENDPOINT"),
            S3Bucket = S("S3_BUCKET"),
            S3AccessKey = S("S3_ACCESS_KEY"),
            S3SecretKey = S("S3_SECRET_KEY"),
            S3Region = S("S3_REGION", "us-east-1"),
            EngineBaseUrl = S("ENGINE_BASE_URL"),
            EngineToken = S("ENGINE_INTERNAL_TOKEN"),
            EngineDataRoot = S("ENGINE_DATA_ROOT"),
            JwtSigningKey = S("JWT_SIGNING_KEY"),
            JwtIssuer = S("JWT_ISSUER", "ai-content"),
            JwtHours = I("JWT_HOURS", 12),
            UploadMaxBytes = L("UPLOAD_MAX_BYTES", 512L * 1024 * 1024),
            SeedDemoUser = B("SEED_DEMO_USER"),
            DemoEmail = S("DEMO_EMAIL", "demo@lumen.studio"),
            DemoPassword = S("DEMO_PASSWORD", "Demo-Studio-1"),
            PublicApiUrl = S("PUBLIC_API_URL"),
            ComposeMusic = B("COMPOSE_MUSIC"),
        };
    }
}
