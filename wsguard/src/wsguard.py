# wsguard - single-file Windows service (pure Python, PyInstaller-packaged)
# Combines:
#   1. SCM service plumbing (raw ctypes, no pywin32 dependency)
#   2. RDP keepalive for the Game user's session via aardwolf (headless RDP
#      protocol client, async thread): session Disconnected -> we connect to
#      127.0.0.2 and keep ACK-ing frames so the display pipeline never freezes;
#      user MSTSC always displaces us (single session), we stand by.
#   3. NetEase GameViewer hook guard: launches the embedded PowerShell guard
#      (inject gv_hook.dll into GameViewerService + self-heal server session).
#   4. Display keepalive (SetThreadExecutionState) while an RDP session is active.
# Configuration: config.json next to the service dir (hot-reloaded every tick).
# Modes: (none) = SCM service | --console = foreground | --setup = configure |
#        --test = one-shot RDP connection test
import asyncio
import base64
import ctypes
import glob
import json
import os
import subprocess
import sys
import threading
import time
from ctypes import wintypes

_advapi32 = ctypes.WinDLL('advapi32', use_last_error=True)
_advapi32.RegisterServiceCtrlHandlerExW.restype = ctypes.c_void_p
_kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
_userenv = ctypes.WinDLL('userenv', use_last_error=True)
_psapi = ctypes.WinDLL('psapi', use_last_error=True)

# directory: where this exe lives (PyInstaller onedir) - fully portable,
# the whole Sunshine tree can be moved without repacking
if getattr(sys, 'frozen', False):
    SVC_DIR = os.path.dirname(os.path.abspath(sys.executable))
else:
    SVC_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(SVC_DIR, 'config.json')
LOG_PATH = os.path.join(SVC_DIR, 'watch.log')
CRED_PATH = os.path.join(SVC_DIR, 'rdp.cred')
# injected at build time (UTF-16LE base64 of inject_watch.ps1);
# the __WG_DIR__ placeholder inside is replaced at runtime with SVC_DIR
PS_GUARD_B64 = '__PS_B64__'

# ----------------------------------------------------------------------------
# logging
# Segmented + levelled: the active file is always watch.log (the setup script
# tails that path); when it grows past log_max_kb it is renamed to
# watch-YYYYMMDD_HHMMSS.log and a fresh watch.log starts with a marker line.
# Only the newest log_keep archives are kept, so disk use is bounded at
# ~(log_keep+1) * log_max_kb. Default level is 'warn': routine per-tick lines
# are suppressed and only warnings/errors are written; set log_level to
# 'info' in config.json for the old verbose behaviour (hot-reloaded every
# tick). The embedded PS guard follows the same policy; Python owns rotation.

_LOG_MAX_KB = 100
_LOG_KEEP = 4
_LOG_LEVELS = {'debug': 10, 'info': 20, 'warn': 30, 'warning': 30, 'error': 40}
_LOG_LEVEL = 30  # default: warnings and errors only

def _emit(level, s):
    if level < _LOG_LEVEL:
        return
    tag = {30: '[WARN] ', 40: '[ERROR] '}.get(level, '')
    line = time.strftime('%Y-%m-%d %H:%M:%S ') + tag + str(s)
    try:
        if os.path.getsize(LOG_PATH) > _LOG_MAX_KB * 1024:
            _log_rotate()
    except Exception:
        pass
    try:
        with open(LOG_PATH, 'a', encoding='utf-8', errors='replace') as f:
            f.write(line + '\n')
    except Exception:
        pass
    try:
        print(line, flush=True)
    except Exception:
        pass

def log(s):       # routine info - hidden unless log_level is info/debug
    _emit(20, s)

def log_warn(s):  # transient problems (retried automatically)
    _emit(30, s)

def log_err(s):   # hard failures / misconfiguration
    _emit(40, s)


