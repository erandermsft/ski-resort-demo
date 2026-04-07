using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SharedServices;

/// <summary>
/// Extension methods for configuring <see cref="CosmosChatHistoryProvider"/> with <see cref="ChatClientAgentOptions"/>.
/// </summary>
public static class CosmosChatHistoryProviderExtensions
{
    private static readonly Func<AgentSession?, CosmosChatHistoryProvider.State> s_defaultStateInitializer =
        _ => new CosmosChatHistoryProvider.State(Guid.NewGuid().ToString("N"));

    private static CosmosChatHistoryProvider BuildProvider(
        CosmosClient cosmosClient,
        string databaseId,
        string containerId,
        Func<AgentSession?, CosmosChatHistoryProvider.State> stateInitializer,
        CosmosChatHistoryProviderOptions options,
        bool ownsClient = false,
        ILogger<CosmosChatHistoryProvider>? logger = null)
    {
        var provider = new CosmosChatHistoryProvider(
            cosmosClient, databaseId, containerId, stateInitializer,
            ownsClient, options.StateKey,
            options.ProvideOutputMessageFilter, options.StoreInputMessageFilter, logger)
        {
            ChatReducer = options.ChatReducer,
            ReductionStoragePolicy = options.ReductionStoragePolicy ?? ReductionStoragePolicy.Clear,
        };

        if (options.MaxItemCount.HasValue) provider.MaxItemCount = options.MaxItemCount.Value;
        if (options.MaxBatchSize.HasValue) provider.MaxBatchSize = options.MaxBatchSize.Value;
        if (options.MaxMessagesToRetrieve.HasValue) provider.MaxMessagesToRetrieve = options.MaxMessagesToRetrieve;
        if (options.MessageTtlSeconds.HasValue) provider.MessageTtlSeconds = options.MessageTtlSeconds;
        options.ConfigureProvider?.Invoke(provider);

        return provider;
    }

    private static CosmosChatHistoryProviderOptions ResolveOptions(
        CosmosChatHistoryProviderOptions? baseOptions,
        Action<CosmosChatHistoryProviderOptions>? configure)
    {
        var resolved = baseOptions?.Clone() ?? new CosmosChatHistoryProviderOptions();
        configure?.Invoke(resolved);
        return resolved;
    }

    // ── Aspire / DI registration ──

    public static IServiceCollection AddCosmosChatHistoryProvider(
        this IServiceCollection services,
        string containerServiceKey,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerServiceKey);

        var options = new CosmosChatHistoryProviderOptions();
        configure?.Invoke(options);

