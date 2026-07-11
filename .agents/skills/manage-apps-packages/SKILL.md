---
description: Apps and packages management in Linux. Search for packages, check installed apps, manage dependencies, and understand system packages. ALWAYS requires user confirmation before installing, removing, or updating packages.
keywords: [linux, packages, apt, dnf, pacman, brew, package-management, dependencies, system-apps, app-inventory]
---

# Linux Apps and Packages Management Skill

## Overview

This skill provides comprehensive techniques for managing applications and packages in Linux systems. It helps you:

- **Search for packages** by name or functionality
- **Check installed packages** and their versions
- **Understand dependencies** and their relationships
- **Explore package information** (size, maintainer, description)
- **Manage system applications** safely and efficiently

## ⚠️ IMPORTANT - User Confirmation Required

**NEVER** install, remove, update, or modify packages without explicit user confirmation.

All destructive operations (install, remove, upgrade, purge) must:
1. Show the exact command to be executed
2. Display what will be affected (dependencies, conflicts, size changes)
3. Wait for user approval before proceeding
4. Allow user to cancel or review before committing

## Prerequisites

- Linux system with package manager installed
- Common package managers: `apt/apt-get`, `dnf/yum`, `pacman`, `zypper`, `brew`
- Sufficient permissions (usually `sudo` required for install/remove)
- Optional: `flatpak`, `snap` for sandboxed apps

## Detecting Package Manager

```bash
# Determine system package manager
if command -v apt &> /dev/null; then
  PM="apt"
elif command -v dnf &> /dev/null; then
  PM="dnf"
elif command -v pacman &> /dev/null; then
  PM="pacman"
elif command -v zypper &> /dev/null; then
  PM="zypper"
elif command -v brew &> /dev/null; then
  PM="brew"
else
  echo "Unknown package manager"
fi
```

## Use Case 1: Search for Packages

**Using APT (Debian/Ubuntu):**
```bash
apt search nginx
apt-cache search nginx
apt search nginx --names-only
```

**Using DNF (Fedora/RHEL):**
```bash
dnf search nginx
dnf search --all nginx
```

**Using Pacman (Arch):**
```bash
pacman -Ss nginx
```

**Using Zypper (openSUSE):**
```bash
zypper search nginx
```

**Get detailed package information:**
```bash
# Show full details
apt show nginx
apt-cache show nginx
dnf info nginx
pacman -Si nginx
```

## Use Case 2: Check Installed Packages

**List all installed packages:**
```bash
# APT
dpkg -l
apt list --installed
apt list --installed | grep nginx

# DNF
dnf list installed
dnf list installed | grep nginx

# Pacman
pacman -Q
pacman -Q | grep nginx
```

**Check if specific package is installed:**
```bash
# APT
dpkg -l | grep nginx
dpkg -s nginx  # Shows detailed info if installed

# DNF
dnf list installed nginx

# Pacman
pacman -Q nginx
```

**Get package version:**
```bash
# APT
apt-cache policy nginx
dpkg -l nginx

# DNF
dnf list installed nginx

# Pacman
pacman -Q nginx
```

## Use Case 3: Understand Dependencies

**Show what package depends on:**
```bash
# APT - Show dependencies
apt-cache depends nginx
apt-cache depends --recurse nginx

# APT - Show reverse dependencies (what depends on this)
apt-cache rdepends nginx

# DNF
dnf repoquery --requires nginx
dnf repoquery --whatrequires nginx

# Pacman
pactree nginx
pactree -r nginx  # Reverse dependencies
```

**Check for broken dependencies:**
```bash
# APT
apt check

# DNF
dnf check

# Pacman
pacman -T  # Check missing dependencies
```

## Use Case 4: Package Information

**Get comprehensive package details:**
```bash
# Size information
apt-cache show nginx | grep -E "Size|Installed-Size"
dnf info nginx | grep -E "Size|Download Size"

# Description and purpose
apt-cache show nginx | grep Description
dnf info nginx | grep Description

# Maintainer and source
apt-cache show nginx | grep Maintainer
dnf info nginx | grep Vendor
```

**Search by description:**
```bash
# APT - Search in descriptions
apt search --full 'web server'

# DNF
dnf search --description 'web server'
```

## Use Case 5: Repository and Source Information

**Show package source repository:**
```bash
# APT
apt-cache policy nginx

# DNF
dnf info nginx | grep "Repo"

# Pacman
pacman -Si nginx | grep Repository
```

**List enabled repositories:**
```bash
# APT
grep -r "deb " /etc/apt/

# DNF
dnf repolist

# Pacman
cat /etc/pacman.conf | grep "^\[" -A 1
```

## Use Case 6: System Inventory

**Count installed packages:**
```bash
# APT
apt list --installed | wc -l
dpkg -l | tail -1

# DNF
dnf list installed | wc -l

# Pacman
pacman -Q | wc -l
```

**Find disk space used by packages:**
```bash
# APT - Largest installed packages
dpkg-query -W -f='${Installed-Size}\t${Package}\n' | sort -rn | head -20

# DNF
dnf repoquery --installed --qf "%{size} %{name}"  | sort -rn | head -20

# Pacman
pacman -Q --info | grep -E "^Name|^Installed"
```