def _log_rotate():
    """Rename the full watch.log to a timestamped archive, mark both sides of
    the cut, then delete archives beyond the keep count. Best effort: if the
    file is momentarily locked by the PS guard, rotation is simply retried on
    the next log() call - nothing is lost."""
    arch = os.path.join(SVC_DIR, 'watch-%s.log' % time.strftime('%Y%m%d_%H%M%S'))
    n = 2
    while os.path.exists(arch):  # second-resolution name: add a suffix on collision
        arch = os.path.join(SVC_DIR, 'watch-%s_%d.log' % (time.strftime('%Y%m%d_%H%M%S'), n))
        n += 1
    try:
        os.rename(LOG_PATH, arch)
    except Exception:
        return  # busy/locked: keep appending, rotate on a later call
    try:
        with open(arch, 'a', encoding='utf-8', errors='replace') as f:
            f.write('---- log segment ends here (%s) ----\n'
                    % time.strftime('%Y-%m-%d %H:%M:%S'))
    except Exception:
        pass
    try:
        with open(LOG_PATH, 'a', encoding='utf-8', errors='replace') as f:
            f.write('==== new log segment (previous: %s) ====\n' % os.path.basename(arch))
    except Exception:
        pass
    try:
        archs = sorted(glob.glob(os.path.join(SVC_DIR, 'watch-*.log')))
        for old in archs[:-_LOG_KEEP]:
            try:
                os.remove(old)
            except Exception:
                pass
    except Exception:
        pass


# ----------------------------------------------------------------------------
# config

DEFAULT_CONFIG = {
    'username': 'Game',
    'password_mode': 'dpapi',
    'password_plain': None,
    'cred_path': CRED_PATH,
    'server': '127.0.0.2',
    'width': 1600,
    'height': 720,
    'keepalive': True,
    'guard_uu': True,
    'guard_apollo': True,
    'display_keepalive': True,
    'interval': 60,
    'recycle_minutes': 30,
    # GameViewer version gate (wildcard match, e.g. '4.0.*' = any 4.0.x):
    # the PS guard refuses to inject unless GameViewerService.exe's
    # FileVersion matches one of these patterns. Update after a verified
    # GameViewer upgrade - no repack needed.
    'gameviewer_versions': ['4.0.*'],
    # auto_logon: when NO Game session exists at all (e.g. right after boot),
    # let the RDP keepalive log in to 127.0.0.2 itself, which creates the
    # Game session; sunshine/apollo then launch into it unattended.
    'auto_logon': False,
    # log rotation: watch.log is renamed to watch-YYYYMMDD_HHMMSS.log once it
    # exceeds log_max_kb; the newest log_keep archives are retained.
    'log_max_kb': 100,
    'log_keep': 4,
    # log_level: 'info' = every tick (verbose), 'warn' = warnings and errors
    # only (default), 'error' = errors only. Hot-reloaded every tick.
    'log_level': 'warn',
}

def load_config():
    cfg = dict(DEFAULT_CONFIG)
    try:
        with open(CONFIG_PATH, 'r', encoding='utf-8-sig') as f:
            cfg.update(json.load(f))
    except FileNotFoundError:
        pass
    except Exception as e:
        log_err('config load error: %r' % e)
    return cfg


# ----------------------------------------------------------------------------
# DPAPI credential

class DATA_BLOB(ctypes.Structure):
    _fields_ = [('cbData', wintypes.DWORD), ('pbData', ctypes.POINTER(ctypes.c_char))]

def dpapi_protect(plain):
    raw = plain.encode('utf-8')
    buf = ctypes.create_string_buffer(raw, len(raw))
    bin_ = DATA_BLOB(len(raw), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char)))
    out = DATA_BLOB()
    if not ctypes.windll.crypt32.CryptProtectData(ctypes.byref(bin_), None, None, None, None, 0, ctypes.byref(out)):
        raise OSError('CryptProtectData failed err=%d' % ctypes.GetLastError())
    try:
        return ctypes.string_at(out.pbData, out.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(out.pbData)

def dpapi_unprotect(raw):
    bin_ = DATA_BLOB(len(raw), ctypes.cast(ctypes.create_string_buffer(raw, len(raw)), ctypes.POINTER(ctypes.c_char)))
    out = DATA_BLOB()
    if not ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(bin_), None, None, None, None, 0, ctypes.byref(out)):
        raise OSError('CryptUnprotectData failed err=%d' % ctypes.GetLastError())
    try:
        return ctypes.string_at(out.pbData, out.cbData).decode('utf-8')
    finally:
        ctypes.windll.kernel32.LocalFree(out.pbData)

def read_password(cfg):
    if cfg.get('password_mode') == 'plain':
        return cfg.get('password_plain')
    path = cfg.get('cred_path', CRED_PATH)
    if not os.path.exists(path):
        path = CRED_PATH  # directory moved: fall back to exe-relative default
    with open(path, 'rb') as f:
        raw = f.read()
    try:
        return dpapi_unprotect(raw)
    except Exception:
        return dpapi_unprotect(base64.b64decode(raw.strip()))


# ----------------------------------------------------------------------------
# WTS session enumeration

class WTS_SESSION_INFOW(ctypes.Structure):
    _fields_ = [('SessionId', wintypes.DWORD),
                ('pSessionName', ctypes.c_wchar_p),
                ('State', ctypes.c_int)]

