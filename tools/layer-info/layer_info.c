// Per-layer tensor inventory of GGUF shards, using the fork's own ggml/gguf API (libggml-base).
// Header-only read (no_alloc, no tensor data touched). CPU only.
// Output: TSV  shard  layer  tensor  type  ne0 ne1 ne2 ne3  bytes
#include "gguf.h"
#include "ggml.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int layer_of(const char * name) {
    int il;
    if (sscanf(name, "blk.%d.", &il) == 1) return il;
    return -1;
}

int main(int argc, char ** argv) {
    if (argc < 2) { fprintf(stderr, "usage: %s shard.gguf...\n", argv[0]); return 2; }
    printf("#shard\tlayer\ttensor\ttype\tne0\tne1\tne2\tne3\tbytes\n");
    for (int a = 1; a < argc; a++) {
        struct ggml_context * ctx = NULL;
        struct gguf_init_params p = { .no_alloc = true, .ctx = &ctx };
        struct gguf_context * g = gguf_init_from_file(argv[a], p);
        if (!g) { fprintf(stderr, "FAILED to read %s\n", argv[a]); return 1; }
        if (a == 1 || 1) {
            int64_t n = gguf_get_n_kv(g);
            for (int64_t i = 0; i < n; i++) {
                const char * k = gguf_get_key(g, i);
                enum gguf_type t = gguf_get_kv_type(g, i);
                if (t == GGUF_TYPE_STRING)       fprintf(stderr, "KV\t%s\t%s\t%s\n", argv[a], k, gguf_get_val_str(g, i));
                else if (t == GGUF_TYPE_UINT32)  fprintf(stderr, "KV\t%s\t%s\t%u\n", argv[a], k, gguf_get_val_u32(g, i));
                else if (t == GGUF_TYPE_INT32)   fprintf(stderr, "KV\t%s\t%s\t%d\n", argv[a], k, gguf_get_val_i32(g, i));
                else if (t == GGUF_TYPE_UINT64)  fprintf(stderr, "KV\t%s\t%s\t%llu\n", argv[a], k, (unsigned long long) gguf_get_val_u64(g, i));
                else if (t == GGUF_TYPE_FLOAT32) fprintf(stderr, "KV\t%s\t%s\t%g\n", argv[a], k, gguf_get_val_f32(g, i));
                else if (t == GGUF_TYPE_BOOL)    fprintf(stderr, "KV\t%s\t%s\t%d\n", argv[a], k, gguf_get_val_bool(g, i));
                else if (t == GGUF_TYPE_ARRAY && gguf_get_arr_type(g, i) != GGUF_TYPE_STRING)
                    fprintf(stderr, "KV\t%s\t%s\t<array n=%zu>\n", argv[a], k, gguf_get_arr_n(g, i));
            }
        }
        int64_t nt = gguf_get_n_tensors(g);
        for (int64_t i = 0; i < nt; i++) {
            const char * name = gguf_get_tensor_name(g, i);
            struct ggml_tensor * t = ggml_get_tensor(ctx, name);
            printf("%s\t%d\t%s\t%s\t%lld\t%lld\t%lld\t%lld\t%zu\n",
                   strrchr(argv[a], '/') ? strrchr(argv[a], '/') + 1 : argv[a],
                   layer_of(name), name, ggml_type_name(gguf_get_tensor_type(g, i)),
                   (long long) t->ne[0], (long long) t->ne[1], (long long) t->ne[2], (long long) t->ne[3],
                   gguf_get_tensor_size(g, i));
        }
        gguf_free(g); ggml_free(ctx);
    }
    return 0;
}
