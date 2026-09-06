# Cookbook: Restore Tesla P100 GPU Passthrough to KVM VM

## Problem

After a libvirt VM boot (`ubuntu26_server`), the Tesla P100 is not visible
inside the guest and hydra-head / llama-engine cannot start. Rebooting or
redeploying does not fix it because the root cause lives in the libvirt
domain XML on the **host**.

## Symptoms

In the guest:

```
$ nvidia-smi
NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.
```

On the host, the GPU is bound to vfio-pci and everything looks healthy:

```
$ lspci -nnk -s 08:00.0
08:00.0 3D controller [0302]: NVIDIA Corporation GP100GL [Tesla P100 PCIe 16GB] [10de:15f8]
        Kernel driver in use: vfio-pci
```

## Root Cause

`virsh dumpxml ubuntu26_server` contains **zero `<hostdev>` entries**. The
vfio-pci module binding (`options vfio-pci ids=10de:15f8 ...` in
`/etc/modprobe.d/vfio.conf`) only makes the GPU *available* for passthrough —
it is never *attached* to the domain. Every VM boot therefore comes up
without a GPU, regardless of in-guest driver state.

Confirmed clean IOMMU setup for this host: `0000:08:00.0` sits alone in its
IOMMU group.

## Fix

Run from the repo root on the RTX host:

```bash
bash scripts/setup-p100-vm-gpu.sh
```

The script is idempotent:

1. Verifies `0000:08:00.0` (10de:15f8) is bound to vfio-pci on the host.
2. Attaches the P100 as a persistent PCI hostdev to the domain
   (`virsh attach-device ... --persistent`) **only if** the XML lacks a
   bus `0x08` hostdev already.
3. Reboots the VM and waits up to 120 s for SSH (`hydra-p100`).
4. Verifies in-guest `nvidia-smi` reports a P100 and prints a PASS line
   with the driver version.

If the host-side check fails, the script exits early with remediation hints
(`/etc/modprobe.d/vfio.conf`, initramfs rebuild, host reboot). When
passthrough is already healthy (hostdev present and the guest reports a P100),
the script is a no-op — it skips both the attach and the reboot and goes
straight to verification.

## Verification

```bash
# Host: persistent XML must contain a hostdev on guest-side slot
virsh dumpxml ubuntu26_server | grep -c hostdev          # >= 1
virsh dumpxml ubuntu26_server | grep "bus=.0x08"         # source address

# Guest: GPU live and claimed by driver 580.x+
ssh hydra-p100 'nvidia-smi --query-gpu=name,driver_version --format=csv,noheader'
```

Expected guest output:

```
Tesla P100-PCIE-16GB, 580.173.02
```

Guest-side PCI slot after attach: `07:00.0`.
