# Linux Apps and Packages Management Skill

## Overview

This skill provides comprehensive tools and techniques for managing applications and packages in Linux systems while maintaining safety and control.

**KEY PRINCIPLE: All package modification operations require explicit user confirmation before execution.**

## Files in This Skill

### 1. `SKILL.md` (Main Reference)
Comprehensive documentation with:
- 10 detailed use cases for package management
- Commands for `apt`, `dnf`, `pacman`, `zypper`, `brew`
- Dependency analysis and resolution
- System inventory exploration
- Troubleshooting guide
- Security considerations

### 2. `package-manager.sh` (Safe Utility Script)
Ready-to-use bash script with automatic package manager detection and confirmation prompts:

**Read-only Commands (No confirmation needed):**
```bash
sudo ./package-manager.sh search nginx          # Search for packages
sudo ./package-manager.sh info nginx            # Show package details
sudo ./package-manager.sh installed             # List installed packages
sudo ./package-manager.sh check nginx           # Is package installed?
sudo ./package-manager.sh version nginx         # Show package version
sudo ./package-manager.sh depends nginx         # Show dependencies
sudo ./package-manager.sh rdepends nginx        # Reverse dependencies
sudo ./package-manager.sh upgradable            # Show available updates (WITHOUT installing)
sudo ./package-manager.sh inventory             # System package audit
```

**Modification Commands (Require explicit 'yes' confirmation):**
```bash
sudo ./package-manager.sh install nginx         # Install (asks for confirmation)
sudo ./package-manager.sh remove nginx          # Remove (asks for confirmation)
sudo ./package-manager.sh upgrade               # Upgrade all (asks for confirmation)
sudo ./package-manager.sh clean                 # Clean cache (asks for confirmation)
```

### 3. `README.md` (This File)
Quick start guide and integration examples

## Quick Start

### Using the Utility Script

```bash
cd /path/to/manage-apps-packages

# Make executable (one time)
chmod +x package-manager.sh

# Search for a package (no confirmation needed)
sudo ./package-manager.sh search nginx

# Check what would be installed
sudo ./package-manager.sh info nginx

# Before installing, see what's available
sudo ./package-manager.sh upgradable

# Install a package (will ask for confirmation)
sudo ./package-manager.sh install nginx
# Output shows preview + prompts: "Type 'yes' to confirm"
```

### Using Direct Commands

Modern systems (preferred):
```bash
apt search package-name
apt show package-name
apt list --installed
```

Legacy systems:
```bash
sudo netstat -tlnp | grep package
sudo lsof -i -P -n
```

## Common Workflows

### Workflow 1: Install a New Application
```bash
# 1. Search for available packages
sudo ./package-manager.sh search postgresql

# 2. Check what's in the package
sudo ./package-manager.sh info postgresql

# 3. See dependencies before installing
sudo ./package-manager.sh depends postgresql

# 4. Install (WITH CONFIRMATION)
sudo ./package-manager.sh install postgresql
# Script shows preview of dependencies and size
# Prompts: "Type 'yes' to confirm"
```

### Workflow 2: System Audit Before Changes
```bash
# Get complete inventory first
sudo ./package-manager.sh inventory

# Check available updates (doesn't install)
sudo ./package-manager.sh upgradable

# Then decide if you want to upgrade
sudo ./package-manager.sh upgrade
# Will prompt for confirmation before proceeding
```

### Workflow 3: Troubleshoot Installation Issues
```bash
# Check if package is installed
sudo ./package-manager.sh check nginx

# See what depends on it
sudo ./package-manager.sh rdepends nginx

# View all configuration files
sudo ./package-manager.sh config nginx

# Find which package provides a command
sudo ./package-manager.sh provides nginx
```

### Workflow 4: Find Packages Providing Specific Tools
```bash
# What package has this command?
sudo ./package-manager.sh provides docker

# Get full package information
sudo ./package-manager.sh info docker

# Check if it's already installed
sudo ./package-manager.sh check docker
```

## Safety Features

### Automatic Confirmation Prompt
All destructive operations display:
- **Detailed preview** of what will change
- **Dependencies** that will be installed/removed
- **Size information** (download and installed size)
- **Reverse dependencies** (what else depends on this)
- **User prompt** requiring explicit `yes` confirmation

Example output:
```
=== Installation Preview ===
Package: nginx

Dependencies that will be installed:
  libc6 (>= 2.34) - c6-library
  libpcre3 - perl-compat-regex
  zlib1g - compression-library

Size information:
Download-Size: 850 kB
Installed-Size: 2500 kB

⚠️  CONFIRMATION REQUIRED
Install package 'nginx' with all dependencies?

Type 'yes' to confirm, anything else to cancel:
```

### Automatic Package Manager Detection
Script automatically detects your system:
- Ubuntu/Debian → `apt`
- Fedora/RHEL → `dnf`
- Arch → `pacman`
- openSUSE → `zypper`
- macOS → `brew`

Check detected PM:
```bash
sudo ./package-manager.sh pm-info
```

## Common Scenarios

