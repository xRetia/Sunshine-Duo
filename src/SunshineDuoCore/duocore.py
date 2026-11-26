# SunshineDuoCore - single-file Windows service (pure Python, PyInstaller-packaged)
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
import atexit
import base64
import ctypes
import glob
import json
import os
import re
import subprocess
import sys
import threading
import time
from collections import deque
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

_repo_root_cache = None

def repo_root():
    """Runtime root = the directory holding sunshine.exe. Since the bin/
    migration SVC_DIR is <root>\\bin\\SunshineDuoCore, so dirname(SVC_DIR) is
    bin\\ and NOT the root - deriving conf/log paths from it silently broke
    stream detection and state-file handling. Walk up to the exe instead;
    works for the bin\\ layout, the src\\ tree and the pre-migration layout."""
    global _repo_root_cache
    if _repo_root_cache:
        return _repo_root_cache
    d = SVC_DIR
    for _ in range(6):
        if os.path.isfile(os.path.join(d, 'sunshine.exe')):
            break
        parent = os.path.dirname(d)
        if parent == d:
            d = os.path.dirname(os.path.dirname(SVC_DIR))
            break
        d = parent
    _repo_root_cache = d
    return d

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
    'guard_apollo_console': True,
    'console_conf_path': 'config\\sunshine.conf',
    'game_conf_path': 'config\\sunshine-game.conf',
    'display_keepalive': True,
    'interval': 60,
    'recycle_minutes': 30,
    # GameViewer version gate (wildcard match, e.g. '4.0.*' = any 4.0.x):
    # the PS guard refuses to inject unless GameViewerService.exe's
    # FileVersion matches one of these patterns. Update after a verified
    # GameViewer upgrade - no repack needed.
    'gameviewer_versions': ['4.0.*', '4.46.*'],
    # auto_logon: when NO Game session exists at all (e.g. right after boot),
    # let the RDP keepalive log in to 127.0.0.2 itself, which creates the
    # Game session; sunshine/apollo then launch into it unattended.
    'auto_logon': False,
    # log rotation: watch.log is renamed to watch-YYYYMMDD_HHMMSS.log once it
    # exceeds log_max_kb; the newest log_keep archives are retained.
    'log_max_kb': 100,
    'log_keep': 4,
    # pair event watcher: tailed Apollo logs are the passive signal for new
    # devices (unpaired client certs fail TLS verification), the tray polls
    # events over the ctrl pipe and auto-opens the pairing dialog
    'pair_watch': True,
    'pair_watch_interval': 2,
    # log_level: 'info' = every tick (verbose), 'warn' = warnings and errors
    # only (default), 'error' = errors only. Hot-reloaded every tick.
    'log_level': 'warn',
    # Optional console-desktop hotkey substitution, both off by default and
    # hot-reloaded by the display worker (docs/plan.md 9.2):
    #   win_from_ralt    - R-Alt is detected and forwarded as Win
    #   winl_from_ralt_l - R-Alt+L is detected and forwarded as Win+L (lock)
    # easytier 内网穿透（可选）：服务托管的 easytier-core 子进程，
    # 配置节含义见下方 easytier 段（bin\SunshineDuoCore\README.md）
    'easytier': {'enabled': False, 'network': '', 'secret': '', 'server_mode': 'public',
                 'peers': [], 'ipv4': '', 'tcp_hole_punch': False, 'extra_args': []},
    'win_from_ralt': False,
    'winl_from_ralt_l': False,
    # hide_cursor_on_stream: while a client streams the Console session, replace
    # every system cursor with a transparent one so the captured frame carries
    # no Windows pointer; the original cursors are restored on disconnect.
    # Applied by the Console display worker (~120ms hot reload).
    'hide_cursor_on_stream': False,
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
    # 将相对路径配置（相对于仓库根目录）展开为绝对路径，避免工作目录漂移
    root = repo_root()
    for key in ('console_conf_path', 'game_conf_path'):
        if cfg.get(key) and not os.path.isabs(cfg[key]):
            cfg[key] = os.path.join(root, cfg[key])
    return cfg


def save_config_display_mode(mode):
    """Persist display mode choice to config.json (hot-reloaded by every tick).
    mode: 'mirror' | 'only' | 'extend'. display_exclusive follows 'only'."""
    if mode not in ('mirror', 'only', 'extend'):
        return False
    try:
        with open(CONFIG_PATH, 'r', encoding='utf-8-sig') as f:
            data = json.load(f)
    except FileNotFoundError:
        data = {}
    except Exception as e:
        log_err('display mode save: read error %r' % e)
        return False
    data['default_display_mode'] = mode
    data['display_exclusive'] = (mode == 'only')
    try:
        with open(CONFIG_PATH, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=4)
        log('display mode set to %s (display_exclusive=%s)' % (mode, mode == 'only'))
        return True
    except Exception as e:
        log_err('display mode save: write error %r' % e)
        return False


def save_config_hotkeys(win_from_ralt, winl_from_ralt_l):
    """Persist the two console-desktop hotkey substitutions. The display worker
    hot-reloads them on its next ~120ms poll, so no service restart is needed."""
    try:
        with open(CONFIG_PATH, 'r', encoding='utf-8-sig') as f:
            data = json.load(f)
    except FileNotFoundError:
        data = {}
    except Exception as e:
        log_err('hotkeys save: read error %r' % e)
        return False
    data['win_from_ralt'] = bool(win_from_ralt)
    data['winl_from_ralt_l'] = bool(winl_from_ralt_l)
    try:
        with open(CONFIG_PATH, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=4)
        log('hotkeys set: win_from_ralt=%s winl_from_ralt_l=%s'
            % (data['win_from_ralt'], data['winl_from_ralt_l']))
        return True
    except Exception as e:
        log_err('hotkeys save: write error %r' % e)
        return False


def save_config_hide_cursor(enabled):
    """Persist hide_cursor_on_stream. The Console display worker hot-reloads it
    on its next ~120ms poll, so a stream already in flight toggles immediately."""
    try:
        with open(CONFIG_PATH, 'r', encoding='utf-8-sig') as f:
            data = json.load(f)
    except FileNotFoundError:
        data = {}
    except Exception as e:
        log_err('hide cursor save: read error %r' % e)
        return False
    data['hide_cursor_on_stream'] = bool(enabled)
    try:
        with open(CONFIG_PATH, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=4)
        log('hide_cursor_on_stream set to %s' % data['hide_cursor_on_stream'])
        return True
    except Exception as e:
        log_err('hide cursor save: write error %r' % e)
        return False


def save_config_resolution(width, height, resolutions=None):
    """Persist virtual-display resolution (width x height) to config.json.
    Hot-reloaded by every tick; consumed by the display topology logic for
    width/height for the
    RDP keepalive and SudoVDA video mode. An optional resolutions list is
    stored as sudovda_resolutions (candidate resolutions kept for the setup
    wizard / future driver panes)."""
    try:
        width = int(width); height = int(height)
    except Exception:
        return False
    if width <= 0 or height <= 0:
        return False
    clean = None
    if resolutions is not None:
        clean = []
        for item in resolutions:
            s = str(item).strip()
            if s and s not in clean:
                clean.append(s)
    try:
        with open(CONFIG_PATH, 'r', encoding='utf-8-sig') as f:
            data = json.load(f)
    except FileNotFoundError:
        data = {}
    except Exception as e:
        log_err('resolution save: read error %r' % e)
        return False
    data['width'] = width
    data['height'] = height
    if clean is not None:
        data['sudovda_resolutions'] = clean
    try:
        with open(CONFIG_PATH, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=4)
        log('resolution set to %dx%d' % (width, height))
        if clean:
            log('sudovda_resolutions: %d entries' % len(clean))
        return True
    except Exception as e:
        log_err('resolution save: write error %r' % e)
        return False


# ----------------------------------------------------------------------------
# EasyTier 内网穿透（可选）：由本服务以自身身份托管 easytier-core 子进程。
# 放在 core 而非托盘：core 常驻（服务身份，TUN 所需权限天然具备）、已有控制
# 管道与配置热重载；托盘只经 ctrl pipe 下发开关并查询状态。
# 配置节 config.json "easytier"（热重载）：
#   enabled         bool   服务 tick 时发现未运行则拉起（含开机自启与崩溃拉起）
#   network         str    --network-name；留空首次启用时用计算机名
#   secret          str    --network-secret（留空=无密钥）
#   server_mode     str    'public' 本机作为公共共享节点 | 'custom' 连指定节点
#   peers           [str]  custom 模式的节点地址（-p，可多条）
#   ipv4            str    手动虚拟 IP（-i）；留空 = --dhcp
#   tcp_hole_punch  bool   默认 false → 传 --disable-tcp-hole-punching
#   extra_args      [str]  附加命令行（高级手工，如 --no-tun）
#   exe             str    覆盖二进制路径（默认与服务 exe 同目录的 easytier-core.exe）
# 进程匹配按完整映像路径，避免误杀用户在别处手工运行的 easytier-core。

_easytier_pid = 0
_easytier_started_at = 0.0
_easytier_last_fail = 0.0
_easytier_args_used = None


def _easytier_cfg(cfg):
    et = cfg.get('easytier')
    return et if isinstance(et, dict) else {}


def easytier_exe(cfg):
    custom = str(_easytier_cfg(cfg).get('exe') or '').strip()
    if custom:
        return custom if os.path.isabs(custom) else os.path.join(repo_root(), custom)
    # 默认随服务同目录分发（bin\SunshineDuoCore\easytier-core.exe，与 gv_hook 等
    # 载荷同级）；src 树直跑时退回部署目录，再退回旧的 tools\EasyTier 位置。
    for d in (SVC_DIR, os.path.join(repo_root(), 'bin', 'SunshineDuoCore'),
              os.path.join(repo_root(), 'tools', 'EasyTier')):
        cand = os.path.join(d, 'easytier-core.exe')
        if os.path.isfile(cand):
            return cand
    return os.path.join(SVC_DIR, 'easytier-core.exe')


def save_config_easytier(enabled=None, network=None, secret=None, server_mode=None,
                         peers=None, ipv4=None, tcp_hole_punch=None):
    """Persist the easytier section (None = leave the key untouched);
    hot-reloaded by every tick."""
    try:
        with open(CONFIG_PATH, 'r', encoding='utf-8-sig') as f:
            data = json.load(f)
    except FileNotFoundError:
        data = {}
    except Exception as e:
        log_err('easytier save: read error %r' % e)
        return False
    et = data.get('easytier') if isinstance(data.get('easytier'), dict) else {}
    if enabled is not None:
        et['enabled'] = bool(enabled)
    if network is not None:
        et['network'] = str(network).strip()
    if secret is not None:
        et['secret'] = str(secret)
    if server_mode is not None and str(server_mode) in ('public', 'custom'):
        et['server_mode'] = str(server_mode)
    if peers is not None:
        et['peers'] = [str(p).strip() for p in peers if str(p).strip()]
    if ipv4 is not None:
        et['ipv4'] = str(ipv4).strip()
    if tcp_hole_punch is not None:
        et['tcp_hole_punch'] = bool(tcp_hole_punch)
    data['easytier'] = et
    try:
        with open(CONFIG_PATH, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=4)
        log('easytier set: enabled=%s network=%s server=%s ipv4=%s tcp_punch=%s'
            % (et.get('enabled'), et.get('network') or '', et.get('server_mode') or 'public',
               et.get('ipv4') or 'dhcp', bool(et.get('tcp_hole_punch'))))
        return True
    except Exception as e:
        log_err('easytier save: write error %r' % e)
        return False


def _find_procs_by_image(image_name):
    """(pid, full image path) for every live process whose image file name
    matches, in any session."""
    out = []
    arr = (wintypes.DWORD * 2048)()
    cb = wintypes.DWORD(0)
    if not _psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(cb)):
        return out
    for i in range(cb.value // ctypes.sizeof(wintypes.DWORD)):
        pid = arr[i]
        if not pid:
            continue
        h = _kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            continue
        try:
            buf = ctypes.create_unicode_buffer(1024)
            n = wintypes.DWORD(1024)
            if _kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)) and \
                    buf.value.lower().endswith('\\' + image_name.lower()):
                out.append((pid, buf.value))
        finally:
            _kernel32.CloseHandle(h)
    return out


def easytier_pid(exe):
    """Tracked pid if still alive, else the first live easytier-core.exe whose
    image path matches exe (adopted: started by an earlier service run).
    0 = not running."""
    global _easytier_pid, _easytier_started_at
    want = os.path.abspath(exe).lower()
    if _easytier_pid:
        path = ''
        h = _kernel32.OpenProcess(0x1000, False, _easytier_pid)
        if h:
            buf = ctypes.create_unicode_buffer(1024)
            n = wintypes.DWORD(1024)
            if _kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)):
                path = buf.value
            _kernel32.CloseHandle(h)
        if path and path.lower() == want:
            return _easytier_pid
        _easytier_pid = 0
        _easytier_started_at = 0.0
    for pid, path in _find_procs_by_image('easytier-core.exe'):
        if path.lower() == want:
            return pid
    return 0


def easytier_machine_name():
    return str(os.environ.get('COMPUTERNAME') or '').strip()


# 公共服务器（默认）：节点发现/中继用的公共共享节点
ET_PUBLIC_SERVER = 'tcp://easytier.zcm.cloudns.ch:11010'


def easytier_args(et):
    """Command line for easytier-core (v2.6.4 flags). 网络名为空时用计算机名；
    ipv4 为空走 DHCP；server_mode=public 连默认公共服务器，custom 连指定节点；
    TCP 打洞默认关闭（--disable-tcp-hole-punching）；密钥不打印到日志。"""
    net = str(et.get('network') or '').strip() or easytier_machine_name()
    args = ['--network-name', net] if net else []
    if str(et.get('secret') or ''):
        args += ['--network-secret', str(et.get('secret'))]
    ipv4 = str(et.get('ipv4') or '').strip()
    args += ['-i', ipv4] if ipv4 else ['--dhcp']
    if str(et.get('server_mode') or 'public') == 'custom':
        for p in (et.get('peers') or []):
            s = str(p).strip()
            if s:
                args += ['-p', s]
    else:
        args += ['-p', ET_PUBLIC_SERVER]
    if not bool(et.get('tcp_hole_punch')):
        args += ['--disable-tcp-hole-punching']
    # 虚拟网卡显示名（设备管理器里不再是自动生成的 et_xxx Tunn）
    args += ['--dev-name', 'MoeArt Remote Tunnel']
    for a in (et.get('extra_args') or []):
        s = str(a).strip()
        if s:
            args.append(s)
    return args, net


_ET_NODE_TTL = 4.0
_et_node = {'ts': 0.0, 'data': {}}


def easytier_node(cfg):
    """`easytier-cli -o json node` (RPC portal 15888 by default), cached for a
    few seconds so tray polling does not spawn a CLI per request."""
    now = time.time()
    if now - _et_node['ts'] < _ET_NODE_TTL:
        return _et_node['data']
    data = {}
    cli = os.path.join(os.path.dirname(easytier_exe(cfg)), 'easytier-cli.exe')
    if os.path.isfile(cli):
        try:
            si = subprocess.STARTUPINFO()
            si.dwFlags = subprocess.STARTF_USESHOWWINDOW
            si.wShowWindow = 0
            p = subprocess.run([cli, '-o', 'json', 'node'], capture_output=True,
                               timeout=3, startupinfo=si,
                               creationflags=subprocess.CREATE_NO_WINDOW)
            if p.returncode == 0:
                parsed = json.loads(p.stdout.decode('utf-8', errors='replace'))
                if isinstance(parsed, dict):
                    data = parsed
        except Exception as e:
            log('easytier cli node error: %r' % e)
    _et_node['ts'] = now
    _et_node['data'] = data
    return data


def _easytier_vip(node):
    ip = str(node.get('ipv4_addr') or '').strip()
    return ip.split('/')[0] if ip else ''


