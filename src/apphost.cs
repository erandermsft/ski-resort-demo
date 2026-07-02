#:sdk Aspire.AppHost.Sdk@13.4.6
#:package Aspire.Hosting.Azure.AppContainers@13.4.6
#:package Aspire.Hosting.Foundry@13.4.6-preview.1.26319.6
#:package Aspire.Hosting.Azure.CosmosDB@13.4.6
#:package Aspire.Hosting.Python@13.4.6
#:package Aspire.Hosting.JavaScript@13.4.6
#:package CommunityToolkit.Aspire.Hosting.Golang@13.3.0

#:project ./advisor-agent-dotnet/AdvisorAgent.Dotnet.csproj
#:project ./lift-traffic-agent-dotnet/LiftTrafficAgent.Dotnet.csproj
#:project ./responses-gateway/ResponsesGateway.csproj
#:project ./voice-advisor-agent/VoiceAdvisorAgent.csproj
#:project ./skills-provisioner/SkillsProvisioner.csproj

using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);
const string A2AAgentBaseUrlEnvironmentVariable = "A2A_AGENT_BASE_URL";

var registry = builder.AddAzureContainerRegistry("agents");

var aca = builder.AddAzureContainerAppEnvironment("aca");

var foundry = builder.AddFoundry("aifskiresort");
var project = foundry.AddProject("projvoiceskiresort");

// Share a single ACR across the ACA container apps and the Foundry hosted agents,
// so every containerized workload lands in the same registry (no per-resource
// .WithContainerRegistry(...) needed). Wire it in publish/deploy only: in run mode
// nothing is containerized and the registry is never provisioned, so referencing its
// outputs breaks provisioning of the Foundry project ("No output for name on resource
// agents").
if (builder.ExecutionContext.IsPublishMode)
{
    aca.WithAzureContainerRegistry(registry);
    project.WithAzureContainerRegistry(registry);
}
var deployment = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt51)
    .WithProperties(configure => configure.SkuCapacity = 10);
var voiceDeployment = project.AddModelDeployment("gptrealtime", FoundryModel.OpenAI.GptRealtime)
    .WithProperties(configure => configure.SkuCapacity = 5);

var webSearch = project.AddWebSearchTool("websearch");

var skiResearcher = project.AddPromptAgent("skiresearcher", deployment,
    instructions: """You are a ski researcher agent. Your job is to research and provide information about ski.""")
    .WithTool(webSearch);

#pragma warning disable ASPIRECOSMOSDB001
var cosmos = builder.AddAzureCosmosDB("cosmosdb")
    .RunAsPreviewEmulator(
        emulator =>
        {
            emulator.WithDataExplorer();
            emulator.WithLifetime(ContainerLifetime.Persistent);
        });

// Aspire's vNext preview emulator forces HTTPS-only mode (PROTOCOL=https), so the
// container only opens the gateway (8081) and data explorer (1234). Yet the hosting
// integration still registers a plaintext HTTP "/ready" health probe on container
// port 8080, which the image never opens in HTTPS mode — leaving "cosmosdb" stuck as
// Unhealthy even though the emulator is fully functional. Real readiness is already
// gated by the built-in OnResourceReady hook (ReadAccountAsync + database/container
// creation), so drop the broken probe to let health reflect the actual container state.
foreach (var healthCheck in cosmos.Resource.Annotations.OfType<HealthCheckAnnotation>().ToArray())
{
    cosmos.Resource.Annotations.Remove(healthCheck);
}

var db = cosmos.AddCosmosDatabase("db");
var conversations = db.AddContainer("conversations", "/conversationId");
var sessions = db.AddContainer("sessions", "/conversationId");

// ---------------------------------------------------------------------------
// Data Generator (Go)
// ---------------------------------------------------------------------------
var dataGenerator = builder.AddGolangApp("datagenerator", "./data-generator")
    .WithGoModTidy()
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithComputeEnvironment(aca);

