// GPU smoke-test kernels: correctness check (light) + sustained full-power soak (full)
// + full-VRAM capacity/stress soak (vram).
// Built for the host GPUs' compute capabilities (sm_86 = RTX 3060, sm_120 = RTX 5060 Ti).
// One binary, mode selected by argv[1].
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <cuda_runtime.h>
#define CUDA_CHECK(x) do { cudaError_t e=(x); if(e!=cudaSuccess){ \
    printf("RESULT device=%d status=FAIL error=\"%s\"\n", dev, cudaGetErrorString(e)); return 1; } } while(0)

#define N_LIGHT (1<<20)
__global__ void vec_add(int n, const float* a, const float* b, float* c) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) c[i] = a[i] + b[i];
}

__global__ void sgemm(const float* A, const float* B, float* C, int N) {
    int row = blockIdx.y * blockDim.y + threadIdx.y;
    int col = blockIdx.x * blockDim.x + threadIdx.x;
    if (row >= N || col >= N) return;
    float acc = 0.f;
    for (int k = 0; k < N; ++k) acc += A[row * N + k] * B[k * N + col];
    C[row * N + col] = acc;
}

__global__ void compare_f32(const float* a, const float* b, int n, unsigned long long* mismatches) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n && a[i] != b[i]) atomicAdd(mismatches, 1ULL);
}

static int run_light(int dev) {
    cudaSetDevice(dev);
    float *a, *b, *c;
    CUDA_CHECK(cudaMalloc(&a, N_LIGHT * 4));
    CUDA_CHECK(cudaMalloc(&b, N_LIGHT * 4));
    CUDA_CHECK(cudaMalloc(&c, N_LIGHT * 4));
    float *ha = (float*)malloc(N_LIGHT * 4), *hb = (float*)malloc(N_LIGHT * 4), *hc = (float*)malloc(N_LIGHT * 4);
    for (int i = 0; i < N_LIGHT; ++i) { ha[i] = i; hb[i] = 2 * i; }
    CUDA_CHECK(cudaMemcpy(a, ha, N_LIGHT * 4, cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(b, hb, N_LIGHT * 4, cudaMemcpyHostToDevice));
    vec_add<<<(N_LIGHT + 255) / 256, 256>>>(N_LIGHT, a, b, c);
    CUDA_CHECK(cudaDeviceSynchronize());
    CUDA_CHECK(cudaMemcpy(hc, c, N_LIGHT * 4, cudaMemcpyDeviceToHost));
    int bad = 0;
    for (int i = 0; i < N_LIGHT; ++i) if (hc[i] != 3 * i) bad++;
    printf("RESULT device=%d status=%s bad=%d mode=light\n", dev, bad ? "FAIL" : "PASS", bad);
    return bad ? 1 : 0;
}

static int run_full(int dev, int N, int seconds) {    cudaSetDevice(dev);
    size_t mbytes = (size_t)N * N * sizeof(float);
    float *A, *B, *C, *C_ref;
    CUDA_CHECK(cudaMalloc(&A, mbytes));
    CUDA_CHECK(cudaMalloc(&B, mbytes));
    CUDA_CHECK(cudaMalloc(&C, mbytes));
    CUDA_CHECK(cudaMalloc(&C_ref, mbytes));
    float *ha = (float*)malloc(mbytes), *hb = (float*)malloc(mbytes);
    srand(42);
    for (size_t i = 0; i < (size_t)N * N; ++i) { ha[i] = (float)rand() / RAND_MAX - 0.5f; hb[i] = (float)rand() / RAND_MAX - 0.5f; }
    CUDA_CHECK(cudaMemcpy(A, ha, mbytes, cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(B, hb, mbytes, cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemset(C, 0, mbytes));
    free(ha); free(hb);

    dim3 blk(16, 16), grd((N + 15) / 16, (N + 15) / 16);
    // Golden reference: compute once, compare every iteration after
    sgemm<<<grd, blk>>>(A, B, C_ref, N);
    CUDA_CHECK(cudaDeviceSynchronize());
    unsigned long long *d_mismatches;
    CUDA_CHECK(cudaMalloc(&d_mismatches, sizeof(unsigned long long)));
    CUDA_CHECK(cudaMemset(d_mismatches, 0, sizeof(unsigned long long)));
    int n = N * N;
    time_t start = time(NULL);
    int iter = 0;
    while (time(NULL) - start < seconds) {
        sgemm<<<grd, blk>>>(A, B, C, N);
        CUDA_CHECK(cudaDeviceSynchronize());
        compare_f32<<<(n + 255)/256, 256>>>(C, C_ref, n, d_mismatches);
        CUDA_CHECK(cudaDeviceSynchronize());
        ++iter;
    }
    unsigned long long mismatches = 0;
    CUDA_CHECK(cudaMemcpy(&mismatches, d_mismatches, sizeof(mismatches), cudaMemcpyDeviceToHost));
    const char *status = mismatches ? "FAIL" : "PASS";
    printf("RESULT device=%d status=%s mode=full iters=%d seconds=%ld mismatches=%llu\n", dev, status, iter, (long)(time(NULL) - start), mismatches);
    return mismatches ? 1 : 0;
}

// Full-VRAM capacity + stress soak: allocate `frac` of free VRAM across 1 GiB chunks,
// then continuously read-modify-write the whole allocation (bandwidth + capacity stress)
// interleaved with a GEMM pass (compute/power stress) for `seconds`. Catches faults that
// only appear under sustained full-memory load (the #701 Xid class), not just light/idle.
__global__ void vram_rmw(float* p, int n, float addend) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) p[i] = p[i] + addend;
}

__global__ void verify_rmw(const float* p, int n, float expected, unsigned long long* mismatches) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n && p[i] != expected) atomicAdd(mismatches, 1ULL);
}

