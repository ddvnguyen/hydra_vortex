#!/bin/bash

set -e

if ! ldconfig -p | grep -q libgomp.so.1; then
    echo "Installing runtime dependencies..."

    apt-get update

    DEBIAN_FRONTEND=noninteractive apt-get install -y \
        libgomp1 \
        libstdc++6 \
        libnccl2 \
        libibverbs1 \
        glibc

    echo "Dependencies installed."
fi

echo "Container init complete."
exec "$@"