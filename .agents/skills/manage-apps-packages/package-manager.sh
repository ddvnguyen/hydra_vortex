#!/bin/bash
# Linux Package Management Utility
# IMPORTANT: Requires user confirmation before any destructive operations
# Usage: ./package-manager.sh [command] [options]

set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
BOLD='\033[1m'
NC='\033[0m' # No Color

# Detect package manager
detect_pm() {
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
    echo -e "${RED}Error: No supported package manager found${NC}"
    exit 1
  fi
}

usage() {
  cat << EOF
Linux Package Management Utility

⚠️  IMPORTANT: All package modifications require explicit user confirmation

Usage: $0 [command] [options]

Search & Information Commands (Read-only, no confirmation needed):
  search PACKAGE        Search for a package by name
  info PACKAGE          Show detailed package information
  installed             List all installed packages
  check PACKAGE         Check if package is installed
  version PACKAGE       Show package version
  depends PACKAGE       Show package dependencies
  rdepends PACKAGE      Show reverse dependencies
  size PACKAGE          Show package size information
  provides CMD          Find package providing a command
  config PACKAGE        Show package config files
  inventory             System package inventory report
  upgradable            List available updates (does NOT install)

Management Commands (Requires user confirmation before execution):
  install PACKAGE       Install a package (WITH CONFIRMATION)
  remove PACKAGE        Remove a package (WITH CONFIRMATION)
  upgrade               Upgrade all packages (WITH CONFIRMATION)
  update PACKAGE        Update specific package (WITH CONFIRMATION)
  clean                 Clean package cache (WITH CONFIRMATION)

System Commands:
  pm-info              Show detected package manager info
  help                 Show this help message

Examples:
  $0 search nginx              # Find nginx packages
  $0 info nginx                # Show nginx details
  $0 installed | grep python   # List python packages
  $0 check nginx               # Is nginx installed?
  $0 depends nginx             # What does nginx need?
  $0 upgradable                # What can be upgraded?
  $0 install nginx             # Install nginx (ASKS FOR CONFIRMATION)
  $0 inventory                 # Full package inventory

EOF
  exit 0
}

confirm() {
  local prompt="$1"
  local response
  
  echo -e ""
  echo -e "${YELLOW}${BOLD}⚠️  CONFIRMATION REQUIRED${NC}"
  echo -e "${YELLOW}$prompt${NC}"
  echo -e ""
  
  read -p "Type 'yes' to confirm, anything else to cancel: " response
  
  if [[ "$response" == "yes" ]]; then
    return 0
  else
    echo -e "${RED}✗ Operation cancelled${NC}"
    return 1
  fi
}

show_install_preview() {
  local pkg=$1
  
  echo -e "${BLUE}=== Installation Preview ===${NC}"
  echo -e "Package: ${BOLD}$pkg${NC}"
  echo ""
  
  case $PM in
    apt)
      echo -e "${YELLOW}Dependencies that will be installed:${NC}"
      apt-cache depends --no-suggests --no-recommends "$pkg" 2>/dev/null || true
      echo ""
      echo -e "${YELLOW}Size information:${NC}"
      apt-cache show "$pkg" 2>/dev/null | grep -E "^(Download-Size|Installed-Size):" || true
      ;;
    dnf)
      echo -e "${YELLOW}Dependencies that will be installed:${NC}"
      dnf repoquery --requires --recursive "$pkg" 2>/dev/null | head -20 || true
      echo ""
      echo -e "${YELLOW}Size information:${NC}"
      dnf info "$pkg" 2>/dev/null | grep -E "^(Download Size|Installed Size):" || true
      ;;
    pacman)
      echo -e "${YELLOW}Dependencies that will be installed:${NC}"
      pacman -Si "$pkg" 2>/dev/null | grep -A 5 "^Depends" || true
      echo ""
      echo -e "${YELLOW}Size information:${NC}"
      pacman -Si "$pkg" 2>/dev/null | grep -E "^(Compressed|Installed)" || true
      ;;
  esac
  echo ""
}

