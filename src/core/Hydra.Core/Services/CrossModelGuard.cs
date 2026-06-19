namespace Hydra.Core.Services;

/// <summary>
/// M-Perf.9 #289: pure decision function for the cross-model KV safety check.
/// Extracted from <see cref="WorkerSchedulerService.RestoreKvAsync"/> so the
/// behaviour can be unit-tested without a live llama-server.
/// </summary>
public static class CrossModelGuard
{
    public enum Outcome
    {
        /// <summary>No cross-model issue. Proceed with the restore.</summary>
        Proceed,
        /// <summary>Hashes differ and the operator has not opted in to the
        /// unsafe mode. Abort the restore and re-prefill on the correct model.</summary>
        Abort,
        /// <summary>Hashes differ but the operator set
        /// <c>HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE=true</c>. Warn and proceed
        /// — the model is likely to reject the KV at decode time.</summary>
        WarnAndProceed,
        /// <summary>At least one of the hashes is empty (pre-#289 data or
        /// META query failed). Treat as "no opinion" and skip the check.</summary>
        Skip
    }

    /// <summary>
    /// Decide whether a stored KV may be restored into a slot loaded with a
    /// potentially-different model. Pure function: same inputs → same output.
    /// </summary>
    /// <param name="storedHash">The model_hash of the slot that built the KV
    ///   (from the WorkItem's prefill, or from the Store manifest on restore
    ///   after a Coordinator restart).</param>
    /// <param name="slotHash">The model_hash of the slot the KV is being
    ///   restored into (from the slot META query after StatePut).</param>
    /// <param name="allowCrossModelKvReuse">Operator flag — <c>true</c> turns
    ///   Abort into WarnAndProceed.</param>
    public static Outcome Decide(
        string? storedHash,
        string? slotHash,
        bool allowCrossModelKvReuse)
    {
        bool storedKnown = !string.IsNullOrEmpty(storedHash);
        bool slotKnown   = !string.IsNullOrEmpty(slotHash);
        bool bothKnown   = storedKnown && slotKnown;

        if (!bothKnown)
            return Outcome.Skip; // back-compat: pre-#289 data or META failure

        bool hashesMatch = string.Equals(storedHash, slotHash, StringComparison.Ordinal);
        if (hashesMatch)
            return Outcome.Proceed;

        return allowCrossModelKvReuse ? Outcome.WarnAndProceed : Outcome.Abort;
    }
}
