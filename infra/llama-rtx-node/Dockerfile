FROM ubuntu:26.04

# Prevent interactive prompts during package installations
ENV DEBIAN_FRONTEND=noninteractive

# Install fundamental bootstrap dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    gnupg \
    && rm -rf /var/lib/apt/lists/*

# Add NVIDIA's CUDA repository keyring using the stable 24.04 pathing layer
RUN curl -fSsL https://developer.download.nvidia.com/compute/cuda/repos/ubuntu2404/x86_64/cuda-keyring_1.1-1_all.deb -o cuda-keyring.deb \
    && dpkg -i cuda-keyring.deb \
    && rm cuda-keyring.deb

# Install the exact CUDA 13.2 runtime components
# Pinned to 13-2 to guarantee you don't accidentally pull newer versions
RUN apt-get update && apt-get install -y --no-install-recommends \
    cuda-cudart-13-2 \
    libcublas-13-2 \
    libnccl2 \
    libgomp1 \
    libibverbs1 \
    && rm -rf /var/lib/apt/lists/*

# CRITICAL: Core environment variables for NVIDIA Container Toolkit
# Without these, the container cannot hook into the host's GPU drivers
ENV NVIDIA_VISIBLE_DEVICES=all
ENV NVIDIA_DRIVER_CAPABILITIES=compute,utility

# Configure system paths to discover CUDA binaries and shared objects (.so)
ENV PATH=/usr/local/cuda-13.2/bin:${PATH}
ENV LD_LIBRARY_PATH=/usr/local/cuda-13.2/lib64:${LD_LIBRARY_PATH}

WORKDIR /workspace