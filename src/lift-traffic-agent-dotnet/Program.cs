using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.AI;
using Azure.Identity;
using A2A;
using A2A.AspNetCore;
using LiftTrafficAgent.Dotnet.Services;
using LiftTrafficAgent.Dotnet.Tools;
using Microsoft.Agents.AI.OpenAI;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Configure Azure chat client
builder.AddAzureChatCompletionsClient(connectionName: "gpt41",
    configureSettings: settings =>
    {
        settings.TokenCredential = new DefaultAzureCredential();
        settings.EnableSensitiveTelemetryData = true;
    })
    .AddChatClient();

// Register HttpClientFactory for LiftDataService
builder.Services.AddHttpClient();

// Register services
builder.Services.AddSingleton<LiftDataService>();
builder.Services.AddSingleton<LiftTrafficTools>();

// Register the agent
var liftAgentBuilder = builder.AddAIAgent("lifttrafficagent", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    var tools = sp.GetRequiredService<LiftTrafficTools>().GetFunctions();

    var agent = chatClient.AsAIAgent(
        instructions: @"You are the Lift Traffic Agent for AlpineAI ski resort. You provide real-time lift status, wait times, and congestion analysis. Help skiers find the least crowded areas and plan efficient lift usage.",
        name: key,
        description: "Lift congestion and traffic intelligence agent",
        tools: tools.ToArray()
    );

    return agent;
});

// Configure CORS for frontend access
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Enable CORS
app.UseCors();

var agentBaseUrl = app.Configuration["ASPNETCORE_URLS"]?.Split(';')[0] ?? "http://localhost:5196";
var agentUrl = $"{agentBaseUrl}/agenta2a";
var agentCard = new AgentCard
{
    Name = "lifttrafficagent",
    Description = "Lift congestion and traffic intelligence agent",
    Url = agentUrl,
    Version = "1.0",
    PreferredTransport = AgentTransport.JsonRpc,
    AdditionalInterfaces = [
        new AgentInterface
        {
            Url = agentUrl,
            Transport = AgentTransport.JsonRpc
        }
    ],
    DefaultInputModes = ["text"],
    DefaultOutputModes = ["text"],
    Capabilities = new AgentCapabilities
    {
        Streaming = true,
        PushNotifications = false
    },
    Skills = [
        new A2A.AgentSkill
        {
            Name = "Lift Traffic Analysis",
            Description = "Real-time lift status, wait times, and congestion analysis",
            Examples = [
                "What's the current wait time for Lift 1?",
                "Show me all lift wait times",
                "Which area of the resort is least crowded?",
                "Where should I ski to avoid long lines?"
            ]
        }
    ]
};

// Map A2A endpoint
app.MapA2A(liftAgentBuilder, "/agenta2a", agentCard);

app.MapDefaultEndpoints();
app.Run();