        services.AddSingleton(new CosmosChatHistoryProviderRegistration(containerServiceKey, options));
        return services;
    }

    public static IServiceCollection AddCosmosChatHistoryProvider(
        this IServiceCollection services,
        string containerServiceKey,
        Action<IServiceProvider, CosmosChatHistoryProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerServiceKey);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton(sp =>
        {
            var options = new CosmosChatHistoryProviderOptions();
            configure(sp, options);
            return new CosmosChatHistoryProviderRegistration(containerServiceKey, options);
        });

        return services;
    }

    // ── Aspire / DI consumption ──

    [RequiresUnreferencedCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with trimming.")]
    [RequiresDynamicCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with NativeAOT.")]
    public static ChatClientAgentOptions WithCosmosChatHistoryProvider(
        this ChatClientAgentOptions options,
        IServiceProvider serviceProvider,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var registration = serviceProvider.GetRequiredService<CosmosChatHistoryProviderRegistration>();
        var container = serviceProvider.GetRequiredKeyedService<Container>(registration.ContainerServiceKey);
        var logger = serviceProvider.GetService<ILogger<CosmosChatHistoryProvider>>();
        var providerOptions = ResolveOptions(registration.Options, configure);

        options.ChatHistoryProvider = BuildProvider(
            container.Database.Client, container.Database.Id, container.Id,
            s_defaultStateInitializer, providerOptions, logger: logger);

        return options;
    }

    // ── Standalone (no Aspire) ──

    [RequiresUnreferencedCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with trimming.")]
    [RequiresDynamicCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with NativeAOT.")]
    public static ChatClientAgentOptions WithCosmosDBChatHistoryProvider(
        this ChatClientAgentOptions options,
        string connectionString,
        string databaseId,
        string containerId,
        Func<AgentSession?, CosmosChatHistoryProvider.State>? stateInitializer = null,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var providerOptions = ResolveOptions(null, configure);
        options.ChatHistoryProvider = BuildProvider(
            new CosmosClient(connectionString), databaseId, containerId,
            stateInitializer ?? s_defaultStateInitializer, providerOptions, ownsClient: true);

        return options;
    }

    [RequiresUnreferencedCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with trimming.")]
    [RequiresDynamicCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with NativeAOT.")]
    public static ChatClientAgentOptions WithCosmosDBChatHistoryProviderUsingManagedIdentity(
        this ChatClientAgentOptions options,
        string accountEndpoint,
        string databaseId,
        string containerId,
        TokenCredential tokenCredential,
        Func<AgentSession?, CosmosChatHistoryProvider.State>? stateInitializer = null,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenCredential);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountEndpoint);

        var providerOptions = ResolveOptions(null, configure);
        options.ChatHistoryProvider = BuildProvider(
            new CosmosClient(accountEndpoint, tokenCredential), databaseId, containerId,
            stateInitializer ?? s_defaultStateInitializer, providerOptions, ownsClient: true);

        return options;
    }

    [RequiresUnreferencedCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with trimming.")]
    [RequiresDynamicCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with NativeAOT.")]
    public static ChatClientAgentOptions WithCosmosDBChatHistoryProvider(
        this ChatClientAgentOptions options,
        CosmosClient cosmosClient,
        string databaseId,
        string containerId,
        Func<AgentSession?, CosmosChatHistoryProvider.State>? stateInitializer = null,
        Action<CosmosChatHistoryProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cosmosClient);

        var providerOptions = ResolveOptions(null, configure);
        options.ChatHistoryProvider = BuildProvider(
            cosmosClient, databaseId, containerId,
            stateInitializer ?? s_defaultStateInitializer, providerOptions);

        return options;
    }
}

internal sealed class CosmosChatHistoryProviderRegistration(string containerServiceKey, CosmosChatHistoryProviderOptions options)
{
    public string ContainerServiceKey { get; } = containerServiceKey ?? throw new ArgumentNullException(nameof(containerServiceKey));
    public CosmosChatHistoryProviderOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));
}

/// <summary>
/// Options for configuring <see cref="CosmosChatHistoryProvider"/> behavior.
/// </summary>
public sealed class CosmosChatHistoryProviderOptions
{
    public string? StateKey { get; set; }

    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? ProvideOutputMessageFilter { get; set; }

    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? StoreInputMessageFilter { get; set; }

#pragma warning disable MEAI001
    public IChatReducer? ChatReducer { get; set; }
#pragma warning restore MEAI001

    public ReductionStoragePolicy? ReductionStoragePolicy { get; set; }

    public int? MaxItemCount { get; set; }

    public int? MaxBatchSize { get; set; }

    public int? MaxMessagesToRetrieve { get; set; }

    public int? MessageTtlSeconds { get; set; }

    public Action<CosmosChatHistoryProvider>? ConfigureProvider { get; set; }

    internal CosmosChatHistoryProviderOptions Clone() => new()
    {
        StateKey = StateKey,
        ProvideOutputMessageFilter = ProvideOutputMessageFilter,
        StoreInputMessageFilter = StoreInputMessageFilter,
        ChatReducer = ChatReducer,
        ReductionStoragePolicy = ReductionStoragePolicy,
        MaxItemCount = MaxItemCount,
        MaxBatchSize = MaxBatchSize,
        MaxMessagesToRetrieve = MaxMessagesToRetrieve,
        MessageTtlSeconds = MessageTtlSeconds,
        ConfigureProvider = ConfigureProvider
    };
}
