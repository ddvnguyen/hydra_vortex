#!/usr/bin/env python3
"""Project file scanner for understand-anything - Python fallback (no Node.js)."""
import sys, os, re, subprocess, fnmatch, json, argparse
from pathlib import Path

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('project_root')
    parser.add_argument('output_file')
    args = parser.parse_args()
    
    project_root = Path(args.project_root)
    output_file = Path(args.output_file)
    
    # Step 1: Discover files via git ls-files
    try:
        result = subprocess.run(
            ['git', 'ls-files'], cwd=project_root, capture_output=True, text=True, timeout=30
        )
        if result.returncode != 0:
            print(f"Error running git ls-files: {result.stderr}", file=sys.stderr)
            sys.exit(1)
    except Exception as e:
        print(f"Git error: {e}", file=sys.stderr)
        sys.exit(1)
    
    all_files = [f for f in result.stdout.strip().split('\n') if f]
    
    # Step 2: Load .understandignore patterns
    ignore_patterns = []
    for ign_path in ['.understand-anything/.understandignore', '.understandignore']:
        full_path = project_root / ign_path
        if full_path.exists():
            with open(full_path) as f:
                for line in f:
                    line = line.strip()
                    if line and not line.startswith('#'):
                        ignore_patterns.append(line)
    
    # Hardcoded exclusion patterns
    hardcoded_excludes = [
        '*/node_modules/*', '*/.git/*', '*/vendor/*', '*/venv/*', '*/.venv/*',
        '*/__pycache__/*', '*/dist/*', '*/build/*', '*/out/*', '*/coverage/*',
        '*/.next/*', '*/.cache/*', '*/.turbo/*', '*/target/*', '*/obj/*',
        '*.lock', 'package-lock.json', 'yarn.lock', 'pnpm-lock.yaml',
        '*.png', '*.jpg', '*.jpeg', '*.gif', '*.svg', '*.ico',
        '*.woff', '*.woff2', '*.ttf', '*.eot', '*.mp3', '*.mp4',
        '*.pdf', '*.zip', '*.tar', '*.gz',
        '*.min.js', '*.min.css', '*.map', '*.generated.*',
        '*/.idea/*', '*/.vscode/*', '*/.vs/*',
        'LICENSE', '*.log',
    ]
    
    def should_exclude(file_path, patterns):
        """Check if a file path matches any exclusion pattern."""
        for p in patterns:
            # Convert glob to fnmatch pattern
            p = p.replace('/', os.sep)
            # Check both the full path and individual components
            if fnmatch.fnmatch(file_path, p):
                return True
            # Check relative path against pattern
            rel = str(Path(file_path).relative_to(project_root)) if file_path.startswith(str(project_root)) else file_path
            if fnmatch.fnmatch(rel, p):
                return True
        return False
    
    # Filter files
    filtered_files = [f for f in all_files if not should_exclude(f, hardcoded_excludes + ignore_patterns)]
    
    # Count files excluded by user patterns
    excluded_by_user = len(all_files) - len([f for f in all_files if not should_exclude(f, hardcoded_excludes)])
    filtered_by_ignore = sum(1 for f in all_files 
                           if not should_exclude(f, hardcoded_excludes) and should_exclude(f, ignore_patterns))
    
    # Step 3: Language detection
    ext_to_lang = {
        '.ts': 'typescript', '.tsx': 'typescript', '.js': 'javascript', '.jsx': 'javascript',
        '.py': 'python', '.go': 'go', '.rs': 'rust', '.java': 'java', '.rb': 'ruby',
        '.cpp': 'cpp', '.cc': 'cpp', '.cxx': 'cpp', '.h': 'c', '.hpp': 'cpp',
        '.cs': 'csharp', '.swift': 'swift', '.kt': 'kotlin', '.php': 'php',
        '.vue': 'vue', '.svelte': 'svelte', '.sh': 'shell', '.bash': 'shell',
        '.ps1': 'powershell', '.bat': 'batch', '.cmd': 'batch',
        '.md': 'markdown', '.rst': 'rst', '.yaml': 'yaml', '.yml': 'yaml',
        '.json': 'json', '.jsonc': 'jsonc', '.toml': 'toml', '.sql': 'sql',
        '.graphql': 'graphql', '.gql': 'graphql', '.proto': 'protobuf',
        '.tf': 'terraform', '.tfvars': 'terraform', '.html': 'html', '.htm': 'html',
        '.css': 'css', '.scss': 'scss', '.sass': 'sass', '.less': 'less',
        '.xml': 'xml', '.cfg': 'config', '.ini': 'config', '.env': 'config',
    }
    
    def detect_language(file_path):
        ext = Path(file_path).suffix.lower()
        name = os.path.basename(file_path)
        if ext_to_lang.get(ext):
            return ext_to_lang[ext]
        if name == 'Dockerfile':
            return 'dockerfile'
        if name.startswith('Makefile'):
            return 'makefile'
        if name == 'Jenkinsfile':
            return 'jenkinsfile'
        return ext.lstrip('.') if ext else 'unknown'
    
    # Step 4: File category detection
    def detect_category(file_path):
        name = os.path.basename(file_path)
        dir_part = file_path.replace(os.sep, '/').rsplit('/', 1)[0] if '/' in file_path else ''
        
        # Infra
        if (file_path == 'Dockerfile' or 
            name.startswith('docker-compose') or 
            name.endswith('.tf') or
            '.github/workflows/' in dir_part or
            name == '.gitlab-ci.yml' or
            'Jenkinsfile' in file_path or
            name.startswith('Makefile') or
            name.endswith('.k8s.yaml') or
            name.endswith('.k8s.yml')):
            return 'infra'
        
        # Data
        if (name.endswith('.sql') or name.endswith('.graphql') or name.endswith('.gql') or
            name.endswith('.proto') or name.endswith('.prisma') or
            name.endswith('.schema.json') or name.endswith('.csv')):
            return 'data'
        
        # Script
        if (name.endswith('.sh') or name.endswith('.bash') or name.endswith('.ps1') or
            name.endswith('.bat')):
            return 'script'
        
        # Markup
        if (name.endswith('.html') or name.endswith('.htm') or name.endswith('.css') or
            name.endswith('.scss') or name.endswith('.sass') or name.endswith('.less')):
            return 'markup'
        
        # Docs
        if (name.endswith('.md') or name.endswith('.rst') or name.endswith('.txt')):
            return 'docs'
        
        # Config - specific files first
        config_names = {'pyproject.toml', 'package.json', 'Cargo.toml', 'go.mod',
                       'requirements.txt', 'setup.py', 'setup.cfg', 'Pipfile', 'Gemfile'}
        if name in config_names or file_path.startswith('Directory.Build.props'):
            return 'config'
        
        # Config - extension-based
        if (name.endswith('.yaml') or name.endswith('.yml') or name.endswith('.json') or
            name.endswith('.jsonc') or name.endswith('.toml') or name.endswith('.xml') or
            name.endswith('.cfg') or name.endswith('.ini') or name.endswith('.env') or
            name == 'tsconfig.json'):
            return 'config'
        
        return 'code'
    
    # Step 5: Count lines
    def count_lines(file_path):
        full_path = project_root / file_path
        try:
            with open(full_path, encoding='utf-8', errors='replace') as f:
                return sum(1 for _ in f)
        except Exception:
            return 0
    
    # Step 6: Framework detection
    frameworks = []
    
    # Python frameworks from pyproject.toml
    pyproject = project_root / 'pyproject.toml'
    if pyproject.exists():
        with open(pyproject) as f:
            content = f.read().lower()
        py_frameworks = ['fastapi', 'uvicorn', 'pydantic', 'structlog', 'aiofiles',
                        'prometheus-client', 'langfuse', 'aiosqlite']
        for fw in py_frameworks:
            if fw in content:
                frameworks.append(fw)
    
    # .NET frameworks from csproj files
    dotnet_frameworks = ['system.io.pipelines', 'protobuf-net']
    csproj_files = list(project_root.rglob('*.csproj'))
    for csproj in csproj_files:
        try:
            content = csproj.read_text().lower()
            for fw in dotnet_frameworks:
                if fw in content and fw not in frameworks:
                    frameworks.append(fw)
        except Exception:
            pass
    
    # Infrastructure tooling
    if (project_root / 'Dockerfile').exists():
        frameworks.append('Docker')
    if list(project_root.glob('docker-compose*')):
        frameworks.append('Docker Compose')
    if (project_root / '.github' / 'workflows').exists():
        frameworks.append('GitHub Actions')
    
    # Step 7: Complexity estimation
    total = len(filtered_files)
    complexity = 'small'
    if total > 150: complexity = 'very-large'
    elif total > 50: complexity = 'large'
    elif total > 20: complexity = 'moderate'
    
    # Step 8: Project name
    version_file = project_root / 'VERSION'
    name = 'hydra-vortex'
    if version_file.exists():
        try:
            name += '-' + version_file.read_text().strip()
        except Exception:
            pass
    
    # Step 9: Import resolution for Python files only (C# needs .NET tooling)
    def resolve_python_imports(file_content, file_path):
        imports = []
        
        # Relative imports: from .x import y, from ..x import y, import x.y.z
        # Simple pattern matching - look for 'from' and 'import' statements
        for match in re.finditer(r'from\s+(\.{1,3}\w+)', file_content):
            dots = len(match.group(1)) - 2  # . is depth 0, .. is depth 1
            parts = file_path.replace(os.sep, '/').split('/')
            base_parts = parts[:-1]  # Remove filename
            if dots > 0:
                base_parts = base_parts[:-(dots)]  # Go up directories
            import_name = match.group(1)[dots:]  # Get the actual module name after dots
            
            # Check as package or module
            rel_pkg = '/'.join(base_parts) + '/' + import_name + '/__init__.py' if import_name else ''
            rel_mod = '/'.join(base_parts) + '/' + import_name + '.py' if import_name else ''
            
            if rel_pkg in filtered_files:
                imports.append(rel_pkg)
            elif rel_mod in filtered_files:
                imports.append(rel_mod)
        
        for match in re.finditer(r'(?:^|\n)\s*import\s+([\w.]+)', file_content):
            module_path = match.group(1).replace('.', '/') + '.py'
            if module_path in filtered_files:
                imports.append(module_path)
        
        return list(set(imports))
    
    import_map = {}
    for fp in filtered_files:
        ext = Path(fp).suffix.lower()
        if ext == '.py':
            try:
                full_path = project_root / fp
                content = full_path.read_text(encoding='utf-8', errors='replace')
                resolved = resolve_python_imports(content, fp)
                import_map[fp] = resolved
            except Exception:
                import_map[fp] = []
        else:
            import_map[fp] = []
    
    # Assemble result
    files = sorted([
        {
            'path': fp,
            'language': detect_language(fp),
            'sizeLines': count_lines(fp),
            'fileCategory': detect_category(fp)
        }
        for fp in filtered_files
    ], key=lambda x: x['path'])
    
    languages = sorted(set(f['language'] for f in files if f['language'] != 'unknown'))
    
    result = {
        'name': name,
        'description': f"High-throughput multi-GPU LLM inference system with KV state management. Routes requests across RTX 5060 Ti and Tesla P100 GPUs, migrating ~800 MB KV cache between GPUs without re-prefill.",
        'languages': languages,
        'frameworks': sorted(set(frameworks)),
        'files': files,
        'totalFiles': total,
        'filteredByIgnore': filtered_by_ignore,
        'estimatedComplexity': complexity,
        'importMap': import_map
    }
    
    # Write output
    output_file.parent.mkdir(parents=True, exist_ok=True)
    with open(output_file, 'w') as f:
        json.dump(result, f, indent=2)
    
    print(f"Scan complete: {total} files analyzed ({filtered_by_ignore} excluded by user patterns)")

if __name__ == '__main__':
    main()
