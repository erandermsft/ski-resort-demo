using System.Text.Json;

namespace IntegrationTests.Dotnet;

/// <summary>
/// Aspire distributed-application tests. The wiring test builds the application model and
/// asserts the resource graph without contacting Azure. The "Live" tests actually start the
/// app (which provisions Azure Foundry + the Cosmos emulator) and are skipped unless
/// RUN_LIVE_TESTS=1 so a plain `dotnet test` never reaches out to Azure.
/// </summary>
public class AppHostTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private static bool LiveEnabled =>
        Environment.GetEnvironmentVariable("RUN_LIVE_TESTS") == "1";

    // -----------------------------------------------------------------------
    // Cheap: model/wiring assertions only. No StartAsync, no Azure calls.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AppModel_ContainsExpectedResources()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>();

        var names = appHost.Resources.Select(r => r.Name).ToHashSet();

        foreach (var expected in new[]
                 {
                     "datagenerator",
                     "weatheragent",
                     "safetyagent",
                     "skicoachagent",
                     "lifttrafficagent",
                     "advisoragent",
                     "voiceadvisoragent",
                     "frontend",
                     "cosmosdb",
                 })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public async Task Advisor_ReferencesSpecialistAgents()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>();

        var advisor = Assert.Single(appHost.Resources, r => r.Name == "advisoragent");

        // The advisor waits on each specialist, expressed as WaitAnnotations in the model.
        var waited = advisor.Annotations
            .OfType<WaitAnnotation>()
            .Select(w => w.Resource.Name)
            .ToHashSet();

        foreach (var specialist in new[] { "weatheragent", "lifttrafficagent", "safetyagent", "skicoachagent" })
        {
            Assert.Contains(specialist, waited);
        }
    }

    // -----------------------------------------------------------------------
    // Live: starts the whole distributed app (provisions Azure). Gated.
    // -----------------------------------------------------------------------
    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task DataGenerator_IsHealthy_AndServesResortState()
    {
        Skip.IfNot(LiveEnabled, "Set RUN_LIVE_TESTS=1 to run live Aspire tests (provisions Azure).");

        using var cts = new CancellationTokenSource(DefaultTimeout);
        var ct = cts.Token;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>(ct);

        await using var app = await appHost.BuildAsync(ct).WaitAsync(DefaultTimeout, ct);
        await app.StartAsync(ct).WaitAsync(DefaultTimeout, ct);

        await app.ResourceNotifications
            .WaitForResourceHealthyAsync("datagenerator", ct)
            .WaitAsync(DefaultTimeout, ct);

        using var client = app.CreateHttpClient("datagenerator");
        using var response = await client.GetAsync("/api/current-state", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("lifts", out var lifts) && lifts.GetArrayLength() > 0,
            "expected non-empty 'lifts' array in resort state");
        Assert.True(root.TryGetProperty("slopes", out var slopes) && slopes.GetArrayLength() > 0,
            "expected non-empty 'slopes' array in resort state");
        Assert.True(root.TryGetProperty("weather", out _), "expected 'weather' in resort state");
        Assert.True(root.TryGetProperty("safety", out _), "expected 'safety' in resort state");
    }

    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task LiftTrafficAgent_PublishesAgentCard()
    {
        Skip.IfNot(LiveEnabled, "Set RUN_LIVE_TESTS=1 to run live Aspire tests (provisions Azure).");

        using var cts = new CancellationTokenSource(DefaultTimeout);
        var ct = cts.Token;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>(ct);

        await using var app = await appHost.BuildAsync(ct).WaitAsync(DefaultTimeout, ct);
        await app.StartAsync(ct).WaitAsync(DefaultTimeout, ct);

        await app.ResourceNotifications
            .WaitForResourceHealthyAsync("lifttrafficagent", ct)
            .WaitAsync(DefaultTimeout, ct);

        using var client = app.CreateHttpClient("lifttrafficagent");
        using var response = await client.GetAsync("/.well-known/agent-card.json", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var card = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("lift", card, StringComparison.OrdinalIgnoreCase);
    }
}
