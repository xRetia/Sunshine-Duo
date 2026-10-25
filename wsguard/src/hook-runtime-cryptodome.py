# runtime hook: make PyInstaller skip pycryptodomex analysis; we ship the package
# verbatim via datas and import it from the bundled directory at runtime.
import sys

CYPHER_PKGS = ['Cryptodome', 'pycryptodomex']


def _maybe_add(path):
    if path and path not in sys.path:
        sys.path.insert(0, path)


# _MEIPASS root (onefile) or _internal (onedir) both land here
import os
_base = getattr(sys, '_MEIPASS', os.path.dirname(os.path.abspath(sys.executable)))
_patched = os.path.join(_base, 'rdp-extra')
_maybe_add(_patched)
try:
    import Cryptodome.Util.Counter  # noqa: F401
    _ok = True
except Exception:
    _ok = False