show_remove_preview() {
  local pkg=$1
  
  echo -e "${BLUE}=== Removal Preview ===${NC}"
  echo -e "Package: ${BOLD}$pkg${NC}"
  echo ""
  
  case $PM in
    apt)
      echo -e "${YELLOW}Packages that depend on this (will break if removed):${NC}"
      apt-cache rdepends "$pkg" 2>/dev/null | head -10 || true
      echo ""
      echo -e "${YELLOW}Installed size:${NC}"
      apt-cache show "$pkg" 2>/dev/null | grep "^Installed-Size:" || true
      ;;
    dnf)
      echo -e "${YELLOW}Packages that depend on this:${NC}"
      dnf repoquery --whatrequires "$pkg" 2>/dev/null | head -10 || true
      ;;
    pacman)
      echo -e "${YELLOW}Packages that depend on this:${NC}"
      pactree -r "$pkg" 2>/dev/null | head -10 || true
      ;;
  esac
  echo ""
}

# Read-only commands
cmd_search() {
  local pkg=$1
  if [[ -z "$pkg" ]]; then
    echo -e "${RED}Error: Please specify package name${NC}"
    exit 1
  fi
  
  echo -e "${BLUE}=== Searching for '$pkg' ===${NC}"
  
  case $PM in
    apt)
      apt search "$pkg" 2>/dev/null || true
      ;;
    dnf)
      dnf search "$pkg" 2>/dev/null || true
      ;;
    pacman)
      pacman -Ss "$pkg" 2>/dev/null || true
      ;;
    brew)
      brew search "$pkg" 2>/dev/null || true
      ;;
  esac
}

cmd_info() {
  local pkg=$1
  if [[ -z "$pkg" ]]; then
    echo -e "${RED}Error: Please specify package name${NC}"
    exit 1
  fi
  
  echo -e "${BLUE}=== Package Information for '$pkg' ===${NC}"
  
  case $PM in
    apt)
      apt show "$pkg" 2>/dev/null || echo "Package not found"
      ;;
    dnf)
      dnf info "$pkg" 2>/dev/null || echo "Package not found"
      ;;
    pacman)
      pacman -Si "$pkg" 2>/dev/null || echo "Package not found"
      ;;
    brew)
      brew info "$pkg" 2>/dev/null || echo "Package not found"
      ;;
  esac
}

cmd_installed() {
  echo -e "${BLUE}=== Installed Packages ===${NC}"
  
  case $PM in
    apt)
      apt list --installed 2>/dev/null | head -30
      echo ""
      echo -e "${YELLOW}Total: $(apt list --installed 2>/dev/null | wc -l) packages${NC}"
      ;;
    dnf)
      dnf list installed 2>/dev/null | head -30
      echo ""
      echo -e "${YELLOW}Total: $(dnf list installed 2>/dev/null | wc -l) packages${NC}"
      ;;
    pacman)
      pacman -Q | head -30
      echo ""
      echo -e "${YELLOW}Total: $(pacman -Q | wc -l) packages${NC}"
      ;;
  esac
}

cmd_check() {
  local pkg=$1
  if [[ -z "$pkg" ]]; then
    echo -e "${RED}Error: Please specify package name${NC}"
    exit 1
  fi
  
  case $PM in
    apt)
      if dpkg -s "$pkg" &>/dev/null 2>&1; then
        echo -e "${GREEN}✓ Package '$pkg' is installed${NC}"
        dpkg -s "$pkg" | grep -E "^(Package|Version|Status):" 
      else
        echo -e "${YELLOW}✗ Package '$pkg' is NOT installed${NC}"
      fi
      ;;
    dnf)
      if dnf list installed "$pkg" &>/dev/null 2>&1; then
        echo -e "${GREEN}✓ Package '$pkg' is installed${NC}"
      else
        echo -e "${YELLOW}✗ Package '$pkg' is NOT installed${NC}"
      fi
      ;;
    pacman)
      if pacman -Q "$pkg" &>/dev/null 2>&1; then
        echo -e "${GREEN}✓ Package '$pkg' is installed${NC}"
        pacman -Q "$pkg"
      else
        echo -e "${YELLOW}✗ Package '$pkg' is NOT installed${NC}"
      fi
      ;;
  esac
}

