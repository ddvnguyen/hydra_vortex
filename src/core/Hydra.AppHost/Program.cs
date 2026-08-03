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
// to point at the two fake engines above. Ports are injected via env so
// Aspire can bind them dynamically.
//
// Hydra.Core also needs HYDRA_STORE_PORT / HYDRA_STORE_PG_CONN for the
// embedded store. For Tier-1 E2E tests, the store is not needed — set
// HYDRA_COORD_ENABLED=false to skip the coordinator WebApplication and
// only run the store server (which can be a lightweight no-op for now).
//
// Open questions for a later task:
//   1. Hydra.Core's Program.cs boots both StoreServer AND Coordinator in
//      one process. For hermetic E2E we likely need to either:
//      a) Set HYDRA_COORD_ENABLED=false and mock the store endpoint, OR
//      b) Extract the coordinator into a standalone project.
//      Option (a) is the pragmatic first cut.
//   2. The coordinator needs StoreClient (RPC :9500) to save/restore KV.
//      For fake-engine-only E2E, sessions skip KV save/restore, so the
//      store can be a no-op stub or omitted entirely.
//   3. Hydra.Core is referenced as a project dependency so Aspire can
//      manage its lifecycle, but Projects.HydraCore is not used in code
//      because Hydra.Core boots its own WebApplication internally.

var hydraCore = builder.AddProject<Projects.Hydra_Core>("hydra-core")
    .WithEnvironment("HYDRA_COORD_ENABLED", "false")
    .WithEnvironment("HYDRA_STORE_PORT", "9500")
    .WithEnvironment("HYDRA_STORE_DEBUG_PORT", "9501")
    .WithEnvironment("HYDRA_STORE_HOST", "0.0.0.0")
    .WithEnvironment("HYDRA_STORE_DIR", "/tmp/hydra-store")
    .WithEnvironment("HYDRA_STORE_PG_CONN",
        builder.Configuration["ConnectionStrings:hydra-store"]
        ?? "Host=localhost;Database=hydra_store;Username=hydra;Password=hydra");

builder.Build().Run();