def _easytier_public_ip(node):
    stun = node.get('stun_info')
    addrs = stun.get('public_ip') if isinstance(stun, dict) else None
    return str(addrs[0]) if isinstance(addrs, list) and addrs else ''


def easytier_start(cfg=None):
    """Spawn easytier-core; adopts an already-running instance instead of
    duplicating it (so a service restart never stacks a second tunnel)."""
    global _easytier_pid, _easytier_started_at, _easytier_last_fail, _easytier_args_used
    cfg = cfg or load_config()
    exe = easytier_exe(cfg)
    cur = easytier_pid(exe)
    if cur:
        return {'ok': True, 'running': True, 'pid': cur, 'adopted': True}
    if not os.path.isfile(exe):
        if time.time() - _easytier_last_fail > 600:
            _easytier_last_fail = time.time()
            log_err('easytier start: exe not found: %s' % exe)
        return {'ok': False, 'error': '未找到 easytier-core.exe：%s' % exe}
    et = _easytier_cfg(cfg)
    args, net = easytier_args(et)
    out_path = os.path.join(SVC_DIR, 'easytier.out')
    try:
        if os.path.getsize(out_path) > 5 * 1024 * 1024:
            os.remove(out_path)
    except OSError:
        pass
    si = subprocess.STARTUPINFO()
    si.dwFlags = subprocess.STARTF_USESHOWWINDOW
    si.wShowWindow = 0
    try:
        out = open(out_path, 'ab', buffering=0)
        try:
            p = subprocess.Popen([exe] + args, cwd=os.path.dirname(exe), startupinfo=si,
                                 creationflags=subprocess.CREATE_NO_WINDOW,
                                 stdin=subprocess.DEVNULL,
                                 stdout=out, stderr=subprocess.STDOUT)
        finally:
            out.close()  # the child keeps its inherited handle
    except Exception as e:
        log_err('easytier spawn error: %r' % e)
        return {'ok': False, 'error': '启动 easytier-core 失败：%r' % e}
    _easytier_pid = p.pid
    _easytier_started_at = time.time()
    _easytier_args_used = args
    _et_node['ts'] = 0.0
    log('easytier: started pid=%d network=%s opts=%s' % (p.pid, net, args[2:]))
    return {'ok': True, 'running': True, 'pid': p.pid}


def easytier_stop(cfg=None):
    """Terminate every easytier-core.exe running from OUR exe path (instances
    started from other directories are never touched)."""
    global _easytier_pid, _easytier_started_at
    cfg = cfg or load_config()
    exe = easytier_exe(cfg)
    want = os.path.abspath(exe).lower()
    killed = 0
    for pid, path in _find_procs_by_image('easytier-core.exe'):
        if path.lower() == want and _terminate_pid(pid):
            killed += 1
    _easytier_pid = 0
    _easytier_started_at = 0.0
    _et_node['ts'] = 0.0
    _et_node['data'] = {}
    if killed:
        log('easytier: stopped %d process(es)' % killed)
    return {'ok': True, 'stopped': killed}


def easytier_status(cfg=None):
    cfg = cfg or load_config()
    et = _easytier_cfg(cfg)
    exe = easytier_exe(cfg)
    pid = easytier_pid(exe)
    running = bool(pid)
    ours = bool(running and _easytier_pid == pid)
    node = easytier_node(cfg) if running else {}
    _args, net = easytier_args(et)
    return {
        'ok': True, 'running': running, 'pid': pid,
        'managed': ours,
        'started_at': _easytier_started_at if ours else 0,
        'enabled': bool(et.get('enabled')),
        'network': net,
        'network_saved': str(et.get('network') or '').strip(),
        'machine': easytier_machine_name(),
        'secret_set': bool(str(et.get('secret') or '')),
        'server_mode': str(et.get('server_mode') or 'public'),
        'default_server': ET_PUBLIC_SERVER,
        'peers': [str(p) for p in (et.get('peers') or [])],
        'ipv4': str(et.get('ipv4') or '').strip(),
        'tcp_hole_punch': bool(et.get('tcp_hole_punch')),
        'virtual_ip': _easytier_vip(node),
        'public_ip': _easytier_public_ip(node),
        'exe': exe, 'exe_exists': os.path.isfile(exe),
    }


def ensure_easytier(cfg):
    """tick hook: keep the tunnel up while enabled (boot autostart + crash
    recovery). A missing exe is warn-throttled so ticks cannot spam the log."""
    if not _easytier_cfg(cfg).get('enabled'):
        return
    if easytier_pid(easytier_exe(cfg)):
        return
    r = easytier_start(cfg)
    if not r.get('ok'):
        log_warn('easytier autostart failed: %s' % r.get('error'))



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

def find_console_session():
    """WTSGetActiveConsoleSessionId，返回控制台会话 id（-1 表示未知）。"""
    _kernel32.WTSGetActiveConsoleSessionId.restype = wintypes.DWORD
    _kernel32.WTSGetActiveConsoleSessionId.argtypes = []
    sid = _kernel32.WTSGetActiveConsoleSessionId()
    return -1 if sid in (0, 0xFFFFFFFF) else sid


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
# display exclusivity (SudoVDA-only topology while streaming)
#
# Why here and not in Apollo: Apollo 0.4.6 applies dd_configuration_option
# (ensure_only_display) at stream start via libdisplaydevice, but the apply
# runs into "API is available: false" right after the virtual display was
# just created (SetDisplayConfig(SDC_VALIDATE) fails during the topology
# transition), and the 5s retry it schedules is immediately cancelled by the
# unconditional display_device::reset_persistence() that follows in
# process.cpp. Net effect: ensure_only_display never takes effect for virtual
# display sessions and the physical monitor stays extended. SunshineDuoCore owns the
# topology instead: whenever a SudoMaker virtual display is active, every
# other active source is detached; when it disappears, everything is restored.
# The virtual display only exists while a streaming session runs (Apollo
# creates it when a client connects, removes it at session stop), so
# exclusivity is scoped exactly to "a Moonlight client is streaming" -
# including the Desktop app entry.

_user32 = ctypes.WinDLL('user32', use_last_error=True)
_user32.EnumDisplayDevicesW.restype = wintypes.BOOL
_user32.EnumDisplayDevicesW.argtypes = [ctypes.c_wchar_p, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]
_user32.EnumDisplaySettingsExW.restype = wintypes.BOOL
_user32.EnumDisplaySettingsExW.argtypes = [ctypes.c_wchar_p, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]
_user32.ChangeDisplaySettingsExW.restype = ctypes.c_long
_user32.ChangeDisplaySettingsExW.argtypes = [ctypes.c_wchar_p, ctypes.c_void_p, wintypes.HWND, wintypes.DWORD, ctypes.c_void_p]
_user32.SetDisplayConfig.restype = ctypes.c_long
_user32.SetDisplayConfig.argtypes = [ctypes.c_uint32, ctypes.c_void_p,
                                     ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32]
SDC_TOPOLOGY_INTERNAL = 0x00000001
SDC_TOPOLOGY_EXTEND = 0x00000004
SDC_TOPOLOGY_CLONE = 0x00000002
SDC_TOPOLOGY_EXTERNAL = 0x00000008
SDC_APPLY = 0x00000080
SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020
SDC_ALLOW_CHANGES = 0x00000400
QDC_ALL_PATHS = 0x00000001
QDC_ONLY_ACTIVE_PATHS = 0x00000002
DISPLAYCONFIG_PATH_ACTIVE = 0x00000001
DISPLAYCONFIG_PATH_UPDATE = 0x00000004
DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF
DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 0x00000001
SUDOVDA_ADAPTER = 'SudoMaker Virtual Display Adapter'
ENUM_CURRENT_SETTINGS = -1
ENUM_REGISTRY_SETTINGS = -2
DM_POSITION = 0x00000020
DM_BITSPERPEL = 0x00040000
DM_PELSWIDTH = 0x00080000
DM_PELSHEIGHT = 0x00100000
DM_DISPLAYFLAGS = 0x00200000
DM_DISPLAYFREQUENCY = 0x00400000
CDS_UPDATEREGISTRY = 0x00000001
CDS_NORESET = 0x10000000
CDS_SET_PRIMARY = 0x00000010
DISP_CHANGE_SUCCESSFUL = 0
ENUM_STABLE_SECS = 2.0      # topology must stay in one state this long before acting
ENUM_RECHECK_SECS = 2.0     # cool-down after an apply
ENUM_FAST_SECS = 0.25       # cool-down while a stream races the VDA naming window
ENUM_RESWEEP_SECS = 60.0    # re-sweep stale EnumDisplayDevices index cache
FP_RACE_WINDOW = 5.0        # proactive EXTERNAL push stays bound to the VDA naming race
_FP_BACKOFF_BASE = 0.5       # first EXTERNAL retry backoff (s)
_FP_BACKOFF_MAX = 4.0       # cap for EXTERNAL retry backoff (s)


class _LUID(ctypes.Structure):
    _fields_ = [('LowPart', wintypes.DWORD), ('HighPart', ctypes.c_long)]


class _RATIONAL(ctypes.Structure):
    _fields_ = [('numerator', wintypes.DWORD), ('denominator', wintypes.DWORD)]


class _2DREGION(ctypes.Structure):
    _fields_ = [('cx', ctypes.c_long), ('cy', ctypes.c_long)]


class _VIDEO_SIGNAL_INFO(ctypes.Structure):
    _fields_ = [('pixelRate', ctypes.c_ulonglong),
                ('hSyncFreq', _RATIONAL),
                ('vSyncFreq', _RATIONAL),
                ('activeSize', _2DREGION),
                ('totalSize', _2DREGION),
                ('videoStandard', wintypes.DWORD),
                ('scanLineOrdering', wintypes.DWORD)]


class _SOURCE_MODE(ctypes.Structure):
    _fields_ = [('positionX', ctypes.c_long), ('positionY', ctypes.c_long),
                ('width', wintypes.DWORD), ('height', wintypes.DWORD),
                ('pixelFormat', wintypes.DWORD)]


class _TARGET_MODE(ctypes.Structure):
    _fields_ = [('videoSignalInfo', _VIDEO_SIGNAL_INFO)]


class _DESKTOP_IMAGE_INFO(ctypes.Structure):
    _fields_ = [('sourcePointX', ctypes.c_long), ('sourcePointY', ctypes.c_long),
                ('targetWidth', wintypes.DWORD), ('targetHeight', wintypes.DWORD),
                ('imageLeft', ctypes.c_long), ('imageTop', ctypes.c_long),
                ('imageRight', ctypes.c_long), ('imageBottom', ctypes.c_long)]


class _MODE_INFO_UNION(ctypes.Union):
    _fields_ = [('sourceMode', _SOURCE_MODE),
                ('targetMode', _TARGET_MODE),
                ('desktopImageInfo', _DESKTOP_IMAGE_INFO),
                ('raw', ctypes.c_ubyte * 48)]


class _MODE_INFO(ctypes.Structure):
    _fields_ = [('infoType', wintypes.DWORD), ('id', wintypes.DWORD),
                ('adapterId', _LUID), ('modeInfo', _MODE_INFO_UNION)]


class _PATH_SOURCE_INFO(ctypes.Structure):
    _fields_ = [('adapterId', _LUID), ('id', wintypes.DWORD),
                ('modeInfoIdx', wintypes.DWORD), ('statusFlags', wintypes.DWORD)]


class _PATH_TARGET_INFO(ctypes.Structure):
    _fields_ = [('adapterId', _LUID), ('id', wintypes.DWORD),
                ('modeInfoIdx', wintypes.DWORD), ('widgetId', wintypes.DWORD),
                ('outputTechnology', wintypes.DWORD), ('rotation', wintypes.DWORD),
                ('scaling', wintypes.DWORD), ('refreshRate', _RATIONAL),
                ('scanLineOrdering', wintypes.DWORD), ('targetAvailable', wintypes.BOOL),
                ('statusFlags', wintypes.DWORD)]


class _PATH_INFO(ctypes.Structure):
    _fields_ = [('sourceInfo', _PATH_SOURCE_INFO),
                ('targetInfo', _PATH_TARGET_INFO),
                ('flags', wintypes.DWORD)]


class _DEVICE_INFO_HEADER(ctypes.Structure):
    _fields_ = [('type', wintypes.DWORD), ('size', wintypes.DWORD),
                ('adapterId', _LUID), ('id', wintypes.DWORD)]


class _SOURCE_DEVICE_NAME(ctypes.Structure):
    _fields_ = [('header', _DEVICE_INFO_HEADER),
                ('viewGdiDeviceName', wintypes.WCHAR * 32)]


_user32.GetDisplayConfigBufferSizes.restype = ctypes.c_long
_user32.GetDisplayConfigBufferSizes.argtypes = [wintypes.DWORD,
                                                ctypes.POINTER(wintypes.DWORD),
                                                ctypes.POINTER(wintypes.DWORD)]
_user32.QueryDisplayConfig.restype = ctypes.c_long
_user32.QueryDisplayConfig.argtypes = [wintypes.DWORD,
                                       ctypes.POINTER(wintypes.DWORD),
                                       ctypes.POINTER(_PATH_INFO),
                                       ctypes.POINTER(wintypes.DWORD),
                                       ctypes.POINTER(_MODE_INFO),
                                       wintypes.DWORD]
_user32.DisplayConfigGetDeviceInfo.restype = ctypes.c_long
_user32.DisplayConfigGetDeviceInfo.argtypes = [ctypes.c_void_p]


class DEVMODEW(ctypes.Structure):
    _fields_ = [('dmDeviceName', wintypes.WCHAR * 32),
                ('dmSpecVersion', wintypes.WORD),
                ('dmDriverVersion', wintypes.WORD),
                ('dmSize', wintypes.WORD),
                ('dmDriverExtra', wintypes.WORD),
                ('dmFields', wintypes.DWORD),
                ('dmPositionX', ctypes.c_long),
                ('dmPositionY', ctypes.c_long),
                ('dmDisplayOrientation', wintypes.DWORD),
                ('dmDisplayFixedOutput', wintypes.DWORD),
                ('dmColor', wintypes.SHORT),
                ('dmDuplex', wintypes.SHORT),
                ('dmYResolution', wintypes.SHORT),
                ('dmTTOption', wintypes.SHORT),
                ('dmCollate', wintypes.SHORT),
                ('dmFormName', wintypes.WCHAR * 32),
                ('dmLogPixels', wintypes.WORD),
                ('dmBitsPerPel', wintypes.DWORD),
                ('dmPelsWidth', wintypes.DWORD),
                ('dmPelsHeight', wintypes.DWORD),
                ('dmDisplayFlags', wintypes.DWORD),
                ('dmDisplayFrequency', wintypes.DWORD),
                ('dmICMMethod', wintypes.DWORD),
                ('dmICMIntent', wintypes.DWORD),
                ('dmMediaType', wintypes.DWORD),
                ('dmDitherType', wintypes.DWORD),
                ('dmReserved1', wintypes.DWORD),
                ('dmReserved2', wintypes.DWORD),
                ('dmPanningWidth', wintypes.DWORD),
                ('dmPanningHeight', wintypes.DWORD)]


class _DISPLAY_DEVICEW(ctypes.Structure):
    _fields_ = [('cb', wintypes.DWORD),
                ('DeviceName', wintypes.WCHAR * 32),
                ('DeviceString', wintypes.WCHAR * 128),
                ('StateFlags', wintypes.DWORD),
                ('DeviceID', wintypes.WCHAR * 128),
                ('DeviceKey', wintypes.WCHAR * 128)]


