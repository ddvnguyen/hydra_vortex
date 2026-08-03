using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// ── PostgreSQL (Hydra.Core StoreMetadata requires it) ────────────────
// Pin to :16 for hermetic CI — avoids untagged "latest" pulls and
// ensures the E2E tests don't depend on a network-fetched image.
var pgPassword = builder.AddParameter("pg-password", "hydra-test-pw");
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("16")
    .WithDataVolume(isReadOnly: false)
    .WithPassword(pgPassword);

var hydraDb = postgres.AddDatabase("hydra-store");

// ── Fake LLM engine nodes ────────────────────────────────────────────
// Ports offset from production (8080/9000/9601/9602) to avoid collisions
// when the real hydra-system pod is running on the same host.
var fakeEngine1 = builder.AddProject<Projects.FakeLlamaEngine>("fake-engine-rtx")
    .WithEnvironment("FAKE_ENGINE_HTTP_PORT", "18080")
    .WithEnvironment("FAKE_ENGINE_RPC_PORT", "19601");

var fakeEngine2 = builder.AddProject<Projects.FakeLlamaEngine>("fake-engine-p100")
    .WithEnvironment("FAKE_ENGINE_HTTP_PORT", "18081")
    .WithEnvironment("FAKE_ENGINE_RPC_PORT", "19602");

// ── Hydra.Core coordinator ───────────────────────────────────────────
// Coordinator reads workers from HYDRA_COORD_WORKERS (inline JSON).
// All ports are test-only offsets (18xxx/19xxx) to avoid production
// port collisions. HYDRA_COORD_PORT defaults to 9000 in CoordinatorConfig
// so we must set it explicitly to 19000.
var hydraWorkersJson = """
[{"name":"rtx","host":"localhost","rpc_port":19601,"llama_rpc_port":19601,"llama_url":"http://localhost:18080","worker_type":3,"slots":2,"prefill_priority":1,"decode_priority":2},{"name":"p100","host":"localhost","rpc_port":19602,"llama_rpc_port":19602,"llama_url":"http://localhost:18081","worker_type":2,"slots":1,"prefill_priority":100,"decode_priority":1}]
""";

var hydraCore = builder.AddProject<Projects.Hydra_Core>("hydra-core")
    .WithHttpEndpoint(targetPort: 19000, name: "http")
    .WithReference(fakeEngine1)
    .WithReference(fakeEngine2)
    .WithReference(hydraDb)
    .WithEnvironment("HYDRA_COORD_ENABLED", "true")
    .WithEnvironment("HYDRA_COORD_PORT", "19000")
    .WithEnvironment("HYDRA_COORD_WORKERS", hydraWorkersJson)
    .WithEnvironment("HYDRA_STORE_PORT", "19500")
    .WithEnvironment("HYDRA_STORE_DEBUG_PORT", "19501")
    .WithEnvironment("HYDRA_STORE_HOST", "0.0.0.0")
    .WithEnvironment("HYDRA_STORE_DIR", "/tmp/hydra-store")
    .WithEnvironment("HYDRA_COORD_STORE_PORT", "19500")
    .WithEnvironment("HYDRA_COORD_NO_STORE_KV_RESTORE", "true");

builder.Build().Run();