static int run_vram(int dev, int seconds, float frac) {
    cudaSetDevice(dev);
    // Reserved GEMM working set first so the VRAM allocation never starves it
    // (the GEMM pass drives compute/power; if it OOMs the whole test fails).
    const int GEMM_N = 4096;
    size_t gmb = (size_t)GEMM_N * GEMM_N * sizeof(float);
    float *A, *B, *C;
    CUDA_CHECK(cudaMalloc(&A, gmb));
    CUDA_CHECK(cudaMalloc(&B, gmb));
    CUDA_CHECK(cudaMalloc(&C, gmb));
    CUDA_CHECK(cudaMemset(C, 0, gmb));
    float *ha = (float*)malloc(gmb), *hb = (float*)malloc(gmb);
    srand(7);
    for (size_t i = 0; i < (size_t)GEMM_N * GEMM_N; ++i) { ha[i] = (float)rand() / RAND_MAX - 0.5f; hb[i] = (float)rand() / RAND_MAX - 0.5f; }
    CUDA_CHECK(cudaMemcpy(A, ha, gmb, cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(B, hb, gmb, cudaMemcpyHostToDevice));
    free(ha); free(hb);
    dim3 blk(16, 16), grd((GEMM_N + 15) / 16, (GEMM_N + 15) / 16);

    size_t freeB, totalB;
    CUDA_CHECK(cudaMemGetInfo(&freeB, &totalB));
    size_t budget = (size_t)(freeB * (double)frac);
    const size_t CHUNK = (size_t)1 << 30; // 1 GiB
    int nchunks = (int)(budget / CHUNK);
    if (nchunks < 1) nchunks = 1;
    size_t *sizes = (size_t*)malloc(sizeof(size_t) * nchunks);
    float **bufs = (float**)malloc(sizeof(float*) * nchunks);
    if (!sizes || !bufs) { printf("RESULT device=%d status=FAIL error=\"host alloc\"\n", dev); return 1; }
    size_t totalElems = 0;
    for (int c = 0; c < nchunks; ++c) {
        size_t nb = (c == nchunks - 1) ? (budget - (size_t)(nchunks - 1) * CHUNK) : CHUNK;
        nb &= ~(size_t)15; // align to float
        sizes[c] = nb / sizeof(float);
        totalElems += sizes[c];
        CUDA_CHECK(cudaMalloc(&bufs[c], nb));
        CUDA_CHECK(cudaMemset(bufs[c], 0, nb));
    }

    printf("RESULT device=%d status=RUN mode=vram chunks=%d vram_mb=%ld gemm_n=%d seconds=%d\n",
           dev, nchunks, (long)(totalElems * 4 / (1024 * 1024)), GEMM_N, seconds);

    time_t start = time(NULL);
    int iter = 0;
    unsigned long long touched = 0;
    while (time(NULL) - start < seconds) {
        for (int c = 0; c < nchunks; ++c) {
            int n = (int)sizes[c];
            vram_rmw<<<(n + 255) / 256, 256>>>(bufs[c], n, 1.0f);
        }
        CUDA_CHECK(cudaDeviceSynchronize());
        sgemm<<<grd, blk>>>(A, B, C, GEMM_N);
        CUDA_CHECK(cudaDeviceSynchronize());
        touched += totalElems * 2; // RMW read+write
        ++iter;
    }
    // Verify RMW: every element must equal exactly (float)iter (exact for realistic iter counts, well within float exact-int range)
    unsigned long long *d_mismatches;
    CUDA_CHECK(cudaMalloc(&d_mismatches, sizeof(unsigned long long)));
    CUDA_CHECK(cudaMemset(d_mismatches, 0, sizeof(unsigned long long)));
    float expected = (float)iter;
    for (int c = 0; c < nchunks; ++c) {
        int n = (int)sizes[c];
        verify_rmw<<<(n + 255)/256, 256>>>(bufs[c], n, expected, d_mismatches);
    }
    CUDA_CHECK(cudaDeviceSynchronize());
    unsigned long long mismatches = 0;
    CUDA_CHECK(cudaMemcpy(&mismatches, d_mismatches, sizeof(mismatches), cudaMemcpyDeviceToHost));
    long long mb = (long long)totalElems * 4 / (1024 * 1024);
    double secs = (double)(time(NULL) - start);
    const char *status = mismatches ? "FAIL" : "PASS";
    printf("RESULT device=%d status=%s mode=vram iters=%d seconds=%ld vram_mb=%lld bw_gbs=%.1f mismatches=%llu expected=%.0f\n",
           dev, status, iter, (long)secs, mb, secs > 0 ? (touched * 4.0 / (1024.0*1024.0*1024.0)) / secs : 0.0, mismatches, expected);
    return mismatches ? 1 : 0;
}

int main(int argc, char** argv) {
    if (argc < 3) {
        fprintf(stderr, "usage: %s <light|full|vram> <device> [arg] [arg]\n"
                        "  light <dev>\n"
                        "  full  <dev> [gemm_n] [seconds]\n"
                        "  vram  <dev> [seconds] [frac]\n", argv[0]);
        return 2;
    }
    int dev = atoi(argv[2]);
    if (strcmp(argv[1], "light") == 0) return run_light(dev);
    if (strcmp(argv[1], "full") == 0) {
        int gemm_n = argc > 3 ? atoi(argv[3]) : 8192;
        int seconds = argc > 4 ? atoi(argv[4]) : 20;
        return run_full(dev, gemm_n, seconds);
    }
    if (strcmp(argv[1], "vram") == 0) {
        int seconds = argc > 3 ? atoi(argv[3]) : 200;
        float frac = argc > 4 ? (float)atof(argv[4]) : 0.85f;
        return run_vram(dev, seconds, frac);
    }
    fprintf(stderr, "unknown mode %s\n", argv[1]);
    return 2;
}
