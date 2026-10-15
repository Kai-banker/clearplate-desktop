#!/usr/bin/env python3
"""Package the published Windows executable and notices, never local state."""
import pathlib
import json
import shutil
import sys
import xml.etree.ElementTree as ET
import zipfile

root = pathlib.Path(__file__).resolve().parents[1]
version = ET.parse(root/'src/ClearPlate.Windows/ClearPlate.Windows.csproj').findtext('.//Version')
target = pathlib.Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else root/f'artifacts/ClearPlate-Windows-x64-v{version}.zip'
folder = root/'artifacts/windows-package'
folder.mkdir(parents=True, exist_ok=True)
files = [(root/'artifacts/win-x64/ClearPlate.exe', 'ClearPlate.exe'), (root/'README.md', 'README.md'), (root/'LICENSE', 'LICENSE.txt')]
assets = json.loads((root/'src/ClearPlate.Windows/obj/project.assets.json').read_text())
for framework in assets['project']['frameworks'].values():
    for dep in framework.get('downloadDependencies', []):
        if dep['name'] not in ('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64'):
            continue
        runtime_version = dep['version'].split(',')[0].lstrip('[').strip()
        package = next(pathlib.Path(base)/dep['name'].lower()/runtime_version for base in assets['packageFolders']
                       if (pathlib.Path(base)/dep['name'].lower()/runtime_version).is_dir())
        prefix = 'DOTNET' if dep['name'].startswith('Microsoft.NETCore') else 'WINDOWS-DESKTOP'
        for item in package.iterdir():
            name = item.name.upper()
            if item.is_file() and (name.startswith('LICENSE') or name.startswith('THIRD-PARTY-NOTICES')):
                files.append((item, prefix + '-' + name.removesuffix('.TXT') + '.txt'))
target.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(target, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for source, name in files:
        shutil.copy2(source, folder/name)
        archive.write(source, name)
print(f'Packaged Windows x64 v{version}: {target.name}, {target.stat().st_size} bytes; executable, README and licenses only.')
