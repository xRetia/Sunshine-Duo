// Windows service host (runs as SYSTEM via SCM with "GameManager --service").
//
// Listens on a named pipe for SHOW signals sent by the steam:// protocol handler.
// When a signal arrives, the service spawns "GameManager --show" inside the
// calling user's session using its own SYSTEM token (higher than admin rights),
// so every seat/user on the machine gets an elevated Game Manager window.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace GameManager;

internal static class ServiceHost
{
    internal const string PipeName = "GameManagerShow";
    internal const string ServiceName = "GameManagerService";
    internal const string ServiceDisplayName = "Game Manager Service";

    /// <summary>Diagnostic log next to the exe (install/service tracing).</summary>
    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "install-log.txt"),
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                System.Text.Encoding.UTF8);
        }
        catch
        {
            // Best effort only.
        }
    }

    // ---------------- service entry point (--service) ----------------

    private static ServiceMainDelegate? _serviceMain;
    private static HandlerDelegate? _handler;
    private static IntPtr _statusHandle;
    private static ManualResetEvent? _stopEvent;
    private static CancellationTokenSource? _cts;
    private static readonly Dictionary<int, DateTime> LastShowPerSession = new();

    public static void Run()
    {
        Log("ServiceHost.Run() (SCM dispatch) pid=" + Environment.ProcessId);
        _serviceMain = ServiceMainProc;
        _handler = HandlerProc;
        var table = new SERVICE_TABLE_ENTRY[2];
        table[0].lpServiceName = Marshal.StringToHGlobalUni(ServiceName);
        table[0].lpServiceProc = Marshal.GetFunctionPointerForDelegate(_serviceMain);
        StartServiceCtrlDispatcher(table); // blocks until ServiceMainProc returns
    }

    private static void ServiceMainProc(int argc, IntPtr argv)
    {
        Log("ServiceMain entered");
        _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, _handler!, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            Log("ServiceMain: RegisterServiceCtrlHandlerEx failed, win32 error " + Marshal.GetLastWin32Error());
            return;
        }

        _stopEvent = new ManualResetEvent(false);
        _cts = new CancellationTokenSource();
        SetServiceState(ServiceState.Running, acceptStop: true);

        var listener = new Thread(ListenLoop) { IsBackground = true, Name = "GameMgrPipe" };
        listener.Start();

        _stopEvent.WaitOne();
        _cts.Cancel();
        SetServiceState(ServiceState.Stopped, acceptStop: false);
    }

    private static int HandlerProc(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ServiceControl.Stop:
                _stopEvent?.Set();
                break;
            case ServiceControl.Interrogate:
                break;
        }
        return 0; // NO_ERROR
    }

    private static void SetServiceState(ServiceState state, bool acceptStop)
    {
        var status = new SERVICE_STATUS
        {
            dwServiceType = ServiceWin32OwnProcess,
            dwCurrentState = (int)state,
            dwControlsAccepted = acceptStop ? ServiceAcceptStop : 0,
            dwWin32ExitCode = 0,
            dwServiceSpecificExitCode = 0,
            dwCheckPoint = 0,
            dwWaitHint = 0
        };
        SetServiceStatus(_statusHandle, ref status);
    }

    // ---------------- pipe listener ----------------

    private static void ListenLoop()
    {
        while (!_cts!.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));

                server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 1024, 1024, security);

                server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();

                using (server)
                {
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? line = reader.ReadLine();
                    HandleCommand(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                server?.Dispose();
                Thread.Sleep(500); // pipe might be in a bad state; retry shortly
            }
        }
    }

    private static void HandleCommand(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        string[] parts = line.Split(' ');
        if (parts.Length < 2 || !parts[0].Equals("SHOW", StringComparison.OrdinalIgnoreCase)) return;
        if (!int.TryParse(parts[1], out int sessionId)) return;

        lock (LastShowPerSession)
        {
            if (LastShowPerSession.TryGetValue(sessionId, out DateTime last) &&
                (DateTime.UtcNow - last).TotalMilliseconds < 2500)
            {
                return; // debounce double launches of the same URL
            }
            LastShowPerSession[sessionId] = DateTime.UtcNow;
        }

        LaunchUiInSession(sessionId);
    }

    /// <summary>Spawns the UI as the target session's user, with that user's elevated (admin)
    /// token when available - never as SYSTEM.</summary>
    private static void LaunchUiInSession(int sessionId)
    {
        Log("LaunchUiInSession session=" + sessionId);
        try
        {
            // 1) primary token of the user logged into that session
            if (!WTSQueryUserToken(sessionId, out IntPtr hUserToken))
            {
                Log("LaunchUiInSession: WTSQueryUserToken failed, win32 error " + Marshal.GetLastWin32Error());
                return;
            }

            IntPtr hTokenToUse = GetElevatedLinkedToken(hUserToken, out bool usedLinked);
            try
            {
                // 2) make a primary token out of it
                if (!DuplicateTokenEx(hTokenToUse, TokenAllAccess, IntPtr.Zero,
                        SecurityImpersonation, TokenPrimary, out IntPtr hPrimary))
                {
                    Log("LaunchUiInSession: DuplicateTokenEx failed, win32 error " + Marshal.GetLastWin32Error());
                    return;
                }

                try
                {
                    // 3) the user's own environment (TEMP, profile, ...)
                    CreateEnvironmentBlock(out IntPtr hEnv, hPrimary, true);
                    try
                    {
                        var si = new STARTUPINFO
                        {
                            cb = Marshal.SizeOf<STARTUPINFO>(),
                            lpDesktop = "winsta0\\default"
                        };
                        string command = "\"" + Launcher.ExePath + "\" --show";
                        var pi = new PROCESS_INFORMATION();

                        bool ok = CreateProcessAsUser(hPrimary, null, command, IntPtr.Zero, IntPtr.Zero,
                            false, NormalPriorityClass | CreateUnicodeEnvironment, hEnv,
                            Path.GetDirectoryName(Launcher.ExePath) ?? @"C:\", ref si, out pi);
                        Log("LaunchUiInSession: CreateProcessAsUser ok=" + ok +
                            " (linked-elevated=" + usedLinked + "), win32 error " + Marshal.GetLastWin32Error());
                        if (ok)
                        {
                            CloseHandle(pi.hProcess);
                            CloseHandle(pi.hThread);
                        }
                    }
                    finally
                    {
                        if (hEnv != IntPtr.Zero) DestroyEnvironmentBlock(hEnv);
                    }
                }
                finally
                {
                    CloseHandle(hPrimary);
                }
            }
            finally
            {
                if (usedLinked) CloseHandle(hTokenToUse);
                CloseHandle(hUserToken);
            }
        }
        catch
        {
            // Never crash the service.
        }
    }

    /// <summary>Returns the linked (elevated) token of an admin user, or the token itself.
    /// For plain users there is no elevated token, so the UI simply runs at normal rights.</summary>
    private static IntPtr GetElevatedLinkedToken(IntPtr token, out bool usedLinked)
    {
        var linked = new TOKEN_LINKED_TOKEN();
        if (GetTokenInformation(token, TokenLinkedTokenClass, ref linked,
                Marshal.SizeOf<TOKEN_LINKED_TOKEN>(), out _) && linked.LinkedToken != IntPtr.Zero)
        {
            usedLinked = true;
            return linked.LinkedToken;
        }
        usedLinked = false;
        return token;
    }

    // ---------------- client side (used by the protocol handler in the user session) ----------------

    /// <summary>Signals the service to show the elevated UI in this session. True when delivered.</summary>
    public static bool TrySignalShow()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            client.Connect(1500);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine("SHOW " + System.Diagnostics.Process.GetCurrentProcess().SessionId);
            client.WaitForPipeDrain();
            return true;
        }
        catch
        {
            return false; // service not installed / not running
        }
    }

    // ---------------- install / uninstall ----------------

    public static void Install()
    {
        Log("Install() start, exe=" + Launcher.ExePath);
        IntPtr scm = OpenSCManager(null, null, ScManagerAllAccess);
        if (scm == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Log("Install: OpenSCManager failed, win32 error " + err);
            ReportError("Administrator rights are required to install the service.\nPlease accept the UAC prompt.");
            return;
        }
        Log("Install: OpenSCManager ok");
        try
        {
            string command = "\"" + Launcher.ExePath + "\" --service";
            Log("Install: CreateService command=" + command);
            IntPtr svc = CreateService(scm, ServiceName, ServiceDisplayName, ServiceAllAccess,
                ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal,
                command, null, IntPtr.Zero, null, "LocalSystem", null);
            if (svc == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Log("Install: CreateService failed, win32 error " + error);
                if (error == ErrorServiceExists)
                {
                    Log("Install: service exists, opening existing");
                    svc = OpenService(scm, ServiceName, ServiceAllAccess);
                    if (svc == IntPtr.Zero)
                    {
                        Log("Install: OpenService failed, win32 error " + Marshal.GetLastWin32Error());
                        ReportError("Administrator rights are required to open the existing service.\nPlease accept the UAC prompt.");
                        return;
                    }
                }
                else
                {
                    ReportError("Operation failed (Win32 error " + error + ").");
                    return;
                }
            }
            else
            {
                Log("Install: CreateService ok");
            }
            try
            {
                bool started = StartService(svc, 0, Array.Empty<string>());
                Log("Install: StartService ok=" + started + ", win32 error " + Marshal.GetLastWin32Error());
                ReportInfo(ServiceDisplayName + " installed and started (runs as SYSTEM).");
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static void Uninstall()
    {
        Log("Uninstall() start");
        IntPtr scm = OpenSCManager(null, null, ScManagerAllAccess);
        if (scm == IntPtr.Zero)
        {
            ReportError("Administrator rights are required to remove the service.\nPlease accept the UAC prompt.");
            return;
        }
        try
        {
            IntPtr svc = OpenService(scm, ServiceName, ServiceAllAccess | DeleteAccess);
            if (svc == IntPtr.Zero)
            {
                ReportInfo("The service is not installed.");
                return;
            }
            try
            {
                // best-effort stop, then delete
                var status = new SERVICE_STATUS();
                ControlService(svc, ServiceControlStop, ref status);
                Thread.Sleep(800);
                if (!DeleteService(svc))
                {
                    ReportError("Operation failed (Win32 error " + Marshal.GetLastWin32Error() + ").");
                    return;
                }
                ReportInfo(ServiceDisplayName + " removed.");
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static void ReportInfo(string message)
    {
        try
        {
            if (Launcher.CliMode || Console.IsOutputRedirected)
            {
                Console.WriteLine(message);
                return;
            }
        }
        catch { }
        System.Windows.MessageBox.Show(message, "Game Manager",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private static void ReportError(string message)
    {
        try
        {
            if (Launcher.CliMode || Console.IsOutputRedirected)
            {
                Console.Error.WriteLine(message);
                return;
            }
        }
        catch { }
        System.Windows.MessageBox.Show(message, "Game Manager",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    // ---------------- constants ----------------

    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAllAccess = 0xF01FF;
    private const uint TokenPrimary = 1;
    private const uint SecurityImpersonation = 2; // SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation
    private const uint NormalPriorityClass = 0x20;
    private const uint CreateUnicodeEnvironment = 0x400;
    private const int TokenLinkedTokenClass = 19;

    private const int ServiceWin32OwnProcess = 0x10;
    private const int ServiceAcceptStop = 1;
    private const int ServiceControlStop = 1;
    private const int ServiceAutoStart = 2;
    private const int ServiceErrorNormal = 1;
    private const int ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint DeleteAccess = 0x10000;
    private const int ErrorServiceExists = 1073;
    private const int TokenSessionId = 12;

    private enum ServiceState { Stopped = 1, Running = 4 }
    private static class ServiceControl { public const int Stop = 1; public const int Interrogate = 4; }

    // ---------------- interop ----------------

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ServiceMainDelegate(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int HandlerDelegate(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_TABLE_ENTRY
    {
        public IntPtr lpServiceName;
        public IntPtr lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_LINKED_TOKEN
    {
        public IntPtr LinkedToken;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass,
        ref TOKEN_LINKED_TOKEN info, int infoLength, out int returnLength);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inheritExisting);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcher([In] SERVICE_TABLE_ENTRY[] table);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName,
        HandlerDelegate handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr tokenAttributes,
        uint impersonationLevel, uint tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass,
        ref int info, int infoLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string applicationName, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateService(IntPtr scm, string serviceName, string displayName,
        uint access, int serviceType, int startType, int errorControl, string binaryPathName,
        string loadOrderGroup, IntPtr tagId, string dependencies, string accountName, string password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartService(IntPtr service, int numArgs, string[] args);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, int control, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
