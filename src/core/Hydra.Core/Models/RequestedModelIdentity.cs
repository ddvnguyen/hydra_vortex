using Hydra.Core.Services;

namespace Hydra.Core.Models;

/// <summary>
/// #470: canonical model identity for a request, resolved ONCE at ingress
/// (<c>WorkerSchedulerService.SubmitAsync</c> — immediately after the
/// AutoRouter block) from the request's raw <c>model</c> field (string or
/// <see cref="System.Text.Json.JsonElement"/>) + <see cref="ModelConfigLoader"/>,
/// then consumed by every payload builder (PREFILL 0x42 body, DECODE 0x43
/// frame, HTTP-proxy body, cold-atomic swap check, force multi-engine plan).
///
/// The raw routing key (e.g. <c>dense-27b-combined</c>) must NEVER reach the
/// engine wire — it is not a key of the engine's <c>--models-preset</c>, so
/// the engine answers "preset has 3 alias(es)" → model_fallback → broken pipe.
/// The identity record translates it to the GGUF-file alias the engine's
/// preset expects, with role-aware prefill/decode quants for P/D split
/// (moe-35b-pd → prefill qwen3.6-35B-mini / decode qwen3.6-35B-balanced).
/// <see cref="WorkItem.ModelIdentity"/> carries it through the pipeline;
/// <c>Request["model"]</c> itself stays frozen as the raw routing key and is
/// never mutated downstream (body-level substitution instead).
/// </summary>
public sealed record RequestedModelIdentity(
	string? RoutingKey,
	bool Combined,
	string? PrefillAlias,
	string? DecodeAlias)
{
	/// <summary>
	/// Resolve the identity from the request's raw model string (already
	/// unwrapped via <c>RequestModelString</c>). Mirrors
	/// <c>TranslateModelAlias</c> semantics:
	///   PrefillAlias = template.PrefillAlias ?? RoutingKey
	///   DecodeAlias  = template.DecodeAlias ?? PrefillAlias ?? RoutingKey
	/// Unknown routing keys / no loader → passthrough (aliases == routing
	/// key), preserving pre-feature behavior.
	/// </summary>
	public static RequestedModelIdentity Resolve(string? routingKey, ModelConfigLoader? loader)
	{
		var template = !string.IsNullOrWhiteSpace(routingKey)
			? loader?.GetModelTemplate(routingKey)
			: null;
		var prefillAlias = !string.IsNullOrWhiteSpace(template?.PrefillAlias)
			? template!.PrefillAlias
			: routingKey;
		var decodeAlias = !string.IsNullOrWhiteSpace(template?.DecodeAlias)
			? template!.DecodeAlias
			: prefillAlias;
		return new RequestedModelIdentity(
			routingKey,
			routingKey != null && routingKey.Contains("combined", StringComparison.OrdinalIgnoreCase),
			prefillAlias,
			decodeAlias);
	}
}
