#!/usr/bin/env python3
"""Project scanner for Hydra codebase — Phase 1 discovery script."""

import json
import os
import subprocess
import sys
from pathlib import Path

PROJECT_ROOT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.cwd()
OUTPUT_FILE = PROJECT_ROOT / ".understand-anything" / "intermediate" / "scan-result.json"

# Exclusion patterns from project-scanner.md
EXCLUDE_DIRS = {
    'node_modules/', '.git/', 'vendor/', 'venv/', '.venv/', '__pycache__/',
    'dist/', 'build/', 'out/', 'coverage/', '.next/', '.cache/', '.turbo/',
    'target/', 'obj/', 'bin/', 'lib/'
}
EXCLUDE_EXTS = {
    '.png', '.jpg', '.jpeg', '.gif', '.svg', '.ico', '.woff', '.woff2', '.ttf', '.eot',
    '.mp3', '.mp4', '.pdf', '.zip', '.tar', '.gz'
}
EXCLUDE_LOCK = {'*.lock', 'package-lock.json', 'yarn.lock', 'pnpm-lock.yaml'}
EXCLUDE_GEN = {'*.min.js', '*.min.css', '*.map', '*.generated.*'}
EXCLUDE_MIS = {
    '.idea/', '.vscode/', 'LICENSE', '.gitignore', '.editorconfig', '.prettierrc',
    '.eslintrc*', '*.log'
}

# Language mapping (project-scanner.md Step 3)
EXT_TO_LANG = {
    # TypeScript/JS
    '.ts': 'typescript', '.tsx': 'typescript', '.js': 'javascript', '.jsx': 'javascript',
    '.mjs': 'javascript', '.cjs': 'javascript',
    # Python
    '.py': 'python', '.pyi': 'python', '.pyx': 'python',
    # Go
    '.