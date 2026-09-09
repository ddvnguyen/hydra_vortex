# CUDA Version Management

## Layout

```
/opt/software/cuda/
├── 12.9/          # CUDA 12.9 (needed for P100 sm_60)
├── 13.2/          # CUDA 13.2 (RTX sm_120, current default)
└── 13.3/          # CUDA 13.3 (fixes IQ3_S gemm precision bug)
```

Each version is a self-contained CUDA toolkit installed via runfile with
`--toolkit --silent --toolkitpath=/opt/software/cuda/<version>`.

## Switching CUDA Versions

No formal module system. Set environment variables before building:

```bash
export PATH=/opt/software/cuda/<VERSION>/bin:$PATH
export LD_LIBRARY_PATH=/opt/software/cuda/<VERSION>/lib64:$LD_LIBRARY_PATH

# Verify
nvcc --version
```

## Installing a New CUDA Version

```bash
# 1. Download the runfile (Linux x86_64)
wget https://developer.download.nvidia.com/compute/cuda/13.3.0/local_installers/cuda_13.3.0_595.71.05_linux.run

# 2. Make executable
chmod +x cuda_13.3.0_595.71.05_linux.run

# 3. Extract only (don't install driver or overwrite existing)
./cuda_13.3.0_595.71.05_linux.run --extract=/tmp/cuda-13.3
cd /tmp/cuda-13.3

# 4. Install toolkit only, skip driver
./cuda-install-samples-*.sh /opt/software/cuda/13.3   # skip if no samples needed

# For the main toolkit, use the extracted runfiles:
./cuda-linux*.run --toolkit --silent \
  --toolkitpath=/opt/software/cuda/13.3 \
  --no-man-page \
  --override

# 5. Verify
/opt/software/cuda/13.3/bin/nvcc --version
```

Or single-step runfile (skip driver):
```bash
./cuda_13.3.0_595.71.05_linux.run --toolkit --silent \
  --toolkitpath=/opt/software/cuda/13.3 --no-driver
```

## Driver Compatibility

```
nvidia-smi → Driver Version + CUDA Version shown is the MAXIMUM supported.
CUDA toolkit must be ≤ driver's max CUDA version.
```

| Driver | Max CUDA | Smokes |
|--------|----------|--------|
| 595.71.05 | 13.x | sm_120 (RTX 5060 Ti) |
| 580.159.03 | 13.0 | sm_60 (Tesla P100) |

## Building llama.cpp for a Specific CUDA

```bash
# Set CUDA version
export PATH=/opt/software/cuda/13.3/bin:$PATH
export LD_LIBRARY_PATH=/opt/software/cuda/13.3/lib64:$LD_LIBRARY_PATH

# Configure (out-of-tree build keeps CUDA variants separate)
cmake -B build_sm120-cuda13.3 \
  -DGGML_CUDA=ON \
  -DCMAKE_CUDA_ARCHITECTURES=120 \
  -DCUDAToolkit_ROOT=/opt/software/cuda/13.3

# Build specific targets
cmake --build build_sm120-cuda13.3 --target llama-bench llama-server rpc-server -j16
```

## CUDA Version Bugs

| Version | Issue | Affects |
|---------|-------|---------|
| **13.2** | IQ3_S gemm precision (produces gibberish) | IQ3_S quants only |
| **13.3** | Fixes IQ3_S, adds Blackwell FP4 native support | Upgrade recommended |

Our models:
- Nano (IQ2_XXS): NOT affected by 13.2 bug
- Balanced (Q5_K): NOT affected
- Quality (IQ?): MAY be affected — test after download

## Checking Current CUDA

```bash
nvidia-smi                          # driver + max CUDA
nvcc --version                      # active toolkit
cmake --build build_sm120 --target llama-bench 2>&1 | grep "CUDA"
```

## P100 VM (192.168.122.21)

```bash
ssh hydra-p100 "nvidia-smi | head -3"
ssh hydra-p100 "/opt/software/cuda/12.9/bin/nvcc --version"   # guest-side toolkit (VM FS, unaffected by host cleanup)
```

P100 sm_60 binaries are built with CUDA 12.9 as a **minimal micromamba
prefix** (~3.0 GB) at `~/opt/cuda-12.9-min`. 2026-08-27 owner cleanup: the old
7.3 GB full-runfile `/opt/software/cuda/12.9/` was **removed** — this prefix is
now the only CUDA 12.9 on the host. Proven build dir:
`src/llama-cpp/build_sm60-min` (replaced the 7.3 GB full-runfile layout,
2026-08-27).

### CUDA 12.9 minimal install (micromamba recipe)

```bash
export PATH=/home/ddv/anaconda3/bin:$PATH
micromamba create -y -p ~/opt/cuda-12.9-min \
  -c nvidia/label/cuda-12.9.1 -c conda-forge \
  'cuda-nvcc=12.9.*' 'cuda-cudart-dev=12.9.*' libcublas-dev nccl libgomp rdma-core
# VERSION PINS MANDATORY: bare names let the solver pick CUDA 13.x from
# conda-forge (label channels alone contribute nothing if satisfiable there).

# MANDATORY HEADER PATCH — conda cudart-dev 12.9.79 ships crt/math_functions.{h,hpp}
# missing noexcept qualifiers -> glibc mathcalls.h clash fails every C++ compile.
# (Applied once to ~/opt/cuda-12.9-min; HEADER-PATCH-NOTICE.md carries md5s.)
# NOTE 2026-08-27: the /opt/software/cuda/12.9 host copy used below as patch
# SOURCE was removed — for a FRESH prefix, take the two files from the VM
# (ssh hydra-p100 "cat /opt/software/cuda/12.9/include/crt/math_functions.h")
# or any other CUDA 12.9 install:
cp /opt/software/cuda/12.9/include/crt/math_functions.h   <prefix>/targets/x86_64-linux/include/crt/
cp /opt/software/cuda/12.9/include/crt/math_functions.hpp <prefix>/targets/x86_64-linux/include/crt/
# Full detail + md5 verify: ~/opt/cuda-12.9-min/HEADER-PATCH-NOTICE.md

# Smoke (sm_60); nvcc 12.9 rejects system gcc-15 -> pass gcc-13 as ccbin:
printf '__global__ void k(){}\nint main(){return 0;}\n' > main.cu
<prefix>/bin/nvcc -arch=sm_60 -ccbin /usr/bin/g++-13 main.cu -o main && ./main && echo RUN_OK
```

Verified end-to-end 2026-08-27: `build_sm60-min` configured against this
prefix (`CUDAToolkit_ROOT`, `CMAKE_CUDA_COMPILER=<prefix>/bin/nvcc`,
`CMAKE_CUDA_HOST_COMPILER=/usr/bin/g++-13`) produced llama-engine whose ldd
resolves cudart/cublas/cublasLt/nccl from the prefix; the binary was staged
to the P100 VM and served real GPU inference at 183.8 tok/s (isolated :8088
test, `~/hydra-min-test`).

### lmod

The toolkit is exposed as **`module load cuda/12.9-min`**
(`~/modulefiles/cuda/12.9-min.lua`, micromamba pattern — `lib/`,
`targets/x86_64-linux/include`, sets `CUDA_HOME/CUDA_PATH/CUDACXX`).
`cuda/12.9.lua` is a deprecated alias to the same prefix (kept so old
scripts/muscle memory keep working). Compiling sm_60 with either module
still requires `-ccbin /usr/bin/g++-13`.
