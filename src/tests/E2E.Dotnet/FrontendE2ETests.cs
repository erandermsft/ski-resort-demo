using Microsoft.Playwright;

namespace E2E.Dotnet;

/// <summary>
/// End-to-end UI test: the Aspire test host starts the whole distributed app, then
/// Playwright drives a real browser against the frontend dashboard.
///
/// Live-gated: skipped unless RUN_LIVE_TESTS=1 (starting the app provisions Azure).
/// Requires Playwright browsers to be installed once:
///   pwsh src/tests/E2E.Dotnet/bin/Debug/net10.0/playwright.ps1 install chromium
/// </summary>
public class FrontendE2ETests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private static bool LiveEnabled =>
        Environment.GetEnvironmentVariable("RUN_LIVE_TESTS") == "1";

    [SkippableFact]
    [Trait("Category", "Live")]
    public async Task Dashboard_Loads_AndRenders()
    {
        Skip.IfNot(LiveEnabled, "Set RUN_LIVE_TESTS=1 to run live E2E tests (provisions Azure, needs Playwright browsers).");

        using var cts = new CancellationTokenSource(DefaultTimeout);
        var ct = cts.Token;

        // 1) Start the full distributed app via the Aspire test host.
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>(ct);
        await using var app = await appHost.BuildAsync(ct).WaitAsync(DefaultTimeout, ct);
        await app.StartAsync(ct).WaitAsync(DefaultTimeout, ct);

        await app.ResourceNotifications
            .WaitForResourceHealthyAsync("frontend", ct)
            .WaitAsync(DefaultTimeout, ct);

        var frontendUrl = app.GetEndpoint("frontend", "http").ToString();

        // 2) Drive the dashboard with a real (headless) browser.
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();

        var response = await page.GotoAsync(frontendUrl, new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60_000,
        });

        Assert.NotNull(response);
        Assert.True(response!.Ok, $"navigation to {frontendUrl} failed with status {response.Status}");

        var title = await page.TitleAsync();
        Assert.Contains("Ski Resort Dashboard", title);

        // React renders into #root — assert the app actually mounted content.
        await page.WaitForSelectorAsync("#root *", new() { Timeout = 30_000 });
    }
}
