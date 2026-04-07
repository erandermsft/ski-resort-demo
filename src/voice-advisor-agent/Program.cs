using A2A;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Azure.Cosmos;
using OpenTelemetry.Trace;
using SharedServices;
using VoiceAdvisorAgent;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddKeyedAzureCosmosContainer("conversations",
    configureClientOptions: (option) =>
    {
        option.Serializer = new CosmosSystemTextJsonSerializer();
    });

builder.Services.AddSingleton(sp => sp.GetRequiredKeyedService<Container>("conversations"));

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(VoiceSessionTraceEmitter.ActivitySourceName));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Parse the project endpoint and deployment name from Foundry connection strings
var projectConnectionString = "https://aif-voice-ski-resort-dem.cognitiveservices.azure.com/openai/realtime?api-version=2024-10-01-preview&deployment=gptrealtime";
// var endpoint = ParseConnectionStringValue(projectConnectionString, "Endpoint")
//     ?? ParseConnectionStringValue(projectConnectionString, "EndpointAIInference");
var endpoint = projectConnectionString;

var deploymentConnectionString = builder.Configuration.GetConnectionString("gptrealtime") ?? "";
var model = ParseConnectionStringValue(deploymentConnectionString, "Deployment")
    ?? builder.Configuration["VoiceLive:Model"]
    ?? "gpt-realtime";
var voice = builder.Configuration["VoiceLive:Voice"] ?? "alloy";

// Connect to downstream agents via A2A
var agents = new Dictionary<string, AIAgent>();

var agentConfigs = new Dictionary<string, string>
{
    ["weather_agent"] = "services__weather-agent-python__https__0",
    ["lift_traffic_agent"] = "services__lift-traffic-agent-dotnet__https__0",
    ["safety_agent"] = "services__safety-agent-python__https__0",
    ["ski_coach_agent"] = "services__ski-coach-agent-python__https__0",
};

foreach (var (agentName, envVar) in agentConfigs)
{
    var url = Environment.GetEnvironmentVariable(envVar)
        ?? Environment.GetEnvironmentVariable(envVar.Replace("https", "http"));

    if (!string.IsNullOrEmpty(url))
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(url),
            Timeout = TimeSpan.FromSeconds(60)
        };

        // Python agents use well-known path, .NET agents use /agenta2a/v1/card
        var cardPath = agentName == "lift_traffic_agent"
            ? "/agenta2a/v1/card"
            : "/.well-known/agent-card.json";

        var cardResolver = new A2ACardResolver(
            httpClient.BaseAddress!,
            httpClient,
            agentCardPath: cardPath);

        agents[agentName] = cardResolver.GetAIAgentAsync().Result;
    }
}

builder.Services.AddSingleton(agents);
builder.Services.AddSingleton(new DefaultAzureCredential());

var systemPrompt = File.ReadAllText(
    Path.Combine(builder.Environment.ContentRootPath, "Prompts", "system-prompt.txt"));

var app = builder.Build();

app.UseCors();
app.UseWebSockets();

app.MapGet("/health", () => Results.Ok("healthy"));

app.Map("/ws/voice", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket connection expected");
        return;
    }

    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger<VoiceWebSocketHandler>();

    var credential = context.RequestServices.GetRequiredService<DefaultAzureCredential>();
    var a2aAgents = context.RequestServices.GetRequiredService<Dictionary<string, AIAgent>>();
    var cosmosContainer = context.RequestServices.GetRequiredService<Container>();

    // Use conversationId directly (no suffix) so voice and chat histories are merged
    var conversationId = context.Request.Query["conversationId"].FirstOrDefault();

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

    var handler = new VoiceWebSocketHandler(
        webSocket,
        credential,
        endpoint,
        model,
        voice,
        systemPrompt,
        a2aAgents,
        logger,
        conversationId,
        cosmosContainer);

    await handler.RunAsync(context.RequestAborted);
});

app.MapDefaultEndpoints();
app.Run();

static string ParseConnectionStringValue(string connectionString, string key)
{
    foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var kv = part.Split('=', 2);
        if (kv.Length != 2) continue;

        if (kv[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            return kv[1].Trim().TrimEnd('/');
    }

    if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase)
        && connectionString.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        return connectionString.TrimEnd('/');

    return null!;
}