wtsapi32 = ctypes.windll.wtsapi32

def find_user_session(username):
    """Return (sessionId, state) for the given user's remote session, or (-1, -1).
    state: 0=Active 1=Connected 2=ConnectQuery 4=Disconnected"""
    p = ctypes.POINTER(WTS_SESSION_INFOW)()
    n = wintypes.DWORD(0)
    if not wtsapi32.WTSEnumerateSessionsW(None, 0, 1, ctypes.byref(p), ctypes.byref(n)):
        return -1, -1
    found, found_state = -1, -1
    try:
        for i in range(n.value):
            si = p[i]
            if si.SessionId in (0, 1, 65536):
                continue
            if si.State not in (0, 1, 2, 4):
                continue
            buf = ctypes.c_wchar_p()
            length = wintypes.DWORD(0)
            if wtsapi32.WTSQuerySessionInformationW(None, si.SessionId, 5, ctypes.byref(buf), ctypes.byref(length)):
                user = buf.value or ''
                ctypes.windll.wtsapi32.WTSFreeMemory(buf)
                if user.lower() == username.lower():
                    if si.State == 0:
                        return si.SessionId, 0
                    found, found_state = si.SessionId, si.State
    finally:
        wtsapi32.WTSFreeMemory(p)
    return found, found_state


# ----------------------------------------------------------------------------
# display keepalive

ES_CONTINUOUS = 0x80000000
ES_DISPLAY_REQUIRED = 0x00000002
_display_held = False

def display_keep(on):
    global _display_held
    if on == _display_held:
        return
    ctypes.windll.kernel32.SetThreadExecutionState(ES_CONTINUOUS | (ES_DISPLAY_REQUIRED if on else 0))
    _display_held = on


# ----------------------------------------------------------------------------
# UU hook guard (embedded PowerShell, verified-safe channel)

_ps_child = None

def run_ps_guard():
    global _ps_child
    try:
        if _ps_child is not None and _ps_child.poll() is None:
            return  # previous one still running
    except Exception:
        pass
    try:
        ps = base64.b64decode(PS_GUARD_B64).decode('utf-16-le')
        ps = ps.replace('__WG_DIR__', SVC_DIR.replace("'", "''"))
        enc = base64.b64encode(ps.encode('utf-16-le')).decode('ascii')
        si = subprocess.STARTUPINFO()
        si.dwFlags = subprocess.STARTF_USESHOWWINDOW
        si.wShowWindow = 0
        _ps_child = subprocess.Popen(
            ['powershell.exe', '-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden',
             '-ExecutionPolicy', 'Bypass', '-EncodedCommand', enc],
            startupinfo=si, creationflags=subprocess.CREATE_NO_WINDOW)
    except Exception as e:
        log_err('ps guard spawn error: %r' % e)


# ----------------------------------------------------------------------------
# Apollo guard (MultiSeat-style): launch sunshine.exe with the user's ELEVATED
# token inside their session, straight from this SYSTEM service.
# Why elevated: Apollo needs high integrity to inject input into elevated
# windows/games (UIPI would block a medium-integrity process).
# Why from a service and not Task Scheduler/Run key: a scheduler-launched
# Apollo lands inside a no-breakaway Job, and Apollo's own
# CREATE_BREAKAWAY_FROM_JOB spawns (steam:// URLs, detached apps) then fail
# with ERROR_ACCESS_DENIED(5); explorer-launched Apollo is never elevated.
# A SYSTEM service has the TCB privilege for WTSQueryUserToken and creates
# the process in NO Job at all, which satisfies both requirements.

TOKEN_ASSIGN_PRIMARY = 0x0001
TOKEN_DUPLICATE = 0x0002
TOKEN_QUERY = 0x0008
TOKEN_ADJUST_DEFAULT = 0x0080
TOKEN_ADJUST_SESSIONID = 0x0100
TOKEN_TYPE_PRIMARY = 1
SECURITY_IMPERSONATION = 2
TokenSessionId = 12
TokenElevation = 20
TokenLinkedToken = 19
CREATE_UNICODE_ENV_FLAG = 0x0400

_kernel32.OpenProcess.restype = wintypes.HANDLE
_kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
_kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
_kernel32.ProcessIdToSessionId.argtypes = [wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
_kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
_kernel32.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.c_wchar_p, ctypes.POINTER(wintypes.DWORD)]

wtsapi32.WTSQueryUserToken.restype = wintypes.BOOL
wtsapi32.WTSQueryUserToken.argtypes = [wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]