def _display_sources(refresh=False):
    """Return (sudovda, others) lists of ACTIVE source device names (StateFlags
    & ATTACHED_TO_DESKTOP). DeviceString selects the SudoMaker virtual adapter.
    EnumDisplayDevices is moderately expensive; the resweep cap keeps the fast
    poll cheap while still catching attach/detach within a second. refresh=True
    skips the cache (used right after a topology apply, where the previous
    answer is stale by definition)."""
    now = time.time()
    cache = _display_sources._cache
    if (not refresh and cache is not None and now < _display_sources._next
            and now - cache[2] < ENUM_RESWEEP_SECS):
        return cache[0], cache[1]
    vda, others = [], []
    dev = _DISPLAY_DEVICEW()
    i = 0
    while True:
        dev.cb = ctypes.sizeof(_DISPLAY_DEVICEW)
        if not _user32.EnumDisplayDevicesW(None, i, ctypes.byref(dev), 0):
            break
        i += 1
        if not (dev.StateFlags & 0x1):  # DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
            continue
        if dev.DeviceString == SUDOVDA_ADAPTER:
            vda.append(dev.DeviceName)
        else:
            others.append(dev.DeviceName)
    _display_sources._cache = (vda, others, now)
    _display_sources._next = now + 0.8
    return vda, others

_display_sources._cache = None
_display_sources._next = 0.0


def _vda_devices_any():
    """All SudoMaker VDA monitors known to this session, attached or not.
    A CDS-detached VDA keeps its EnumDisplayDevices entry (it only loses the
    ATTACHED_TO_DESKTOP flag), so this distinguishes 'VDA exists but is
    switched off' (re-attachable) from 'Apollo removed the VDA' (gone until
    the next stream creates it)."""
    out = []
    dev = _DISPLAY_DEVICEW()
    i = 0
    while True:
        dev.cb = ctypes.sizeof(_DISPLAY_DEVICEW)
        if not _user32.EnumDisplayDevicesW(None, i, ctypes.byref(dev), 0):
            break
        i += 1
        if dev.DeviceString == SUDOVDA_ADAPTER:
            out.append(dev.DeviceName)
    return out


def _physical_devices_any():
    """True when at least one non-SudoVDA display device is known to this
    session, attached or not. Used to tell 'physical display detached by a
    previous only-session' (must restore) apart from a truly headless machine
    (no physical display exists at all - keep the VDA)."""
    dev = _DISPLAY_DEVICEW()
    i = 0
    while True:
        dev.cb = ctypes.sizeof(_DISPLAY_DEVICEW)
        if not _user32.EnumDisplayDevicesW(None, i, ctypes.byref(dev), 0):
            break
        i += 1
        if dev.DeviceString != SUDOVDA_ADAPTER:
            return True
    return False


def _set_topology_preset(flag):
    """Apply a CCD topology preset (EXTEND/EXTERNAL/INTERNAL) via
    SetDisplayConfig. Returns True on success. This is the only reliable way
    to re-attach a fully detached source on this machine (ChangeDisplaySettingsEx
    re-attach is rejected by the AMD driver with DISP_CHANGE_FAILED - observed
    live), measured working for both physical and virtual sources."""
    r = _user32.SetDisplayConfig(0, None, 0, None, flag | SDC_APPLY)
    if r != 0:
        log_warn('display: SetDisplayConfig(preset 0x%x) returned %d' % (flag, r))
        return False
    return True


def _query_display_paths():
    """Returns (paths, modes) as raw ctypes arrays (SetDisplayConfig needs the
    native buffers, not Python lists). Each element is an _PATH_INFO/_MODE_INFO
    view - indexable and iterable as usual."""
    count = wintypes.DWORD()
    mode_count = wintypes.DWORD()
    r = _user32.GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, ctypes.byref(count),
                                            ctypes.byref(mode_count))
    if r != 0:
        log_warn('display topology: GetDisplayConfigBufferSizes returned %d' % r)
        return [], []
    paths = (_PATH_INFO * count.value)()
    modes = (_MODE_INFO * mode_count.value)()
    r = _user32.QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ctypes.byref(count), paths,
                                   ctypes.byref(mode_count), modes, 0)
    if r != 0:
        log_warn('display topology: QueryDisplayConfig returned %d' % r)
        return [], []
    return paths, modes


def _path_source_name(path):
    info = _SOURCE_DEVICE_NAME()
    info.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
    info.header.size = ctypes.sizeof(info)
    info.header.adapterId = path.sourceInfo.adapterId
    info.header.id = path.sourceInfo.id
    if _user32.DisplayConfigGetDeviceInfo(ctypes.byref(info)) != 0:
        return ''
    return info.viewGdiDeviceName


def _source_mode_of(path, modes):
    idx = path.sourceInfo.modeInfoIdx & 0xFFFF
    if idx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID or idx >= len(modes):
        return None
    return modes[idx].modeInfo.sourceMode


def _activate_vda_external():
    """Put every attachable display into the EXTERNAL projection topology, the
    only CCD preset that activates a freshly created SudoVDA monitor on this
    machine (EXTEND leaves it inactive - measured live). Fires only when a
    SudoVDA source is GDI-attached but not yet in the active path set."""
    vda, others = _display_sources()
    if not vda:
        return False, 'no VDA attached'
    paths, modes = _query_display_paths()
    if not paths:
        return False, 'QueryDisplayConfig failed'
    names = [_path_source_name(p) for p in paths]
    vda_set = set(vda)
    vda_active = any(name in vda_set for name in names)
    if vda_active:
        return True, 'vda already active'
    r = _user32.SetDisplayConfig(0, None, 0, None, SDC_TOPOLOGY_EXTERNAL | SDC_APPLY)
    if r != 0:
        return False, 'SetDisplayConfig(EXTERNAL) returned %d' % r
    return True, 'fired EXTERNAL topology'


def _apply_display_topology(mode, reattach_names=()):
    """Apply the projection topology via CCD/SetDisplayConfig, the same API
    Win+P uses. The VDA is only selectable while Apollo has it attached.
    reattach_names: VDA sources this worker detached itself (mirror mode);
    only those may be revived - the session can also hold stale SudoVDA
    monitors (probe leftovers) that must never be dragged back in."""
    if mode not in ('mirror', 'only', 'extend'):
        return False, 'unknown mode: %s' % mode
    vda, others = _display_sources()
    if mode in ('mirror', 'extend') and not others and _physical_devices_any():
        # A previous 'only' session (or a service restart that lost the
        # disabled-bookkeeping) can leave every physical source detached.
        # Detached sources are invisible to both EnumDisplayDevices(ATTACHED)
        # and QDC_ONLY_ACTIVE_PATHS, so the path flips below could never bring
        # them back and mirror/extend would silently behave like 'only'.
        # Re-attach via the EXTEND topology preset first (measured live: the
        # CCD preset works where a CDS re-attach is rejected by the driver).
        if not _set_topology_preset(SDC_TOPOLOGY_EXTEND):
            return False, 'physical re-attach (EXTEND) failed'
        vda, others = _display_sources(refresh=True)
    reattach = set(reattach_names or ()) & set(_vda_devices_any())
    if mode in ('only', 'extend') and not vda:
        # A mirror session detaches the VDA via CDS, which drops it from the
        # active-path set while the device itself is still known to the
        # session - re-activate it with the EXTERNAL projection (the only CCD
        # preset that activates a SudoVDA target here) before flipping paths.
        if not reattach:
            return False, 'virtual display not attached'
        r = _user32.SetDisplayConfig(0, None, 0, None, SDC_TOPOLOGY_EXTERNAL | SDC_APPLY)
        if r != 0:
            return False, 'SetDisplayConfig(EXTERNAL) returned %d' % r
        deadline = time.time() + 1.5
        while time.time() < deadline:
            time.sleep(0.05)
            vda, others = _display_sources(refresh=True)
            if set(vda) & reattach:
                break
        if not (set(vda) & reattach):
            return False, 'virtual display re-attach timed out'
    paths, modes = _query_display_paths()
    if not paths:
        return False, 'QueryDisplayConfig failed'
    names = [_path_source_name(p) for p in paths]
    vda_set, phys_set = set(vda), set(others)
    changed = False
    if mode == 'mirror':
        for p, name in zip(paths, names):
            if name in vda_set and (p.flags & DISPLAYCONFIG_PATH_ACTIVE):
                p.flags = (p.flags & ~DISPLAYCONFIG_PATH_ACTIVE) | DISPLAYCONFIG_PATH_UPDATE
                changed = True
    elif mode == 'only':
        keep = (vda_set & reattach) if reattach else vda_set
        for p, name in zip(paths, names):
            if name in phys_set and (p.flags & DISPLAYCONFIG_PATH_ACTIVE):
                p.flags = (p.flags & ~DISPLAYCONFIG_PATH_ACTIVE) | DISPLAYCONFIG_PATH_UPDATE
                changed = True
            elif (name in vda_set and (p.flags & DISPLAYCONFIG_PATH_ACTIVE)
                  and name not in keep):
                # orphan virtual display dragged in by the EXTERNAL re-attach
                p.flags = (p.flags & ~DISPLAYCONFIG_PATH_ACTIVE) | DISPLAYCONFIG_PATH_UPDATE
                changed = True
            elif (name in keep and not (p.flags & DISPLAYCONFIG_PATH_ACTIVE)
                  and p.targetInfo.targetAvailable):
                p.flags |= DISPLAYCONFIG_PATH_ACTIVE | DISPLAYCONFIG_PATH_UPDATE
                changed = True
    else:
        wanted = vda_set | phys_set
        for p, name in zip(paths, names):
            if (name in wanted and not (p.flags & DISPLAYCONFIG_PATH_ACTIVE)
                    and p.targetInfo.targetAvailable):
                p.flags |= DISPLAYCONFIG_PATH_ACTIVE | DISPLAYCONFIG_PATH_UPDATE
                changed = True
        right = 0
        for p, name in zip(paths, names):
            if name in phys_set and (p.flags & DISPLAYCONFIG_PATH_ACTIVE):
                sm = _source_mode_of(p, modes)
                if sm is not None:
                    right = max(right, sm.positionX + int(sm.width))
        for p, name in zip(paths, names):
            if name in vda_set and (p.flags & DISPLAYCONFIG_PATH_ACTIVE):
                sm = _source_mode_of(p, modes)
                if sm is not None and sm.positionX != right:
                    sm.positionX = right
                    sm.positionY = 0
                    p.flags |= DISPLAYCONFIG_PATH_UPDATE
                    changed = True
    if not changed:
        return True, 'topology already matches %s' % mode
    flags = SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_APPLY
    p_buf = ctypes.cast(paths, ctypes.c_void_p) if len(paths) else None
    m_buf = ctypes.cast(modes, ctypes.c_void_p) if len(modes) else None
    r = _user32.SetDisplayConfig(len(paths), p_buf, len(modes), m_buf, flags)
    if r != 0:
        return False, 'SetDisplayConfig returned %d' % r
    return True, 'applied %s topology' % mode


def _devmode_of(device_name, enum_index=ENUM_CURRENT_SETTINGS):
    dm = DEVMODEW()
    dm.dmSize = ctypes.sizeof(DEVMODEW)
    dm.dmSpecVersion = 0x0400  # DM_SPECVERSION - required, plain EnumDisplaySettingsW
    if not _user32.EnumDisplaySettingsExW(device_name, enum_index, ctypes.byref(dm), 0):
        return None
    return dm


def _cds_batch(changes):
    """changes: list of (device_name, devmode_or_None). Writes each with
    CDS_UPDATEREGISTRY|CDS_NORESET, then applies everything in one call.
    Returns True only if every step succeeded."""
    ok = True
    for name, dm in changes:
        r = _user32.ChangeDisplaySettingsExW(name, ctypes.byref(dm) if dm else None, None,
                                             CDS_UPDATEREGISTRY | CDS_NORESET, None)
        if r != DISP_CHANGE_SUCCESSFUL:
            log_warn('display exclusive: ChangeDisplaySettingsEx(%s) returned %d' % (name, r))
            ok = False
    r = _user32.ChangeDisplaySettingsExW(None, None, None, 0, None)
    if r != DISP_CHANGE_SUCCESSFUL:
        log_warn('display exclusive: apply call returned %d' % r)
        ok = False
    return ok


def _exclusive_enabled(cfg):
    if 'display_exclusive' in cfg:
        return bool(cfg.get('display_exclusive'))
    return cfg.get('default_display_mode') == 'only'


def _display_mode(cfg):
    """Active topology mode: 'mirror' | 'only' | 'extend'."""
    mode = str(cfg.get('default_display_mode', '')).strip().lower()
    if mode not in ('mirror', 'only', 'extend'):
        mode = 'only' if _exclusive_enabled(cfg) else 'mirror'
    return mode


# ---- streaming cursor hiding -------------------------------------------------
# Moonlight clients show the host cursor the server sends. When a client is
# streaming the Console session, games and remote-desktop use often need the
# Windows pointer out of the captured frame. SetSystemCursor is session-scoped
# (the display worker runs on WinSta0\Default in that session), so every shape
# is replaced with a fully transparent 32x32 monochrome cursor. The originals
# are snapshotted first and copied back on disconnect, so the user's cursor
# scheme survives even though the worker runs as SYSTEM.

_SPI_SETCURSORS = 0x0057
_OCR_CURSOR_IDS = (
    32512,  # normal arrow
    32513,  # I-beam
    32514,  # wait
    32515,  # cross
    32516,  # up arrow
    32640,  # size
    32641,  # icon
    32642,  # size NWSE
    32643,  # size NESW
    32644,  # size WE
    32645,  # size NS
    32646,  # size all
    32648,  # no
    32649,  # hand
    32650,  # app starting
    32651,  # help
    32671,  # pin
)
_user32.SetSystemCursor.restype = wintypes.BOOL
_user32.SetSystemCursor.argtypes = [wintypes.HANDLE, wintypes.DWORD]
_user32.LoadCursorW.restype = wintypes.HANDLE
_user32.LoadCursorW.argtypes = [wintypes.HANDLE, ctypes.c_void_p]
_user32.CopyIcon.restype = wintypes.HANDLE
_user32.CopyIcon.argtypes = [wintypes.HANDLE]
_user32.CreateCursor.restype = wintypes.HANDLE
_user32.CreateCursor.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_int,
                                 ctypes.c_int, ctypes.c_int,
                                 ctypes.c_void_p, ctypes.c_void_p]
_user32.DestroyCursor.argtypes = [wintypes.HANDLE]
_user32.SystemParametersInfoW.restype = wintypes.BOOL
_user32.SystemParametersInfoW.argtypes = [wintypes.UINT, wintypes.UINT,
                                          ctypes.c_void_p, wintypes.UINT]


def _blank_cursor():
    """32x32 monochrome cursor: AND mask keeps the screen, XOR mask paints
    nothing, so the drawn pointer is invisible for every hotspot."""
    and_plane = ctypes.create_string_buffer(b'\xff' * 128, 128)
    xor_plane = ctypes.create_string_buffer(b'\x00' * 128, 128)
    return _user32.CreateCursor(None, 0, 0, 32, 32, and_plane, xor_plane)


class SystemCursorHider:
    def __init__(self):
        self._saved = {}   # cursor id -> original handle copy
        self._hidden = False
        self._marker = os.path.join(SVC_DIR, 'cursor-hidden.marker')

    def recover_leftover(self):
        """A killed worker cannot restore handles (they die with the process).
        The marker tells a fresh worker to reload the scheme first, otherwise
        it would snapshot the blank cursors and restore those forever."""
        try:
            if os.path.exists(self._marker):
                _user32.SystemParametersInfoW(_SPI_SETCURSORS, 0, None, 0)
                os.remove(self._marker)
                log_warn('cursor hide: restored cursors left hidden by a stopped worker')
        except OSError:
            pass

    def hide(self):
        if self._hidden:
            return
        saved = {}
        changed = 0
        for cid in _OCR_CURSOR_IDS:
            src = _user32.LoadCursorW(None, ctypes.c_void_p(cid))
            if not src:
                continue
            orig = _user32.CopyIcon(src)
            if not orig:
                continue
            blank = _blank_cursor()
            if not blank:
                _user32.DestroyCursor(orig)
                continue
            if _user32.SetSystemCursor(blank, cid):
                saved[cid] = orig
                changed += 1
            else:
                _user32.DestroyCursor(blank)
                _user32.DestroyCursor(orig)
        if not changed:
            log_warn('cursor hide: SetSystemCursor failed err=%d'
                     % ctypes.get_last_error())
            return
        self._saved = saved
        self._hidden = True
        try:
            with open(self._marker, 'w', encoding='utf-8') as f:
                f.write(str(os.getpid()))
        except OSError:
            pass
        log('cursor hide: system cursors hidden (%d shapes)' % changed)

    def restore(self):
        if not self._hidden:
            return
        for cid, orig in self._saved.items():
            copy = _user32.CopyIcon(orig)
            if copy:
                _user32.SetSystemCursor(copy, cid)
            _user32.DestroyCursor(orig)
        self._saved.clear()
        self._hidden = False
        try:
            os.remove(self._marker)
        except OSError:
            pass
        log('cursor hide: system cursors restored')

    def sync(self, enabled, streaming):
        if enabled and streaming:
            self.hide()
        else:
            self.restore()