cmd_version() {
  local pkg=$1
  if [[ -z "$pkg" ]]; then
    echo -e "${RED}Error: Please specify package name${NC}"
    exit 1
  fi
  
  echo -e "${BLUE}=== Version Information for '$pkg' ===${NC}"
  
  case $PM in
    apt)
      apt-cache policy "$pkg" 2>/dev/null || echo "Package not found"
      ;;
    dnf)
      dnf info "$pkg" 2>/dev/null | grep "^Version" || echo "Package not found"
      ;;
    pacman)
      pacman -Q "$pkg" 2>/dev/null || echo "Package not found"
      ;;
  esac
}

cmd_depends() {
  local pkg=$1
  if [[ -z "$pkg" ]]; then
    echo -e "${RED}Error: Please specify package name${NC}"
    exit 1
  fi
  
  echo -e "${BLUE}=== Dependencies for '$pkg' ===${NC}"
  
  case $PM in
    apt)
      apt-cache depends --no-suggests --no-recommends "$pkg" 2>/dev/null || true
      ;;
    dnf)
      dnf repoquery --requires "$pkg" 2>/dev/null | head -20 || true
      ;;
    pacman)
      pactree "$pkg" 2>/dev/null || true
      ;;
  esac
}

cmd_rdepends() {
  local pkg=$1
  if [[ -z "$pkg" ]]; then
    echo -e "${RED}Error: Please specify package name${NC}"
    exit 1
  fi
  
  echo -e "${BLUE}=== Reverse Dependencies for '$pkg' ===${NC}"
  
  case $PM in
    apt)
      apt-cache rdepends "$pkg" 2>/dev/null | head -30 || true
      ;;
    dnf)
      dnf repoquery --whatrequires "$pkg" 2>/dev/null | head -20 || true
      ;;
    pacman)
      pactree -r "$pkg" 2>/dev/null | head -30 || true
      ;;
  esac
}

cmd_size() {
  local pkg=$1
  if [[ -z "$pkg" ]]; then
    echo -e "${RED}Error: Please specify package name${NC}"
    exit 1
  fi
  
  echo -e "${BLUE}=== Size Information for '$pkg' ===${NC}"
  
  case $PM in
    apt)
      apt-cache show "$pkg" 2>/dev/null | grep -E "^(Download-Size|Installed-Size):" || true
      ;;
    dnf)
      dnf info "$pkg" 2>/dev/null | grep -E "^(Download Size|Installed Size):" || true
      ;;
    pacman)
      pacman -Si "$pkg" 2>/dev/null | grep -E "^(Compressed|Installed)" || true
      ;;
  esac
}

cmd_provides() {
  local cmd=$1
  if [[ -z "$cmd" ]]; then
    echo -e "${RED}Error: Please specify command name${NC}"
    exit 1
  fi
  
  echo -e "${BLUE}=== Packages providing '$cmd' ===${NC}"
  
  case $PM in
    apt)
      apt-file search "/usr/bin/$cmd" 2>/dev/null | head -10 || echo "Install apt-file or provide full path"
      ;;
    dnf)
      dnf provides "/usr/bin/$cmd" 2>/dev/null || true
      ;;
    pacman)
      pacman -F "/usr/bin/$cmd" 2>/dev/null || true
      ;;
  esac
}

cmd_config() {
  local pkg=$1
  if [[ -z "$pkg" ]]; then
    echo -e "${RED}Error: Please specify package name${NC}"
    exit 1
  fi
  
  echo -e "${BLUE}=== Configuration files for '$pkg' ===${NC}"
  
  case $PM in
    apt)
      dpkg -L "$pkg" 2>/dev/null | grep '/etc/' || echo "No config files found"
      ;;
    dnf)
      rpm -q -c "$pkg" 2>/dev/null || echo "No config files found"
      ;;
    pacman)
      pacman -Ql "$pkg" 2>/dev/null | grep '/etc/' || echo "No config files found"
      ;;
  esac
}

cmd_upgradable() {
  echo -e "${BLUE}=== Available Updates (NOT installing) ===${NC}"
  
  case $PM in
    apt)
      apt list --upgradable 2>/dev/null
      echo ""
      echo -e "${YELLOW}Run 'upgrade' command to install updates (WITH CONFIRMATION)${NC}"
      ;;
    dnf)
      dnf check-upgrade 2>/dev/null || true
      echo ""
      echo -e "${YELLOW}Run 'upgrade' command to install updates (WITH CONFIRMATION)${NC}"
      ;;
    pacman)
      pacman -Qu 2>/dev/null || echo "No upgrades available"
      echo ""
      echo -e "${YELLOW}Run 'upgrade' command to install updates (WITH CONFIRMATION)${NC}"
      ;;
  esac
}