_advapi32.DuplicateTokenEx.restype = wintypes.BOOL
_advapi32.DuplicateTokenEx.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
_advapi32.GetTokenInformation.restype = wintypes.BOOL
_advapi32.GetTokenInformation.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
_advapi32.SetTokenInformation.restype = wintypes.BOOL
_advapi32.SetTokenInformation.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]
_advapi32.CreateProcessAsUserW.restype = wintypes.BOOL
_advapi32.CreateProcessAsUserW.argtypes = [wintypes.HANDLE, wintypes.LPCWSTR, ctypes.c_wchar_p, ctypes.c_void_p, ctypes.c_void_p, wintypes.BOOL, wintypes.DWORD, ctypes.c_void_p, wintypes.LPCWSTR, ctypes.c_void_p, ctypes.c_void_p]

_userenv.CreateEnvironmentBlock.restype = wintypes.BOOL
_userenv.CreateEnvironmentBlock.argtypes = [ctypes.POINTER(ctypes.c_void_p), wintypes.HANDLE, wintypes.BOOL]
_userenv.DestroyEnvironmentBlock.argtypes = [ctypes.c_void_p]


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [('cb', wintypes.DWORD),
                ('lpReserved', wintypes.LPWSTR),
                ('lpDesktop', wintypes.LPWSTR),
                ('lpTitle', wintypes.LPWSTR),
                ('dwX', wintypes.DWORD), ('dwY', wintypes.DWORD),
                ('dwXSize', wintypes.DWORD), ('dwYSize', wintypes.DWORD),
                ('dwXCountChars', wintypes.DWORD), ('dwYCountChars', wintypes.DWORD),
                ('dwFillAttribute', wintypes.DWORD), ('dwFlags', wintypes.DWORD),
                ('wShowWindow', wintypes.WORD), ('cbReserved2', wintypes.WORD),
                ('lpReserved2', ctypes.c_void_p),
                ('hStdInput', wintypes.HANDLE), ('hStdOutput', wintypes.HANDLE), ('hStdError', wintypes.HANDLE)]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [('hProcess', wintypes.HANDLE), ('hThread', wintypes.HANDLE),
                ('dwProcessId', wintypes.DWORD), ('dwThreadId', wintypes.DWORD)]


_apollo_last_launch = 0.0


def apollo_running_in(session_id):
    """True if sunshine.exe is running inside the given session."""
    arr = (wintypes.DWORD * 2048)()
    cb = wintypes.DWORD(0)
    if not _psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(cb)):
        return False
    for i in range(cb.value // ctypes.sizeof(wintypes.DWORD)):
        pid = arr[i]
        if pid == 0:
            continue
        h = _kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            continue
        try:
            sid = wintypes.DWORD(0)
            if not _kernel32.ProcessIdToSessionId(pid, ctypes.byref(sid)) or sid.value != session_id:
                continue
            buf = ctypes.create_unicode_buffer(1024)
            n = wintypes.DWORD(1024)
            if _kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)):
                if buf.value.lower().endswith('\\sunshine.exe'):
                    return True
        finally:
            _kernel32.CloseHandle(h)
    return False


def _elevated_primary_token(raw_token):
    """Duplicate raw_token into a primary token usable by CreateProcessAsUserW;
    swap in the linked ELEVATED token when the user account is an admin whose
    session token was filtered. Returns a HANDLE (int) or None."""
    dup = wintypes.HANDLE()
    need = TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID
    elev = wintypes.DWORD(0)
    rl = wintypes.DWORD(0)
    linked = None
    if _advapi32.GetTokenInformation(raw_token, TokenElevation, ctypes.byref(elev), ctypes.sizeof(elev), ctypes.byref(rl)) and not elev.value:
        linked = wintypes.HANDLE()
        if _advapi32.GetTokenInformation(raw_token, TokenLinkedToken, ctypes.byref(linked), ctypes.sizeof(linked), ctypes.byref(rl)) and linked.value:
            pass
        else:
            linked = None
    source = linked if linked is not None else raw_token
    try:
        if _advapi32.DuplicateTokenEx(source, need, None, SECURITY_IMPERSONATION, TOKEN_TYPE_PRIMARY, ctypes.byref(dup)) and dup.value:
            return dup.value, (linked.value if linked is not None else None)
        return None, None
    finally:
        if linked is not None and linked.value:
            _kernel32.CloseHandle(linked)


