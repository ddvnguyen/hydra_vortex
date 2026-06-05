const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const PROJECT_ROOT = process.argv[2];
const OUTPUT_FILE = process.argv[3];

if (!PROJECT_ROOT || !OUTPUT_FILE) {
  console.error('Usage: node ua-scan.js <project-root> <output-file>');
  process.exit(1);
}

// Exclusion patterns (hardcoded defaults from project-scanner.md)
const EXCLUDE_DIRS = [
  'node_modules/', '.git/', 'vendor/', 'venv/', '.venv/', '__pycache__/',
  'dist/', 'build/', 'out/', 'coverage/', '.next/', '.cache/', '.turbo/',
  'target/', 'obj/', 'bin/', 'lib/'
];
const EXCLUDE_EXTS = [
  '.png', '.jpg', '.jpeg', '.gif', '.svg', '.ico', '.woff', '.woff2', '.ttf', '.eot',
  '.mp3', '.mp4', '.pdf', '.zip', '.tar', '.gz'
];
const EXCLUDE_LOCK = ['*.lock', 'package-lock.json', 'yarn.lock', 'pnpm-lock.yaml'];
const EXCLUDE_GEN = ['*.min.js', '*.min.css', '*.map', '*.generated.*'];
const EXCLUDE_MIS = [
  '.idea/', '.vscode/', 'LICENSE', '.gitignore', '.editorconfig', '.prettierrc',
  '.eslintrc*', '*.log'
];

// Language mapping from extensions (project-scanner.md Step 3)
const EXT_LANG_MAP = {
  // TypeScript/JS
  '.ts': 'typescript', '.tsx': 'typescript', '.js': 'javascript', '.jsx': 'javascript',
  // Python
  '.py': 'python', '.pyi': 'python',
  // Go
  '.go': 'go',
  // Rust
  '.rs': 'rust',
  // Java
  '.java