cmd_inventory() {
  echo -e "${BLUE}╔════════════════════════════════════════╗${NC}"
  echo -e "${BLUE}║    SYSTEM PACKAGE INVENTORY REPORT      ║${NC}"
  echo -e "${BLUE}╚════════════════════════════════════════╝${NC}"
  echo ""
  
  echo -e "${YELLOW}Package Manager:${NC}"
  echo "  $PM"
  echo ""
  
  echo -e "${YELLOW}Total Installed Packages:${NC}"
  case $PM in
    apt)
      echo "  $(apt list --installed 2>/dev/null | wc -l)"
      ;;
    dnf)
      echo "  $(dnf list installed 2>/dev/null | wc -l)"
      ;;
    pacman)
      echo "  $(pacman -Q | wc -l)"
      ;;
  esac
  echo ""
  
  echo -e "${YELLOW}Available Updates:${NC}"
  case $PM in
    apt)
      count=$(apt list --upgradable 2>/dev/null | wc -l)
      echo "  $((count - 1)) packages can be upgraded"
      ;;
    dnf)
      count=$(dnf check-upgrade 2>/dev/null | wc -l)
      echo "  $((count - 1)) packages can be upgraded"
      ;;
    pacman)
      pacman -Qu 2>/dev/null | wc -l
      ;;
  esac
  echo ""
  
  echo -e "${YELLOW}Largest Installed Packages:${NC}"
  case $PM in
    apt)
      dpkg-query -W -f='${Installed-Size}\t${Package}\n' 2>/dev/null | sort -rn | head -5 | awk '{print "  " $2 " (" int($1/1024) " MB)"}'
      ;;
    dnf)
      dnf repoquery --installed --qf "%{size} %{name}" 2>/dev/null | sort -rn | head -5 | awk '{print "  " $2 " (" int($1/1024/1024) " MB)"}'
      ;;
    pacman)
      pacman -Q --info 2>/dev/null | grep -E "^(Name|Installed Size)" | paste - - | head -5
      ;;
  esac
  echo ""
}