class DisplayExclusive:
    """Runs the configured topology per display mode and cleans up leftovers.

    Streaming  (client connected, per Apollo log markers):
      only   -> detach every physical source (virtual display solo)
      extend -> keep physical sources, move virtual display to the right of the
                rightmost physical source (and make it non-primary safe)
      mirror -> keep physical sources, detach leftover virtual displays
    Idle (no client):
      every mode -> detach leftover virtual displays and reattach any physical
                    sources this worker previously detached (task: "Console 会话
                    没有链接客户端时关闭虚拟屏")."""

    def __init__(self):
        self.disabled = {}          # device name -> saved mode dict
        self.detached_vda = set()   # VDA sources this worker detached (mirror)
        self._file = os.path.join(SVC_DIR, 'display-disabled.json')
        self._armed_since = None    # "needs a topology change" first seen at
        self._restore_since = None  # "needs restore" first seen at
        self._last_action = 0.0
        self._last_state = ''
        self._last_mode = ''        # display mode the state was seen under
        self._log_off = 0           # console log tail offset
        self._streaming = False     # a Moonlight client is actually streaming
        self._vda_created = False   # Apollo logged "Virtual Display created at"
        self._vda_failed = False    # Apollo logged the creation-failed warning
        self._last_mode_req_id = None
        self._last_fp_detail = None
        self._fp_until = 0.0      # proactive EXTERNAL push window for the VDA
                                  # naming race, closed after this time
        self._fp_fail = 0         # consecutive EXTERNAL push failures (backoff)
        self._fp_backoff_until = 0.0
        self._pending_restore = False  # stream just ended: restore physical now
        self._restore_attempts = 0
        self._next_restore_try = 0.0
        self._load_disabled()

    def apply_mode_request(self):
        try:
            with open(_MODE_REQ, 'r', encoding='utf-8') as f:
                req = json.load(f)
        except (OSError, ValueError):
            return
        rid = str(req.get('id', ''))
        if not rid or rid == self._last_mode_req_id:
            return
        self._last_mode_req_id = rid
        mode = str(req.get('mode', '')).strip().lower()
        if mode not in ('mirror', 'only', 'extend'):
            mode = 'mirror'
        if mode == self._last_mode:
            ok, detail = True, 'already %s' % mode
        else:
            if self.disabled and self._reattach_via_topology():
                self.disabled.clear()
                self._save_disabled()
                log('display mode: reattached physical displays before %s switch' % mode)
            ok, detail = _apply_display_topology(mode, reattach_names=self.detached_vda)
        if ok:
            if mode in ('only', 'extend'):
                self.detached_vda.clear()
            self._last_mode = mode
            self._last_state = ''
            self._armed_since = None
            self._restore_since = None
        line = 'display mode request %s: %s (%s)' % (mode, 'ok' if ok else 'failed', detail)
        if ok:
            log(line)
        else:
            log_warn(line)
        _write_json_atomic(_MODE_RES, {
            'id': rid, 'mode': mode, 'ok': ok, 'detail': detail, 'ts': time.time(),
        })
        if ok:
            # Consume the request: the dedupe id only lives in this worker, so a
            # stale request file would be re-applied by every future worker
            # restart - each re-apply is a full topology change the user sees as
            # a screen flash (and it can hit a Game session mid-reconnect).
            try:
                os.remove(_MODE_REQ)
            except OSError:
                pass

    def _console_log_path(self, cfg):
        conf = cfg.get('console_conf_path')
        if not conf:
            return None
        try:
            with open(conf, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    name, sep, value = line.partition('=')
                    if sep and name.strip() == 'log_path':
                        path = value.strip().strip('"\'')
                        if path and not os.path.isabs(path):
                            path = os.path.join(os.path.dirname(conf), path)
                        return path
        except Exception:
            pass
        return None

    def _read_stream_state(self, cfg):
        """Gate exclusivity on Apollo's log markers so a lingering virtual
        display (probe leftovers, stale driver state) never turns off the
        physical monitor while no client is streaming."""
        path = self._console_log_path(cfg)
        if not path:
            self._streaming = False
            return
        was_streaming = self._streaming
        try:
            size = os.path.getsize(path)
            if size < self._log_off:
                # log truncated/recreated (Apollo restart)
                self._log_off = 0
                self._streaming = False
            if size == self._log_off:
                return
            with open(path, 'rb') as f:
                if self._log_off:
                    f.seek(self._log_off)
                data = f.read()
                self._log_off = f.tell()
        except Exception:
            return
        for line in data.splitlines():
            if b'CLIENT CONNECTED' in line or b'New streaming session started' in line:
                self._streaming = True
                self._fp_until = time.time() + FP_RACE_WINDOW
            elif (b'CLIENT DISCONNECTED' in line
                  or b'Session pausing for app' in line
                  or b'Virtual Display removed successfully' in line):
                self._streaming = False
            elif b'Display mode for client' in line and b'requested' in line:
                self._streaming = True  # session setup starts before CONNECTED
                self._fp_until = time.time() + FP_RACE_WINDOW
            if b'Virtual Display created at' in line:
                self._vda_created = True
                self._vda_failed = False
            elif b'Virtual Display creation failed' in line:
                self._vda_failed = True
                self._vda_created = False
            elif b'Virtual Display removed successfully' in line:
                self._vda_created = False
                self._vda_failed = False
        if self._streaming and not was_streaming:
            self._last_fp_detail = None          # new race: log progress again
            self._fp_fail = 0
            self._fp_backoff_until = 0.0
        elif was_streaming and not self._streaming:
            # the stream just ended: switch the physical display back at once
            # and keep retrying until it succeeds (never stay on a dead 0-source
            # topology). A new stream start cancels this in poll().
            self._pending_restore = True
            self._restore_attempts = 0
            self._next_restore_try = 0.0
            log('display: stream ended -> restoring physical display')

    def _state(self, vda, others):
        if vda and others:
            # only detach while a client is really streaming
            return 'mixed-stream' if self._streaming else 'mixed-idle'
        if vda:
            return 'vda-only' if self._streaming else 'vda-idle'
        if others:
            return 'phys-only' if self._streaming else 'idle'
        return 'needs-restore' if self.disabled else 'none-active'

    def poll(self, cfg):
        mode = _display_mode(cfg)
        self._read_stream_state(cfg)
        vda, others = _display_sources()
        state = self._state(vda, others)
        # Feature: stream just ended -> switch the physical display back now and
        # retry until it succeeds. A new stream start cancels the pending work.
        if self._streaming and self._pending_restore:
            self._pending_restore = False
            self._restore_attempts = 0
            self._next_restore_try = 0.0
        if self._pending_restore:
            now = time.time()
            if now >= self._next_restore_try:
                if self._try_restore_physical(vda):
                    self._pending_restore = False
                    self._restore_attempts = 0
                    self._last_state = ''  # log the settled state next tick
                else:
                    self._restore_attempts += 1
                    delay = min(4.0, 2.0 ** min(self._restore_attempts - 1, 2))
                    self._next_restore_try = now + delay
                    log_warn('display restore: attempt %d failed, retry in %.0fs'
                             % (self._restore_attempts, delay))
            return
        if state != self._last_state or mode != self._last_mode:
            log('display %s: state %s -> %s (vda=%s others=%d disabled=%d)' % (
                mode, self._last_state or 'init', state, vda, len(others), len(self.disabled)))
            self._last_state = state
            self._last_mode = mode
            self._armed_since = None
            self._restore_since = None
            # The VDA naming race is over once the state settles; fall back to
            # the slow cadence when the stream is over.
            self._last_action = time.time()
            return
        now = time.time()
        streaming = state.endswith('-stream') or state == 'vda-only'
        cool = ENUM_FAST_SECS if streaming and self._vda_race_active(vda) else ENUM_RECHECK_SECS
        if now - self._last_action < cool:
            return
        # ---- idle cleanup: virtual display must be OFF when no client ----
        # The physical displays are re-enabled (SetDisplayConfig) BEFORE the
        # virtual ones detach: Windows refuses a topology with zero active
        # sources, so detaching the last display first fails forever.
        if state in ('vda-idle', 'mixed-idle'):
            if (not others and not self.disabled
                    and not _physical_devices_any()):
                pass  # truly headless: no physical display exists, keep the VDA
            elif self._armed_since is None:
                self._armed_since = now
            elif now - self._armed_since >= ENUM_STABLE_SECS:
                self._last_action = now
                self._idle_cleanup(vda)
        elif state == 'needs-restore':
            if self._restore_since is None:
                self._restore_since = now
            elif now - self._restore_since >= ENUM_STABLE_SECS:
                self._last_action = now
                self._restore()
        elif state == 'phys-only' and self.disabled:
            # previously detached displays are all back (user action etc.) -
            # clear stale bookkeeping so it cannot fight future applies
            self._last_action = now
            self.disabled.clear()
            self._save_disabled()
            log('display %s: cleared stale disabled bookkeeping' % mode)
        # ---- streaming: apply the configured topology ----
        if state == 'mixed-stream':
            if mode == 'only':
                if self._armed_since is None:
                    self._armed_since = now
                elif now - self._armed_since >= ENUM_FAST_SECS:
                    self._last_action = now
                    self._disable_others(vda, others)
            elif mode == 'extend':
                if not self._extend_placed(vda, others):
                    if self._armed_since is None:
                        self._armed_since = now
                    elif now - self._armed_since >= ENUM_FAST_SECS:
                        self._last_action = now
                        self._place_extend(vda, others)
                else:
                    self._armed_since = None
            else:  # mirror: stream captures the physical primary - drop the VDA
                # only when physical displays are actually present; a lone VDA
                # means the stream itself runs on it - detaching would kill it
                if others and self._armed_since is None:
                    self._armed_since = now
                elif others and now - self._armed_since >= ENUM_STABLE_SECS:
                    self._last_action = now
                    self.detached_vda = set(self._detach_vda(vda, 'mirror streaming'))
        elif state == 'vda-only' and mode != 'only':
            # physical displays detached by a previous 'only' session (or a
            # restart that lost the bookkeeping) must come back for both
            # mirror and extend while a stream runs on the lone VDA
            if self._restore_since is None:
                self._restore_since = now
            elif now - self._restore_since >= ENUM_STABLE_SECS:
                self._last_action = now
                self._restore()

    def _vda_race_active(self, vda):
        """True while Apollo's ~1.3s name-wait window may still be open: the VDA
        just appeared and we have not yet seen the created/failed log marker."""
        return bool(vda) and self._streaming and not (self._vda_created or self._vda_failed)

    def _extend_placed(self, vda, others):
        """True when every active VDA source sits right of all physical ones."""
        right = 0
        for name in others:
            dm = _devmode_of(name)
            if dm is None or not (dm.dmPelsWidth or dm.dmPelsHeight):
                continue
            right = max(right, dm.dmPositionX + dm.dmPelsWidth)
        for name in vda:
            dm = _devmode_of(name)
            if dm is None or not (dm.dmPelsWidth or dm.dmPelsHeight):
                continue
            if dm.dmPositionX < right:
                return False
        return True

    def _idle_cleanup(self, vda):
        """Idle (no streaming client): re-enable the physical displays and
        detach every active SudoVDA source. The re-enable MUST run before the
        detach: Windows refuses a topology with zero active sources, so the
        physical display has to be back before the last virtual one leaves."""
        if not self._reattach_via_topology():
            log_warn('display: idle cleanup - topology restore failed, will retry')
            return
        self.disabled.clear()
        self.detached_vda.clear()
        self._save_disabled()
        changes = []
        for name in vda:
            dm = _devmode_of(name)
            if dm is None or not (dm.dmPelsWidth or dm.dmPelsHeight):
                continue  # already detached, keep silent
            detach = DEVMODEW()
            detach.dmSize = ctypes.sizeof(DEVMODEW)
            detach.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT
            detach.dmPelsWidth = 0
            detach.dmPelsHeight = 0
            changes.append((name, detach))
        if not changes:
            log('display: idle cleanup - physical displays re-enabled, no virtual displays left')
            return
        if _cds_batch(changes):
            log('display: idle cleanup - re-enabled physical displays, detached %d virtual display(s)'
                % len(changes))
        else:
            # Do NOT collapse the topology with SDC_TOPOLOGY_INTERNAL here: that
            # is machine-wide and breaks the Game session's RDP display stack
            # (2026-11-24 incident, docs/plan.md 2.4). Physical displays are
            # already back; retry the VDA detach on the next cadence instead.
            log_warn('display: idle cleanup - physical restored, VDA detach refused, will retry')

    def _detach_vda(self, vda, why):
        changes = []
        for name in vda:
            dm = _devmode_of(name)
            if dm is None:
                continue
            if not (dm.dmPelsWidth or dm.dmPelsHeight):
                continue  # already detached, keep silent
            detach = DEVMODEW()
            detach.dmSize = ctypes.sizeof(DEVMODEW)
            detach.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT
            detach.dmPelsWidth = 0
            detach.dmPelsHeight = 0
            changes.append((name, detach))
        if not changes:
            return []
        if _cds_batch(changes):
            log('display: %s - detached %d virtual display(s)' % (why, len(changes)))
        else:
            log_warn('display: %s - detach incomplete' % why)
        return [c[0] for c in changes]

    def _place_extend(self, vda, others):
        """Extend mode: put the virtual display right of the rightmost physical
        source. Only acts when the VDA currently overlaps a physical source."""
        try:
            right = 0
            for name in others:
                dm = _devmode_of(name)
                if dm is None or not (dm.dmPelsWidth or dm.dmPelsHeight):
                    continue
                right = max(right, dm.dmPositionX + dm.dmPelsWidth)
            overlaps = False
            vda_dm = None
            for name in vda:
                dm = _devmode_of(name)
                if dm is None or not (dm.dmPelsWidth or dm.dmPelsHeight):
                    continue
                vda_dm = dm
                if (dm.dmPositionX < right and dm.dmPositionX + dm.dmPelsWidth > 0):
                    overlaps = True
            if vda_dm is None:
                return
            if not overlaps and vda_dm.dmPositionX >= right:
                return  # already placed correctly
            dm = DEVMODEW()
            dm.dmSize = ctypes.sizeof(DEVMODEW)
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT
            dm.dmPositionX = right
            dm.dmPositionY = 0
            dm.dmPelsWidth = vda_dm.dmPelsWidth
            dm.dmPelsHeight = vda_dm.dmPelsHeight
            if _cds_batch([(vda[0], dm)]):
                log('display extend: placed %s at x=%d (right of %d physical source(s))'
                    % (vda[0], right, len(others)))
        except Exception as e:
            log_warn('display extend: placement error %r' % e)

    def _set_primary(self, name):
        """Make `name` the primary display via ChangeDisplaySettingsEx
        (CDS_SET_PRIMARY), shifting the other sources so the new primary sits
        at (0,0). Used as the extend-mode rescue when Apollo lost the VDA
        naming race: capture reinit then lands on the primary virtual screen."""
        target = _devmode_of(name)
        if target is None:
            return False
        ox, oy = target.dmPositionX, target.dmPositionY
        _, others = _display_sources()
        changes = []
        for other in others:
            if other == name:
                continue
            dm = _devmode_of(other)
            if dm is None or not (dm.dmPelsWidth or dm.dmPelsHeight):
                continue
            shifted = DEVMODEW()
            shifted.dmSize = ctypes.sizeof(DEVMODEW)
            shifted.dmFields = DM_POSITION
            shifted.dmPositionX = dm.dmPositionX - ox
            shifted.dmPositionY = dm.dmPositionY - oy
            changes.append((other, shifted))
        primary = DEVMODEW()
        primary.dmSize = ctypes.sizeof(DEVMODEW)
        primary.dmFields = DM_POSITION
        primary.dmPositionX = 0
        primary.dmPositionY = 0
        ok = True
        for other, dm in changes:
            r = _user32.ChangeDisplaySettingsExW(other, ctypes.byref(dm), None,
                                                 CDS_UPDATEREGISTRY | CDS_NORESET, None)
            if r != DISP_CHANGE_SUCCESSFUL:
                log_warn('display extend rescue: shift %s returned %d' % (other, r))
                ok = False
        r = _user32.ChangeDisplaySettingsExW(name, ctypes.byref(primary), None,
                                             CDS_UPDATEREGISTRY | CDS_NORESET | CDS_SET_PRIMARY, None)
        if r != DISP_CHANGE_SUCCESSFUL:
            log_warn('display extend rescue: set primary %s returned %d' % (name, r))
            ok = False
        r = _user32.ChangeDisplaySettingsExW(None, None, None, 0, None)
        if r != DISP_CHANGE_SUCCESSFUL:
            log_warn('display extend rescue: apply returned %d' % r)
            ok = False
        if ok:
            log('display extend rescue: %s is now primary at (0,0)' % name)
        return ok

    def _fast_pass(self, mode, vda, others):
        """Sub-second pass racing Apollo's virtual-display naming window
        (~1.3s after the client requests a mode). Windows never auto-activates
        a freshly added SudoVDA monitor while a physical display is active
        (measured live: the target stays out of the active path set until a
        topology apply, and it does not even show up as GDI-attached before
        that), so fire the EXTERNAL projection on every poll while the race is
        open - the moment the driver registers the new target it joins the
        active set and Apollo's name lookup succeeds. After Apollo reports a
        failed naming attempt, rescue the capture target instead:
        only -> detach physicals (capture reinit lands on the lone VDA),
        extend -> make the VDA primary. Mirror never activates the VDA."""
        if not self._streaming or mode == 'mirror' or self._vda_created:
            return False
        if not self._vda_failed:
            if vda:
                ok, detail = _activate_vda_external()
                self._fp_fail = 0
            elif time.time() < self._fp_until:
                # VDA not GDI-visible yet: push the preset anyway so the
                # target activates the instant the driver registers it.
                # Bounded by the race window - pushing EXTERNAL with no
                # virtual display at all would switch the physical panel off.
                if time.time() < self._fp_backoff_until:
                    return True  # in backoff - retry on the next cadence tick
                ok = _set_topology_preset(SDC_TOPOLOGY_EXTERNAL)
                if ok:
                    self._fp_fail = 0
                    detail = 'fired EXTERNAL topology (proactive)'
                else:
                    self._fp_fail += 1
                    n = self._fp_fail
                    if n == 1 or n & (n - 1) == 0 or n % 16 == 0:
                        log_warn('display fast-pass: EXTERNAL push failed (%d), '
                                 'backing off up to %.0fs'
                                 % (n, _FP_BACKOFF_MAX))
                    self._fp_backoff_until = time.time() + min(
                        _FP_BACKOFF_MAX, _FP_BACKOFF_BASE * (2 ** min(n - 1, 4)))
                    detail = 'EXTERNAL push failed (%d)' % n
            else:
                return False  # race window over, no VDA - Apollo streams physical
            if detail != self._last_fp_detail:
                self._last_fp_detail = detail
                (log if ok else log_warn)('display fast-pass: %s' % detail)
            return True  # race still open: keep polling fast
        if not vda:
            return False  # Apollo gave up and no VDA exists - it streams the physical
        # Apollo already gave up on the name - rescue the capture target once.
        if mode == 'only':
            log_warn('display fast-pass: Apollo lost the naming race, detaching '
                     'physical displays so capture lands on the VDA')
            self._last_action = time.time()
            self._disable_others(vda, others)
            return False
        log_warn('display fast-pass: Apollo lost the naming race, forcing %s primary '
                 'so capture lands on the VDA' % vda[0])
        self._last_action = time.time()
        self._set_primary(vda[0])
        return False

    def _disable_others(self, vda, others):
        changes = []
        for name in others:
            dm = _devmode_of(name)
            if dm is None:
                log_warn('display exclusive: no current mode for %s, skipping' % name)
                continue
            if not (dm.dmPelsWidth or dm.dmPelsHeight):
                continue  # already detached, keep silent
            self.disabled[name] = {
                'x': dm.dmPositionX, 'y': dm.dmPositionY,
                'w': dm.dmPelsWidth, 'h': dm.dmPelsHeight,
                'bpp': dm.dmBitsPerPel, 'freq': dm.dmDisplayFrequency,
                'orient': dm.dmDisplayOrientation,
            }
            detach = DEVMODEW()
            detach.dmSize = ctypes.sizeof(DEVMODEW)
            detach.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT
            detach.dmPelsWidth = 0
            detach.dmPelsHeight = 0
            changes.append((name, detach))
        if not changes:
            return
        if _cds_batch(changes):
            self._save_disabled()
            log('display exclusive: streaming on SudoVDA - detached %d display(s)' % len(changes))
        else:
            self.disabled = {k: v for k, v in self.disabled.items()
                             if k not in [c[0] for c in changes]}
            log_warn('display exclusive: detach incomplete, state not persisted')

    def _reattach_via_topology(self):
        """Rebuild the topology via CCD. ChangeDisplaySettingsEx cannot re-attach
        a fully detached source on this machine (the AMD driver rejects it with
        DISP_CHANGE_FAILED - observed live), but the CCD topology switch does.
        Always EXTEND (re-enable all available sources). SDC_TOPOLOGY_INTERNAL is
        deliberately not used: it is machine-wide and breaks the Game session's
        RDP display stack (2026-11-24 incident, docs/plan.md 2.4)."""
        return _set_topology_preset(SDC_TOPOLOGY_EXTEND)

    def _try_restore_physical(self, vda):
        """Stream just ended: put the physical display back. EXTEND re-attaches
        every available source (CDS cannot re-attach a detached source here),
        then SudoVDA sources are CDS-detached. Returns True once the physical
        display is back; callers retry with backoff while it returns False.
        Never collapses the topology to INTERNAL (see 2.4)."""
        if not _physical_devices_any():
            return True  # truly headless: nothing to switch back to
        if not self._reattach_via_topology():
            return False
        self.disabled.clear()
        self.detached_vda.clear()
        self._save_disabled()
        changes = []
        for name in vda:
            dm = _devmode_of(name)
            if dm is None or not (dm.dmPelsWidth or dm.dmPelsHeight):
                continue
            detach = DEVMODEW()
            detach.dmSize = ctypes.sizeof(DEVMODEW)
            detach.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT
            detach.dmPelsWidth = 0
            detach.dmPelsHeight = 0
            changes.append((name, detach))
        if changes:
            _cds_batch(changes)  # best effort; physical is already back
        log('display: stream ended - physical display restored (extend topology%s)'
            % (', detached %d VDA' % len(changes) if changes else ''))
        return True

    def _restore(self):
        if not self.disabled:
            # Bookkeeping lost (e.g. service restart): the physical displays
            # may still be detached - bring them back via the topology preset.
            if _set_topology_preset(SDC_TOPOLOGY_EXTEND):
                log('display: stream ended - physical displays re-enabled (extend topology)')
            return
        if self._reattach_via_topology():
            self.disabled.clear()
            self._save_disabled()
            log('display: stream ended - physical displays re-enabled (extend topology)')
        else:
            log_warn('display: reattach incomplete, will retry')

    def _load_disabled(self):
        try:
            with open(self._file, 'r', encoding='utf-8') as f:
                data = json.load(f)
            if isinstance(data, dict):
                self.disabled = {str(k): v for k, v in data.items() if isinstance(v, dict)}
        except Exception:
            pass

    def _save_disabled(self):
        try:
            with open(self._file, 'w', encoding='utf-8') as f:
                json.dump(self.disabled, f)
        except Exception as e:
            log_warn('display exclusive: cannot save disabled list: %r' % e)


_dx = DisplayExclusive()


_WORKER_HB = os.path.join(SVC_DIR, 'display-worker.json')
_WORKER_STOP = os.path.join(SVC_DIR, 'display-worker.stop')
_MODE_REQ = os.path.join(SVC_DIR, 'display-mode-request.json')
_MODE_RES = os.path.join(SVC_DIR, 'display-mode-result.json')


def _write_json_atomic(path, data):
    tmp = path + '.tmp'
    try:
        with open(tmp, 'w', encoding='utf-8') as f:
            json.dump(data, f)
        os.replace(tmp, path)
    except Exception as e:
        log_warn('cannot write %s: %r' % (os.path.basename(path), e))


def _write_worker_hb():
    # silent on purpose: transient contention with a dying/overlapping worker
    # must not spam the log every 120ms
    tmp = _WORKER_HB + '.tmp'
    try:
        with open(tmp, 'w', encoding='utf-8') as f:
            json.dump({'pid': os.getpid(), 'ts': time.time()}, f)
        os.replace(tmp, _WORKER_HB)
    except Exception:
        try:
            os.remove(tmp)
        except Exception:
            pass


def _clear_worker_stop():
    try:
        if os.path.exists(_WORKER_STOP):
            os.remove(_WORKER_STOP)
    except Exception:
        pass


def _signal_worker_stop():
    try:
        with open(_WORKER_STOP, 'w', encoding='utf-8') as f:
            f.write('service stop\n')
    except Exception:
        pass


# ----------------------------------------------------------------------------
# optional Win-key substitution (console desktop only)
# The console display worker lives on the interactive desktop, so a low-level
# keyboard hook there sees the console's keyboard (hardware and the events
# Apollo injects for a Moonlight client) and forwards R-Alt / R-Alt+L to the
# system as Win / Win+L. It never touches the display topology, so it cannot
# conflict with the stream-end restore above. Both switches default off.

WH_KEYBOARD_LL = 13
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105
WM_QUIT = 0x0012
VK_RMENU = 0xA5
VK_LWIN = 0x5B
VK_L = 0x4C
VK_R = 0x52
INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002
_INJECT_TAG = 0x53444D4F  # 'SDMO': marks only our own SendInput events, so an
                          # R-Alt injected by Apollo (Moonlight) is still mapped


class _KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [('vkCode', wintypes.DWORD), ('scanCode', wintypes.DWORD),
                ('flags', wintypes.DWORD), ('time', wintypes.DWORD),
                ('dwExtraInfo', ctypes.c_size_t)]


class _KEYBDINPUT(ctypes.Structure):
    _fields_ = [('wVk', wintypes.WORD), ('wScan', wintypes.WORD),
                ('dwFlags', wintypes.DWORD), ('time', wintypes.DWORD),
                ('dwExtraInfo', ctypes.c_size_t)]


class _MOUSEINPUT(ctypes.Structure):
    _fields_ = [('dx', wintypes.LONG), ('dy', wintypes.LONG),
                ('mouseData', wintypes.DWORD), ('dwFlags', wintypes.DWORD),
                ('time', wintypes.DWORD), ('dwExtraInfo', ctypes.c_size_t)]


class _HARDWAREINPUT(ctypes.Structure):
    _fields_ = [('uMsg', wintypes.DWORD), ('wParamL', wintypes.WORD),
                ('wParamH', wintypes.WORD)]


class _INPUT_UNION(ctypes.Union):
    _fields_ = [('mi', _MOUSEINPUT), ('ki', _KEYBDINPUT), ('hi', _HARDWAREINPUT)]


class _INPUT(ctypes.Structure):
    _fields_ = [('type', wintypes.DWORD), ('u', _INPUT_UNION)]


_HOOKPROC = ctypes.WINFUNCTYPE(wintypes.LPARAM, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)
_user32.SetWindowsHookExW.restype = wintypes.HANDLE
_user32.SetWindowsHookExW.argtypes = [ctypes.c_int, _HOOKPROC, wintypes.HINSTANCE, wintypes.DWORD]
_user32.UnhookWindowsHookEx.argtypes = [wintypes.HANDLE]
_user32.CallNextHookEx.restype = wintypes.LPARAM
_user32.CallNextHookEx.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM]
_user32.GetMessageW.argtypes = [ctypes.POINTER(wintypes.MSG), wintypes.HWND,
                                wintypes.UINT, wintypes.UINT]