def launch_apollo(session_id):
    """Launch sunshine.exe as the elevated user token inside their session."""
    global _apollo_last_launch
    exe = os.path.join(os.path.dirname(SVC_DIR), 'sunshine.exe')
    if not os.path.exists(exe):
        log_err('apollo guard: sunshine.exe not found: %s' % exe)
        return None
    # rate-limit: never relaunch more often than once per 20s
    if time.time() - _apollo_last_launch < 20:
        return None
    _apollo_last_launch = time.time()
    raw = wintypes.HANDLE()
    if not wtsapi32.WTSQueryUserToken(session_id, ctypes.byref(raw)):
        log_warn('apollo guard: WTSQueryUserToken(%d) failed err=%d' % (session_id, ctypes.GetLastError()))
        return None
    try:
        tok, linked = _elevated_primary_token(raw)
        if not tok:
            log_warn('apollo guard: could not build an elevated primary token')
            return None
        try:
            # the linked token may carry session 0: stamp the target session on
            sid_val = wintypes.DWORD(session_id)
            if not _advapi32.SetTokenInformation(wintypes.HANDLE(tok), TokenSessionId, ctypes.byref(sid_val), ctypes.sizeof(sid_val)):
                log_err('apollo guard: SetTokenInformation(TokenSessionId) failed err=%d' % ctypes.GetLastError())
                return None
            env = ctypes.c_void_p()
            if not _userenv.CreateEnvironmentBlock(ctypes.byref(env), wintypes.HANDLE(tok), False):
                env = ctypes.c_void_p()
            try:
                si = STARTUPINFOW()
                si.cb = ctypes.sizeof(STARTUPINFOW)
                si.lpDesktop = 'WinSta0\\Default'
                pi = PROCESS_INFORMATION()
                cmdline = ctypes.create_unicode_buffer('"%s"' % exe)
                ok = _advapi32.CreateProcessAsUserW(wintypes.HANDLE(tok), None, cmdline, None, None, False,
                                                    CREATE_UNICODE_ENV_FLAG, env,
                                                    os.path.dirname(exe), ctypes.byref(si), ctypes.byref(pi))
                if ok:
                    log('apollo guard: launched sunshine.exe pid=%d session=%d (elevated=%s)' % (
                        pi.dwProcessId, session_id, bool(linked)))
                    _kernel32.CloseHandle(pi.hThread)
                    _kernel32.CloseHandle(pi.hProcess)
                    return pi.dwProcessId
                log_err('apollo guard: CreateProcessAsUserW failed err=%d' % ctypes.GetLastError())
                return None
            finally:
                if env.value:
                    _userenv.DestroyEnvironmentBlock(env.value)
        finally:
            _kernel32.CloseHandle(wintypes.HANDLE(tok))
    finally:
        _kernel32.CloseHandle(raw)


# ----------------------------------------------------------------------------
# RDP keepalive (aardwolf, async thread)

