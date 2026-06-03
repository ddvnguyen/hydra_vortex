# 5. Deploy (only if the change touches runtime or the fork)

**Goal:** get the merged change running on the nodes. Commands: `DevelopmentRunBook.md`
("llama-server build", "P100 VM setup", "Quick Start").

1. **Services (C#/Python):** redeploy via the control plane —
   `cd infra && docker compose -f docker-compose.infra.yml -f docker-compose.hydra.yml up -d`
   (or `start-env.sh`). Confirm health endpoints.
2. **llama.cpp fork change — two parts, both required:**
   - Build per node: RTX **sm_120** (`GGML_CUDA_FORCE_CUBLAS=ON`) → `build_sm120/`,
     P100 **sm_60** → `build_sm60/`; deploy via `start-env.sh` / `setup-p100.sh`.
   - **Push the fork** branch `hydra-state-streaming` to its remote **and** bump the
     `src/llama-cpp` submodule pointer in the parent repo. Skipping the push leaves the
     pinned SHA dangling → breaks fresh clones and the Deploy CI job.
3. Verify the deployed version (`/version`, `/health` on each node).

→ Next: `06-monitoring.md`
