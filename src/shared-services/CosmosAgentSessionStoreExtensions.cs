using Microsoft.Agents.AI.Hosting;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharedServices;

/// <summary>
/// Extension methods for registering <see cref="CosmosAgentSessionStore"/>.
/// </summary>
public static class CosmosAgentSessionStoreExtensions
{
    /// <summary>
    /// Adds <see cref="CosmosAgentSessionStore"/> using a keyed Container service.
    /// </summary>
    public static IServiceCollection AddCosmosAgentSessionStore(
        this IServiceCollection services,
        string containerServiceKey,
        Action<CosmosAgentSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(containerServiceKey);

        var options = new CosmosAgentSessionStoreOptions();
        configure?.Invoke(options);

        services.AddSingleton(sp =>
            new CosmosAgentSessionStore(
                sp.GetRequiredKeyedService<Container>(containerServiceKey),
                sp.GetRequiredService<ILogger<CosmosAgentSessionStore>>(),
                options.TtlSeconds));

        return services;
    }

    /// <summary>
    /// Adds <see cref="CosmosAgentSessionStore"/> using an existing CosmosClient.
    /// </summary>
    public static IServiceCollection AddCosmosAgentSessionStore(
        this IServiceCollection services,
        CosmosClient client,
        string databaseId,
        string containerId,
        Action<CosmosAgentSessionStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(containerId);

        var options = new CosmosAgentSessionStoreOptions();
        configure?.Invoke(options);

        var container = client.GetContainer(databaseId, containerId);
        services.AddSingleton(sp =>
            new CosmosAgentSessionStore(
                container,
                sp.GetRequiredService<ILogger<CosmosAgentSessionStore>>(),
                options.TtlSeconds));

        return services;
    }

    /// <summary>
    /// Configures the hosted agent builder to use the registered <see cref="CosmosAgentSessionStore"/>.
    /// </summary>
    public static IHostedAgentBuilder WithCosmosSessionStore(this IHostedAgentBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithSessionStore((sp, _) => sp.GetRequiredService<CosmosAgentSessionStore>());
    }
}
