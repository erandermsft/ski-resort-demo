using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

// Idempotently ensures the advisor's skill and its toolbox exist in the Foundry project, then exits.
// Wired into the AppHost as a run-to-completion resource the advisor waits on.
//
// Uses the Skills/Toolboxes REST API rather than the SDK because the documented .NET admin surface
// (AgentAdministrationClient.GetAgentSkills/GetAgentToolboxes) isn't in the released package yet, and
// `azd ai toolbox create` rejects a skills-only toolbox (it only counts tools/connections).

var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? ParseEndpoint(Environment.GetEnvironmentVariable("ConnectionStrings__projvoiceskiresort"))
    ?? throw new InvalidOperationException("Set FOUNDRY_PROJECT_ENDPOINT or ConnectionStrings__projvoiceskiresort.");
var skill = Environment.GetEnvironmentVariable("SKILL_NAME") ?? "safety-disclosure";
var toolbox = Environment.GetEnvironmentVariable("TOOLBOX_NAME") ?? "ski-advisor-skills";
var skillFile = Environment.GetEnvironmentVariable("SKILL_PATH")
    ?? Path.Combine(AppContext.BaseDirectory, "skills", skill, "SKILL.md");

var token = (await new DefaultAzureCredential().GetTokenAsync(
    new TokenRequestContext(["https://ai.azure.com/.default"]), default)).Token;
using var http = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
Console.WriteLine($"[skills-provisioner] project = {endpoint}");

// 1) Skill: upload SKILL.md and let the server parse the front matter into description + instructions.
if (await Exists($"skills/{skill}?api-version=v1", "Skills=V1Preview"))
{
    Console.WriteLine($"[skills-provisioner] skill '{skill}' already exists.");
}
else
{
    using var form = new MultipartFormDataContent();
    var md = new ByteArrayContent(await File.ReadAllBytesAsync(skillFile));
    md.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
    form.Add(md, "files", "SKILL.md");
    await Send(new(HttpMethod.Post, $"skills/{skill}/versions?api-version=v1") { Content = form },
        "Skills=V1Preview", $"create skill '{skill}'");
}

// 2) Toolbox: reference the skill with an empty tools array (the part the azd CLI can't express).
if (await Exists($"toolboxes/{toolbox}?api-version=v1", "Toolboxes=V1Preview"))
{
    Console.WriteLine($"[skills-provisioner] toolbox '{toolbox}' already exists.");
}
else
{
    var body = JsonSerializer.Serialize(new
    {
        description = "Skills for the AlpineAI ski resort advisor.",
        tools = Array.Empty<object>(),
        skills = new[] { new { type = "skill_reference", name = skill } },
    });
    await Send(new(HttpMethod.Post, $"toolboxes/{toolbox}/versions?api-version=v1")
    { Content = new StringContent(body, Encoding.UTF8, "application/json") },
        "Toolboxes=V1Preview", $"create toolbox '{toolbox}' -> skill '{skill}'");
}

Console.WriteLine("[skills-provisioner] done.");

async Task<bool> Exists(string path, string feature)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, path);
    req.Headers.Add("Foundry-Features", feature);
    using var res = await http.SendAsync(req);
    return res.StatusCode == HttpStatusCode.OK;
}

async Task Send(HttpRequestMessage req, string feature, string what)
{
    req.Headers.Add("Foundry-Features", feature);
    using var res = await http.SendAsync(req);
    if (!res.IsSuccessStatusCode)
        throw new InvalidOperationException($"{what} failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
    Console.WriteLine($"[skills-provisioner] {what}: {(int)res.StatusCode}");
}

static string? ParseEndpoint(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString)) return null;
    var b = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
    return b.TryGetValue("Endpoint", out var v) ? v as string : null;
}
