// Copyright (c) Microsoft. All rights reserved.

using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace SharedServices;

/// <summary>
/// Provides a Cosmos DB implementation of the <see cref="ChatHistoryProvider"/> abstract class.
/// </summary>
[RequiresUnreferencedCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with trimming.")]
[RequiresDynamicCode("The CosmosChatHistoryProvider uses JSON serialization which is incompatible with NativeAOT.")]
public sealed class CosmosChatHistoryProvider : ChatHistoryProvider, IDisposable
{
    private readonly ProviderSessionState<State> _sessionState;
    private readonly CosmosClient _cosmosClient;
    private readonly Container _container;
    private readonly bool _ownsClient;
    private readonly ILogger<CosmosChatHistoryProvider>? _logger;
    private bool _disposed;

    private CosmosChatMessageRepository _messageRepository;

    private static readonly JsonSerializerOptions s_defaultJsonOptions = CreateDefaultJsonOptions();

    private static JsonSerializerOptions CreateDefaultJsonOptions()
    {
        var options = new JsonSerializerOptions();
#if NET9_0_OR_GREATER
        options.TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
#endif
        return options;
    }

    public int MaxItemCount
    {
        get => _messageRepository.MaxItemCount;
        set => _messageRepository.MaxItemCount = value;
    }

    public int MaxBatchSize
    {
        get => _messageRepository.MaxBatchSize;
        set => _messageRepository.MaxBatchSize = value;
    }

    public int? MaxMessagesToRetrieve { get; set; }

    public int? MessageTtlSeconds { get; set; } = 86400;

    public string DatabaseId { get; init; }

    public string ContainerId { get; init; }

    /// <inheritdoc />
    public override string StateKey => this._sessionState.StateKey;

#pragma warning disable MEAI001

    public IChatReducer? ChatReducer { get; init; } = null;

    public ReductionStoragePolicy ReductionStoragePolicy { get; init; } = ReductionStoragePolicy.Clear;

#pragma warning restore MEAI001

    public CosmosChatHistoryProvider(
        CosmosClient cosmosClient,
        string databaseId,
        string containerId,
        Func<AgentSession?, State> stateInitializer,
        bool ownsClient = false,
        string? stateKey = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null,
        ILogger<CosmosChatHistoryProvider>? logger = null)
        : base(provideOutputMessageFilter, storeInputMessageFilter)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(stateInitializer);

        this._sessionState = new ProviderSessionState<State>(stateInitializer, stateKey ?? this.GetType().Name);
        this._cosmosClient = cosmosClient;
        this.DatabaseId = databaseId;
        this.ContainerId = containerId;
        this._container = this._cosmosClient.GetContainer(databaseId, containerId);
        this._ownsClient = ownsClient;
        this._logger = logger;

        _messageRepository = new CosmosChatMessageRepository(cosmosClient, databaseId, containerId);
    }

    public CosmosChatHistoryProvider(
        string connectionString,
        string databaseId,
        string containerId,
        Func<AgentSession?, State> stateInitializer,
        string? stateKey = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null,
        ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(CreateCosmosClient(connectionString), databaseId, containerId, stateInitializer, ownsClient: true, stateKey, provideOutputMessageFilter, storeInputMessageFilter, logger)
    {
    }

    private static CosmosClient CreateCosmosClient(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new CosmosClient(connectionString);
    }

    public CosmosChatHistoryProvider(
        string accountEndpoint,
        TokenCredential tokenCredential,
        string databaseId,
        string containerId,
        Func<AgentSession?, State> stateInitializer,
        string? stateKey = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideOutputMessageFilter = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? storeInputMessageFilter = null,
        ILogger<CosmosChatHistoryProvider>? logger = null)
        : this(CreateCosmosClient(accountEndpoint, tokenCredential), databaseId, containerId, stateInitializer, ownsClient: true, stateKey, provideOutputMessageFilter, storeInputMessageFilter, logger)
    {
    }

    private static CosmosClient CreateCosmosClient(string accountEndpoint, TokenCredential tokenCredential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountEndpoint);
        ArgumentNullException.ThrowIfNull(tokenCredential);
        return new CosmosClient(accountEndpoint, tokenCredential);
    }

    private static bool UseHierarchicalPartitioning(State state) =>
        state.TenantId is not null && state.UserId is not null;

    private static PartitionKey BuildPartitionKey(State state)
    {
        if (UseHierarchicalPartitioning(state))
        {
            return new PartitionKeyBuilder()
                .Add(state.TenantId)
                .Add(state.UserId)
                .Add(state.ConversationId)
                .Build();
        }

        return new PartitionKey(state.ConversationId);
    }

    private static PartitionKey BuildArchivePartitionKey(State state, string newConversationId)
    {
        if (UseHierarchicalPartitioning(state))
        {
            return new PartitionKeyBuilder()
                .Add(state.TenantId)
                .Add(state.UserId)
                .Add(newConversationId)
                .Build();
        }

        return new PartitionKey(newConversationId);
    }

    /// <inheritdoc />
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
#pragma warning disable CA1513
        if (this._disposed)
            throw new ObjectDisposedException(this.GetType().FullName);
#pragma warning restore CA1513

        var state = this._sessionState.GetOrInitializeState(context.Session);
        var partitionKey = BuildPartitionKey(state);

        var documents = await this._messageRepository.GetMessageDocumentAsync(state.ConversationId, partitionKey, this.MaxMessagesToRetrieve, cancellationToken).ConfigureAwait(false);

        var messages = new List<ChatMessage>();
        foreach (var document in documents)
        {
            if (string.IsNullOrEmpty(document.Message)) continue;

            if (JsonSerializer.Deserialize<ChatMessage>(document.Message, s_defaultJsonOptions) is { } message)
                messages.Add(message);
        }

        if (!this.MaxMessagesToRetrieve.HasValue && this.ChatReducer is not null)
        {
            var initialCount = messages.Count;
            this._logger?.LogDebug("Evaluating reduction for conversation {ConversationId} with {MessageCount} messages.", state.ConversationId, initialCount);

            messages = [.. await ChatReducer.ReduceAsync(messages, cancellationToken).ConfigureAwait(false)];

            if (messages.Count < initialCount)
            {
                this._logger?.LogInformation(
                    "Reducer reduced messages for conversation {ConversationId} from {InitialCount} to {FinalCount}.",
                    state.ConversationId, initialCount, messages.Count);

                await ApplyReductionStrategyAsync(state, documents, messages, cancellationToken).ConfigureAwait(false);
            }
        }

        return messages;
    }

    /// <inheritdoc />
    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
