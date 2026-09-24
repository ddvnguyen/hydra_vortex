#include <cstdio>
#include <cstdlib>
#include <cuda_runtime.h>
int main(int argc,char**argv){
  size_t n=(size_t)256<<20; void*d; cudaMalloc(&d,n);
  void*pin; cudaHostAlloc(&pin,n,0); void*pg=malloc(n); for(size_t i=0;i<n;i+=4096){((char*)pg)[i]=1;((char*)pin)[i]=1;}
  cudaEvent_t a,b; cudaEventCreate(&a); cudaEventCreate(&b);
  for(int pass=0;pass<2;pass++){
    for(int kind=0;kind<2;kind++){
      void*h=kind?pin:pg; float best=1e9,tot=0; int reps=12;
      for(int i=0;i<reps;i++){cudaEventRecord(a); cudaMemcpyAsync(d,h,n,cudaMemcpyHostToDevice,0); cudaEventRecord(b); cudaEventSynchronize(b); float ms; cudaEventElapsedTime(&ms,a,b); tot+=ms; if(ms<best)best=ms;}
      printf("pass%d %s: best %.2f GB/s  mean %.2f GB/s\n",pass,kind?"pinned":"pageable",n/best/1e6,n/(tot/reps)/1e6);
    }
  }
  return 0;}
