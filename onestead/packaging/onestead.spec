# -*- mode: python ; coding: utf-8 -*-
from pathlib import Path
import sys
from PyInstaller.utils.hooks import collect_data_files, collect_dynamic_libs, collect_submodules, copy_metadata

root = Path(SPECPATH).parent
sys.path.insert(0, str(root / 'src'))
datas = collect_data_files('onestead', include_py_files=False)
binaries = collect_dynamic_libs('onestead')
hidden = collect_submodules('onestead')
datas += copy_metadata('mcp')
hidden += collect_submodules('mcp.server')
a = Analysis([str(root / 'packaging' / 'entry.py')], pathex=[str(root / 'src')],
             binaries=binaries, datas=datas, hiddenimports=hidden,
             excludes=['tkinter', 'pytest', 'pip'], noarchive=False)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name='onestead',
          console=True, strip=False, upx=False)
coll = COLLECT(exe, a.binaries, a.datas, strip=False, upx=False, name='onestead')
