# -*- mode: python ; coding: utf-8 -*-
# spec: collect all runtime deps; Cryptodome shipped verbatim via datas
# NOTE: the service uses raw-ctypes SCM plumbing - NO pywin32 anywhere, so
# there is nothing win32-ish to collect and builds stay environment-proof.
# rdp-lib is inserted explicitly: some python launchers ignore PYTHONPATH,
# and without it on the path collect_all() fails (silently skipping the
# runtime deps), leaving a bundle whose keepalive cannot import aardwolf.
import os as _os
import sys as _sys
_sys.path.insert(0, _os.path.abspath('rdp-lib'))
from PyInstaller.utils.hooks import collect_all

datas = []
binaries = []
hiddenimports = []

for pkg in ['aardwolf', 'asysocks', 'asyauth', 'unicomm', 'unicrypto',
            'minikerberos', 'cryptography', 'asn1crypto', 'oscrypto',
            'unicrypto.backends.pycryptodomex', 'unicrypto.backends.cryptography',
            'PIL']:
    try:
        d, b, h = collect_all(pkg)
        datas += d
        binaries += b
        hiddenimports += h
    except Exception:
        pass

# PIL's C extensions are NOT collected by collect_all/collect_dynamic_libs
# (no package hooks are loaded: see hookspath=[] below); its .pyd files ship
# inside the PIL package dir, so name them explicitly for the build graph.
hiddenimports += ['PIL._imaging', 'PIL._imagingcms', 'PIL._imagingft',
                  'PIL._imagingmath', 'PIL._avif']

# ship Cryptodome verbatim (PyInstaller hook mis-collects .py files)
import os
_extra = os.path.abspath('rdp-lib/Cryptodome')
datas.append((_extra, 'rdp-extra/Cryptodome'))

# runtime hook adds rdp-extra to sys.path so verbatim Cryptodome wins
_rth = os.path.abspath('hook-runtime-cryptodome.py')
assert os.path.exists(_rth), 'runtime hook missing'

a = Analysis(
    ['wsguard_final.py'],
    pathex=[],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports + ['unicrypto.backends.pycryptodomex', 'unicrypto.backends.cryptography'],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[_rth],
    excludes=['tkinter', 'matplotlib', 'numpy', 'pandas', 'Cryptodome', 'pycryptodomex'],
    noarchive=False,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='wsguard_final',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=True,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    name='wsguard_final',
)
