#!/usr/bin/env python3
import pathlib, subprocess, tempfile
root = pathlib.Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='clearplate-synthetic-') as scratch:
    folder = pathlib.Path(scratch)
    source = (root/'src/ClearPlate.Mac/ClearPlate.swift').read_text().split('struct PlateView: View {')[0]
    start = source.index('    static let session = URL(')
    end = source.index('    static func prepare()', start)
    source = source[:start] + '    static let session = URL(fileURLWithPath: ' + '"' + str(folder/'session.json') + '")\n' + source[end:]
    (folder/'Core.swift').write_text(source)
    subprocess.run(['swiftc','-warnings-as-errors','-D','TESTING','-parse-as-library', str(folder/'Core.swift'), str(root/'src/ClearPlate.Mac/MacCapture.swift'), str(root/'src/ClearPlate.Mac/SessionVault.swift'), str(root/'tests/mac/Checks.swift'), '-o', str(folder/'checks')],check=True)
    subprocess.run([str(folder/'checks'),str(root/'tests/mac')],check=True)
