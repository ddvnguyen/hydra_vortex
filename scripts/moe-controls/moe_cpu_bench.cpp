// CPU-serve cost of j experts of one MoE layer on the real model tensors (F1/F2 of section 130 R4).
// One decode token, one layer: gate/up mul_mat_id -> silu*up -> down mul_mat_id, exactly the ggml ops the engine runs for
// a CPU-served expert group, on the real quant types, shapes and weight bytes of Qwen3.8-Flash-Next-APEX-I-Mini.
// Weights are mmap'd straight from the shard (no repack), touched once to be page-cache resident, and cycled over
// layers with random expert ids so every iteration reads cold weights, like a decode token does.
//
// build: see run_f1f2.sh.   usage: moe_cpu_bench --gguf a.gguf[,b.gguf] [--threads 6] [--layers 8] [--iters 600]
//        [--js 1,2,4,6,10] [--gap-us 1500] [--label idle]
// prints one line per j:  BENCH label j=<j> n=<iters> median_ms=.. mean_ms=.. p90_ms=.. c_per_expert_ms=.. (median/j)
#include "ggml.h"
#include "ggml-backend.h"
#include "ggml-cpu.h"
#include "gguf.h"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fcntl.h>
#include <numeric>
#include <random>
#include <string>
#include <sys/mman.h>
#include <sys/stat.h>
#include <thread>
#include <unistd.h>
#include <vector>

struct layer_weights {
    int layer;
    ggml_tensor * gate;
    ggml_tensor * up;
    ggml_tensor * down;
};

static std::vector<std::string> split(const std::string & s, char sep) {
    std::vector<std::string> out;
    size_t start = 0;
    while (start <= s.size()) {
        size_t end = s.find(sep, start);
        if (end == std::string::npos) end = s.size();
        if (end > start) out.push_back(s.substr(start, end - start));
        start = end + 1;
    }
    return out;
}

