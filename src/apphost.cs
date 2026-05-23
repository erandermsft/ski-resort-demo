#:sdk Aspire.AppHost.Sdk@13.4.0-preview.1.26262.1
#:package Aspire.Hosting.Azure.AppContainers@13.4.0-preview.1.26262.1
#:package Aspire.Hosting.Foundry@13.4.0-preview.1.26262.1
#:package Aspire.Hosting.Azure.CosmosDB@13.4.0-preview.1.26262.1
#:package Aspire.Hosting.Python@13.4.0-preview.1.26262.1
#:package Aspire.Hosting.JavaScript@13.4.0-preview.1.26262.1
#:package CommunityToolkit.Aspire.Hosting.Golang@13.3.0

#:project ./advisor-agent-dotnet/AdvisorAgent.Dotnet.csproj
#:project ./lift-traffic-agent-dotnet/LiftTrafficAgent.Dotnet.csproj
#:project ./voice-advisor-agent/VoiceAdvisorAgent.csproj

using Aspire.Hosting.Foundry;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Expressions;

var builder = DistributedApplication.CreateBuilder(args);

var aca = builder.AddAzureContainerAppEnvironment("aca");

// var tenantId = builder.AddParameterFromConfiguration("tenant", "Azure:TenantId");

var foundry = builder.AddFoundry("aifskiresort");
var project = foundry.AddProject("projvoiceskiresort")
    // workaround for https://github.com/microsoft/aspire/issues/15971
    .ConfigureInfrastructure(infra =>
    {
        var project = infra.GetProvisionableResources().OfType<CognitiveServicesProject>().Single();

        var foundryAccount = foundry.Resource.AddAsExistingResource(infra);

        var cogUserRa = foundryAccount.CreateRoleAssignment(CognitiveServicesBuiltInRole.CognitiveServicesUser, RoleManagementPrincipalType.ServicePrincipal, project.Identity.PrincipalId);
        // There's a bug in the CDK, see https://github.com/Azure/azure-sdk-for-net/issues/47265
        cogUserRa.Name = BicepFunction.CreateGuid(foundryAccount.Id, project.Id, cogUserRa.RoleDefinitionId);
        infra.Add(cogUserRa);
    });
var deployment = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41)
    .WithProperties(configure => configure.SkuCapacity = 10);
var voiceDeployment = project.AddModelDeployment("gptrealtime", FoundryModel.OpenAI.GptRealtime)
    .WithProperties(configure => configure.SkuCapacity = 5);

var webSearch = project.AddWebSearchTool("websearch");

var skiResearcher =project.AddPromptAgent(deployment, name: "skiresearcher",
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
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(deployment).WaitFor(deployment)
    // .WithEnvironment("AZURE_TENANT_ID", tenantId)
    .WithReference(dataGenerator).WaitFor(dataGenerator)
    .WithComputeEnvironment(aca);

// ---------------------------------------------------------------------------
// Safety Agent (Python)
// ---------------------------------------------------------------------------
var safetyAgent = builder.AddUvicornApp("safetyagent", "./safety-agent-python", "safety_agent_python.main:app")
    .WithUv()
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(deployment).WaitFor(deployment)
    // .WithEnvironment("AZURE_TENANT_ID", tenantId)
    .WithReference(dataGenerator).WaitFor(dataGenerator)
    .WithComputeEnvironment(aca);

// ---------------------------------------------------------------------------
// Ski Coach Agent (Python)
// ---------------------------------------------------------------------------
var coachAgent = builder.AddUvicornApp("skicoachagent", "./ski-coach-agent-python", "ski_coach_agent_python.main:app")
    .WithUv()
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(deployment).WaitFor(deployment)
    // .WithEnvironment("AZURE_TENANT_ID", tenantId)
    .WithReference(dataGenerator).WaitFor(dataGenerator)
    .WithComputeEnvironment(aca);

// ---------------------------------------------------------------------------
// Lift Traffic Agent (.NET)
// ---------------------------------------------------------------------------
var liftAgent = builder.AddProject<Projects.LiftTrafficAgent_Dotnet>("lifttrafficagent")
    .WithExternalHttpEndpoints()
    .WithReference(deployment).WaitFor(deployment)
    .WithReference(dataGenerator).WaitFor(dataGenerator)
    .WithComputeEnvironment(aca);

// ---------------------------------------------------------------------------
// Advisor Agent (.NET) — Orchestrator
// ---------------------------------------------------------------------------
var advisorAgent = builder.AddProject<Projects.AdvisorAgent_Dotnet>("advisoragent")
    .WithHttpEndpoint(targetPort: 9000)
    .WithReference(project).WaitFor(project)
    .WithReference(deployment).WaitFor(deployment)
    .WithReference(weatherAgent).WaitFor(weatherAgent)
    .WithReference(liftAgent).WaitFor(liftAgent)
    .WithReference(safetyAgent).WaitFor(safetyAgent)
    .WithReference(coachAgent).WaitFor(coachAgent)
    .WithReference(skiResearcher).WaitFor(skiResearcher);

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

advisorAgent.PublishAsHostedAgent(project);

if (builder.ExecutionContext.IsRunMode)
{
// ---------------------------------------------------------------------------
// Frontend Dashboard (Vite + React)
// ---------------------------------------------------------------------------

    builder.AddViteApp("frontend", "./frontend", "dev")
        .WithReference(advisorAgent).WaitFor(advisorAgent)
        .WithReference(voiceAdvisorAgent).WaitFor(voiceAdvisorAgent)
        .WithReference(dataGenerator).WaitFor(dataGenerator)
        .WithUrls((e) =>
        {
            e.Urls.Clear();
            e.Urls.Add(new() { Url = "/", DisplayText = "⛷️ Ski Resort Dashboard", Endpoint = e.GetEndpoint("http") });
        });

    builder.AddViteApp("slides", "../slides", "start")
        .WithUrls((e) =>
        {
            e.Urls.Clear();
            e.Urls.Add(new() { Url = "/", DisplayText = "Slides", Endpoint = e.GetEndpoint("http") });
        });
}

builder.Build().Run();
