using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;
using Azure.AI.Projects;
using Azure.AI.AgentServer.Responses;
using Azure.Identity;
using System.Data.Common;
using A2A;
using Azure.AI.Extensions.OpenAI;
using CreateResponse = Azure.AI.AgentServer.Responses.Models.CreateResponse;

#pragma warning disable OPENAI001

const string AgentResponseIdHeader = "x-agent-response-id";

string port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

var projectConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__projvoiceskiresort")
    ?? throw new InvalidOperationException("ConnectionStrings__projvoiceskiresort is not set.");
var chatConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__gpt41")
    ?? throw new InvalidOperationException("ConnectionStrings__gpt41 is not set.");

DbConnectionStringBuilder projectConnectionBuilder = new() { ConnectionString = projectConnectionString };
DbConnectionStringBuilder chatConnectionBuilder = new() { ConnectionString = chatConnectionString };

var projectEndpoint = GetRequiredConnectionValue(projectConnectionBuilder, "Endpoint");
var deploymentName = GetRequiredConnectionValue(chatConnectionBuilder, "Deployment");

if (!Uri.TryCreate(projectEndpoint, UriKind.Absolute, out var projectUri) || projectUri is null)
{
    throw new InvalidOperationException("ConnectionStrings__projvoiceskiresort contains an invalid Endpoint value.");
}

var credential = new DefaultAzureCredential();

// Helper to resolve a remote agent via A2A
AIAgent ResolveA2AAgent(string envVar, string cardPath = "/.well-known/agent-card.json", string? endpointPath = null)
{
    var url = Environment.GetEnvironmentVariable(envVar)
        ?? throw new InvalidOperationException($"{envVar} not configured.");
    var httpClient = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };
    var resolver = new A2ACardResolver(httpClient.BaseAddress!, httpClient, agentCardPath: cardPath);
    var agentCard = resolver.GetAgentCardAsync().Result;
    if (!string.IsNullOrWhiteSpace(endpointPath))
    {
        foreach (var supportedInterface in agentCard.SupportedInterfaces)
            supportedInterface.Url = AppendPath(supportedInterface.Url, endpointPath);
    }

    return agentCard.AsAIAgent(httpClient);
}

static string AppendPath(string url, string path)
    => $"{url.TrimEnd('/')}/{path.TrimStart('/')}";

// Connect to specialist agents via A2A
var weatherAgent = ResolveA2AAgent(Environment.GetEnvironmentVariable("services__weatheragent__https__0") != null 
    ? "services__weatheragent__https__0" 
    : "services__weatheragent__http__0");
var liftAgent = ResolveA2AAgent(Environment.GetEnvironmentVariable("services__lifttrafficagent__https__0") != null
        ? "services__lifttrafficagent__https__0"
        : "services__lifttrafficagent__http__0");
var safetyAgent = ResolveA2AAgent(Environment.GetEnvironmentVariable("services__safetyagent__https__0") != null
        ? "services__safetyagent__https__0"
        : "services__safetyagent__http__0");
var coachAgent = ResolveA2AAgent(Environment.GetEnvironmentVariable("services__skicoachagent__https__0") != null
        ? "services__skicoachagent__https__0"
        : "services__skicoachagent__http__0");
var foundryProjectClient = new AIProjectClient(projectUri, credential);

// var skiResearcherAgent = await foundryProjectClient.AgentAdministrationClient.GetAgentAsync("ski-researcher");
var skiResearcherAgentReference = new AgentReference(name: Environment.GetEnvironmentVariable("SKIRESEARCHER_AGENTNAME"));
var responseClient = foundryProjectClient.ProjectOpenAIClient.GetProjectResponsesClientForAgent(skiResearcherAgentReference);
var skiResearcherAgent = responseClient.AsIChatClient("gpt41").AsAIAgent(Environment.GetEnvironmentVariable("SKIRESEARCHER_AGENTNAME"), description: "I can search the web. Use me for any generic question about skiing.");

