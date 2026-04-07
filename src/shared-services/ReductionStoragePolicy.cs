namespace SharedServices;

/// <summary>
/// Specifies the storage policy to apply when chat history reduction occurs.
/// </summary>
public enum ReductionStoragePolicy
{
    /// <summary>
    /// Clears the existing messages and replaces them with the reduced set.
    /// </summary>
    Clear,

    /// <summary>
    /// Archives the existing messages by renaming their conversationId with an "_archived_{timestamp}" suffix,
    /// then stores the reduced messages with the original conversationId.
    /// </summary>
    Archive
}