**Find packages not in any repository (orphaned):**
```bash
# APT
apt list --installed | cut -d '/' -f1 > /tmp/installed.txt
apt-cache pkgnames > /tmp/available.txt
comm -23 /tmp/installed.txt /tmp/available.txt

# DNF
dnf repoquery --installed --latest-limit=1 -q | wc -l

# Pacman
pacman -Qdt  # Display orphaned packages
```

## Use Case 7: Update and Upgrade (WITH CONFIRMATION)

⚠️ **ALWAYS confirm before executing update commands**

**Show what would be updated (dry-run):**
```bash
# APT - Show available upgrades WITHOUT installing
apt list --upgradable
apt upgrade --dry-run

# DNF - Check for updates
dnf check-upgrade

# Pacman - Check for updates
pacman -Qu
```

**Example confirmation flow:**
```bash
#!/bin/bash
echo "Available updates:"
apt list --upgradable
echo ""
read -p "Confirm upgrade? (yes/no): " confirm
if [[ "$confirm" == "yes" ]]; then
  sudo apt upgrade
else
  echo "Upgrade cancelled"
fi
```

## Use Case 8: Flatpak and Snap Management

**Search for flatpak/snap apps:**
```bash
flatpak search nginx
snapd find nginx
```

**Check installed flatpaks:**
```bash
flatpak list --app
```

**Package info for sandboxed apps:**
```bash
flatpak info org.nginx.Nginx
snap info nginx
```

## Use Case 9: Dependency Resolution Issues

**Check for unmet dependencies:**
```bash
# APT
sudo apt check

# DNF
sudo dnf check

# Look for broken packages
dpkg --configure -a
```

**See dependency tree before action:**
```bash
# Show what will be installed with a package
apt-cache depends --no-suggests --no-recommends nginx

# Show exact installation size impact
apt-cache show nginx | grep "Installed-Size"
```

## Use Case 10: Safe System Exploration

**Find packages providing a command:**
```bash
# APT
apt-file search /usr/bin/nginx
apt-file search --regex 'bin/.*nginx'

# DNF
dnf provides /usr/bin/nginx

# Pacman
pacman -F nginx
pacman -F /usr/bin/nginx
```

**Find all configuration files for a package:**
```bash
# APT
dpkg -L nginx | grep '/etc/'

# DNF
rpm -q -c nginx

# Pacman
pacman -Ql nginx | grep '/etc/'
```

## Troubleshooting

### "Package not found"
```bash
# Update package index first
sudo apt update
sudo dnf makecache
sudo pacman -Sy

# Then search again
apt search package-name
```

### "Unmet dependencies"
```bash
# APT
sudo apt --fix-broken install

# DNF
sudo dnf install --setopt=strict=False package-name

# Try dependency resolution
apt-cache depends package-name
```

### "Repository not found"
```bash
# Check enabled repositories
apt-cache policy
dnf repolist

# Add repository if needed (WITH USER CONFIRMATION ONLY)
```

## Integration with Monitoring Stacks

When deploying monitoring tools, check package availability first:

```bash
#!/bin/bash
# Pre-deployment package check
PACKAGES=("prometheus" "grafana" "prometheus-node-exporter")

echo "Checking package availability:"
for pkg in "${PACKAGES[@]}"; do
  if apt-cache search "^$pkg$" | grep -q .; then
    echo "✓ $pkg found in repositories"
  else
    echo "✗ $pkg NOT found - may need manual installation"
  fi
done

# Show what would be installed
echo ""
echo "Installation preview (with dependencies):"
apt-cache depends prometheus
```

## Summary Table

| Task | APT Command | DNF Command | Pacman Command |
|------|-------------|-------------|----------------|
| Search | `apt search pkg` | `dnf search pkg` | `pacman -Ss pkg` |
| Show info | `apt show pkg` | `dnf info pkg` | `pacman -Si pkg` |
| List installed | `apt list --installed` | `dnf list installed` | `pacman -Q` |
| Check if installed | `dpkg -s pkg` | `dnf list installed pkg` | `pacman -Q pkg` |
| Dependencies | `apt-cache depends pkg` | `dnf repoquery --requires pkg` | `pactree pkg` |
| Upgradable | `apt list --upgradable` | `dnf check-upgrade` | `pacman -Qu` |
| Updates available | `apt upgrade --dry-run` | `dnf check-upgrade` | `pacman -Qu` |

## Best Practices

1. **Always Preview First**: Use `--dry-run` or check-only modes before making changes
2. **Get User Confirmation**: Never silently install/remove packages
3. **Document Changes**: Keep track of what packages were added/removed and why
4. **Check Dependencies**: Understand what will be installed with a package
5. **Backup Configuration**: Before removing packages, note important configs
6. **Test on Non-Production**: Always test package operations on non-critical systems first
7. **Review Update Impact**: Check what will be upgraded before running updates

## Security Considerations

- Verify package sources before installation
- Check for known vulnerabilities in packages
- Review permissions required by packages
- Use minimal dependency sets where possible
- Keep audit logs of package changes

