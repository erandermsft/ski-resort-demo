using System.Text;
using SharedServices;
using SharedServices.Models;
using Xunit;

namespace UnitTests.Dotnet;

/// <summary>
/// Unit tests for <see cref="CosmosSystemTextJsonSerializer"/> — the pure serialization
/// policy (camelCase naming, null omission, empty-stream handling) used for Cosmos storage.
/// </summary>
public class CosmosSystemTextJsonSerializerTests
{
    private readonly CosmosSystemTextJsonSerializer _serializer = new();

    private static string ReadAll(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void ToStream_UsesCamelCasePropertyNames()
    {
        var request = new AIChatRequest(
            [new AIChatMessage("Is the north face safe?", "user")],
            SessionState: "abc");

        var json = ReadAll(_serializer.ToStream(request));

        Assert.Contains("\"messages\"", json);
        Assert.Contains("\"content\"", json);
        Assert.Contains("\"role\"", json);
        Assert.Contains("\"sessionState\"", json);
        // PascalCase property names must not leak through.
        Assert.DoesNotContain("\"Messages\"", json);
        Assert.DoesNotContain("\"Content\"", json);
    }

    [Fact]
    public void ToStream_OmitsNullProperties()
    {
        // Context and SessionState are null and should be omitted (WhenWritingNull).
        var request = new AIChatRequest([new AIChatMessage("hi", "user")]);

        var json = ReadAll(_serializer.ToStream(request));

        Assert.DoesNotContain("context", json);
        Assert.DoesNotContain("sessionState", json);
    }

    [Fact]
    public void RoundTrip_PreservesMessageContent()
    {
        var request = new AIChatRequest(
            [
                new AIChatMessage("What's the weather?", "user"),
                new AIChatMessage("Cold and clear.", "assistant", Context: "weather-agent"),
            ],
            SessionState: "session-1");

        var roundTripped = _serializer.FromStream<AIChatRequest>(_serializer.ToStream(request));

        Assert.NotNull(roundTripped);
        Assert.Equal("session-1", roundTripped.SessionState);
        Assert.Equal(2, roundTripped.Messages.Count);
        Assert.Equal("What's the weather?", roundTripped.Messages[0].Content);
        Assert.Equal("user", roundTripped.Messages[0].Role);
        Assert.Equal("assistant", roundTripped.Messages[1].Role);
        Assert.Equal("weather-agent", roundTripped.Messages[1].Context);
    }

    [Fact]
    public void FromStream_EmptySeekableStream_ReturnsDefault()
    {
        using var empty = new MemoryStream();

        var result = _serializer.FromStream<AIChatRequest>(empty);

        Assert.Null(result);
    }
}