var agent = new AIProjectClient(new Uri(projectEndpoint), new DefaultAzureCredential())
    .GetProjectOpenAIClient()
    .GetProjectResponsesClient()
    .AsIChatClient(deploymentName) // Converts into a Microsoft.Extensions.AI.IChatClient
    .AsBuilder()
    .ConfigureOptions(options => options.AllowMultipleToolCalls = true)
    .UseOpenTelemetry(sourceName: "Foundry.Agents", configure: (cfg) => cfg.EnableSensitiveData = true)    // Enable OpenTelemetry instrumentation with sensitive data
    .Build()
    .AsAIAgent(
        name: "advisor-agent",
        instructions: @"You are the Ski Resort Advisor, the main AI concierge for AlpineAI ski resort.

You have access to four specialist agents as tools:
- Weather Agent: current conditions, forecasts, storm alerts
- Lift Traffic Agent: lift status, wait times, congestion
- Safety Agent: risk evaluation, slope safety, closures
- Ski Coach Agent: personalized slope recommendations, day plans
- Ski Researcher Agent: general ski-related questions

IMPORTANT: Only call the agents that are relevant to the user's question. Do NOT call all agents for every question.
Never use your internal knowledge to answer questions. For generic ski questions, use the Ski Researcher Agent tool to search the web for up-to-date information.

Examples:
- ""What's the weather like?"" → call Weather Agent only
- ""Which lifts are open?"" → call Lift Traffic Agent only
- ""Is it safe to ski today?"" → call Safety Agent (and Weather Agent if you need conditions context)
- ""I'm a beginner, where should I ski?"" → call Ski Coach Agent
- ""Plan my full day"" → call multiple agents as needed
- ""I have a general question about skiing"" → call Ski Researcher Agent
- ""Hi"" or ""Thanks"" → respond directly, no agent calls needed

When you DO call agents, synthesize their responses into one clear answer. Mention any safety concerns prominently. Be friendly, concise, and helpful.",
        tools: [
            weatherAgent.AsAIFunction(),
            liftAgent.AsAIFunction(),
            safetyAgent.AsAIFunction(),
            coachAgent.AsAIFunction(),
            skiResearcherAgent.AsAIFunction()
        ]);

var ficc = agent.GetService<FunctionInvokingChatClient>();
ficc?.AllowConcurrentInvocation = true;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://+:{port}");

builder.AddServiceDefaults();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddFoundryResponses(agent, new FileSystemAgentSessionStore(GetCheckpointDirectory()));
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<HostedSessionIsolationKeyProvider, LocalDevelopmentHostedSessionIsolationKeyProvider>();
}

var app = builder.Build();

// Enable CORS
app.UseCors();

// Work around hosted storage conflicts caused by replayed platform response IDs.
app.Use(UseSdkGeneratedResponseIdsForResponses);

// Map Foundry Responses API endpoint at /responses.
app.MapFoundryResponses();
app.MapGet("/liveness", () => Results.Ok("Healthy"));
app.MapGet("/readiness", () => Results.Ok("Ready"));

// app.MapDefaultEndpoints();
app.Run();

static string GetRequiredConnectionValue(DbConnectionStringBuilder connectionBuilder, string key)
{
    if (!connectionBuilder.TryGetValue(key, out var rawValue) || rawValue is null)
    {
        throw new InvalidOperationException($"Connection string is missing '{key}'.");
    }

    var value = rawValue.ToString();
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Connection string has an empty '{key}' value.");
    }

    return value;
}

static string GetCheckpointDirectory()
{
    var homeDirectory = Environment.GetEnvironmentVariable("HOME");

    return string.IsNullOrWhiteSpace(homeDirectory)
        ? Path.Combine(Directory.GetCurrentDirectory(), FileSystemAgentSessionStore.LocalCheckpointDirectoryName)
        : Path.Combine(homeDirectory, FileSystemAgentSessionStore.LocalCheckpointDirectoryName);
}

static async Task UseSdkGeneratedResponseIdsForResponses(HttpContext context, Func<Task> next)
{
    if (HttpMethods.IsPost(context.Request.Method)
        && string.Equals(context.Request.Path.Value, "/responses", StringComparison.OrdinalIgnoreCase)
        && context.Request.Headers.ContainsKey(AgentResponseIdHeader))
    {
        context.Request.Headers.Remove(AgentResponseIdHeader);
    }

    await next();
}

sealed class LocalDevelopmentHostedSessionIsolationKeyProvider : HostedSessionIsolationKeyProvider
{
    private const string UserIsolationKeyEnvironmentVariable = "HOSTED_USER_ISOLATION_KEY";
    private const string ChatIsolationKeyEnvironmentVariable = "HOSTED_CHAT_ISOLATION_KEY";
    private const string DefaultLocalUserIsolationKey = "local-dev-user";
    private const string DefaultLocalChatIsolationKey = "local-dev-chat";

    public override ValueTask<HostedSessionContext?> GetKeysAsync(
        ResponseContext context,
        CreateResponse request,
        CancellationToken cancellationToken)
    {
        var userId = !string.IsNullOrWhiteSpace(context.Isolation?.UserIsolationKey)
            ? context.Isolation.UserIsolationKey
            : Environment.GetEnvironmentVariable(UserIsolationKeyEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = DefaultLocalUserIsolationKey;
        }

        var chatId = !string.IsNullOrWhiteSpace(context.Isolation?.ChatIsolationKey)
            ? context.Isolation.ChatIsolationKey
            : Environment.GetEnvironmentVariable(ChatIsolationKeyEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(chatId))
        {
            chatId = DefaultLocalChatIsolationKey;
        }

        return ValueTask.FromResult<HostedSessionContext?>(new HostedSessionContext(userId, chatId));
    }
}