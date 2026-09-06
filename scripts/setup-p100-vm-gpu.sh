#!/usr/bin/env bash
# Attach the Tesla P100 (PCI 0000:08:00.0, 10de:15f8) to the KVM VM
# ("ubuntu26_server") as a persistent PCIe hostdev and verify the guest
# sees it. Fixes "P100 missing after VM boot" caused by libvirt domain
# XML having no <hostdev> entry.
#
# Run from the repo root on the RTX host: bash scripts/setup-p100-vm-gpu.sh
# Requires: virsh domain ubuntu26_server, SSH alias hydra-p100.
set -euo pipefail

DOMAIN="ubuntu26_server"
VM="hydra-p100"
PCI_ADDR="0000:08:00.0"

echo "==> Checking $PCI_ADDR is bound to vfio-pci on the host"
LSPCI_OUT="$(lspci -nnk -s "$PCI_ADDR")"
if ! echo "$LSPCI_OUT" | grep -q "Kernel driver in use: vfio-pci"; then
  echo "ERROR: $PCI_ADDR (10de:15f8) is not bound to vfio-pci."
  echo "       Remediation: ensure /etc/modprobe.d/vfio.conf contains"
  echo "       'options vfio-pci ids=10de:15f8 disable_vga=1', rebuild"
  echo "       initramfs (update-initramfs -u), then reboot the host."
  echo "--- lspci output ---"
  echo "$LSPCI_OUT"
  exit 1
fi

DID_ATTACH=0
echo "==> Checking $DOMAIN XML for an existing P100 hostdev"
if virsh dumpxml "$DOMAIN" | sed -n '/<hostdev/,/<\/hostdev>/p' | grep -q "bus=.0x08"; then
  echo "    Hostdev already present — skipping attach"
else
  echo "    No P100 hostdev in XML — attaching persistently"
  ATTACH_XML="$(mktemp)"
  trap 'rm -f "$ATTACH_XML"' EXIT
  cat > "$ATTACH_XML" <<'EOF'
<hostdev mode="subsystem" type="pci" managed="yes">
  <source>
    <address domain="0x0000" bus="0x08" slot="0x00" function="0x0"/>
  </source>
</hostdev>
EOF
  virsh attach-device "$DOMAIN" "$ATTACH_XML" --persistent
  echo "    Attached $PCI_ADDR to $DOMAIN (persistent)"
  DID_ATTACH=1
fi

if [ "$DID_ATTACH" -eq 0 ] && \
   ssh -o ConnectTimeout=5 -o BatchMode=yes "$VM" 'nvidia-smi --query-gpu=name --format=csv,noheader' 2>/dev/null | grep -q P100; then
  echo "==> Already attached and guest sees GPU — skipping reboot"
else
  echo "==> Rebooting $DOMAIN so the guest picks up the GPU"
  virsh reboot "$DOMAIN"
fi

echo "==> Waiting up to 120s for SSH on $VM"
SSH_UP=0
for _ in $(seq 1 24); do
  if ssh -o ConnectTimeout=5 -o BatchMode=yes "$VM" true 2>/dev/null; then
    SSH_UP=1
    break
  fi
  sleep 5
done
if [ "$SSH_UP" -ne 1 ]; then
  echo "ERROR: $VM unreachable 120s after reboot."
  exit 1
fi

echo "==> Verifying GPU inside the guest"
if ! GPU_NAME="$(ssh "$VM" 'nvidia-smi --query-gpu=name --format=csv,noheader')" ; then
  echo "ERROR: nvidia-smi failed or unavailable in guest."
  exit 1
fi
if ! echo "$GPU_NAME" | grep -q "P100"; then
  echo "ERROR: Guest reports GPU '$GPU_NAME', expected Tesla P100."
  echo "       Check in-guest NVIDIA driver load: ssh $VM 'dmesg | grep -i nvrm'"
  exit 1
fi

DRIVER_VERSION="$(ssh "$VM" 'nvidia-smi --query-gpu=driver_version --format=csv,noheader')"
echo "PASS: Tesla P100 visible in guest ($GPU_NAME), NVIDIA driver $DRIVER_VERSION"
