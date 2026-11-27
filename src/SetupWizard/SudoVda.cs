using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace SunshineDuoSetup;

// SudoVDA 虚拟显卡驱动：证书（X509Store）+ 设备节点与驱动（SetupAPI / newdev / CfgMgr32）。
// 取代旧 certutil + nefconc 方案：安装/卸载/检测全程走系统 API，可拿到准确状态码。
internal static class SudoVda
{
    public const string HardwareId = @"root\sudomaker\sudovda";
    public static readonly Guid DisplayClass = new("4D36E968-E325-11CE-BFC1-08002BE10318");

    public enum DevState { Absent, Started, NeedRestart, Failed }

    public sealed record DeviceInfo(string InstanceId, DevState State, int Problem, string? FriendlyName)
    {
        public override string ToString() => $"{InstanceId} [{State}{(Problem != 0 ? " prob " + Problem : "")}]";
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public int cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_REMOVEDEVICE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint Scope;
        public uint HwProfile;
    }

    private const uint DIF_REMOVE = 0x05;
    private const uint DIF_REGISTERDEVICE = 0x19;
    private const int DIGCF_PRESENT = 0x2;
    private const int DIGCF_ALLCLASSES = 0x4;
    private const int DICD_GENERATE_ID = 0x1;
    private const uint SPDRP_HARDWAREID = 1;
    private const uint SPDRP_FRIENDLYNAME = 0x0C;
    private const uint DI_REMOVEDEVICE_GLOBAL = 1;
    private const int ERROR_NO_MORE_ITEMS = 259;
    private const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
    private const int CM_GETIDLIST_FILTER_PRESENT = 0x100;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint hwndParent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiCreateDeviceInfoList(ref Guid classGuid, nint hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint deviceInfoSet, int memberIndex, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiOpenDeviceInfo(nint deviceInfoSet, string deviceId, nint hwndParent, int openFlags, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCreateDeviceInfo(nint deviceInfoSet, string deviceName, ref Guid classGuid, string? deviceDescription, nint hwndParent, int creationFlags, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceRegistryProperty(nint deviceInfoSet, ref SP_DEVINFO_DATA data, uint property, byte[] propertyBuffer, int propertyBufferSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint deviceInfoSet, ref SP_DEVINFO_DATA data, uint property, out uint propertyRegDataType, byte[] propertyBuffer, int propertyBufferSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(nint deviceInfoSet, ref SP_DEVINFO_DATA data, System.Text.StringBuilder deviceId, int deviceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(nint deviceInfoSet, ref SP_DEVINFO_DATA data, ref SP_REMOVEDEVICE_PARAMS paramsData, int paramsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, nint deviceInfoSet, ref SP_DEVINFO_DATA data);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(nint hwndParent, string hardwareId, string fullInfPath, uint installFlags, out bool rebootRequired);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_Size(out int len, string? filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List(string? filter, [Out] char[] buffer, int bufferLen, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out int devInst, string deviceId, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, int devInst, int flags);

    private const uint INSTALLFLAG_FORCE = 1;
    private const uint INSTALLFLAG_NONINTERACTIVE = 4;

    // ------------------------------------------------------------------
    // 检测
    // ------------------------------------------------------------------

    // 枚举显示类中硬件 ID 命中 SudoVDA 的设备（含幽灵设备），用 CM 状态判定
    public static List<DeviceInfo> EnumDevices()
    {
        var result = new List<DeviceInfo>();
        var cls = DisplayClass;
        var list = SetupDiGetClassDevs(ref cls, null, nint.Zero, DIGCF_ALLCLASSES);
        if (list == nint.Zero || list == new nint(-1)) return result;
        try
        {
            for (var i = 0; ; i++)
            {
                var data = NewData();
                if (!SetupDiEnumDeviceInfo(list, i, ref data))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == ERROR_NO_MORE_ITEMS) break;
                    continue;
                }
                if (!TryHardwareId(list, ref data, out var hwIds)) continue;
                if (!hwIds.Any(h => h.Trim().Equals(HardwareId, StringComparison.OrdinalIgnoreCase))) continue;

                var state = DevState.Absent;
                var problem = 0;
                if (CM_Locate_DevNode(out var devInst, InstanceId(list, ref data), 0) == 0)
                {
                    CM_Get_DevNode_Status(out var status, out var problemNum, devInst, 0);
                    problem = (int)problemNum;
                    const uint DN_STARTED = 8;
                    const uint DN_HAS_PROBLEM = 0x400;
                    if ((status & DN_STARTED) != 0) state = DevState.Started;
                    else if ((status & DN_HAS_PROBLEM) != 0)
                        state = problem switch
                        {
                            0x0E => DevState.NeedRestart,   // CM_PROB_NEED_RESTART
                            0x12 => DevState.NeedRestart,   // CM_PROB_REINSTALL
                            0x1F => DevState.NeedRestart,   // CM_PROB_FAILED_ADD（31）：SudoVDA 装后常见，重启电脑才正常启动，视作安装成功
                            0x16 => DevState.Failed,        // CM_PROB_DISABLED
                            _ => DevState.Failed,
                        };
                    else state = DevState.Started;
                }
                var friendly = TryString(list, ref data, SPDRP_FRIENDLYNAME);
                result.Add(new DeviceInfo(InstanceId(list, ref data), state, problem, friendly));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(list); }
        return result;
    }

    public static (DevState State, int Problem) Query()
    {
        var devs = EnumDevices();
        if (devs.Count == 0) return (DevState.Absent, 0);
        if (devs.Any(d => d.State == DevState.Started)) return (DevState.Started, 0);
        if (devs.Any(d => d.State == DevState.NeedRestart)) return (DevState.NeedRestart, 0);
        var bad = devs.First(d => d.State == DevState.Failed);
        return (DevState.Failed, bad.Problem);
    }

    // ------------------------------------------------------------------
    // 安装 / 卸载
    // ------------------------------------------------------------------

    public static bool CertInstalled()
    {
        try
        {
            using var cert = X509CertificateLoader.LoadCertificateFromFile(Paths.SudoVdaCer);
            foreach (var storeName in new[] { StoreName.Root, StoreName.TrustedPublisher })
            {
                using var store = new X509Store(storeName, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
                if (found.Count == 0) return false;
            }
            return true;
        }
        catch { return false; }
    }

    public static void EnsureCertificates(Action<string> log)
    {
        using var cert = X509CertificateLoader.LoadCertificateFromFile(Paths.SudoVdaCer);
        foreach (var (name, cn) in new[] { (StoreName.Root, "受信任的根证书颁发机构"), (StoreName.TrustedPublisher, "受信任的发布者") })
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            if (store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count == 0)
            {
                store.Add(cert);
                log($"OK: 证书已装入{cn}（{cert.Thumbprint[..12]}…）");
            }
            else log($"跳过: 证书已在{cn}");
        }
    }

    /// <summary>安装驱动。返回 true 表示设备已就绪但需重启电脑才能生效（代码 31 等）。</summary>
    public static bool Install(Action<string> log, CancellationToken ct)
    {
        if (!File.Exists(Paths.SudoVdaInf)) throw new FileNotFoundException("未找到 " + Paths.SudoVdaInf);
        EnsureCertificates(log);

        var existing = EnumDevices();
        if (existing.Count > 0)
        {
            log("清理旧设备节点...");
            RemoveAll(log);
        }

        log("创建设备节点（root\\sudomaker\\sudovda）...");
        var cls = DisplayClass;
        var list = SetupDiCreateDeviceInfoList(ref cls, nint.Zero);
        if (list == nint.Zero || list == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfoList 失败");
        try
        {
            var data = NewData();
            if (!SetupDiCreateDeviceInfo(list, "SUDOVDA", ref cls, "SudoVDA Virtual Display Adapter", nint.Zero, DICD_GENERATE_ID, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfo 失败");

            var hwId = HardwareId;
            var buf = System.Text.Encoding.Unicode.GetBytes(hwId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(list, ref data, SPDRP_HARDWAREID, buf, buf.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "写入 HardwareID 失败");

            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, list, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "注册设备失败");

            log("安装驱动（UpdateDriverForPlugAndPlayDevices）...");
            if (!UpdateDriverForPlugAndPlayDevices(nint.Zero, HardwareId, Paths.SudoVdaInf, INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE, out var reboot))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"驱动安装失败（Win32 错误码 {err}）");
            }
            if (reboot) log("注意: 驱动安装完成，需重启电脑后生效");
        }
        finally { SetupDiDestroyDeviceInfoList(list); }

        // PnP 枚举与启动可能滞后：轮询至 Started 或 NeedRestart；Failed 视为失败
        var deadline = DateTime.UtcNow.AddSeconds(15);
        DevState state = DevState.Absent;
        int problem = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(500);
            (state, problem) = Query();
            if (state is DevState.Started or DevState.NeedRestart) break;
        }
        switch (state)
        {
            case DevState.Started:
                log("OK: 虚拟显卡驱动已安装并运行");
                return false;
            case DevState.NeedRestart:
                log(problem == 0x1F
                    ? $"OK: 虚拟显卡驱动已安装（设备状态码 {problem}，重启电脑后生效）"
                    : "OK: 虚拟显卡驱动已安装（重启电脑后生效）");
                return true;
            default:
                throw new Exception($"虚拟显卡设备未就绪（状态 {state}，问题码 {problem}）；若设备管理器提示需重启，重启电脑即生效");
        }
    }

    public static void Uninstall(Action<string> log, CancellationToken ct)
    {
        RemoveAll(log);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(500);
            if (EnumDevices().Count == 0)
            {
                log("OK: 虚拟显卡驱动已卸载");
                return;
            }
        }
        log("注意: 设备节点仍存在（remove 未生效），重启电脑后再卸载一次即可移除");
    }

    private static void RemoveAll(Action<string> log)
    {
        var devs = EnumDevices();
        foreach (var dev in devs)
        {
            var cls = DisplayClass;
        var list = SetupDiCreateDeviceInfoList(ref cls, nint.Zero);
            if (list == nint.Zero || list == new nint(-1)) continue;
            try
            {
                var data = NewData();
                if (!SetupDiOpenDeviceInfo(list, dev.InstanceId, nint.Zero, 0, ref data))
                {
                    log($"跳过: 无法打开设备 {dev.InstanceId}");
                    continue;
                }
                var remove = new SP_REMOVEDEVICE_PARAMS
                {
                    ClassInstallHeader = new SP_CLASSINSTALL_HEADER { cbSize = Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(), InstallFunction = DIF_REMOVE },
                    Scope = DI_REMOVEDEVICE_GLOBAL,
                };
                if (SetupDiSetClassInstallParams(list, ref data, ref remove, Marshal.SizeOf<SP_REMOVEDEVICE_PARAMS>())
                    && SetupDiCallClassInstaller(DIF_REMOVE, list, ref data))
                {
                    log($"OK: 已移除设备节点 {dev.InstanceId}");
                }
                else
                {
                    var err = Marshal.GetLastWin32Error();
                    log($"注意: 移除 {dev.InstanceId} 返回错误码 {err}（可能本不存在，忽略）");
                }
            }
            finally { SetupDiDestroyDeviceInfoList(list); }
        }
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private static SP_DEVINFO_DATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>(),
        ClassGuid = Guid.Empty,
        DevInst = 0,
        Reserved = nint.Zero,
    };

    private static string InstanceId(nint list, ref SP_DEVINFO_DATA data)
    {
        var sb = new System.Text.StringBuilder(512);
        SetupDiGetDeviceInstanceId(list, ref data, sb, sb.Capacity, out _);
        return sb.ToString();
    }

    private static bool TryHardwareId(nint list, ref SP_DEVINFO_DATA data, out List<string> ids)
    {
        ids = [];
        if (!TryString(list, ref data, SPDRP_HARDWAREID, out var raw)) return false;
        ids.AddRange(raw.Split(['\0'], StringSplitOptions.RemoveEmptyEntries));
        return ids.Count > 0;
    }

    private static bool TryString(nint list, ref SP_DEVINFO_DATA data, uint property, out string value)
    {
        value = "";
        try
        {
            var buf = new byte[1024];
            if (!SetupDiGetDeviceRegistryProperty(list, ref data, property, out _, buf, buf.Length, out var size))
            {
                if (size > buf.Length)
                {
                    buf = new byte[size];
                    if (!SetupDiGetDeviceRegistryProperty(list, ref data, property, out _, buf, buf.Length, out _)) return false;
                }
                else return false;
            }
            value = System.Text.Encoding.Unicode.GetString(buf, 0, size).TrimEnd('\0');
            return true;
        }
        catch { return false; }
    }

    private static string? TryString(nint list, ref SP_DEVINFO_DATA data, uint property)
        => TryString(list, ref data, property, out var v) && v.Length > 0 ? v : null;
}
