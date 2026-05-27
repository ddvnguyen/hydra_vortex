#!/bin/bash
set -euo pipefail
RAMDISK="/mnt/llm-ram"
SIZE="30G"
if ! mountpoint -q "$RAMDISK"; then
    sudo mkdir -p "$RAMDISK"
    sudo mount -t tmpfs -o size=$SIZE,mode=0755 tmpfs "$RAMDISK"
fi
sudo mkdir -p "$RAMDISK"/store/{raw/kv,chunks,manifests}
echo "tmpfs ready at $RAMDISK"
