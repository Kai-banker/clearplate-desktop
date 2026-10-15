#!/usr/bin/env python3
"""Create a source deployment bundle from an explicit non-secret allowlist."""
import hashlib
import io
import json
import pathlib
import subprocess
import sys
import tarfile
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parents[1]
version = ET.parse(root/'src/ClearPlate.Server/ClearPlate.Server.csproj').findtext('.//Version')
target = pathlib.Path(sys.argv[1]).resolve() if len(sys.argv)>1 else root/f'artifacts/ClearPlate-Server-Docker-v{version}.tar.gz'
prefix = f'clearplate-server-v{version}'
files = {pathlib.Path(name) for name in ('Directory.Build.props', '.dockerignore', '.gitignore', 'LICENSE', 'docs/server.md',
    'deploy/server/Dockerfile', 'deploy/server/compose.yml', 'deploy/server/init.sh', 'src/ClearPlate.Windows/Assets/plate.png', 'scripts/package-server.py')}
for folder in ('src/ClearPlate.Core', 'src/ClearPlate.Server', 'tests/ClearPlate.Tests', 'tests/ClearPlate.Server.Tests'):
    for path in (root/folder).rglob('*'):
        if (path.is_file() and not any(part in ('bin','obj','.secrets','data') for part in path.relative_to(root).parts)
            and path.suffix in ('.cs','.csproj','.html','.css','.js','.png')):
            files.add(path.relative_to(root))
try:
    commit = subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True,stderr=subprocess.DEVNULL).strip()
except (subprocess.CalledProcessError, FileNotFoundError):
    commit = 'source-deployment-bundle'
target.parent.mkdir(parents=True,exist_ok=True)
def add(archive, name, data, executable=False):
    entry = tarfile.TarInfo(prefix+'/'+name); entry.size=len(data); entry.mode=0o755 if executable else 0o644; entry.mtime=0
    archive.addfile(entry,io.BytesIO(data))
with tarfile.open(target,'w:gz') as archive:
    for relative in sorted(files):
        path=root/relative
        if path.is_symlink(): raise ValueError('Symlinks are not allowed in the deployment bundle')
        add(archive, relative.as_posix(), path.read_bytes(), relative.name=='init.sh')
    add(archive,'README.md',(root/'docs/server.md').read_bytes())
    add(archive,'BUILD-INFO.json',json.dumps(dict(version=version,source_commit=commit,kind='source-docker-deployment'),indent=2).encode())
digest=hashlib.sha256(target.read_bytes()).hexdigest()
target.with_name(target.name+'.sha256').write_text(digest+'  '+target.name+'\n')
print(json.dumps(dict(package=target.name,bytes=target.stat().st_size,source_commit=commit,sha256=digest,files=len(files)+2)))