// ---------------------------------------------------------------------------
// Weather Agent (Python)
// ---------------------------------------------------------------------------
var weatherAgent = builder.AddUvicornApp("weatheragent", "./weather-agent-python", "weather_agent_python.main:app")
    .WithUv()
    .WithArgs("--reload-dir", "weather_agent_python", "--reload-dir", "services", "--reload-dir", "tools")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(deployment).WaitFor(deployment)
    .WithReference(dataGenerator).WaitFor(dataGenerator)
    .WithComputeEnvironment(aca);
weatherAgent.WithEnvironment(A2AAgentBaseUrlEnvironmentVariable, weatherAgent.GetEndpoint("http"));

// ---------------------------------------------------------------------------
// Safety Agent (Python)
// ---------------------------------------------------------------------------
var safetyAgent = builder.AddUvicornApp("safetyagent", "./safety-agent-python", "safety_agent_python.main:app")
    .WithUv()
    .WithArgs("--reload-dir", "safety_agent_python", "--reload-dir", "services", "--reload-dir", "tools")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(deployment).WaitFor(deployment)
    .WithReference(dataGenerator).WaitFor(dataGenerator)
    .WithComputeEnvironment(aca);
safetyAgent.WithEnvironment(A2AAgentBaseUrlEnvironmentVariable, safetyAgent.GetEndpoint("http"));

// ---------------------------------------------------------------------------
// Ski Coach Agent (Python)
// ---------------------------------------------------------------------------
var coachAgent = builder.AddUvicornApp("skicoachagent", "./ski-coach-agent-python", "ski_coach_agent_python.main:app")
    .WithUv()
    .WithArgs("--reload-dir", "ski_coach_agent_python", "--reload-dir", "services", "--reload-dir", "tools")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(deployment).WaitFor(deployment)
    .WithReference(dataGenerator).WaitFor(dataGenerator)
    .WithComputeEnvironment(aca);
coachAgent.WithEnvironment(A2AAgentBaseUrlEnvironmentVariable, coachAgent.GetEndpoint("http"));

// ---------------------------------------------------------------------------
// Lift Traffic Agent (.NET)
// ---------------------------------------------------------------------------
var liftAgent = builder.AddProject<Projects.LiftTrafficAgent_Dotnet>("lifttrafficagent")
    .WithExternalHttpEndpoints()
    .WithReference(deployment).WaitFor(deployment)
    .WithReference(dataGenerator).WaitFor(dataGenerator)
    .WithComputeEnvironment(aca);
liftAgent.WithEnvironment(A2AAgentBaseUrlEnvironmentVariable, liftAgent.GetEndpoint("http"));

// ---------------------------------------------------------------------------
// Skills Provisioner (.NET) — run-to-completion: ensures the advisor's skill and
// its (skills-only) toolbox exist in the Foundry project before the advisor starts.
// Run mode only: in publish/deploy the toolbox is already provisioned and the agent
// consumes it directly (and the deploy identity may lack provisioning rights).
// ---------------------------------------------------------------------------
IResourceBuilder<ProjectResource>? skillsProvisioner = null;
if (builder.ExecutionContext.IsRunMode)
{
    skillsProvisioner = builder.AddProject<Projects.SkillsProvisioner>("skillsprovisioner")
        .WithReference(project).WaitFor(project)
        .WithEnvironment("SKILL_NAME", "safety-disclosure")
        .WithEnvironment("TOOLBOX_NAME", "ski-advisor-skills")
        .WithEnvironment("SKILL_PATH", Path.Combine(
            builder.AppHostDirectory, "advisor-agent-dotnet", "skills", "safety-disclosure", "SKILL.md"));
}

