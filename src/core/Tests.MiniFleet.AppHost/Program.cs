using Tests.MiniFleet.AppHost;

// Real AppHost entry point — primarily a formality (the Sdk requires an
// executable); tests bootstrap the topology via
// DistributedApplicationTestingBuilder.CreateAsync<Projects.Tests_MiniFleet_AppHost>()
// with env vars carrying the configuration. Direct `dotnet run` uses the
// cpu-2node preset defaults.

var engineBinary = Environment.GetEnvironmentVariable("MINIFLEET_ENGINE_BIN")
    ?? throw new InvalidOperationException(
        "MINIFLEET_ENGINE_BIN is required (path to a CPU llama-engine build).");
var modelPath = Environment.GetEnvironmentVariable("MINIFLEET_MODEL_PATH")
    ?? throw new InvalidOperationException(
        "MINIFLEET_MODEL_PATH is required (path to Qwen3.5-9B-Q4_K_M.gguf).");

// "enginePortA:rpcPortA:nglA:enginePortB:rpcPortB:nglB:threads:ctx"
// (set by Tests.MiniFleet.MiniFleetAppHost.StartCpuTwoNodeAsync; default =
// Presets.Cpu2Node for direct `dotnet run`).
var portsSpec = Environment.GetEnvironmentVariable("MINIFLEET_PRESET_PORTS")
    ?? "18088:19513:0:18089:19514:0:3:4096";
var parts = portsSpec.Split(':');
if (parts.Length != 8 || !parts.All(p => int.TryParse(p, out _)))
{
    throw new InvalidOperationException(
        $"MINIFLEET_PRESET_PORTS must be 8 colon-separated ints, got: {portsSpec}");
}
var ports = parts.Select(int.Parse).ToArray();

var builder = DistributedApplication.CreateBuilder(args);

Topology.Build(
    builder,
    engineBinary,
    modelPath,
    enginePortA: ports[0], rpcPortA: ports[1], nglA: ports[2],
    enginePortB: ports[3], rpcPortB: ports[4], nglB: ports[5],
    threadsPerEngine: ports[6], contextSize: ports[7]);

builder.Build().Run();
