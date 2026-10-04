# Stage 0e raw nsys artefacts

The capture **succeeded** — 52.6 MB report, non-empty kernel summary (307 264 DEQUANT launches etc.).
Contrast with `../stage0d-nsys/`, which is a *failed* capture (zero CUDA kernels).

| file | what it is |
|---|---|
| `S1OFF34068ff9-prefill.nsys-rep` | the report, `34068ff9` flag OFF, p4k `max_tokens=1`, wall 24.873 s |
| `kern_sum.csv` | `cuda_gpu_kern_sum` — per-kernel totals/launch counts (the §2 table) |
| `api_sum.csv` | `cuda_api_sum` — host-side API totals (the §7 table) |
| `mem_time_sum.csv`, `mem_size_sum.csv` | `cuda_gpu_mem_time_sum` / `_size_sum` — H2D 96 001 MB in 15.751 s (§4) |
| `S1OFF34068ff9-server.log` | llama-server stderr at `-lv 5` during the profiled run |
| `plan-diag.log` | separate flag-ON run; contains the verbatim `moe-stage: bank=0 staged=0 declined=141` line (§5) |
| `S1OFF34068ff9-link.tsv` | 1 Hz PCIe sampler — **`pcie.rx.util` is not a valid field on this driver**, so duty in §4 is memcpy-derived, not sampled |

The full 296 MB `cuda_gpu_trace.csv` and the `.sqlite` are **not** committed (size); both are
regenerable from the `.nsys-rep` with
`nsys stats --force-export=true --report <name> --format csv --output <prefix> <rep>`.

nsys: `~/nsys-local/opt/nvidia/nsight-systems/2025.6.3/target-linux-x64/nsys`
(NVIDIA Nsight Systems 2025.6.3.541-256337736014v0)
