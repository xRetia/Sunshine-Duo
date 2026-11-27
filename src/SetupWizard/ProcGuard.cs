using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SunshineDuoSetup;

// 进程清场：识别祖先链，避免 Kill(entireProcessTree) 因"调用者在目标树内"而抛异常。
// 场景：托盘是 setup.exe 的父进程，托盘后代包含 setup.exe 自身，
// 按树终止托盘会抛 "Cannot be used to terminate a process tree containing the calling process"，
// 导致托盘永远杀不掉、文件替换无法进行。对祖先只终止其本身即可。
internal static class ProcGuard
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // 当前进程的祖先 PID 集合（不含自身）
    public static HashSet<int> AncestorPids()
    {
        var parents = new Dictionary<int, uint>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return new HashSet<int>();
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    parents[(int)entry.th32ProcessID] = entry.th32ParentProcessID;
                } while (Process32Next(snapshot, ref entry));
            }
        }
        finally { CloseHandle(snapshot); }

        var result = new HashSet<int>();
        var current = (uint)Environment.ProcessId;
        while (parents.TryGetValue((int)current, out var parent) && parent != 0 && parent != current)
        {
            result.Add((int)parent);
            current = parent;
        }
        return result;
    }

    // 终止目标进程：祖先进程只终止自身（不走树），其余连同子进程树一起终止。
    // 返回 false 时 error 给出原因。
    public static bool TryKill(Process p, HashSet<int> ancestors, out string error)
    {
        error = "";
        int pid;
        string name;
        try { pid = p.Id; name = p.ProcessName; }
        catch (Exception ex) { error = ex.Message; return false; }

        if (pid == Environment.ProcessId)
        {
            error = "是调用者自身";
            return false;
        }
        var isAncestor = ancestors.Contains(pid);
        try
        {
            if (!p.HasExited)
            {
                if (isAncestor) p.Kill();
                else p.Kill(entireProcessTree: true);
            }
            if (!p.WaitForExit(5_000))
            {
                error = "等待退出超时";
                return false;
            }
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // 目标树内含调用者：退化为只终止该进程本身
            try
            {
                if (!p.HasExited) p.Kill();
                p.WaitForExit(5_000);
                error = "";
                return true;
            }
            catch (Exception ex2) { error = $"{ex.Message} / {ex2.Message}"; return false; }
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
