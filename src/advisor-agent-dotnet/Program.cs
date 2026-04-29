using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;
using Azure.AI.Projects;
using Azure.Identity;
using System.Data.Common;
using A2A;
using SharedServices;

using FoundryAgentSessionStore = Microsoft.Agents.AI.Foundry.Hosting.AgentSessionStore;

#pragma warning disable OPENAI001

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddAzureChatCompletionsClient(connectionName: "gpt41",
    configureSettings: settings =>
    {
        settings.TokenCredential = new DefaultAzureCredential();
        settings.EnableSensitiveTelemetryData = true;
    })
    .AddChatClient().ConfigureOptions(options => options.AllowMultipleToolCalls = true);

var projectConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__proj-voice-ski-resort-demo")
    ?? throw new InvalidOperationException("ConnectionStrings__proj-voice-ski-resort-demo is not set.");
var chatConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__gpt41")
    ?? throw new InvalidOperationException("ConnectionStrings__gpt41 is not set.");

DbConnectionStringBuilder projectConnectionBuilder = new() { ConnectionString = projectConnectionString };
DbConnectionStringBuilder chatConnectionBuilder = new() { ConnectionString = chatConnectionString };

var projectEndpoint = GetRequiredConnectionValue(projectConnectionBuilder, "Endpoint");
var deploymentName = GetRequiredConnectionValue(chatConnectionBuilder, "Deployment");

if (!Uri.TryCreate(projectEndpoint, UriKind.Absolute, out var projectUri) || projectUri is null)
{
    throw new InvalidOperationException("ConnectionStrings__proj-voice-ski-resort-demo contains an invalid Endpoint value.");
}

var credential = new DefaultAzureCredential();

// Register Cosmos containers for session storage
builder.AddKeyedAzureCosmosContainer("sessions",
    configureClientOptions: (option) => option.Serializer = new CosmosSystemTextJsonSerializer());

// Register Cosmos containers for conversation storage
builder.AddKeyedAzureCosmosContainer("conversations",
    configureClientOptions: (option) => option.Serializer = new CosmosSystemTextJsonSerializer());

// Register session and chat history providers
builder.Services.AddCosmosAgentSessionStore("sessions", opt => { opt.TtlSeconds = 86400 * 7; });
builder.Services.AddSingleton<FoundryCosmosAgentSessionStore>();
builder.Services.AddSingleton<FoundryAgentSessionStore>(sp => sp.GetRequiredService<FoundryCosmosAgentSessionStore>());
builder.Services.AddKeyedSingleton<FoundryAgentSessionStore>("advisor-agent", (sp, _) => sp.GetRequiredService<FoundryCosmosAgentSessionStore>());
builder.Services.AddCosmosChatHistoryProvider("conversations", (sp, opt) =>
{
    opt.MessageTtlSeconds = 86400 * 7;
});

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
        agentCard.Url = AppendPath(agentCard.Url, endpointPath);
        foreach (var additionalInterface in agentCard.AdditionalInterfaces)
            additionalInterface.Url = AppendPath(additionalInterface.Url, endpointPath);
    }

    return agentCard.AsAIAgent(httpClient);
}

static string AppendPath(string url, string path)
    => $"{url.TrimEnd('/')}/{path.TrimStart('/')}";

// Connect to specialist agents via A2A
var weatherAgent = ResolveA2AAgent("services__weather-agent-python__https__0");
var liftAgent = ResolveA2AAgent(Environment.GetEnvironmentVariable("services__lift-traffic-agent-dotnet__https__0") != null
        ? "services__lift-traffic-agent-dotnet__https__0"
        : "services__lift-traffic-agent-dotnet__http__0",
    "/agenta2a/v1/card");
var safetyAgent = ResolveA2AAgent("services__safety-agent-python__https__0");
var coachAgent = ResolveA2AAgent("services__ski-coach-agent-python__https__0");

const string AdvisorInstructions = @"You are the Ski Resort Advisor, the main AI concierge for AlpineAI ski resort.

You have access to four specialist agents as tools:
- Weather Agent: current conditions, forecasts, storm alerts
- Lift Traffic Agent: lift status, wait times, congestion
- Safety Agent: risk evaluation, slope safety, closures
- Ski Coach Agent: personalized slope recommendations, day plans

