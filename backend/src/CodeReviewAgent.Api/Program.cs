using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CodeReviewAgent.Api.Data;
using CodeReviewAgent.Api.Services;
using CodeReviewAgent.Core.Agent;
using CodeReviewAgent.Core.Llm;
using CodeReviewAgent.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Agent + model provider ----------------------------------------------------------------------------------
// Agent:Provider picks the model API: gemini (default, free tier), groq, openrouter, openai-compatible or anthropic.
builder.Services.AddOptions<AgentOptions>().Bind(config.GetSection(AgentOptions.SectionName));
var agentOptions = config.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
var selection = ProviderPresets.Resolve(agentOptions, name => config[name]);
builder.Services.AddSingleton(selection);
builder.Services.AddHttpClient(ChatModelFactory.HttpClientName, http => http.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<IChatModel>(sp => ChatModelFactory.Create(
    selection,
    sp.GetRequiredService<IOptions<AgentOptions>>().Value,
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(ChatModelFactory.HttpClientName),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton(sp => new CodeReviewAgent.Core.Agent.CodeReviewAgent(
    sp.GetRequiredService<IChatModel>(),
    sp.GetRequiredService<IOptions<AgentOptions>>().Value,
    sp.GetRequiredService<ILogger<CodeReviewAgent.Core.Agent.CodeReviewAgent>>()));

// ---- GitHub ---------------------------------------------------------------------------------------------------
var githubToken = config["GITHUB_TOKEN"] ?? config["GitHub:Token"];
builder.Services.AddHttpClient(GitHubApiClient.HttpClientName, http => GitHubApiClient.Configure(http, githubToken))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// ---- Persistence ----------------------------------------------------------------------------------------------
builder.Services.AddDbContext<ReviewDbContext>(o =>
    o.UseSqlite(config.GetConnectionString("Reviews") ?? "Data Source=reviews.db"));

// ---- Web ------------------------------------------------------------------------------------------------------
builder.Services.AddScoped<ReviewService>();
builder.Services.AddSingleton(new SampleCatalog(
    Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, config["Samples:Path"] ?? "../../../samples"))));

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Each review costs real money: cap it per client IP.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("reviews", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimit:ReviewsPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
});

var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(allowedOrigins).AllowAnyHeader().WithMethods("GET", "POST")));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // Small demo app: create the schema on startup. A production deployment would use EF migrations.
    var db = scope.ServiceProvider.GetRequiredService<ReviewDbContext>();
    db.Database.EnsureCreated();

    // A run still marked Running at startup was cut off by a restart; record that instead of showing it forever.
    db.Reviews.Where(r => r.Status == ReviewStatus.Running).ExecuteUpdate(s => s
        .SetProperty(r => r.Status, ReviewStatus.Failed)
        .SetProperty(r => r.Error, "Interrupted: the server stopped before the review finished."));
}

app.Logger.LogInformation("Model provider: {Provider}, model {Model}", selection.Provider, selection.Model);
if (!selection.IsConfigured)
{
    app.Logger.LogWarning("{KeyVar} is not set. The API will start, but reviews will return 503 until it is configured.", selection.ApiKeyEnvVar);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseRateLimiter();
app.MapControllers();

// Serve the built React app (frontend/dist copied to wwwroot) when present, so one process can run the demo.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