int main(int argc, char ** argv) {
    std::string gguf_arg;
    std::string js_arg = "1,2,4,6,10";
    std::string label = "run";
    int n_threads = 6, n_layers = 8, iters = 600, gap_us = 1500;
    for (int i = 1; i + 1 < argc; i += 2) {
        std::string k = argv[i], v = argv[i + 1];
        if (k == "--gguf") gguf_arg = v;
        else if (k == "--threads") n_threads = atoi(v.c_str());
        else if (k == "--layers") n_layers = atoi(v.c_str());
        else if (k == "--iters") iters = atoi(v.c_str());
        else if (k == "--js") js_arg = v;
        else if (k == "--gap-us") gap_us = atoi(v.c_str());
        else if (k == "--label") label = v;
        else { fprintf(stderr, "unknown arg %s\n", k.c_str()); return 2; }
    }
    if (gguf_arg.empty()) { fprintf(stderr, "--gguf required\n"); return 2; }
    ggml_cpu_init();

    struct shard {
        gguf_context * g;
        ggml_context * meta;
        void * base;
        ggml_backend_buffer_t buf;
        size_t data_off;
    };
    std::vector<shard> shards;
    std::vector<std::pair<int, int>> candidates; // (shard, layer) that hold all three expert tensors
    for (const auto & path : split(gguf_arg, ',')) {
        shard sh = {};
        gguf_init_params gp = { /*no_alloc=*/true, &sh.meta };
        sh.g = gguf_init_from_file(path.c_str(), gp);
        if (!sh.g) { fprintf(stderr, "cannot read %s\n", path.c_str()); return 1; }
        int fd = open(path.c_str(), O_RDONLY);
        struct stat st; fstat(fd, &st);
        sh.base = mmap(nullptr, st.st_size, PROT_READ, MAP_SHARED, fd, 0);
        if (sh.base == MAP_FAILED) { perror("mmap"); return 1; }
        sh.buf = ggml_backend_cpu_buffer_from_ptr(sh.base, st.st_size);
        sh.data_off = gguf_get_data_offset(sh.g);
        for (int l = 0; l < 64; ++l) {
            char a[96], b[96], c[96];
            snprintf(a, sizeof a, "blk.%d.ffn_gate_exps.weight", l);
            snprintf(b, sizeof b, "blk.%d.ffn_up_exps.weight", l);
            snprintf(c, sizeof c, "blk.%d.ffn_down_exps.weight", l);
            if (gguf_find_tensor(sh.g, a) >= 0 && gguf_find_tensor(sh.g, b) >= 0 && gguf_find_tensor(sh.g, c) >= 0) {
                candidates.push_back({ (int) shards.size(), l });
            }
        }
        shards.push_back(sh);
    }
    // evenly spaced over all candidate layers so the quant mix matches the model
    std::vector<layer_weights> layers;
    for (int k = 0; k < n_layers && !candidates.empty(); ++k) {
        const auto & cand = candidates[(size_t) k * candidates.size() / n_layers];
        shard & sh = shards[cand.first];
        auto place = [&](const char * fmt) -> ggml_tensor * {
            char name[96];
            snprintf(name, sizeof name, fmt, cand.second);
            ggml_tensor * t = ggml_get_tensor(sh.meta, name);
            ggml_backend_tensor_alloc(sh.buf, t, (char *) sh.base + sh.data_off + gguf_get_tensor_offset(sh.g, gguf_find_tensor(sh.g, name)));
            return t;
        };
        layers.push_back({ cand.second, place("blk.%d.ffn_gate_exps.weight"), place("blk.%d.ffn_up_exps.weight"),
                           place("blk.%d.ffn_down_exps.weight") });
    }
    if (layers.empty()) { fprintf(stderr, "no expert layers found\n"); return 1; }

    const int64_t n_embd = layers[0].gate->ne[0], n_ff = layers[0].gate->ne[1], n_expert = layers[0].gate->ne[2];
    size_t touched = 0;
    auto t0 = std::chrono::steady_clock::now();
    volatile unsigned char sink = 0;
    for (const auto & lw : layers) {
        for (ggml_tensor * t : { lw.gate, lw.up, lw.down }) {
            const unsigned char * p = (const unsigned char *) t->data;
            for (size_t o = 0; o < ggml_nbytes(t); o += 4096) sink ^= p[o];
            touched += ggml_nbytes(t);
        }
    }
    fprintf(stderr, "layers=%zu n_embd=%ld n_ff=%ld n_expert=%ld touched=%.2f GiB in %.1f s\n", layers.size(),
            (long) n_embd, (long) n_ff, (long) n_expert, touched / 1073741824.0,
            std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count());
    for (const auto & lw : layers) {
        fprintf(stderr, "  blk.%d gate=%s up=%s down=%s\n", lw.layer, ggml_type_name(lw.gate->type),
                ggml_type_name(lw.up->type), ggml_type_name(lw.down->type));
    }

    ggml_threadpool_params tpp = ggml_threadpool_params_default(n_threads);
    ggml_threadpool * tp = ggml_threadpool_new(&tpp);

    std::mt19937 rng(12345);
    std::vector<int> perm(n_expert);
    std::iota(perm.begin(), perm.end(), 0);
    std::vector<float> xdata(n_embd);
    for (auto & v : xdata) v = std::uniform_real_distribution<float>(-1.f, 1.f)(rng);

    for (const auto & js : split(js_arg, ',')) {
        const int j = atoi(js.c_str());
        struct graph_set {
            ggml_context * ctx;
            ggml_cgraph * gf;
            ggml_tensor * ids;
            ggml_cplan plan;
            std::vector<uint8_t> work;
        };
        std::vector<graph_set> graphs(layers.size());
        for (size_t li = 0; li < layers.size(); ++li) {
            ggml_init_params ip = { 8u << 20, nullptr, false };
            ggml_context * ctx = ggml_init(ip);
            ggml_tensor * x = ggml_new_tensor_3d(ctx, GGML_TYPE_F32, n_embd, 1, 1);
            memcpy(x->data, xdata.data(), xdata.size() * sizeof(float));
            ggml_tensor * ids = ggml_new_tensor_2d(ctx, GGML_TYPE_I32, j, 1);
            ggml_tensor * gate = ggml_mul_mat_id(ctx, layers[li].gate, x, ids);
            ggml_tensor * up = ggml_mul_mat_id(ctx, layers[li].up, x, ids);
            ggml_tensor * act = ggml_mul(ctx, ggml_silu(ctx, gate), up);
            ggml_tensor * down = ggml_mul_mat_id(ctx, layers[li].down, act, ids);
            ggml_cgraph * gf = ggml_new_graph(ctx);
            ggml_build_forward_expand(gf, down);
            graphs[li].ctx = ctx;
            graphs[li].gf = gf;
            graphs[li].ids = ids;
            graphs[li].plan = ggml_graph_plan(gf, n_threads, tp);
            graphs[li].work.resize(graphs[li].plan.work_size);
            graphs[li].plan.work_data = graphs[li].work.data();
        }
        std::vector<double> ms;
        ms.reserve(iters);
        const int warmup = 40;
        for (int it = 0; it < iters + warmup; ++it) {
            graph_set & gs = graphs[it % graphs.size()];
            for (int k = 0; k < j; ++k) {
                std::swap(perm[k], perm[k + rng() % (n_expert - k)]);
                ((int32_t *) gs.ids->data)[k] = perm[k];
            }
            auto a = std::chrono::steady_clock::now();
            ggml_status s = ggml_graph_compute(gs.gf, &gs.plan);
            auto b = std::chrono::steady_clock::now();
            if (s != GGML_STATUS_SUCCESS) { fprintf(stderr, "compute failed %d\n", (int) s); return 1; }
            if (it >= warmup) ms.push_back(std::chrono::duration<double, std::milli>(b - a).count());
            if (gap_us > 0) std::this_thread::sleep_for(std::chrono::microseconds(gap_us));
        }
        std::sort(ms.begin(), ms.end());
        const double med = ms[ms.size() / 2], p90 = ms[ms.size() * 9 / 10];
        const double mean = std::accumulate(ms.begin(), ms.end(), 0.0) / ms.size();
        printf("BENCH %s j=%d n=%zu threads=%d gap_us=%d median_ms=%.4f mean_ms=%.4f p90_ms=%.4f c_per_expert_ms=%.4f\n",
               label.c_str(), j, ms.size(), n_threads, gap_us, med, mean, p90, med / j);
        fflush(stdout);
        for (auto & gs : graphs) ggml_free(gs.ctx);
    }
    (void) sink;
    return 0;
}