class Keepalive:
    def __init__(self):
        self.desired = False
        self.stop_evt = threading.Event()
        self.connected = False
        self._thread = None
        self._conn_since = None

    def start(self):
        self._thread = threading.Thread(target=self._thread_main, daemon=True)
        self._thread.start()

    def _thread_main(self):
        try:
            loop = asyncio.new_event_loop()
            asyncio.set_event_loop(loop)
            loop.run_until_complete(self._run())
        except Exception as e:
            log_err('keepalive thread fatal: %r' % e)

    async def _drain_loop(self, conn):
        """Continuously consume output frames (video etc.) so internal queues
        never accumulate - this is the memory-safety core."""
        while True:
            await conn.ext_out_queue.get()

    async def _drop_conn(self, conn, why):
        try:
            await conn.terminate()
        except Exception:
            pass
        log('keepalive: ' + why)

    async def _run(self):
        from aardwolf.commons.factory import RDPConnectionFactory
        from aardwolf.commons.iosettings import RDPIOSettings
        conn = None
        drain_task = None
        while not self.stop_evt.is_set():
            try:
                if self.desired and conn is None:
                    cfg = load_config()
                    if not cfg.get('keepalive'):
                        await asyncio.sleep(5)
                        continue
                    try:
                        pwd = read_password(cfg)
                    except Exception as e:
                        log_err('keepalive: cred unavailable: %r' % e)
                        await asyncio.sleep(30)
                        continue
                    settings = RDPIOSettings()
                    settings.channels = []
                    settings.video_width = int(cfg.get('width', 1600))
                    settings.video_height = int(cfg.get('height', 720))
                    settings.video_bpp_min = 15
                    settings.video_bpp_max = 32
                    settings.clipboard_use_pyperclip = False
                    url = 'rdp+ntlm-password://%s:%s@%s' % (
                        cfg.get('username', 'Game'), pwd, cfg.get('server', '127.0.0.2'))
                    factory = RDPConnectionFactory.from_url(url, settings)
                    conn = factory.get_connection(settings)
                    _, err = await conn.connect()
                    if err is not None:
                        log_warn('keepalive: connect failed: %r' % err)
                        conn = None
                        await asyncio.sleep(30)
                        continue
                    self.connected = True
                    drain_task = asyncio.create_task(self._drain_loop(conn))
                    self._conn_since = time.time()
                    log('keepalive: connected (holding)')
                elif not self.desired and conn is not None:
                    if drain_task is not None:
                        drain_task.cancel()
                    await self._drop_conn(conn, 'released (session active or disabled)')
                    conn = None
                    drain_task = None
                    self.connected = False
                elif conn is not None:
                    task = getattr(conn, '_RDPConnection__x224_reader_task', None)
                    recycle = float(load_config().get('recycle_minutes', 30)) * 60
                    if task is not None and task.done():
                        if drain_task is not None:
                            drain_task.cancel()
                        await self._drop_conn(conn, 'connection lost (displaced?), releasing')
                        conn = None
                        drain_task = None
                        self.connected = False
                    elif recycle > 0 and self._conn_since and (time.time() - self._conn_since) > recycle:
                        if drain_task is not None:
                            drain_task.cancel()
                        await self._drop_conn(conn, 'recycling connection (memory ceiling)')
                        conn = None
                        drain_task = None
                        self.connected = False
                        self._conn_since = None
                await asyncio.sleep(3)
            except asyncio.CancelledError:
                raise
            except Exception as e:
                log_warn('keepalive loop error: %r' % e)
                if drain_task is not None:
                    drain_task.cancel()
                if conn is not None:
                    await self._drop_conn(conn, 'dropped after loop error')
                    conn = None
                    drain_task = None
                self.connected = False
                await asyncio.sleep(5)
        if drain_task is not None:
            drain_task.cancel()
        if conn is not None:
            await self._drop_conn(conn, 'final release')
        self.connected = False


ka = Keepalive()


# ----------------------------------------------------------------------------
# main control tick

def tick():
    global _LOG_MAX_KB, _LOG_KEEP, _LOG_LEVEL
    cfg = load_config()
    try:
        _LOG_MAX_KB = max(10, int(cfg.get('log_max_kb', 100)))
        _LOG_KEEP = max(1, int(cfg.get('log_keep', 4)))
        _LOG_LEVEL = _LOG_LEVELS.get(str(cfg.get('log_level', 'warn')).lower(), _LOG_LEVEL)
    except Exception:
        pass
    sid, state = find_user_session(cfg.get('username', 'Game'))
    if cfg.get('keepalive'):
        if state == 4:
            ka.desired = True
        elif state in (0, 1, 2):
            # session active: either a user client holds it (our connection
            # was displaced, reader-task already ended) or WE hold it.
            # Never release while our own connection is alive - the keepalive
            # thread detects displacement itself and drops the connection.
            ka.desired = ka.connected
        elif state == -1:
            # no Game session exists at all (fresh boot, logged off): by
            # default wsguard never logs a user in - stay idle. With
            # auto_logon the keepalive RDPs in itself, which creates the
            # session; the next tick then sees it and everything follows.
            ka.desired = bool(cfg.get('auto_logon'))
    else:
        ka.desired = False

    if cfg.get('display_keepalive') and state == 0:
        display_keep(True)
    else:
        display_keep(False)

    if cfg.get('guard_uu'):
        run_ps_guard()

    apollo_state = 'off'
    if cfg.get('guard_apollo'):
        if sid != -1:
            if apollo_running_in(sid):
                apollo_state = 'ok'
            else:
                apollo_state = 'launch'
                launch_apollo(sid)
        else:
            apollo_state = 'no-session'

    state_names = {0: 'Active', 1: 'Connected', 2: 'Query', 4: 'Disconnected'}
    log('tick: session=%s(%s) ka_desired=%s ka_connected=%s display_hold=%s apollo=%s' % (
        sid, state_names.get(state, str(state)), ka.desired, ka.connected, _display_held, apollo_state))


# ----------------------------------------------------------------------------
# SCM service plumbing (raw ctypes - no pywin32, nothing to collect at build)

