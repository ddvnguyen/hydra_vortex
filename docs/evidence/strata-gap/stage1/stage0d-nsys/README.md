# Stage 0d raw nsys artefacts

Evidence of a **failed** measurement, not of a result.

**Both captures contain ZERO CUDA kernels.** `nsys stats --report cuda_gpu_kern_sum` returns
`SKIPPED: S1OFF-prefill.sqlite does not contain CUDA kernel data`, so the `--delay` window never
overlapped the measured prefill. See `../STAGE0D-NSYS.md` §3.

| file | what it is |
|---|---|
| `S1OFF-prefill.nsys-rep`, `S1ON-prefill.nsys-rep` | the two kernel-free nsys reports |
| `S1OFF-nsys-server.log`, `S1ON-nsys-server.log` | llama-server stderr under nsys |
| `S1OFF-engine.log`, `S1ON-engine.log` | llama-server stderr from the non-nsys verification runs (these DO carry the VRAM / `moe-stage` evidence) |
| `S1OFF-link.tsv`, `S1ON-link.tsv` | 1 Hz PCIe-util sampler; does not bracket a measured ubatch, so no duty figure is quoted |
| `*_cuda_gpu_kern_sum.csv`, `*_cuda_gpu_mem_*.csv` | empty (0 bytes) — the nsys-stats outputs, kept to show they are genuinely empty rather than missing |

nsys: `~/nsys-local/opt/nvidia/nsight-systems/2025.6.3/target-linux-x64/nsys`
(NVIDIA Nsight Systems 2025.6.3.541-256337736014v0)
