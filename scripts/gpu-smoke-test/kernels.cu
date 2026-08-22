// GPU smoke-test kernels: correctness check (light) + sustained full-power soak (full).
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

static int run_full(int dev, int N, int seconds) {
    cudaSetDevice(dev);
    size_t mbytes = (size_t)N * N * sizeof(float);
    float *A, *B, *C;
    CUDA_CHECK(cudaMalloc(&A, mbytes));
    CUDA_CHECK(cudaMalloc(&B, mbytes));
    CUDA_CHECK(cudaMalloc(&C, mbytes));
    float *ha = (float*)malloc(mbytes), *hb = (float*)malloc(mbytes);
    srand(42);
    for (size_t i = 0; i < (size_t)N * N; ++i) { ha[i] = (float)rand() / RAND_MAX - 0.5f; hb[i] = (float)rand() / RAND_MAX - 0.5f; }
    CUDA_CHECK(cudaMemcpy(A, ha, mbytes, cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemcpy(B, hb, mbytes, cudaMemcpyHostToDevice));
    CUDA_CHECK(cudaMemset(C, 0, mbytes));
    free(ha); free(hb);

    dim3 blk(16, 16), grd((N + 15) / 16, (N + 15) / 16);
    time_t start = time(NULL);
    int iter = 0;
    while (time(NULL) - start < seconds) {
        sgemm<<<grd, blk>>>(A, B, C, N);
        CUDA_CHECK(cudaDeviceSynchronize());
        ++iter;
    }
    printf("RESULT device=%d status=PASS mode=full iters=%d seconds=%ld\n", dev, iter, (long)(time(NULL) - start));
    return 0;
}

int main(int argc, char** argv) {
    if (argc < 3) {
        fprintf(stderr, "usage: %s <light|full> <device> [gemm_n] [seconds]\n", argv[0]);
        return 2;
    }
    int dev = atoi(argv[2]);
    if (strcmp(argv[1], "light") == 0) return run_light(dev);
    if (strcmp(argv[1], "full") == 0) {
        int gemm_n = argc > 3 ? atoi(argv[3]) : 8192;
        int seconds = argc > 4 ? atoi(argv[4]) : 20;
        return run_full(dev, gemm_n, seconds);
    }
    fprintf(stderr, "unknown mode %s\n", argv[1]);
    return 2;
}
