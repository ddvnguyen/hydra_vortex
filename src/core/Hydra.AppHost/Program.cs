using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// ── PostgreSQL (Hydra.Core StoreMetadata requires it) ────────────────
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(isReadOnly: false);

var hydraDb = postgres.AddDatabase("hydra-store");

// ── Fake LLM engine nodes ────────────────────────────────────────────
var fakeEngine1 = builder.AddProject<Projects.FakeLlamaEngine>("fake-engine-rtx")
    .WithEnvironment("FAKE_ENGINE_HTTP_PORT", "8080")
    .WithEnvironment("FAKE_ENGINE_RPC_PORT", "9601");

var fakeEngine2 = builder.AddProject<Projects.FakeLlamaEngine>("fake-engine-p100")
    .WithEnvironment("FAKE_ENGINE_HTTP_PORT", "8081")
    .WithEnvironment("FAKE_ENGINE_RPC_PORT", "9602");

// ── Hydra.Core coordinator ───────────────────────────────────────────
// Hydra.Core reads its worker list from HYDRA_COORD_CONFIG_FILE (JSON)
// or HYDRA_COORD_WORKERS (inline JSON). The AppHost sets HYDRA_COORD_WORKERS
// to point at the two fake engines above using their statically-known ports
// (8080/8081 for HTTP, 9601/9602 for RPC).
//
// Hydra.Core also needs HYDRA_STORE_PORT / HYDRA_STORE_PG_CONN for the
// embedded store.
var hydraWorkersJson = """
[{"name":"rtx","host":"localhost","rpc_port":9601,"llama_url":"http://localhost:8080","worker_type":3,"slots":2,"prefill_priority":1,"decode_priority":2},{"name":"p100","host":"localhost","rpc_port":9602,"llama_url":"http://localhost:8081","worker_type":2,"slots":1,"prefill_priority":100,"decode_priority":1}]
""";

var hydraCore = builder.AddProject<Projects.Hydra_Core>("hydra-core")
    .WithReference(fakeEngine1)
    .WithReference(fakeEngine2)
    .WithEnvironment("HYDRA_COORD_ENABLED", "true")
    .WithEnvironment("HYDRA_COORD_WORKERS", hydraWorkersJson)
    .WithEnvironment("HYDRA_STORE_PORT", "9500")
    .WithEnvironment("HYDRA_STORE_DEBUG_PORT", "9501")
    .WithEnvironment("HYDRA_STORE_HOST", "0.0.0.0")
    .WithEnvironment("HYDRA_STORE_DIR", "/tmp/hydra-store")
    .WithEnvironment("HYDRA_STORE_PG_CONN",
        builder.Configuration["ConnectionStrings:hydra-store"]
        ?? "Host=localhost;Database=hydra_store;Username=hydra;Password=hydra");

builder.Build().Run();
