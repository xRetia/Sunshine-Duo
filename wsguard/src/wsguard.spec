# -*- mode: python ; coding: utf-8 -*-
# spec: collect all runtime deps; Cryptodome shipped verbatim via datas
# NOTE: the service uses raw-ctypes SCM plumbing - NO pywin32 anywhere, so
# there is nothing win32-ish to collect and builds stay environment-proof.
from PyInstaller.utils.hooks import collect_all

datas = []
binaries = []
hiddenimports = []

for pkg in ['aardwolf', 'asysocks', 'asyauth', 'unicomm', 'unicrypto',
            'minikerberos', 'cryptography', 'asn1crypto', 'oscrypto',
            'unicrypto.backends.pycryptodomex', 'unicrypto.backends.cryptography']:
    try:
        d, b, h = collect_all(pkg)
        datas += d
        binaries += b
        hiddenimports += h
    except Exception:
        pass

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