_user32.PostThreadMessageW.argtypes = [wintypes.DWORD, wintypes.UINT,
                                       wintypes.WPARAM, wintypes.LPARAM]
_user32.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(_INPUT), ctypes.c_int]
_user32.GetAsyncKeyState.restype = ctypes.c_int
_user32.GetAsyncKeyState.argtypes = [wintypes.DWORD]
_user32.LockWorkStation.restype = wintypes.BOOL
_kernel32.GetCurrentThreadId.restype = wintypes.DWORD


class KeyboardRemapper:
    """Detect R-Alt gestures on the console desktop and act on them directly
    (the proven R-Alt+L pattern: swallow the keys, inject one synthetic burst,
    never hold translated state between events).

    win_from_ralt    - bare R-Alt tap -> Win tap (Start menu etc.); any key
                       pressed while R-Alt is held is forwarded as a Win+key
                       chord in one SendInput batch.
    winl_from_ralt_l - R-Alt+L locks the workstation directly; R-Alt stays a
                       plain Alt key when win_from_ralt is off.
    With both on, R-Alt+L still locks explicitly. Neither option changes the
    display topology.
    """

    def __init__(self):
        self.win_from_ralt = False
        self.winl_from_ralt_l = False
        self._thread = None
        self._tid = 0
        self._ready = threading.Event()
        self._next_try = 0.0
        self._hook = None
        self._proc = None
        self._ralt = False
        self._win_down = False
        self._l_held = False
        self._chord_used = False  # a chord/lock happened during this R-Alt hold
        self._chord_keys = set()  # vk codes swallowed while translated Win is held

    def update(self, cfg):
        win = bool(cfg.get('win_from_ralt'))
        winl = bool(cfg.get('winl_from_ralt_l'))
        if win != self.win_from_ralt or winl != self.winl_from_ralt_l:
            self.win_from_ralt = win
            self.winl_from_ralt_l = winl
            self._ralt = self._win_down = self._l_held = False
            self._chord_used = False
            self._chord_keys.clear()
            log('hotkey remap: mode win=%s winl=%s' % (win, winl))
        if not (win or winl):
            self.stop()
        elif time.time() >= self._next_try and (self._thread is None or not self._thread.is_alive()):
            self._ready.clear()
            self._thread = threading.Thread(target=self._run, daemon=True, name='kb-remap')
            self._thread.start()

    def stop(self):
        t = self._thread
        if t is None:
            return
        if self._ready.wait(2):
            _user32.PostThreadMessageW(self._tid, WM_QUIT, 0, 0)
        t.join(timeout=3)
        self._thread = None
        self._ready.clear()

    def _send(self, *events):
        inputs = (_INPUT * len(events))()
        for i, (vk, down) in enumerate(events):
            inputs[i].type = INPUT_KEYBOARD
            inputs[i].u.ki.wVk = vk
            inputs[i].u.ki.dwExtraInfo = _INJECT_TAG
            if not down:
                inputs[i].u.ki.dwFlags = KEYEVENTF_KEYUP
        sent = _user32.SendInput(len(events), inputs, ctypes.sizeof(_INPUT))
        if sent != len(events):
            log_warn('hotkey remap: SendInput %d/%d failed err=%d'
                     % (sent, len(events), ctypes.get_last_error()))

    def _lock(self):
        # Win+L through the OS lock API: synthetic Win+L is not reliably honored
        # for the secure lock gesture, so guarantee it here.
        if not _user32.LockWorkStation():
            log_warn('hotkey remap: LockWorkStation failed err=%d' % ctypes.get_last_error())

    def _run(self):
        self._tid = _kernel32.GetCurrentThreadId()
        self._proc = _HOOKPROC(self._callback)
        self._hook = _user32.SetWindowsHookExW(WH_KEYBOARD_LL, self._proc, None, 0)
        self._ready.set()
        if not self._hook:
            self._next_try = time.time() + 10
            log_err('hotkey remap: SetWindowsHookEx failed err=%d' % ctypes.get_last_error())
            return
        log('hotkey remap: hook installed (win=%s winl=%s)'
            % (self.win_from_ralt, self.winl_from_ralt_l))
        msg = wintypes.MSG()
        try:
            while True:
                r = _user32.GetMessageW(ctypes.byref(msg), None, 0, 0)
                if r == 0 or r == -1:
                    break
                _user32.TranslateMessage(ctypes.byref(msg))
                _user32.DispatchMessageW(ctypes.byref(msg))
        finally:
            _user32.UnhookWindowsHookEx(self._hook)
            self._hook = None
            log('hotkey remap: hook removed')

    def _callback(self, nCode, wParam, lParam):
        if nCode < 0:
            return _user32.CallNextHookEx(self._hook, nCode, wParam, lParam)
        try:
            k = ctypes.cast(lParam, ctypes.POINTER(_KBDLLHOOKSTRUCT)).contents
            if k.dwExtraInfo != _INJECT_TAG:
                down = wParam in (WM_KEYDOWN, WM_SYSKEYDOWN)
                up = wParam in (WM_KEYUP, WM_SYSKEYUP)
                vk = k.vkCode
                if vk == VK_RMENU and (down or up):
                    self._ralt = down
                    if self.win_from_ralt:
                        if down:
                            self._win_down = True
                            self._chord_used = False
                        elif self._win_down:
                            # Bare tap -> one Win tap; a chord/lock burst already
                            # emitted its own Win pair, so send nothing here.
                            # Chord keys still held are NOT cleared: their key-up
                            # must stay visible to this hook so the injected Win
                            # gets released (release order R-Alt first, key last).
                            self._win_down = False
                            if not self._chord_used:
                                self._send((VK_RMENU, False),
                                           (VK_LWIN, True), (VK_LWIN, False))
                        return 1
                    if not self._ralt:
                        self._l_held = False
                if vk == VK_L and (down or up):
                    # R-Alt+L always locks, whichever substitution is on.
                    if self._ralt and (self.win_from_ralt or self.winl_from_ralt_l):
                        if down and not self._l_held:
                            self._l_held = True
                            self._chord_used = True
                            self._lock()
                        elif up:
                            self._l_held = False
                        return 1
                if self.win_from_ralt and (self._win_down or vk in self._chord_keys) and \
                        vk not in (VK_RMENU, VK_LWIN, VK_L, 0x00):
                    # R-Alt held: forward each physical key as one synthetic
                    # Win+key burst (down pair on press, up on release) so the
                    # key binds to the modifier without holding injected state.
                    # vk in _chord_keys (R-Alt already up) keeps release events
                    # inside the hook so the pending injected Win-up is sent.
                    if down and self._win_down and vk not in self._chord_keys:
                        self._chord_keys.add(vk)
                        self._chord_used = True
                        # The swallowed physical R-Alt down still leaves the
                        # async Alt bit set; release it inside the burst or the
                        # chord arrives as Win+Alt+key (Win+Alt+R = Game Bar).
                        self._send((VK_RMENU, False), (VK_LWIN, True), (vk, True))
                    elif up and vk in self._chord_keys:
                        self._chord_keys.discard(vk)
                        self._send((vk, False), (VK_LWIN, False))
                    return 1
                if vk == VK_L and (down or up):
                    if self.winl_from_ralt_l and not self.win_from_ralt and self._ralt:
                        if down and not self._l_held:
                            self._l_held = True
                            self._send((VK_LWIN, True), (VK_L, True),
                                       (VK_L, False), (VK_LWIN, False))
                            self._lock()
                        elif up:
                            self._l_held = False
                        return 1
        except Exception as e:
            log_warn('hotkey remap: hook callback error %r' % e)
        return _user32.CallNextHookEx(self._hook, nCode, wParam, lParam)