SERVICE_NAME = 'WSGuard'
SERVICE_WIN32_OWN_PROCESS = 0x00000010
SERVICE_ACCEPT_STOP = 0x00000001
SERVICE_RUNNING = 4
SERVICE_STOPPED = 1
SERVICE_STOP_PENDING = 3
SERVICE_CONTROL_STOP = 1
WAIT_OBJECT_0 = 0
ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063


class SERVICE_STATUS(ctypes.Structure):
    _fields_ = [('dwServiceType', wintypes.DWORD),
                ('dwCurrentState', wintypes.DWORD),
                ('dwControlsAccepted', wintypes.DWORD),
                ('dwWin32ExitCode', wintypes.DWORD),
                ('dwServiceSpecificExitCode', wintypes.DWORD),
                ('dwCheckPoint', wintypes.DWORD),
                ('dwWaitHint', wintypes.DWORD)]


class SERVICE_TABLE_ENTRYW(ctypes.Structure):
    _fields_ = [('lpServiceName', wintypes.LPWSTR),
                ('lpServiceProc', ctypes.c_void_p)]


_HANDLER_EX = ctypes.WINFUNCTYPE(wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
                                 wintypes.LPVOID, wintypes.LPVOID)
_SERVICE_MAIN = ctypes.WINFUNCTYPE(None, wintypes.DWORD, ctypes.POINTER(wintypes.LPWSTR))

_advapi32.StartServiceCtrlDispatcherW.argtypes = [ctypes.POINTER(SERVICE_TABLE_ENTRYW)]
_advapi32.StartServiceCtrlDispatcherW.restype = wintypes.BOOL
_advapi32.RegisterServiceCtrlHandlerExW.argtypes = [wintypes.LPCWSTR, _HANDLER_EX, wintypes.LPVOID]
_advapi32.SetServiceStatus.argtypes = [wintypes.HANDLE, ctypes.POINTER(SERVICE_STATUS)]
_advapi32.SetServiceStatus.restype = wintypes.BOOL
_kernel32.CreateEventW.restype = wintypes.HANDLE
_kernel32.CreateEventW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
_kernel32.SetEvent.argtypes = [wintypes.HANDLE]
_kernel32.SetEvent.restype = wintypes.BOOL
_kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
_kernel32.WaitForSingleObject.restype = wintypes.DWORD

_hStatus = None
_stopEvt = None


def _set_status(state, accept=0, wait_hint=0):
    if _hStatus is None:
        return
    ss = SERVICE_STATUS(SERVICE_WIN32_OWN_PROCESS, state, accept, 0, 0, 0, wait_hint)
    try:
        _advapi32.SetServiceStatus(wintypes.HANDLE(_hStatus), ctypes.byref(ss))
    except Exception:
        pass


@_HANDLER_EX
def _handler_ex(ctrl, evt_type, evt_data, context):
    # SCM control callback - must return fast; stop path only signals an event
    if ctrl == SERVICE_CONTROL_STOP:
        try:
            _set_status(SERVICE_STOP_PENDING, wait_hint=30000)
            if _stopEvt is not None:
                _kernel32.SetEvent(_stopEvt)
        except Exception:
            pass
    return 0


@_SERVICE_MAIN
def _service_main(argc, argv):
    global _hStatus, _stopEvt
    _hStatus = _advapi32.RegisterServiceCtrlHandlerExW(SERVICE_NAME, _handler_ex, None)
    if not _hStatus:
        return  # cannot talk to SCM at all; process exits -> start failure
    _stopEvt = _kernel32.CreateEventW(None, 0, 0, None)
    _set_status(SERVICE_RUNNING, accept=SERVICE_ACCEPT_STOP)
    log('service: SvcDoRun entered (ctypes SCM)')
    try:
        ka.start()
    except Exception as e:
        log_err('service: keepalive start error: %r' % e)
    log('service: RUNNING (ctypes SCM)')
    try:
        tick()
    except Exception as e:
        log_err('tick error: %r' % e)
    while True:
        ms = int(load_config().get('interval', 60)) * 1000
        rc = _kernel32.WaitForSingleObject(_stopEvt, ms if ms > 0 else 60000)
        if rc == WAIT_OBJECT_0:
            break
        try:
            tick()
        except Exception as e:
            log_err('tick error: %r' % e)
    display_keep(False)
    _set_status(SERVICE_STOPPED)
    log('service: STOPPED')