cmd_pm_info() {
  echo -e "${BLUE}=== Package Manager Information ===${NC}"
  echo ""
  echo -e "${YELLOW}Detected Package Manager:${NC}"
  echo "  $PM"
  echo ""
  
  case $PM in
    apt)
      echo -e "${YELLOW}Version:${NC}"
      apt --version | head -1
      echo ""
      echo -e "${YELLOW}Repositories:${NC}"
      grep -h "^deb " /etc/apt/sources.list /etc/apt/sources.list.d/*.list 2>/dev/null | head -3 || true
      ;;
    dnf)
      echo -e "${YELLOW}Version:${NC}"
      dnf --version | head -1
      echo ""
      echo -e "${YELLOW}Enabled Repositories:${NC}"
      dnf repolist | head -10 || true
      ;;
    pacman)
      echo -e "${YELLOW}Version:${NC}"
      pacman --version
      echo ""
      echo -e "${YELLOW}Sync Databases:${NC}"
      grep "^\[" /etc/pacman.conf | head -5
      ;;
  esac
  echo ""
}

# Destructive commands (with confirmation)
cmd_install() {
  local pkg=$1
  if [[ -z "$pkg" ]]; then
    echo -e "${RED}Error: Please specify package name${NC}"
    exit 1
  fi
  
  show_install_preview "$pkg"
  
  if ! confirm "Install package '$pkg' with all dependencies?"; then
    exit 1
  fi
  
  echo -e "${GREEN}→ Installing $pkg...${NC}"
  
  case $PM in
    apt)
      sudo apt install -y "$pkg"
      ;;
    dnf)
      sudo dnf install -y "$pkg"
      ;;
    pacman)
      sudo pacman -S "$pkg"
      ;;
    brew)
      brew install "$pkg"
      ;;
  esac
  
  echo -e "${GREEN}✓ Installation complete${NC}"
}

cmd_remove() {
  local pkg=$1
  if [[ -z "$pkg" ]]; then
    echo -e "${RED}Error: Please specify package name${NC}"
    exit 1
  fi
  
  show_remove_preview "$pkg"
  
  if ! confirm "Remove package '$pkg'? This action may break other packages."; then
    exit 1
  fi
  
  echo -e "${GREEN}→ Removing $pkg...${NC}"
  
  case $PM in
    apt)
      sudo apt remove -y "$pkg"
      ;;
    dnf)
      sudo dnf remove -y "$pkg"
      ;;
    pacman)
      sudo pacman -R "$pkg"
      ;;
    brew)
      brew uninstall "$pkg"
      ;;
  esac
  
  echo -e "${GREEN}✓ Removal complete${NC}"
}

cmd_upgrade() {
  echo -e "${BLUE}=== System Upgrade Review ===${NC}"
  echo ""
  
  case $PM in
    apt)
      echo -e "${YELLOW}Updates available:${NC}"
      apt list --upgradable 2>/dev/null | tail -n +2 | head -20
      ;;
    dnf)
      echo -e "${YELLOW}Updates available:${NC}"
      dnf check-upgrade 2>/dev/null | head -20
      ;;
    pacman)
      echo -e "${YELLOW}Updates available:${NC}"
      pacman -Qu | head -20
      ;;
  esac
  
  if ! confirm "Upgrade all packages?"; then
    exit 1
  fi
  
  echo -e "${GREEN}→ Upgrading system...${NC}"
  
  case $PM in
    apt)
      sudo apt update && sudo apt upgrade -y
      ;;
    dnf)
      sudo dnf upgrade -y
      ;;
    pacman)
      sudo pacman -Syu
      ;;
  esac
  
  echo -e "${GREEN}✓ Upgrade complete${NC}"
}

cmd_clean() {
  echo -e "${BLUE}=== Cache Cleanup Review ===${NC}"
  echo ""
  
  case $PM in
    apt)
      echo -e "${YELLOW}Current cache size:${NC}"
      du -sh /var/cache/apt/ || true
      echo ""
      echo -e "${YELLOW}Will remove:${NC}"
      echo "  - Partial package files in /var/cache/apt/archives/partial/"
      echo "  - Old downloaded packages"
      ;;
    dnf)
      echo -e "${YELLOW}Will remove cached packages from dnf cache${NC}"
      ;;
    pacman)
      echo -e "${YELLOW}Will remove pacman package cache${NC}"
      ;;
  esac
  
  if ! confirm "Clean package cache?"; then
    exit 1
  fi
  
  echo -e "${GREEN}→ Cleaning cache...${NC}"
  
  case $PM in
    apt)
      sudo apt clean
      ;;
    dnf)
      sudo dnf clean all
      ;;
    pacman)
      sudo pacman -Sc
      ;;
  esac
  
  echo -e "${GREEN}✓ Cache cleanup complete${NC}"
}

# Main
detect_pm

if [[ $# -eq 0 ]]; then
  usage
fi

case "$1" in
  # Read-only commands
  search)
    cmd_search "$2"
    ;;
  info)
    cmd_info "$2"
    ;;
  installed)
    cmd_installed
    ;;
  check)
    cmd_check "$2"
    ;;
  version)
    cmd_version "$2"
    ;;
  depends)
    cmd_depends "$2"
    ;;
  rdepends)
    cmd_rdepends "$2"
    ;;
  size)
    cmd_size "$2"
    ;;
  provides)
    cmd_provides "$2"
    ;;
  config)
    cmd_config "$2"
    ;;
  upgradable)
    cmd_upgradable
    ;;
  inventory)
    cmd_inventory
    ;;
  pm-info)
    cmd_pm_info
    ;;
  # Destructive commands (require confirmation)
  install)
    cmd_install "$2"
    ;;
  remove)
    cmd_remove "$2"
    ;;
  upgrade)
    cmd_upgrade
    ;;
  update)
    cmd_install "$2"
    ;;
  clean)
    cmd_clean
    ;;
  help|-h|--help)
    usage
    ;;
  *)
    echo -e "${RED}Unknown command: $1${NC}"
    usage
    ;;
esac
