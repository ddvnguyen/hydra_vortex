using Hydra.Core.Models;

namespace Hydra.Core.Services;

/// <summary>
/// M-Perf.9 #289 / #470: pure decision function for the cross-model KV safety check.
/// Extracted from <see cref="WorkerSchedulerService.RestoreKvAsync"/> so the
/// behaviour can be unit-tested without a live llama-server.
///
/// #470: replaced single SHA-256 hash comparison with per-bit identity
/// comparison (tokenizer, name, quant, capabilities) so the guard can
/// make per-capability decisions (e.g. abort on MTP mismatch, allow
/// quant mismatch for P/D mix-quant).
/// </summary>
public static class CrossModelGuard
{
    public enum Outcome
    {
        /// <summary>No cross-model issue. Proceed with the restore.</summary>
        Proceed,
        /// <summary>Identity fields differ in a way that would corrupt decode,
        /// and the operator has not opted in to the unsafe mode. Abort the
        /// restore and re-prefill on the correct model.</summary>
        Abort,
        /// <summary>Identity differs but the operator set
        /// <c>HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE=true</c>. Warn and proceed
        /// — the model is likely to reject the KV at decode time.</summary>
        WarnAndProceed,
        /// <summary>At least one identity is empty (pre-#470 data or META query
        /// failed). Treat as "no opinion" and skip the check.</summary>
        Skip
    }

    /// <summary>
    /// Decide whether a stored KV may be restored into a slot loaded with a
    /// potentially-different model. Pure function: same inputs → same output.
    /// </summary>
    /// <param name="stored">The model identity of the slot that built the KV
    ///   (from the WorkItem's prefill, or from the Store manifest on restore
    ///   after a Coordinator restart).</param>
    /// <param name="slot">The model identity of the slot the KV is being
    ///   restored into (from the slot META query after StatePut).</param>
    /// <param name="allowCrossModelKvReuse">Operator flag — <c>true</c> turns
    ///   Abort into WarnAndProceed for recoverable mismatches.</param>
    public static Outcome Decide(
        ModelIdentity? stored,
        ModelIdentity? slot,
        bool allowCrossModelKvReuse)
    {
        bool storedKnown = stored is not null && !stored.IsEmpty;
        bool slotKnown   = slot is not null && !slot.IsEmpty;
        bool bothKnown   = storedKnown && slotKnown;

        if (!bothKnown)
            return Outcome.Skip;

        // Tokenizer must match — different tokenizers produce incompatible token IDs.
        if (!string.Equals(stored!.Tokenizer, slot!.Tokenizer, StringComparison.Ordinal))
            return allowCrossModelKvReuse ? Outcome.WarnAndProceed : Outcome.Abort;

        // Model name must match — different models have different weights/architectures.
        if (!string.Equals(stored.ModelName, slot.ModelName, StringComparison.Ordinal))
            return allowCrossModelKvReuse ? Outcome.WarnAndProceed : Outcome.Abort;

        // Per-bit capability comparison.
        uint storedCaps = stored.ModelCapabilities;
        uint slotCaps   = slot.ModelCapabilities;
        uint diffCaps   = storedCaps ^ slotCaps;

        if (diffCaps != 0)
        {
            // Bit 0 (MTP) or bit 1 (VISION) differ — unconditional abort (would corrupt decode).
            // The allowCrossModelKvReuse flag does NOT override these: proceeding with
            // a MTP/VISION mismatch corrupts the decode output, not just changes behaviour.
            bool abortBitSet = (diffCaps & (ModelIdentity.CapMTP | ModelIdentity.CapVision)) != 0;
            if (abortBitSet)
                return Outcome.Abort;

            // Bit 2 (REASONING), bit 3 (TOOL_USE), bit 4 (CODE) differ — always WarnAndProceed
            // (doesn't corrupt decode, just changes behaviour).
            return Outcome.WarnAndProceed;
        }

        // ModelQuant differs — P/D mix-quant is by design, Proceed with log only.
        // (The caller logs this; the guard itself does not allocate/throw.)
        if (!string.Equals(stored.ModelQuant, slot.ModelQuant, StringComparison.Ordinal))
            return Outcome.Proceed;

        // All identity fields match.
        return Outcome.Proceed;
    }
}