_kb_remap = KeyboardRemapper()


def run_display_worker():
    """Display pulse for the Console session.

    EnumDisplayDevices/ChangeDisplaySettingsEx operate on the calling session's
    desktop, so the poller cannot live in the session-0 service (it sees no
    attached displays there and never acts). The service launches this worker
    into the console session with its SYSTEM token; the worker polls, applies
    exclusivity and keeps a heartbeat the service checks every tick.
    Cadence: ~120ms while a VDA naming race may be open (sub-second topology
    work), 1s otherwise.
    Single instance: a Global mutex makes any duplicate launch exit at once,
    so a false-negative heartbeat on the service side can never stack workers."""
    try:
        _apply_log_cfg(load_config())
    except Exception:
        pass
    acquired, mut = _worker_mutex_acquire()
    if not acquired:
        log('display worker: another worker already active, exiting pid=%d'
            % os.getpid())
        return
    log('display worker: started pid=%d' % os.getpid())
    hider = SystemCursorHider()
    hider.recover_leftover()
    atexit.register(hider.restore)
    try:
        while True:
            _write_worker_hb()
            if os.path.exists(_WORKER_STOP):
                log('display worker: stop signaled, exiting')
                break
            try:
                cfg = load_config()
                _apply_log_cfg(cfg)
                # stream state must track even on fast-pass ticks (the fast
                # pass returns before poll(), and cursor hiding keys off it)
                _dx._read_stream_state(cfg)
                _kb_remap.update(cfg)
                _dx.apply_mode_request()
                mode = _display_mode(cfg)
                if mode == 'mirror' or not _dx._fast_pass(mode, *_display_sources()):
                    _dx.poll(cfg)
                hider.sync(bool(cfg.get('hide_cursor_on_stream')), _dx._streaming)
            except Exception as e:
                log_err('display worker poll error: %r' % e)
            time.sleep(0.12)
    finally:
        hider.restore()
        _kb_remap.stop()
        _worker_mutex_release(mut)


# Backstop constants for the display worker (each launch is a real process
# that nothing reaps until service stop, so false-negative heartbeats used to
# stack workers without bound).
_WORKER_HB_FRESH = 10         # heartbeat younger than this = healthy worker
_WORKER_DEAD_AFTER = 60       # heartbeat-less this long = stuck worker, kill it
_WORKER_LAUNCH_COOLDOWN = 30  # min spacing between worker launches
_WORKER_LAUNCH_MAX = 5        # circuit breaker: launches per window
_WORKER_LAUNCH_WINDOW = 600   # circuit breaker window (seconds)
_WORKER_MUTEX_NAME = r'Global\SunshineDuoCore-display-worker'
_WAIT_ABANDONED = 0x00000080

_kernel32.CreateMutexW.restype = wintypes.HANDLE
_kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
_kernel32.ReleaseMutex.restype = wintypes.BOOL
_kernel32.ReleaseMutex.argtypes = [wintypes.HANDLE]


def _worker_mutex_acquire():
    """Try to take the global single-worker mutex. Returns (acquired, handle);
    the handle must stay open for the process lifetime. The mutex is created
    with the same permissive SDDL as the control pipe (Everyone+SYSTEM), so a
    lower-integrity duplicate can still open and be rejected by it. A dead
    prior owner yields WAIT_ABANDONED, which still grants ownership. If the
    mutex cannot be created at all, fall back to a process census: run only
    when no other worker exists in this session."""
    try:
        sa_pack = _pipe_sa()
        sa_ptr = None
        if sa_pack is not None:
            sa_ptr = ctypes.cast(ctypes.byref(sa_pack[0]), ctypes.c_void_p)
        h = _kernel32.CreateMutexW(sa_ptr, False, _WORKER_MUTEX_NAME)
        if not h:
            if _worker_pids_in_session(find_console_session()):
                return False, None
            return True, None
        if _kernel32.WaitForSingleObject(h, 0) in (0, _WAIT_ABANDONED):
            return True, h
        _kernel32.CloseHandle(h)
        return False, None
    except Exception:
        return True, None


def _worker_mutex_release(h):
    try:
        if h:
            _kernel32.ReleaseMutex(h)
            _kernel32.CloseHandle(h)
    except Exception:
        pass


def _worker_hb_age():
    """(state, age_seconds). state: 'ok' = parsed a heartbeat, 'missing' = no
    file yet, 'unreadable' = corrupt/locked. age = time since the stamp."""
    try:
        with open(_WORKER_HB, 'r', encoding='utf-8') as f:
            hb = json.load(f)
        return 'ok', max(0.0, time.time() - float(hb.get('ts', 0)))
    except FileNotFoundError:
        return 'missing', float('inf')
    except Exception:
        return 'unreadable', float('inf')