### Scenario 1: Pre-Deployment Check for Monitoring Stack
```bash
#!/bin/bash
# Verify packages before deploying Prometheus/Grafana stack

echo "Checking availability of required packages..."

PACKAGES=("docker.io" "docker-compose" "curl" "wget")

for pkg in "${PACKAGES[@]}"; do
  if sudo ./package-manager.sh check "$pkg" >/dev/null 2>&1; then
    echo "✓ $pkg installed"
  else
    echo "✗ $pkg NOT installed - need to install"
  fi
done

# Check disk space and upgrade availability
echo ""
echo "Available updates:"
sudo ./package-manager.sh upgradable

# Prompt user before any changes
read -p "Install missing packages? (yes/no): " response
if [[ "$response" == "yes" ]]; then
  for pkg in "${PACKAGES[@]}"; do
    sudo ./package-manager.sh install "$pkg"
  done
fi
```

### Scenario 2: System Inventory for Documentation
```bash
#!/bin/bash
# Create system documentation before changes

echo "=== Pre-Change System Inventory ===" > system-before.txt
sudo ./package-manager.sh inventory >> system-before.txt
sudo ./package-manager.sh installed >> system-before.txt

echo "System state documented to system-before.txt"
```

### Scenario 3: Safe Bulk Cleanup
```bash
#!/bin/bash
# Preview what will be removed before cleaning

echo "Packages that could be removed:"
case $(detect_pm) in
  apt)
    sudo apt autoremove --dry-run
    ;;
  dnf)
    sudo dnf autoremove --dry-run
    ;;
esac

# User decides if they want to proceed
read -p "Proceed with cleanup? (yes/no): " response
if [[ "$response" == "yes" ]]; then
  sudo ./package-manager.sh clean
fi
```

## Technical Details

### Package Manager Commands Supported

| Feature | apt | dnf | pacman | zypper | brew |
|---------|-----|-----|--------|--------|------|
| Search | ✓ | ✓ | ✓ | ✓ | ✓ |
| Info | ✓ | ✓ | ✓ | ✓ | ✓ |
| Check installed | ✓ | ✓ | ✓ | ✓ | ✓ |
| Dependencies | ✓ | ✓ | ✓ | ✓ | ✓ |
| Install (with confirmation) | ✓ | ✓ | ✓ | ✓ | ✓ |
| Remove (with confirmation) | ✓ | ✓ | ✓ | ✓ | ✓ |
| Upgrade (with confirmation) | ✓ | ✓ | ✓ | ✓ | ✓ |

### Confirmation Workflow

All destructive operations follow this flow:
1. **Detect** what will change (dependencies, size, conflicts)
2. **Display** detailed preview to user
3. **Prompt** for explicit confirmation (requires typing "yes")
4. **Confirm** user understands the impact
5. **Execute** only if user typed "yes"
6. **Report** completion status

### Size Information

Size is displayed in multiple formats:
- **Download-Size** - Data transferred from repository
- **Installed-Size** - Disk space after installation
- **Largest packages** - Top N packages by installed size

## Troubleshooting

### "Permission denied" on removal/installation
Most operations need sudo:
```bash
sudo ./package-manager.sh install nginx
```

### "Package not found"
Update package index first:
```bash
# APT
sudo apt update

# DNF
sudo dnf makecache

# Then search again
sudo ./package-manager.sh search package-name
```

### "Unmet dependencies"
Check dependencies before installing:
```bash
sudo ./package-manager.sh depends package-name
sudo ./package-manager.sh rdepends package-name
```

## Integration with Monitoring Stacks

When deploying services like Prometheus, Grafana, or LiteLLM:

```bash
# Step 1: Check current state
sudo ./package-manager.sh inventory

# Step 2: Preview what would be needed
for pkg in python3 python3-pip curl; do
  sudo ./package-manager.sh info "$pkg"
done

# Step 3: Install required packages (WITH CONFIRMATION)
sudo ./package-manager.sh install python3-pip

# Step 4: Verify installation
sudo ./package-manager.sh check python3-pip
```

## Related Skills

- **manage-linux-ports**: Check port availability before deploying new services
- **setup-container-monitoring-stack**: Deploy monitoring containers (uses this skill for package checks)
- **validate-container-isolation**: Verify system packages and dependencies

## Best Practices

1. **Always Preview First**: Use read-only commands before making changes
2. **Check Dependencies**: Understand what will be installed/removed
3. **Review Size Impact**: Know disk space requirements
4. **Get Confirmation**: Wait for user "yes" before changes
5. **Document Changes**: Keep audit trail of modifications
6. **Test on Non-Critical Systems**: Verify before production changes
7. **Backup Before Major Changes**: Create snapshots before large operations

## Performance Notes

- Search operations: <500ms
- Info retrieval: <100ms
- Dependency resolution: 200-500ms
- Inventory scan: 1-5s (depends on package count)
- Installation: Varies by package (network dependent)

## Support

For issues or questions:
1. Check `SKILL.md` for detailed examples
2. Review common scenarios above
3. Use `./package-manager.sh help` for command reference
4. Test with read-only commands first before running modifications

