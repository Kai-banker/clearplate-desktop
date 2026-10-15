#!/usr/bin/env python3
"""Pack the original vector icon's PNG renders into a multi-resolution ICO.

Generate the input with create-mac-icon.swift. The resulting ICO is committed so
Windows builds do not depend on macOS, Python, or third-party image libraries.
"""
import pathlib
import struct
import sys

source, destination = map(pathlib.Path, sys.argv[1:])
renders = [(size, (source/name).read_bytes()) for size, name in [
    (16, 'icon_16x16.png'), (32, 'icon_32x32.png'), (64, 'icon_32x32@2x.png'),
    (128, 'icon_128x128.png'), (256, 'icon_256x256.png')]]
header = struct.pack('<HHH', 0, 1, len(renders))
offset = len(header) + 16 * len(renders)
entries, payload = [], []
for size, png in renders:
    assert png[:8] == b'\x89PNG\r\n\x1a\n'
    assert struct.unpack('>II', png[16:24]) == (size, size)
    entries.append(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(png), offset))
    payload.append(png)
    offset += len(png)
destination.write_bytes(header + b''.join(entries) + b''.join(payload))
print('Created Windows ICO with 16, 32, 64, 128 and 256 pixel representations.')