def _worker_pids_in_session(session_id):
    """All OTHER SunshineDuoCore.exe processes in the given session. The
    service itself lives in session 0, so any console-session copy of this
    exe is a display worker (sunshine.exe is a different image)."""
    my_pid = os.getpid()
    size = 2048
    while True:
        arr = (wintypes.DWORD * size)()
        cb = wintypes.DWORD(0)
        if not _psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(cb)):
            return []
        if cb.value < ctypes.sizeof(arr):
            break
        size *= 2
    pids = []
    for i in range(cb.value // ctypes.sizeof(wintypes.DWORD)):
        pid = arr[i]
        if pid == 0 or pid == my_pid:
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
                if buf.value.lower().endswith('\\sunshineduocore.exe'):
                    pids.append(pid)
        finally:
            _kernel32.CloseHandle(h)
    return pids


def _terminate_pid(pid):
    h = _kernel32.OpenProcess(0x0001 | 0x1000, False, pid)  # TERMINATE | QUERY_LIMITED
    if not h:
        return False
    try:
        return bool(_kernel32.TerminateProcess(h, 0))
    finally:
        _kernel32.CloseHandle(h)


def launch_display_worker(session_id):
    if getattr(sys, 'frozen', False):
        exe, args = sys.executable, '--display-worker'
    else:
        exe, args = sys.executable, '"%s" --display-worker' % os.path.abspath(__file__)
    return launch_apollo(session_id, system_token=True, force=True, exe=exe, extra_args=args)


_worker_launch_times = deque()
_worker_no_hb_since = {}
_worker_last_launch = 0.0


def ensure_display_worker(cfg):
    # worker runs in every display mode: it applies the streaming topology
    # (only/extend) and cleans up leftover virtual displays while idle.
    #
    # Runaway-spawn backstops (a heartbeat is the ONLY liveness signal, and a
    # stale/missing one used to trigger a fresh launch every tick with no
    # reaping of the old worker):
    #   1. the worker itself holds a Global mutex, so duplicates exit at once;
    #   2. live SunshineDuoCore.exe processes in the console session are
    #      enumerated first - a merely slow worker (boot, long topology op,
    #      suspend/resume) never gets a duplicate stacked on top;
    #   3. a worker heartbeat-less for _WORKER_DEAD_AFTER is killed so a
    #      replacement can take over;
    #   4. launches are cooldown-limited and circuit-broken (max N per window)
    #      so a broken bundle cannot pump processes indefinitely.
    global _worker_last_launch
    sid = find_console_session()
    if sid == -1:
        return
    now = time.time()
    state, hb_age = _worker_hb_age()
    if state == 'ok' and hb_age <= _WORKER_HB_FRESH:
        _worker_no_hb_since.clear()
        return
    workers = _worker_pids_in_session(sid)
    if workers:
        for wpid in workers:
            _worker_no_hb_since.setdefault(wpid, now)
        for wpid in [p for p in _worker_no_hb_since if p not in workers]:
            del _worker_no_hb_since[wpid]
        stuck = [p for p, t in _worker_no_hb_since.items() if now - t > _WORKER_DEAD_AFTER]
        if stuck:
            log_warn('display worker: no heartbeat for %.0fs, killing stuck worker pid(s) %s'
                     % (now - min(_worker_no_hb_since[p] for p in stuck), stuck))
            for wpid in stuck:
                _terminate_pid(wpid)
                _worker_no_hb_since.pop(wpid, None)
            workers = [p for p in workers if p not in stuck]
        if workers:
            return
    if now - _worker_last_launch < _WORKER_LAUNCH_COOLDOWN:
        return
    while _worker_launch_times and now - _worker_launch_times[0] > _WORKER_LAUNCH_WINDOW:
        _worker_launch_times.popleft()
    if len(_worker_launch_times) >= _WORKER_LAUNCH_MAX:
        log_err('display worker: %d launches in %ds and still no stable worker; '
                'pausing launches (check watch.log / bundle integrity)'
                % (len(_worker_launch_times), _WORKER_LAUNCH_WINDOW))
        return
    _worker_launch_times.append(now)
    _worker_last_launch = now
    pid = launch_display_worker(sid)
    log('display worker: launch requested session=%d pid=%s' % (sid, pid))


def run_displaycheck():
    """One-shot dry run: print what the topology poller would see/do."""
    cfg = load_config()
    mode = _display_mode(cfg)
    vda, others = _display_sources()
    print('display mode: %s (exclusive=%s)' % (mode, _exclusive_enabled(cfg)))
    print('active SudoVDA sources: %s' % vda)
    print('active other sources:   %s' % others)
    print('disabled (pending restore): %s' % _dx.disabled)
    print('streaming=%s vda_created=%s vda_failed=%s' % (
        _dx._streaming, _dx._vda_created, _dx._vda_failed))
    print('hotkeys: win_from_ralt=%s winl_from_ralt_l=%s' % (
        cfg.get('win_from_ralt'), cfg.get('winl_from_ralt_l')))
    state = _dx._state(vda, others)
    print('state: %s' % state)
    if state == 'mixed-stream':
        if mode == 'only':
            print('would detach: %s' % others)
        elif mode == 'extend':
            print('would place VDA right of: %s' % others)
        else:
            print('would detach VDA (mirror): %s' % vda)
    elif state in ('vda-idle', 'mixed-idle'):
        print('would detach VDA (idle cleanup): %s' % vda)
    elif state == 'needs-restore':
        print('would reattach: %s' % list(_dx.disabled))
    return 0


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

_kernel32.GetCurrentProcess.restype = wintypes.HANDLE
_kernel32.GetCurrentProcess.argtypes = []
_advapi32.OpenProcessToken.restype = wintypes.BOOL
_advapi32.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
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


_apollo_last_launch = {}


def state_paths(cfg):
    """State file for each Apollo instance, taken from its conf's file_state."""
    root = repo_root()
    paths = []
    for key in ('console_conf_path', 'game_conf_path'):
        conf = cfg.get(key)
        if not conf:
            continue
        state = ''
        try:
            with open(conf, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    name, sep, value = line.partition('=')
                    if sep and name.strip() == 'file_state':
                        state = value.strip().strip('"\'')
                        break
        except Exception:
            pass
        if not state:
            state = os.path.join(os.path.dirname(conf), 'sunshine_state.json')
        elif not os.path.isabs(state):
            state = os.path.join(os.path.dirname(conf), state)
        if os.path.normcase(os.path.abspath(state)) not in {
                os.path.normcase(os.path.abspath(p)) for p in paths}:
            paths.append(state)
    legacy = os.path.join(root, 'config', 'sunshine_state.json')
    if os.path.normcase(os.path.abspath(legacy)) not in {
            os.path.normcase(os.path.abspath(p)) for p in paths}:
        paths.append(legacy)
    return paths


def normalize_state_types(cfg):
    """Apollo 0.4.6 aborts on startup when a state file carries stringified
    booleans/numbers (upstream issue #835: the Web UI re-saves per-device
    settings in that form). Coerce the known keys back to native types in
    every instance's state before launching, so a Web UI save cannot re-trigger
    the crash loop. Best effort: unreadable/locked files are skipped silently."""
    for path in state_paths(cfg):
        _normalize_state_file(path)


def _normalize_state_file(path):
    try:
        with open(path, 'r', encoding='utf-8') as f:
            data = json.load(f)
    except Exception:
        return
    changed = False
    try:
        for dev in data.get('root', {}).get('named_devices', []):
            for key in ('allow_client_commands', 'always_use_virtual_display', 'enable_legacy_ordering'):
                val = dev.get(key)
                if isinstance(val, str) and val.lower() in ('true', 'false'):
                    dev[key] = val.lower() == 'true'
                    changed = True
            perm = dev.get('perm')
            if isinstance(perm, str) and perm.isdigit():
                dev['perm'] = int(perm)
                changed = True
    except Exception:
        return
    if not changed:
        return
    try:
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=4)
        log('normalized %s value types (Apollo issue #835)' % os.path.basename(path))
    except Exception as e:
        log_warn('could not rewrite %s: %r' % (os.path.basename(path), e))


def console_state_path(cfg):
    """State file of the Console Apollo, taken from its conf's file_state."""
    return state_path_for_conf(cfg.get('console_conf_path'))


def game_state_path(cfg):
    """State file of the Game Apollo, taken from its conf's file_state."""
    return state_path_for_conf(cfg.get('game_conf_path'))


# Session registry: (id, guard flag in config.json, conf path key in config.json).
# "不安装某会话" = 对应 guard 开关为 false —— tick / 配对监听 / 重启 / 托盘菜单
# 全部按这张表与开关工作，新增会话只需在此登记。
SESSION_TARGETS = (
    ('console', 'guard_apollo_console', 'console_conf_path'),
    ('game', 'guard_apollo', 'game_conf_path'),
)


def session_enabled(cfg, target):
    for tid, guard_key, _conf_key in SESSION_TARGETS:
        if tid == target:
            return bool(cfg.get(guard_key))
    return False


def apollo_conf_port(conf):
    """Stream port parsed from a conf's port= line (WebUI = port+1); 0 if unknown."""
    if not conf:
        return 0
    try:
        with open(conf, 'r', encoding='utf-8-sig') as f:
            for line in f:
                name, sep, value = line.partition('=')
                if sep and name.strip() == 'port':
                    return int(value.strip())
    except Exception:
        pass
    return 0


def session_entries(cfg):
    """Descriptor list of every registered session (enabled or not) for the
    tray and the ctrl pipe: id, guard flag, conf/state paths and ports."""
    entries = []
    for tid, guard_key, conf_key in SESSION_TARGETS:
        conf = cfg.get(conf_key)
        port = apollo_conf_port(conf)
        entries.append({
            'id': tid,
            'enabled': bool(cfg.get(guard_key)),
            'conf_path': conf or '',
            'conf_exists': bool(conf) and os.path.isfile(conf),
            'state_path': state_path_for_conf(conf) or '',
            'port': port,
            'web_port': port + 1 if port else 0,
        })
    return entries


def state_path_for_conf(conf):
    if not conf:
        return None
    state = ''
    try:
        with open(conf, 'r', encoding='utf-8-sig') as f:
            for line in f:
                name, sep, value = line.partition('=')
                if sep and name.strip() == 'file_state':
                    state = value.strip().strip('"\'')
                    break
    except Exception:
        pass
    if not state:
        state = os.path.join(os.path.dirname(conf), 'sunshine_state.json')
    elif not os.path.isabs(state):
        state = os.path.join(os.path.dirname(conf), state)
    return os.path.abspath(state)


# ----------------------------------------------------------------------------
# pair event watcher (passive)
# A new Moonlight device reaching Apollo for the first time presents a cert
# that is not in the state file's device list, so TLS verification fails and
# Apollo logs a 'SSL Verification error :: ...' line (Warning level, always
# emitted at the default log config). The watcher only tails the local log
# files - Apollo is never probed - and queues one event per line; the trayapp
# polls the queue over the ctrl pipe and auto-opens the pairing dialog.

_pair_events = deque(maxlen=16)
_pair_lock = threading.Lock()
_pair_seen = {}          # marker line -> monotonic timestamp (dedup)
_pair_seen_max = 128
_pair_offsets = {}       # target -> (log_path, offset)
_pair_last_push = {}     # target -> monotonic time of last queued event
_PAIR_COOLDOWN = 15.0    # one notification per target per connect burst
_PAIR_EVENT_TTL = 600.0  # drop undelivered events older than 10 minutes

# match the log prefix while tolerating GBK-mangled bytes: the message text
# after the level is pure ASCII ('Warning: SSL Verification error :: ...')
_PAIR_TS = re.compile(rb'^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.\d+\]:\s*\w+:\s*(.*)$')

# marker -> payload shown in the pairing dialog title
_PAIR_PATTERNS = (
    (re.compile(rb'SSL Verification error'), '新设备'),
    (re.compile(rb'\[SSL\] unknown -- denied'), '新设备'),
)


def _apollo_log_path(conf):
    """Path of an Apollo instance's log file, from its conf's log_path."""
    if not conf:
        return None
    try:
        with open(conf, 'r', encoding='utf-8-sig') as f:
            for line in f:
                name, sep, value = line.partition('=')
                if sep and name.strip() == 'log_path':
                    path = value.strip().strip('"\'')
                    if path and not os.path.isabs(path):
                        path = os.path.join(os.path.dirname(conf), path)
                    return os.path.abspath(path) if path else None
    except Exception:
        pass
    return None


def _pair_marker(line):
    """True when a raw log line is a passive new-device signal."""
    for pat, _payload in _PAIR_PATTERNS:
        if pat.search(line):
            return True
    return False


def _pair_push(target, stamp, payload):
    """Queue one pairing event. Deduped by (target, timestamp) so a re-read
    of the same log region cannot re-notify, and rate-limited per target so a
    single connect burst (several handshake retry lines within seconds) raises
    exactly one dialog."""
    with _pair_lock:
        now = time.monotonic()
        last = _pair_last_push.get(target)
        if last is not None and now - last < _PAIR_COOLDOWN:
            return
        key = (target, stamp)
        seen = _pair_seen.get(key)
        if seen is not None and now - seen < 3600:
            return
        if len(_pair_seen) >= _pair_seen_max:
            # drop the oldest entry (dicts keep insertion order)
            _pair_seen.pop(next(iter(_pair_seen)), None)
        _pair_seen[key] = now
        _pair_last_push[target] = now
        _pair_events.append({'target': target, 'payload': payload,
                             'ts': time.time()})


def _drain_pair_events():
    """Pop-and-clear: each event is delivered to the tray exactly once.
    Undelivered events older than the TTL are dropped, so restarting the tray
    hours later cannot raise phantom dialogs for long-gone connections."""
    with _pair_lock:
        now = time.time()
        fresh = [e for e in _pair_events
                 if now - e.get('ts', 0) < _PAIR_EVENT_TTL]
        _pair_events.clear()
        return fresh


def pair_watch_loop():
    while True:
        try:
            cfg = load_config()
            if cfg.get('pair_watch'):
                interval = max(0.5, float(cfg.get('pair_watch_interval', 2)))
                for target, guard_key, conf_key in SESSION_TARGETS:
                    if not cfg.get(guard_key):
                        continue  # 单会话安装：未启用的会话不监听配对日志
                    path = _apollo_log_path(cfg.get(conf_key))
                    if path:
                        _pair_poll_file(target, path)
            else:
                interval = 10.0
            time.sleep(interval)
        except Exception as e:
            log_err('pair watch error: %r' % e)
            time.sleep(10)


def _pair_poll_file(target, path):
    try:
        size = os.path.getsize(path)
    except OSError:
        return
    prev_path, off = _pair_offsets.get(target, (None, 0))
    if prev_path != path or size < off:
        # first sight, or log truncated/recreated (Apollo restart): start from
        # the tail so startup noise can never raise phantom pairing dialogs
        off = size
        _pair_offsets[target] = (path, off)
        return
    if size == off:
        return
    try:
        with open(path, 'rb') as f:
            f.seek(off)
            data = f.read()
            _pair_offsets[target] = (path, f.tell())
    except OSError:
        return
    payload = '新设备'
    for raw in data.splitlines():
        if not _pair_marker(raw):
            continue
        m = _PAIR_TS.match(raw)
        stamp = m.group(1) if m else raw[:19]
        # prefer the message text (ASCII after the level tag) for the dialog
        msg = m.group(2).strip() if m else b''
        if msg:
            payload = msg.decode('ascii', 'replace') or '新设备'
        _pair_push(target, stamp.decode('ascii', 'replace'), payload)


def start_pair_watch():
    threading.Thread(target=pair_watch_loop, daemon=True, name='pair-watch').start()


def set_apollo_virtual_display(cfg, want, conf_key='console'):
    """Force always_use_virtual_display on every paired device of one Apollo's
    state. The flag is a per-device record read at Apollo startup from
    named_devices (NOT a conf key - the obsolete sunshine.conf line is inert in
    Apollo 0.4.6). Re-run every tick: the Web UI may overwrite the state later.
    Console wants true (all clients stream onto the virtual display); the
    game/RDP session wants false (no SudoVDA virtual display there)."""
    path = console_state_path(cfg) if conf_key == 'console' else game_state_path(cfg)
    if not path:
        return False
    try:
        with open(path, 'r', encoding='utf-8') as f:
            data = json.load(f)
    except Exception:
        return False
    changed = False
    try:
        for dev in data.get('root', {}).get('named_devices', []):
            if dev.get('always_use_virtual_display') is not want:
                dev['always_use_virtual_display'] = want
                changed = True
    except Exception:
        return False
    if not changed:
        return False
    try:
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=4)
        log('forced always_use_virtual_display=%s in %s' % (want, os.path.basename(path)))
        return True
    except Exception as e:
        log_warn('could not rewrite %s: %r' % (os.path.basename(path), e))
        return False
    except Exception as e:
        log_warn('could not rewrite %s: %r' % (os.path.basename(path), e))


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


def launch_apollo(session_id, config_path=None, system_token=False, force=False,
                  exe=None, extra_args=None):
    """Launch sunshine.exe as the elevated user token inside their session.

    When system_token is True (Console session), use the service's own SYSTEM
    token instead of WTSQueryUserToken, so Apollo runs as SYSTEM into the
    Console session and can handle UAC prompts.
    exe/extra_args override the command line (used to spawn the display worker).
    """
    global _apollo_last_launch
    exe = exe or os.path.join(repo_root(), 'sunshine.exe')
    if not os.path.exists(exe):
        log_err('apollo guard: sunshine.exe not found: %s' % exe)
        return None
    # Rate-limit each session independently so one launch cannot starve the other.
    # force=True (manual restart via the control pipe) bypasses the limiter.
    launch_key = (session_id, os.path.basename(exe))
    if not force and time.time() - _apollo_last_launch.get(launch_key, 0) < 20:
        return None
    _apollo_last_launch[launch_key] = time.time()
    if system_token:
        raw = wintypes.HANDLE()
        if not _advapi32.OpenProcessToken(_kernel32.GetCurrentProcess(),
                                          TOKEN_DUPLICATE | TOKEN_QUERY,
                                          ctypes.byref(raw)):
            log_err('apollo console guard: OpenProcessToken(SYSTEM) failed err=%d' % ctypes.get_last_error())
            return None
        try:
            tok2, _ = _elevated_primary_token(raw)
            if not tok2:
                log_err('apollo console guard: DuplicateTokenEx(SYSTEM) failed err=%d' % ctypes.GetLastError())
                return None
            tok = wintypes.HANDLE(tok2)
        finally:
            _kernel32.CloseHandle(raw)
    else:
        raw = wintypes.HANDLE()
        if not wtsapi32.WTSQueryUserToken(session_id, ctypes.byref(raw)):
            log_warn('apollo guard: WTSQueryUserToken(%d) failed err=%d' % (session_id, ctypes.GetLastError()))
            return None
        try:
            tok2, _ = _elevated_primary_token(raw)
            if not tok2:
                log_warn('apollo guard: could not build an elevated primary token')
                return None
            tok = wintypes.HANDLE(tok2)
        finally:
            _kernel32.CloseHandle(raw)
    try:
        # stamp the target session onto the token
        sid_val = wintypes.DWORD(session_id)
        if not _advapi32.SetTokenInformation(tok, TokenSessionId, ctypes.byref(sid_val), ctypes.sizeof(sid_val)):
            log_err('apollo guard: SetTokenInformation(TokenSessionId) failed err=%d' % ctypes.GetLastError())
            return None
        env = ctypes.c_void_p()
        if not _userenv.CreateEnvironmentBlock(ctypes.byref(env), tok, False):
            env = ctypes.c_void_p()
        try:
            si = STARTUPINFOW()
            si.cb = ctypes.sizeof(STARTUPINFOW)
            si.lpDesktop = 'WinSta0\\Default'
            si.dwFlags = 0x00000001  # STARTF_USESHOWWINDOW
            si.wShowWindow = 0       # SW_HIDE
            pi = PROCESS_INFORMATION()
            if extra_args:
                cmdline = ctypes.create_unicode_buffer('"%s" %s' % (exe, extra_args))
            elif config_path:
                cmdline = ctypes.create_unicode_buffer('"%s" "%s"' % (exe, config_path))
            else:
                cmdline = ctypes.create_unicode_buffer('"%s"' % exe)
            ok = _advapi32.CreateProcessAsUserW(tok, None, cmdline, None, None, False,
                                                CREATE_UNICODE_ENV_FLAG, env,
                                                os.path.dirname(exe), ctypes.byref(si), ctypes.byref(pi))
            if ok:
                log('apollo guard: launched %s pid=%d session=%d (system=%s)' % (
                    os.path.basename(exe), pi.dwProcessId, session_id, system_token))
                _kernel32.CloseHandle(pi.hThread)
                _kernel32.CloseHandle(pi.hProcess)
                return pi.dwProcessId
            log_err('apollo guard: CreateProcessAsUserW failed err=%d' % ctypes.GetLastError())
            return None
        finally:
            if env.value:
                _userenv.DestroyEnvironmentBlock(env.value)
    finally:
        _kernel32.CloseHandle(tok)


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

def _apply_log_cfg(cfg):
    """Hot-reload the logging knobs; used by the service tick and by the
    display worker (a separate process that would otherwise keep the default
    level forever and silently drop its info logs)."""
    global _LOG_MAX_KB, _LOG_KEEP, _LOG_LEVEL
    try:
        _LOG_MAX_KB = max(10, int(cfg.get('log_max_kb', 100)))
        _LOG_KEEP = max(1, int(cfg.get('log_keep', 4)))
        _LOG_LEVEL = _LOG_LEVELS.get(str(cfg.get('log_level', 'warn')).lower(), _LOG_LEVEL)
    except Exception:
        pass