#pragma warning disable CA1513
        if (this._disposed)
            throw new ObjectDisposedException(this.GetType().FullName);
#pragma warning restore CA1513

        var state = this._sessionState.GetOrInitializeState(context.Session);
        var messages = context.RequestMessages.Concat(context.ResponseMessages ?? []).ToList();
        if (messages.Count == 0)
            return;

        var partitionKey = BuildPartitionKey(state);

        var documents = new List<CosmosMessageDocument>(messages.Count);
        var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var message in messages)
            documents.Add(this.CreateMessageDocument(state, message, currentTimestamp));

        await _messageRepository.StoreDocumentsAsync(documents, partitionKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> GetMessageCountAsync(AgentSession? session, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var state = this._sessionState.GetOrInitializeState(session);
        var partitionKey = BuildPartitionKey(state);

        return await _messageRepository.GetDocumentCountAsync(state.ConversationId, partitionKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ClearMessagesAsync(AgentSession? session, CancellationToken cancellationToken = default)
    {
        var state = this._sessionState.GetOrInitializeState(session);
        var partitionKey = BuildPartitionKey(state);

        return await _messageRepository.DeleteDocumentsAsync(state.ConversationId, partitionKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!this._disposed)
        {
            if (this._ownsClient)
                this._cosmosClient?.Dispose();

            this._logger?.LogDebug("Disposed CosmosChatHistoryProvider for {DatabaseId}/{ContainerId}.", this.DatabaseId, this.ContainerId);
            this._disposed = true;
        }
    }

    private CosmosMessageDocument CreateMessageDocument(State state, ChatMessage message, long timestamp)
    {
        var useHierarchical = UseHierarchicalPartitioning(state);

        return new CosmosMessageDocument
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = state.ConversationId,
            Timestamp = timestamp,
            MessageId = message.MessageId,
            Role = message.Role.Value,
            Message = JsonSerializer.Serialize(message, s_defaultJsonOptions),
            Type = "ChatMessage",
            Ttl = this.MessageTtlSeconds,
            TenantId = useHierarchical ? state.TenantId : null,
            UserId = useHierarchical ? state.UserId : null,
            SessionId = useHierarchical ? state.ConversationId : null
        };
    }

    private async Task ApplyReductionStrategyAsync(State state, List<CosmosMessageDocument> originalDocuments, List<ChatMessage> compressedMessages, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        this._logger?.LogInformation(
            "Applying reduction policy {Policy} for conversation {ConversationId} with {MessageCount} reduced messages.",
            this.ReductionStoragePolicy, state.ConversationId, compressedMessages.Count);

        string actualConversationId = state.ConversationId;

        if (ReductionStoragePolicy == ReductionStoragePolicy.Archive)
        {
            var archiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string archiveConversationId = $"{actualConversationId}_archived_{archiveTimestamp}";
            var archivedPartitionKey = BuildArchivePartitionKey(state, archiveConversationId);

            await _messageRepository.CopyDocumentsAsync(originalDocuments, archiveConversationId, archivedPartitionKey, cancellationToken).ConfigureAwait(false);
        }

        await _messageRepository.DeleteDocumentsAsync(conversationId: actualConversationId, partitionKey: BuildPartitionKey(state), cancellationToken).ConfigureAwait(false);

        if (compressedMessages.Count > 0)
        {
            var partitionKey = BuildPartitionKey(state);

            var documents = new List<CosmosMessageDocument>(compressedMessages.Count);
            var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var message in compressedMessages)
                documents.Add(this.CreateMessageDocument(state, message, currentTimestamp));

            await _messageRepository.StoreDocumentsAsync(documents, partitionKey, cancellationToken).ConfigureAwait(false);
        }
    }

    public sealed class State
    {
        public State(string conversationId, string? tenantId = null, string? userId = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
            this.ConversationId = conversationId;
            this.TenantId = tenantId;
            this.UserId = userId;
        }

        public string ConversationId { get; }
        public string? TenantId { get; }
        public string? UserId { get; }
    }
}