// ---------------------------------------------------------------------------
// Advisor Agent (.NET) — Orchestrator
// ---------------------------------------------------------------------------
var advisorAgentBuilder = builder.AddProject<Projects.AdvisorAgent_Dotnet>("advisoragent")
    .WithReference(deployment).WaitFor(deployment)
    .WithReference(weatherAgent).WaitFor(weatherAgent)
    .WithReference(liftAgent).WaitFor(liftAgent)
    .WithReference(safetyAgent).WaitFor(safetyAgent)
    .WithReference(coachAgent).WaitFor(coachAgent)
    .WithReference(skiResearcher).WaitFor(skiResearcher)
    // Foundry Toolbox whose MCP endpoint exposes versioned skills (e.g. safety-disclosure)
    // to the advisor via progressive disclosure. The agent connects fail-soft, so an
    // unprovisioned toolbox simply means no skills are loaded.
    .WithEnvironment("TOOLBOX_NAME", "ski-advisor-skills");

// Ensure the skill + toolbox exist before the advisor connects to the toolbox MCP endpoint.
if (skillsProvisioner is not null)
{
    advisorAgentBuilder.WaitForCompletion(skillsProvisioner);
}

var advisorAgent = advisorAgentBuilder.AsHostedAgent(project);

// ---------------------------------------------------------------------------
// Voice Advisor Agent (.NET) — Voice orchestrator via WebSocket + Voice Live
// ---------------------------------------------------------------------------
var voiceAdvisorAgent = builder.AddProject<Projects.VoiceAdvisorAgent>("voiceadvisoragent")
    .WithReference(project).WaitFor(project)
    .WithReference(deployment).WaitFor(deployment)
    .WithReference(voiceDeployment).WaitFor(voiceDeployment)
    .WithReference(conversations).WaitFor(conversations)
    .WithReference(weatherAgent).WaitFor(weatherAgent)
    .WithReference(liftAgent).WaitFor(liftAgent)
    .WithReference(safetyAgent).WaitFor(safetyAgent)
    .WithReference(coachAgent).WaitFor(coachAgent)
    .WithReference(skiResearcher).WaitFor(skiResearcher)
    .WithComputeEnvironment(aca);

// ---------------------------------------------------------------------------
// Frontend Dashboard (Vite + React)
// ---------------------------------------------------------------------------
var frontend = builder.AddViteApp("frontend", "./frontend", "dev")
    .WithReference(voiceAdvisorAgent).WaitFor(voiceAdvisorAgent)
    .WithReference(dataGenerator).WaitFor(dataGenerator)
    .WithReference(advisorAgent).WaitFor(advisorAgent)
    .WithUrls((e) =>
    {
        e.Urls.Clear();
        e.Urls.Add(new() { Url = "/", DisplayText = "⛷️ Ski Resort Dashboard", Endpoint = e.GetEndpoint("http") });
    })
    .WithComputeEnvironment(aca);

if (builder.ExecutionContext.IsPublishMode)
{
#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    builder.AddProject<Projects.ResponsesGateway>("frontendgateway")
        .WithHttpEndpoint(env: "PORT")
        .WithExternalHttpEndpoints()
        .WithReference(advisorAgent).WaitFor(advisorAgent)
        .WithReference(dataGenerator).WaitFor(dataGenerator)
        .WithReference(voiceAdvisorAgent).WaitFor(voiceAdvisorAgent)
        .WithReference(project).WaitFor(project)
        .WithHttpHealthCheck("/readiness")
        .PublishWithContainerFiles(frontend, "./wwwroot")
        .WithUrls((e) =>
        {
            e.Urls.Clear();
            e.Urls.Add(new() { Url = "/", DisplayText = "⛷️ Ski Resort Dashboard", Endpoint = e.GetEndpoint("http") });
        })
        // Registry comes from the compute environment (aca); this app additionally
        // needs AcrPush because PublishWithContainerFiles builds and pushes an image.
        .WithRoleAssignments(registry, Azure.Provisioning.ContainerRegistry.ContainerRegistryBuiltInRole.AcrPush)
        .WithComputeEnvironment(aca);
#pragma warning restore ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
}

if (builder.ExecutionContext.IsRunMode)
{

    builder.AddViteApp("slides", "../slides", "start")
        .WithUrls((e) =>
        {
            e.Urls.Clear();
            e.Urls.Add(new() { Url = "/", DisplayText = "Slides", Endpoint = e.GetEndpoint("http") });
        });
}

builder.Build().Run();