def tick():
    cfg = load_config()
    _apply_log_cfg(cfg)

    if cfg.get('guard_apollo') or cfg.get('guard_apollo_console'):
        normalize_state_types(cfg)
        _mode = _display_mode(cfg)
        if cfg.get('guard_apollo_console'):
            # Console: paired clients stream onto the SudoVDA virtual display
            # for virtual-display modes (only/extend). Mirror mode streams the
            # physical primary, so no virtual display is wanted there.
            set_apollo_virtual_display(cfg, _mode in ('only', 'extend'), 'console')
        if cfg.get('guard_apollo'):
            # Game session is RDP-based: it never creates SudoVDA virtual
            # displays, so pin its paired devices off (the inverse of console).
            set_apollo_virtual_display(cfg, False, 'game')

    sid, state = find_user_session(cfg.get('username', 'Game'))
    try:
        ensure_display_worker(cfg)
    except Exception as e:
        log_err('display worker guard error: %r' % e)
    try:
        ensure_easytier(cfg)
    except Exception as e:
        log_err('easytier guard error: %r' % e)
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
            # default SunshineDuoCore never logs a user in - stay idle. With
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
                launch_apollo(sid, cfg.get('game_conf_path'))
        else:
            apollo_state = 'no-session'

    apollo_console_state = 'off'
    if cfg.get('guard_apollo_console'):
        console_sid = find_console_session()
        if console_sid != -1:
            if apollo_running_in(console_sid):
                apollo_console_state = 'ok'
            else:
                apollo_console_state = 'launch'
                launch_apollo(console_sid, cfg.get('console_conf_path'), system_token=True)
        else:
            apollo_console_state = 'no-session'

    state_names = {0: 'Active', 1: 'Connected', 2: 'Query', 4: 'Disconnected'}
    log('tick: session=%s(%s) ka_desired=%s ka_connected=%s display_hold=%s apollo=%s apollo_console=%s' % (
        sid, state_names.get(state, str(state)), ka.desired, ka.connected, _display_held, apollo_state, apollo_console_state))


# ----------------------------------------------------------------------------
# control pipe: trayapp (or any local tool) sends one JSON line per connection
# over \\.\pipe\SunshineDuoCore-ctrl and gets a JSON line back. Commands:
#   {"cmd":"ping"}                         -> {"ok":true}
#   {"cmd":"tick"}                         -> run one guard tick immediately
#     (used by the installer right after the service starts, so both Apollos
#      launch within seconds instead of waiting for the next 60s tick)
#   {"cmd":"restart_apollo","target":"console"|"game"}
#     -> kills that session's sunshine.exe and relaunches it immediately
#        (bypasses the 20s launch rate limit), no wait for the next tick.
#   {"cmd":"set_display_mode","mode":"mirror"|"only"|"extend"}
#     -> persists the display topology choice into config.json (hot-reloaded),
#        queues a CCD/SetDisplayConfig topology request for the console-session
#        display worker, then runs one tick so Apollo's virtual-display flag and
#        the worker are updated immediately.
#   {"cmd":"set_hotkeys","win_from_ralt":bool,"winl_from_ralt_l":bool}
#     -> persists the two console-desktop hotkey substitutions; the display
#        worker hot-reloads them on its next poll (no restart).
#   {"cmd":"set_hide_cursor","enabled":bool}
#     -> persists hide_cursor_on_stream; the display worker hides/restores the
#        Console session's system cursors around streaming within ~120ms.
#   {"cmd":"sessions"}
#     -> session registry (id / enabled guard / conf+state paths / stream and
#        WebUI ports) so the tray builds its menus from one source of truth.
#   {"cmd":"easytier_status"}
#     -> tunnel state: running/pid/managed, enabled, network, exe presence.
#   {"cmd":"easytier_set",...easytier 字段}
#     -> persists the given easytier keys (omitted keys untouched), then
#        applies immediately: enabled -> start/adopt, disabled -> stop.
# Local-only pipe; SDDL grants generic access to Everyone+SYSTEM so the
# non-elevated tray app can connect. Commands are restart-only, which is
# acceptable exposure for this home tool.

PIPE_NAME = r'\\.\pipe\SunshineDuoCore-ctrl'
PIPE_ACCESS_DUPLEX = 3
PIPE_UNLIMITED_INSTANCES = 255
ERROR_PIPE_CONNECTED = 535
PROCESS_TERMINATE = 0x0001


class SECURITY_ATTRIBUTES(ctypes.Structure):
    _fields_ = [('nLength', wintypes.DWORD),
                ('lpSecurityDescriptor', ctypes.c_void_p),
                ('bInheritHandle', wintypes.BOOL)]


_advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.restype = wintypes.BOOL
_advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [
    wintypes.LPCWSTR, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p),
    ctypes.POINTER(wintypes.ULONG)]
_kernel32.CreateNamedPipeW.restype = wintypes.HANDLE
_kernel32.CreateNamedPipeW.argtypes = [
    wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
    wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
_kernel32.ConnectNamedPipe.restype = wintypes.BOOL
_kernel32.ConnectNamedPipe.argtypes = [wintypes.HANDLE, ctypes.c_void_p]
_kernel32.ReadFile.restype = wintypes.BOOL
_kernel32.ReadFile.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
                               ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
_kernel32.WriteFile.restype = wintypes.BOOL
_kernel32.WriteFile.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
                                ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
_kernel32.FlushFileBuffers.restype = wintypes.BOOL
_kernel32.FlushFileBuffers.argtypes = [wintypes.HANDLE]
_kernel32.DisconnectNamedPipe.restype = wintypes.BOOL
_kernel32.DisconnectNamedPipe.argtypes = [wintypes.HANDLE]
_kernel32.TerminateProcess.restype = wintypes.BOOL
_kernel32.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]


def _pipe_sa():
    """SECURITY_ATTRIBUTES with SDDL D:(A;;GA;;;WD)(A;;GA;;;SY); returns
    (sa, sd) so the SD buffer outlives the CreateNamedPipeW call, or None
    (fall back to the default DACL) if SDDL conversion fails."""
    sd = ctypes.c_void_p()
    size = wintypes.ULONG(0)
    if not _advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW(
            'D:(A;;GA;;;WD)(A;;GA;;;SY)', 1, ctypes.byref(sd), ctypes.byref(size)):
        return None
    sa = SECURITY_ATTRIBUTES()
    sa.nLength = ctypes.sizeof(SECURITY_ATTRIBUTES)
    sa.lpSecurityDescriptor = sd.value
    sa.bInheritHandle = False
    return sa, sd


def _find_apollo_pids(session_id):
    pids = []
    arr = (wintypes.DWORD * 2048)()
    cb = wintypes.DWORD(0)
    if not _psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(cb)):
        return pids
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
            if _kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)) and \
                    buf.value.lower().endswith('\\sunshine.exe'):
                pids.append(pid)
        finally:
            _kernel32.CloseHandle(h)
    return pids


def _restart_apollo(target):
    cfg = load_config()
    if not session_enabled(cfg, target):
        return {'ok': False, 'error': 'session-disabled: %s' % target}
    if target == 'console':
        sid = find_console_session()
        conf = cfg.get('console_conf_path')
        system_token = True
    else:
        sid, _ = find_user_session(cfg.get('username', 'Game'))
        conf = cfg.get('game_conf_path')
        system_token = False
    if sid == -1:
        return {'ok': False, 'error': 'no-session'}
    # stamp the limiter BEFORE killing so a tick racing in between cannot
    # spawn a second instance
    _apollo_last_launch[(sid, 'sunshine.exe')] = time.time()
    pids = _find_apollo_pids(sid)
    killed = 0
    for pid in pids:
        h = _kernel32.OpenProcess(PROCESS_TERMINATE | 0x1000, False, pid)
        if h:
            if _kernel32.TerminateProcess(h, 1):
                killed += 1
            _kernel32.CloseHandle(h)
    if pids:
        time.sleep(1.5)  # let the ports/AVD release before relaunch
    newpid = launch_apollo(sid, conf, system_token=system_token, force=True)
    log('ctrl pipe: restart_apollo(%s) session=%d killed=%d relaunched=%s' % (
        target, sid, killed, bool(newpid)))
    return {'ok': True, 'target': target, 'session': sid,
            'killed': killed, 'relaunched': bool(newpid)}


def _ctrl_dispatch(req):
    cmd = str(req.get('cmd', ''))
    if cmd == 'ping':
        return {'ok': True, 'cmd': 'ping'}
    if cmd == 'tick':
        try:
            tick()
            return {'ok': True, 'cmd': 'tick'}
        except Exception as e:
            return {'ok': False, 'error': 'tick failed: %r' % e}
    if cmd == 'restart_apollo':
        return _restart_apollo(str(req.get('target', 'game')))
    if cmd == 'set_display_mode':
        mode = str(req.get('mode', 'mirror')).strip().lower()
        ok = save_config_display_mode(mode)
        rid = ''
        if ok:
            rid = str(time.time_ns())
            _write_json_atomic(_MODE_REQ, {'id': rid, 'mode': mode, 'ts': time.time()})
            try:
                tick()
            except Exception as e:
                log_err('set_display_mode tick error: %r' % e)
        return {'ok': ok, 'cmd': 'set_display_mode', 'mode': mode, 'request': rid}
    if cmd == 'set_hotkeys':
        win = bool(req.get('win_from_ralt'))
        winl = bool(req.get('winl_from_ralt_l'))
        ok = save_config_hotkeys(win, winl)
        return {'ok': ok, 'cmd': 'set_hotkeys',
                'win_from_ralt': win, 'winl_from_ralt_l': winl}
    if cmd == 'set_hide_cursor':
        enabled = bool(req.get('enabled'))
        ok = save_config_hide_cursor(enabled)
        return {'ok': ok, 'cmd': 'set_hide_cursor', 'enabled': enabled}
    if cmd == 'set_resolution':
        raw = req.get('resolutions')
        resolutions = [r for r in raw if isinstance(r, str)] if isinstance(raw, list) else None
        ok = save_config_resolution(req.get('width'), req.get('height'), resolutions)
        if ok:
            try:
                tick()
            except Exception as e:
                log_err('set_resolution tick error: %r' % e)
        return {'ok': ok, 'cmd': 'set_resolution'}
    if cmd == 'poll_pair_events':
        # drain the passive new-device queue (trayapp auto-opens the PIN dialog)
        return {'ok': True, 'events': _drain_pair_events()}
    if cmd == 'sessions':
        # session registry for the tray: id / guard flag / conf+state paths /
        # stream+WebUI ports. Lets the tray build its menu without guessing.
        return {'ok': True, 'sessions': session_entries(load_config())}
    if cmd == 'easytier_status':
        # tunnel state for the tray: running/pid, enabled, network, exe presence
        return easytier_status()
    if cmd == 'easytier_set':
        # persist the given easytier keys (omitted = untouched), then apply
        # immediately. secret is only written when present, so a blank
        # password box in the UI never clears a configured secret.
        def _opt(name):
            return req[name] if name in req else None
        enabled = _opt('enabled')
        network = _opt('network')
        cur_et = _easytier_cfg(load_config())
        # 首次启用且未填网络名 → 用计算机名作为默认网络名
        if enabled and not str(network or '').strip() and not str(cur_et.get('network') or '').strip():
            network = easytier_machine_name() or None
        ok = save_config_easytier(enabled, network, _opt('secret'), _opt('server_mode'),
                                  _opt('peers'), _opt('ipv4'), _opt('tcp_hole_punch'))
        resp = {'ok': ok, 'cmd': 'easytier_set'}
        if ok:
            cfg2 = load_config()
            if bool(_easytier_cfg(cfg2).get('enabled')):
                global _easytier_args_used
                touched = any(k in req for k in ('network', 'secret', 'server_mode',
                                                 'peers', 'ipv4', 'tcp_hole_punch'))
                new_args, _net = easytier_args(_easytier_cfg(cfg2))
                running = bool(easytier_pid(easytier_exe(cfg2)))
                changed = running and (
                    (_easytier_args_used is not None and new_args != _easytier_args_used)
                    or (_easytier_args_used is None and touched))
                if changed:
                    # 保存即应用：命令行实质变化时重启隧道（网络/服务器/IP/打洞等）
                    easytier_stop(cfg2)
                r = easytier_start(cfg2)
                if not r.get('ok'):
                    resp['start_error'] = r.get('error')
                resp['restarted'] = changed
            else:
                easytier_stop(cfg2)
            resp.update(easytier_status(cfg2))
        return resp
    return {'ok': False, 'error': 'unknown cmd'}


def ctrl_pipe_server():
    while True:
        try:
            sa_pack = _pipe_sa()
            sa_ptr = None
            if sa_pack is not None:
                sa_ptr = ctypes.cast(ctypes.byref(sa_pack[0]), ctypes.c_void_p)
            h = _kernel32.CreateNamedPipeW(
                PIPE_NAME, PIPE_ACCESS_DUPLEX, 0,  # PIPE_TYPE_BYTE | PIPE_WAIT
                PIPE_UNLIMITED_INSTANCES, 4096, 4096, 0, sa_ptr)
            if not h or h == wintypes.HANDLE(-1).value:
                log_err('ctrl pipe: CreateNamedPipeW failed err=%d' % ctypes.get_last_error())
                time.sleep(10)
                continue
            try:
                ok = _kernel32.ConnectNamedPipe(h, None)
                if not ok and ctypes.get_last_error() != ERROR_PIPE_CONNECTED:
                    time.sleep(0.2)
                    continue
                raw = b''
                while len(raw) < 4096:
                    buf = ctypes.create_string_buffer(4096)
                    got = wintypes.DWORD(0)
                    if not _kernel32.ReadFile(h, buf, 4096, ctypes.byref(got), None) or got.value == 0:
                        break
                    raw += buf.raw[:got.value]
                    if b'\n' in raw:
                        raw = raw.split(b'\n', 1)[0]
                        break
                try:
                    resp = _ctrl_dispatch(json.loads(raw.decode('utf-8', 'replace')))
                except Exception as e:
                    resp = {'ok': False, 'error': 'bad request: %r' % e}
                data = (json.dumps(resp) + '\n').encode('utf-8')
                wrote = wintypes.DWORD(0)
                _kernel32.WriteFile(h, ctypes.create_string_buffer(data), len(data),
                                    ctypes.byref(wrote), None)
                _kernel32.FlushFileBuffers(h)
            finally:
                _kernel32.DisconnectNamedPipe(h)
                _kernel32.CloseHandle(h)
        except Exception as e:
            log_err('ctrl pipe error: %r' % e)
            time.sleep(5)


def start_ctrl_pipe():
    threading.Thread(target=ctrl_pipe_server, daemon=True, name='ctrl-pipe').start()


# ----------------------------------------------------------------------------
# SCM service plumbing (raw ctypes - no pywin32, nothing to collect at build)

SERVICE_NAME = 'SunshineDuoCore'
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
    try:
        start_ctrl_pipe()
    except Exception as e:
        log_err('service: ctrl pipe start error: %r' % e)
    try:
        start_pair_watch()
    except Exception as e:
        log_err('service: pair watch start error: %r' % e)
    _clear_worker_stop()
    # display exclusivity runs in a worker inside the console session
    # (see ensure_display_worker), not in this session-0 process.
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
    _signal_worker_stop()
    _set_status(SERVICE_STOPPED)
    log('service: STOPPED')


def run_service():
    argv1 = sys.argv[1] if len(sys.argv) > 1 else ''
    if argv1 == '' or argv1 == SERVICE_NAME:
        # launched by SCM (argv = [exe, 'SunshineDuoCore']) or a bare console run
        table = (SERVICE_TABLE_ENTRYW * 2)(
            SERVICE_TABLE_ENTRYW(SERVICE_NAME, ctypes.cast(_service_main, ctypes.c_void_p).value),
            SERVICE_TABLE_ENTRYW(None, None))
        if not _advapi32.StartServiceCtrlDispatcherW(table):
            err = ctypes.GetLastError()
            log_err('dispatcher failed err=%d (1063 = not launched by SCM)' % err)
            raise SystemExit(ERROR_FAILED_SERVICE_CONTROLLER_CONNECT)
    else:
        print('unknown argument: %s' % argv1)
        print('usage: SunshineDuoCore.exe [--console | --setup | --test]')
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
    if '--displaycheck' in args:
        sys.exit(run_displaycheck())
    if '--display-worker' in args:
        sys.exit(run_display_worker())
    if '--test' in args:
        sys.exit(run_test(args))
    if '--console' in args:
        log('console mode - Ctrl+C to stop')
        _clear_worker_stop()
        ka.start()
        start_ctrl_pipe()
        start_pair_watch()
        try:
            while True:
                tick()
                time.sleep(load_config().get('interval', 60))
        except KeyboardInterrupt:
            ka.stop_evt.set()
            display_keep(False)
            _signal_worker_stop()
        return
    run_service()

if __name__ == '__main__':
    main()

