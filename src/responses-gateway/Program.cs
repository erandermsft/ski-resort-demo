using System.Net.WebSockets;

const string DataGeneratorClientName = "datagenerator";
const string AdvisorAgentClientName = "advisoragent";
const string VoiceAdvisorEndpoint = "http://voiceadvisoragent";
const string VoiceAdvisorPath = "/ws/voice";
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://+:{port}");

builder.AddServiceDefaults();
builder.Services.AddHttpClient(DataGeneratorClientName, client =>
{
    client.BaseAddress = new Uri("https+http://datagenerator/");
});
builder.Services.AddHttpClient(AdvisorAgentClientName, client =>
{
    client.BaseAddress = new Uri("https+http://advisoragent-ha/");
});

var app = builder.Build();

app.UseFileServer();
app.UseWebSockets();

app.MapGet("/liveness", () => Results.Ok("Healthy"));
app.MapGet("/readiness", () => Results.Ok("Ready"));

app.Map("/api/{**catchAll}", (HttpContext context, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken) =>
    ProxyHttpAsync(context, httpClientFactory, DataGeneratorClientName, cancellationToken));
app.Map("/responses", (HttpContext context, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken) =>
    ProxyHttpAsync(context, httpClientFactory, AdvisorAgentClientName, cancellationToken));
app.Map("/responses/{**catchAll}", (HttpContext context, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken) =>
    ProxyHttpAsync(context, httpClientFactory, AdvisorAgentClientName, cancellationToken));
app.Map("/ws/voice", context => ProxyWebSocketAsync(context, VoiceAdvisorPath));
app.Map("/ws/live", context => ProxyWebSocketAsync(context, VoiceAdvisorPath));

app.MapFallbackToFile("index.html");

app.Run();

static async Task ProxyHttpAsync(
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    string clientName,
    CancellationToken cancellationToken)
{
    using var requestMessage = CreateProxyRequest(context.Request, BuildTargetPath(context.Request));

    var httpClient = httpClientFactory.CreateClient(clientName);
    using var responseMessage = await httpClient.SendAsync(
        requestMessage,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);

    context.Response.StatusCode = (int)responseMessage.StatusCode;
    CopyResponseHeaders(responseMessage, context.Response);

    await responseMessage.Content.CopyToAsync(context.Response.Body, cancellationToken);
}

static string BuildTargetPath(HttpRequest request)
    => $"{request.Path.Value?.TrimStart('/')}{request.QueryString}";

static HttpRequestMessage CreateProxyRequest(HttpRequest request, string targetPath)
{
    var requestMessage = new HttpRequestMessage(new HttpMethod(request.Method), targetPath);

    if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
    {
        requestMessage.Content = new StreamContent(request.Body);
    }

    foreach (var header in request.Headers)
    {
        if (ShouldSkipRequestHeader(header.Key))
        {
            continue;
        }

        if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
        {
            requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    return requestMessage;
}

static async Task ProxyWebSocketAsync(HttpContext context, string targetPath)
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket connection expected", context.RequestAborted);
        return;
    }

    var targetUri = BuildWebSocketUri(context.Request, targetPath);

    using var downstreamSocket = await context.WebSockets.AcceptWebSocketAsync();
    using var upstreamSocket = new ClientWebSocket();
    foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
    {
        upstreamSocket.Options.AddSubProtocol(protocol);
    }

    await upstreamSocket.ConnectAsync(targetUri, context.RequestAborted);

    var downstreamToUpstream = PumpWebSocketAsync(downstreamSocket, upstreamSocket, context.RequestAborted);
    var upstreamToDownstream = PumpWebSocketAsync(upstreamSocket, downstreamSocket, context.RequestAborted);

    await Task.WhenAny(downstreamToUpstream, upstreamToDownstream);
}

static Uri BuildWebSocketUri(HttpRequest request, string targetPath)
{
    var builder = new UriBuilder(VoiceAdvisorEndpoint)
    {
        Scheme = "ws",
        Path = targetPath,
        Query = request.QueryString.HasValue ? request.QueryString.Value![1..] : string.Empty
    };

    return builder.Uri;
}

static async Task PumpWebSocketAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
{
    var buffer = new byte[64 * 1024];

    while (!cancellationToken.IsCancellationRequested && source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
    {
        var result = await source.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await destination.CloseOutputAsync(
                result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                result.CloseStatusDescription,
                cancellationToken);
            return;
        }

        await destination.SendAsync(
            buffer.AsMemory(0, result.Count),
            result.MessageType,
            result.EndOfMessage,
            cancellationToken);
    }
}

static void CopyResponseHeaders(HttpResponseMessage responseMessage, HttpResponse response)
{
    foreach (var header in responseMessage.Headers)
    {
        if (!ShouldSkipResponseHeader(header.Key))
        {
            response.Headers[header.Key] = header.Value.ToArray();
        }
    }

    foreach (var header in responseMessage.Content.Headers)
    {
        if (!ShouldSkipResponseHeader(header.Key))
        {
            response.Headers[header.Key] = header.Value.ToArray();
        }
    }
}

static bool ShouldSkipRequestHeader(string headerName)
    => headerName.Equals("Host", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("TE", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

static bool ShouldSkipResponseHeader(string headerName)
    => headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("TE", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
       || headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

