namespace Tests.AgentWorkload;

/// <summary>
/// Abstraction for driving a coding-agent CLI non-interactively, turn by turn,
/// within the same session so context accumulates.
/// </summary>
public interface IAgentCliDriver
{
    /// <summary>
    /// Execute a single turn in the agent session. The session is identified by
    /// <paramref name="sessionId"/> — subsequent calls with the same ID continue
    /// the conversation, accumulating context.
    /// </summary>
    Task<AgentTurnResult> RunTurnAsync(string sessionId, string prompt, CancellationToken ct = default);

    /// <summary>Check whether the required CLI binary is available on PATH.</summary>
    bool IsAvailable();

    /// <summary>Human-readable name of the CLI driver (e.g. "pi", "opencode").</summary>
    string Name { get; }
}
