using System.Text.RegularExpressions;
using Azure.AI.VoiceLive;
using Microsoft.Agents.AI;

namespace VoiceAdvisorAgent;

/// <summary>
/// Extension methods to convert MAF A2A agents into Voice Live function tool definitions.
/// </summary>
public static partial class VoiceLiveAgentExtensions
{
    private static readonly BinaryData s_queryParameters = BinaryData.FromObjectAsJson(new
    {
        type = "object",
        properties = new
        {
            query = new
            {
                type = "string",
                description = "Input query to invoke the agent"
            }
        },
        required = new[] { "query" }
    });

    /// <summary>
    /// Converts an A2A <see cref="AIAgent"/> into a <see cref="VoiceLiveFunctionDefinition"/>
    /// that can be registered as a tool on a Voice Live session.
    /// </summary>
    public static VoiceLiveFunctionDefinition AsVoiceLiveTool(this AIAgent agent, string fallbackName)
    {
        var toolName = SanitizeAgentName(agent.Name) ?? SanitizeAgentName(fallbackName);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new InvalidOperationException("Voice Live tool name cannot be empty.");
        }

        return new VoiceLiveFunctionDefinition(toolName)
        {
            Description = agent.Description ?? $"Invoke the {agent.Name} agent",
            Parameters = s_queryParameters
        };
    }

    private static string? SanitizeAgentName(string? agentName)
    {
        return agentName is null
            ? agentName
            : InvalidNameCharsRegex().Replace(agentName, "_");
    }

    [GeneratedRegex("[^0-9A-Za-z]+")]
    private static partial Regex InvalidNameCharsRegex();
}
