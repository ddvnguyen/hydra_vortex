namespace Tests.AgentWorkload;

/// <summary>
/// Canonical 6-turn scripted conversation shape from docs/paseo-hydra-agent-test.md §1.
/// Each turn depends on the previous one so context accumulates and prefix reuse is exercised.
/// </summary>
public static class ScriptedConversation
{
    /// <summary>
    /// The 6-turn prompt sequence. Turn N+1 references Turn N's answer.
    /// </summary>
    public static IReadOnlyList<string> Prompts { get; } =
    [
        "List the services under `src/` and pick the one with the most `.cs` files.",
        "For the service you picked, summarise what its largest file does.",
        "Name the three riskiest functions in that file and say why.",
        "Pick the riskiest one and describe its failure modes.",
        "Write (do not save) a test that would catch the first failure mode.",
        "Review the test you just wrote — what would it miss?",
    ];

    /// <summary>Minimum number of turns to exercise KV reuse paths.</summary>
    public const int MinTurns = 5;

    /// <summary>Full canonical turn count.</summary>
    public const int FullTurns = 6;
}
