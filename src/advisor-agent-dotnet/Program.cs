using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using Azure.Identity;
using A2A;
using SharedServices;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Configure Azure chat client
builder.AddAzureChatCompletionsClient(connectionName: "gpt41",
    configureSettings: settings =>
    {
        settings.TokenCredential = new DefaultAzureCredential();
        settings.EnableSensitiveTelemetryData = true;
    })
    .AddChatClient().ConfigureOptions(options => options.AllowMultipleToolCalls = true);

// Register Cosmos containers for session storage
builder.AddKeyedAzureCosmosContainer("sessions",
    configureClientOptions: (option) => option.Serializer = new CosmosSystemTextJsonSerializer());

// Register Cosmos containers for conversation storage
builder.AddKeyedAzureCosmosContainer("conversations",
    configureClientOptions: (option) => option.Serializer = new CosmosSystemTextJsonSerializer());

// Register session and chat history providers
builder.Services.AddCosmosAgentSessionStore("sessions", opt => { opt.TtlSeconds = 86400 * 7; });
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
AIAgent ResolveA2AAgent(string envVar, string? cardPath = "/.well-known/agent-card.json")
{
    var url = Environment.GetEnvironmentVariable(envVar)
        ?? throw new InvalidOperationException($"{envVar} not configured.");
    var httpClient = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };
    var resolver = new A2ACardResolver(httpClient.BaseAddress!, httpClient, agentCardPath: cardPath);
    return resolver.GetAIAgentAsync(httpClient).Result;
}

// Connect to specialist agents via A2A
var weatherAgent = ResolveA2AAgent("services__weather-agent-python__https__0");
var liftAgent = ResolveA2AAgent(Environment.GetEnvironmentVariable("services__lift-traffic-agent-dotnet__https__0") != null
        ? "services__lift-traffic-agent-dotnet__https__0"
        : "services__lift-traffic-agent-dotnet__http__0",
    "/agenta2a/v1/card");
var safetyAgent = ResolveA2AAgent("services__safety-agent-python__https__0");
var coachAgent = ResolveA2AAgent("services__ski-coach-agent-python__https__0");

// Register the orchestrator agent that uses all 4 remote agents as tools
builder.AddAIAgent("advisor-agent", (sp, key) =>
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

var app = builder.Build();

// Enable CORS
app.UseCors();

// Map A2A endpoint
app.MapA2A("advisor-agent", "/agenta2a", new AgentCard
{
    Name = "advisor-agent",
    Url = app.Configuration["ASPNETCORE_URLS"]?.Split(';')[0] + "/agenta2a" ?? "http://localhost:5200/agenta2a",
    Description = "AlpineAI Ski Resort Advisor - your intelligent ski concierge coordinating weather, lifts, safety, and coaching",
    Version = "1.0",
    DefaultInputModes = ["text"],
    DefaultOutputModes = ["text"],
    Capabilities = new AgentCapabilities
    {
        Streaming = true,
        PushNotifications = false
    },
    Skills = [
        new AgentSkill
        {
            Name = "Ski Resort Advisory",
            Description = "Coordinate weather, lift traffic, safety, and coaching information to provide personalized ski resort recommendations",
            Examples = [
                "I'm intermediate and hate crowds. Where should I ski?",
                "Is it safe to ski today?",
                "Plan my day - I'm an advanced skier",
                "What's the weather like and which lifts have short wait times?"
            ]
        }
    ]
});

app.MapDefaultEndpoints();
app.Run();