def run_service():
    argv1 = sys.argv[1] if len(sys.argv) > 1 else ''
    if argv1 == '' or argv1 == SERVICE_NAME:
        # launched by SCM (argv = [exe, 'WSGuard']) or a bare console run
        table = (SERVICE_TABLE_ENTRYW * 2)(
            SERVICE_TABLE_ENTRYW(SERVICE_NAME, ctypes.cast(_service_main, ctypes.c_void_p).value),
            SERVICE_TABLE_ENTRYW(None, None))
        if not _advapi32.StartServiceCtrlDispatcherW(table):
            err = ctypes.GetLastError()
            log_err('dispatcher failed err=%d (1063 = not launched by SCM)' % err)
            raise SystemExit(ERROR_FAILED_SERVICE_CONTROLLER_CONNECT)
    else:
        print('unknown argument: %s' % argv1)
        print('usage: wsguard.exe [--console | --setup | --test]')
        raise SystemExit(2)


# ----------------------------------------------------------------------------
# setup mode

def run_setup(args):
    import getpass
    username = None
    password = None
    server = None
    width = None
    height = None
    i = 0
    while i < len(args):
        a = args[i]
        if a == '--username':
            username = args[i + 1]; i += 2
        elif a == '--password':
            password = args[i + 1]; i += 2
        elif a == '--server':
            server = args[i + 1]; i += 2
        elif a == '--width':
            width = int(args[i + 1]); i += 2
        elif a == '--height':
            height = int(args[i + 1]); i += 2
        elif a == '--plain':
            i += 1
        else:
            i += 1
    if username is None:
        username = input('RDP username [Game]: ').strip() or 'Game'
    if password is None:
        password = getpass.getpass('RDP password: ')
    cfg = load_config()
    cfg['username'] = username
    cfg['password_mode'] = 'dpapi'
    if server:
        cfg['server'] = server
    if width:
        cfg['width'] = width
    if height:
        cfg['height'] = height
    cred_path = cfg.get('cred_path', CRED_PATH)
    enc = dpapi_protect(password)
    try:
        if os.path.exists(cred_path):
            os.remove(cred_path)
    except Exception as e:
        print('cannot remove old cred (run elevated): %r' % e)
        return 1
    with open(cred_path, 'wb') as f:
        f.write(enc)
    subprocess.run(['icacls', cred_path, '/inheritance:r', '/grant', 'SYSTEM:F', '/grant', 'Administrators:F'],
                   capture_output=True)
    with open(CONFIG_PATH, 'w', encoding='utf-8') as f:
        json.dump(cfg, f, indent=2, ensure_ascii=False)
    print('config written: %s' % CONFIG_PATH)
    print('credential written (DPAPI LocalMachine): %s' % cred_path)
    print('tip: run with --test to verify the connection')
    return 0


# ----------------------------------------------------------------------------
# connection test mode

def run_test(args):
    cfg = load_config()
    pwd = None
    if '--password' in args:
        pwd = args[args.index('--password') + 1]
    if pwd is None:
        try:
            pwd = read_password(cfg)
        except Exception as e:
            print('cannot read credential (%r); pass --password for a manual test' % e)
            return 1
    print('connecting to %s as %s (%dx%d)...' % (
        cfg.get('server'), cfg.get('username'), cfg.get('width', 1600), cfg.get('height', 720)))

    async def _t():
        from aardwolf.commons.factory import RDPConnectionFactory
        from aardwolf.commons.iosettings import RDPIOSettings
        settings = RDPIOSettings()
        settings.channels = []
        settings.video_width = int(cfg.get('width', 1600))
        settings.video_height = int(cfg.get('height', 720))
        settings.video_bpp_min = 15
        settings.video_bpp_max = 32
        settings.clipboard_use_pyperclip = False
        url = 'rdp+ntlm-password://%s:%s@%s' % (cfg.get('username', 'Game'), pwd, cfg.get('server', '127.0.0.2'))
        factory = RDPConnectionFactory.from_url(url, settings)
        conn = factory.get_connection(settings)
        _, err = await conn.connect()
        if err is not None:
            print('connect failed: %r' % err)
            return 1
        print('CONNECTED - holding 20s to verify session activation...')
        await asyncio.sleep(20)
        await conn.terminate()
        print('OK - terminated cleanly')
        return 0
    return asyncio.run(_t())


# ----------------------------------------------------------------------------
# entry

def main():
    args = sys.argv[1:]
    if '--setup' in args:
        sys.exit(run_setup(args))
    if '--test' in args:
        sys.exit(run_test(args))
    if '--console' in args:
        log('console mode - Ctrl+C to stop')
        ka.start()
        try:
            while True:
                tick()
                time.sleep(load_config().get('interval', 60))
        except KeyboardInterrupt:
            ka.stop_evt.set()
            display_keep(False)
        return
    run_service()

if __name__ == '__main__':
    main()