IMPORTANT: Only call the agents that are relevant to the user's question. Do NOT call all agents for every question.

Examples:
- ""What's the weather like?"" → call Weather Agent only
- ""Which lifts are open?"" → call Lift Traffic Agent only
- ""Is it safe to ski today?"" → call Safety Agent (and Weather Agent if you need conditions context)
- ""I'm a beginner, where should I ski?"" → call Ski Coach Agent
- ""Plan my full day"" → call multiple agents as needed
- ""Hi"" or ""Thanks"" → respond directly, no agent calls needed

When you DO call agents, synthesize their responses into one clear answer. Mention any safety concerns prominently. Be friendly, concise, and helpful.";

// Register the Foundry-hosted orchestrator agent that uses all 4 remote A2A agents as tools.
var advisorAgentBuilder = builder.AddAIAgent("advisor-agent", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();

    var agentOptions = new ChatClientAgentOptions()
    {
        Name = key,
        Description = "AlpineAI Ski Resort Advisor - your intelligent ski concierge",
        ChatOptions = new ChatOptions()
        {
            Instructions = @"You are the Ski Resort Advisor, the main AI concierge for AlpineAI ski resort.

You have access to four specialist agents as tools:
- Weather Agent: current conditions, forecasts, storm alerts
- Lift Traffic Agent: lift status, wait times, congestion
- Safety Agent: risk evaluation, slope safety, closures
- Ski Coach Agent: personalized slope recommendations, day plans

IMPORTANT: Only call the agents that are relevant to the user's question. Do NOT call all agents for every question.

Examples:
- ""What's the weather like?"" → call Weather Agent only
- ""Which lifts are open?"" → call Lift Traffic Agent only
- ""Is it safe to ski today?"" → call Safety Agent (and Weather Agent if you need conditions context)
- ""I'm a beginner, where should I ski?"" → call Ski Coach Agent
- ""Plan my full day"" → call multiple agents as needed
- ""Hi"" or ""Thanks"" → respond directly, no agent calls needed

When you DO call agents, synthesize their responses into one clear answer. Mention any safety concerns prominently. Be friendly, concise, and helpful.",
            Tools = [
                weatherAgent.AsAIFunction(),
                liftAgent.AsAIFunction(),
                safetyAgent.AsAIFunction(),
                coachAgent.AsAIFunction()
            ]
        },
    }.WithCosmosChatHistoryProvider(sp);

    var agent = chatClient.AsAIAgent(agentOptions, services: sp);

    var ficc = agent.GetService<FunctionInvokingChatClient>();
    ficc?.AllowConcurrentInvocation = true;

    return agent;
}).WithCosmosSessionStore();

// var agentOptions = new ChatClientAgentOptions
// {
//     Name = "advisor-agent",
//     Description = "AlpineAI Ski Resort Advisor - your intelligent ski concierge",
//     ChatOptions = new ChatOptions
//     {
//         ModelId = deploymentName,
//         Instructions = AdvisorInstructions,
//         Tools = [
//             weatherAgent.AsAIFunction(),
//             liftAgent.AsAIFunction(),
//             safetyAgent.AsAIFunction(),
//             coachAgent.AsAIFunction()
//         ]
//     },
// };

// var agent = new AIProjectClient(projectUri, credential).AsAIAgent(agentOptions);

// var ficc = agent.GetService<FunctionInvokingChatClient>();
// ficc?.AllowConcurrentInvocation = true;

builder.Services.AddFoundryResponses();

var app = builder.Build();

// Enable CORS
app.UseCors();

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

sealed class FoundryCosmosAgentSessionStore(CosmosAgentSessionStore inner) : FoundryAgentSessionStore
{
    public override ValueTask SaveSessionAsync(
        AIAgent agent,
        string conversationId,
        AgentSession session,
        CancellationToken cancellationToken = default)
        => inner.SaveSessionAsync(agent, conversationId, session, cancellationToken);

    public override ValueTask<AgentSession> GetSessionAsync(
        AIAgent agent,
        string conversationId,
        CancellationToken cancellationToken = default)
        => inner.GetSessionAsync(agent, conversationId, cancellationToken);
}
