#!/usr/bin/env python3
"""Render the existing Entire SVG into light/dark macOS icon families.
Requires librsvg's rsvg-convert and Apple's iconutil. Generated assets are
committed, so ordinary app builds do not require librsvg.
"""
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'apps/entire-connector-macos/Resources/entire-symbol-dark-icon.svg'
OUTPUT = ROOT / 'apps/simulator-macos/Resources/Icons'
paths = '\n'.join(f'<path d="{p.attrib["d"]}"/>' for p in ET.parse(SOURCE).getroot())
OUTPUT.mkdir(parents=True, exist_ok=True)
# Apple HIG: use a 1024px master, center primary content, and preserve the
# silhouette across appearances. The 22x symbol scale is an optical choice,
# not an Apple-mandated percentage. Keep the existing legacy ICNS tile inset.
# https://developer.apple.com/design/human-interface-guidelines/app-icons
for name, background, end, foreground, edge in [
    ('Light', '#f8f7f2', '#e9e8e1', '#202521', '#ffffff'),
    ('Dark', '#303631', '#1b211d', '#f3f3e9', '#596259'),
]:
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 1024 1024">
  <defs>
    <linearGradient id="tile" x2="0" y2="1"><stop stop-color="{background}"/><stop offset="1" stop-color="{end}"/></linearGradient>
  </defs>
  <rect x="64" y="64" width="896" height="896" rx="196" fill="url(#tile)"/>
  <rect x="66" y="66" width="892" height="892" rx="194" fill="none" stroke="{edge}" stroke-opacity=".65" stroke-width="3"/>
  <g transform="translate(171 160) scale(22)" fill="{foreground}">{paths}</g>
</svg>'''
    source = OUTPUT / f'AppIcon{name}.svg'
    source.write_text(svg + '\n')
    subprocess.run(['rsvg-convert', '-w', '1024', '-h', '1024', '-o', str(OUTPUT / f'AppIcon{name}.png'), str(source)], check=True)
    with tempfile.TemporaryDirectory(prefix='marvin-icon-') as temp:
        iconset = Path(temp) / f'AppIcon{name}.iconset'
        iconset.mkdir()
        for size in [16, 32, 128, 256, 512]:
            for scale in [1, 2]:
                filename = f'icon_{size}x{size}' + ('@2x' if scale == 2 else '') + '.png'
                subprocess.run(['rsvg-convert', '-w', str(size*scale), '-h', str(size*scale),
                                '-o', str(iconset / filename), str(source)], check=True)
        subprocess.run(['iconutil', '-c', 'icns', '-o', str(OUTPUT / f'AppIcon{name}.icns'), str(iconset)], check=True)
    print(f'Created AppIcon{name}: SVG, 1024px PNG, and 16–1024px ICNS')
