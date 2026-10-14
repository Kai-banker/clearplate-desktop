#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
task_dotnet="${DOTNET:-dotnet}"
task_feed="${NUGET_SOURCE:-https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json}"
mkdir -p artifacts/macOS
for task_arch in arm64 x64; do
    "$task_dotnet" publish src/ClearPlate.MacCapture/ClearPlate.MacCapture.csproj -c Release -r "osx-$task_arch" --self-contained true --source "$task_feed" -o "artifacts/mac-$task_arch-helper"
done
for task_arch in arm64 x86_64; do
    swiftc -O -warnings-as-errors -parse-as-library -target "$task_arch-apple-macos15.0" src/ClearPlate.Mac/ClearPlate.swift src/ClearPlate.Mac/MacCapture.swift src/ClearPlate.Mac/SessionVault.swift -o "artifacts/macOS/ui-$task_arch"
done
task_app='artifacts/macOS/光盘打卡.app'
mkdir -p "$task_app/Contents/MacOS" "$task_app/Contents/Resources"
lipo -create artifacts/macOS/ui-arm64 artifacts/macOS/ui-x86_64 -output "$task_app/Contents/MacOS/ClearPlate"
cp src/ClearPlate.Mac/Info.plist "$task_app/Contents/Info.plist"
cp src/ClearPlate.Mac/plate.png "$task_app/Contents/Resources/光盘.png"
for task_arch in arm64 x64; do
    cp "artifacts/mac-$task_arch-helper/ClearPlate.MacCapture" "$task_app/Contents/Resources/ClearPlate.MacCapture-$task_arch"
    codesign --force --sign - "$task_app/Contents/Resources/ClearPlate.MacCapture-$task_arch"
done
codesign --force --sign - "$task_app"
codesign --verify --deep --strict "$task_app"
"$task_app/Contents/MacOS/ClearPlate" --check-default-photo
cp docs/macOS.md artifacts/macOS/使用说明.md
cp LICENSE artifacts/macOS/LICENSE.txt
python3 - <<'PYNOTICES'
import json, pathlib, shutil
assets = json.loads(pathlib.Path('src/ClearPlate.MacCapture/obj/project.assets.json').read_text())
runtime = next(value for value in assets['project']['frameworks']['net10.0']['downloadDependencies'] if value['name'] == 'Microsoft.NETCore.App.Runtime.osx-x64')
version = runtime['version'].split(',')[0].lstrip('[').strip()
package = runtime['name'].lower() + '/' + version
folder = next(pathlib.Path(item)/package for item in assets['packageFolders'] if (pathlib.Path(item)/package).is_dir())
for original, target in [('LICENSE.TXT','DOTNET-LICENSE.txt'),('THIRD-PARTY-NOTICES.TXT','DOTNET-THIRD-PARTY-NOTICES.txt')]:
    shutil.copy2(folder/original, pathlib.Path('artifacts/macOS')/target)
PYNOTICES
printf '%s\n' 'Built artifacts/macOS/光盘打卡.app (Apple Silicon + Intel, macOS 15+).'
