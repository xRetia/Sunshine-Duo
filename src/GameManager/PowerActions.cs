// Power actions behind the header power menu:
//   - LogOff / Shutdown / Restart: forced session end via the built-in
//     shutdown.exe (handles the SE_SHUTDOWN privilege itself, elevated or not).
//
// A former stream-disconnect action (force-dropping the Moonlight clients'
// TCP connections via SetTcpEntry) was removed: Moonlight connects to this
// host over IPv6, and SetTcpEntry is IPv4-only - there is no user-mode API
// to force-drop an IPv6 TCB, so the cut never reached the actual stream.

using System;
using System.Diagnostics;
using System.IO;

namespace GameManager;

internal enum PowerAction
{
    LogOff,
    Shutdown,
    Restart
}

internal static class PowerActions
{
    internal static void LogOff() => RunShutdownTool("/l");

    internal static void Shutdown() => RunShutdownTool("/s /f /t 0");

    internal static void Restart() => RunShutdownTool("/r /f /t 0");

    private static void RunShutdownTool(string arguments)
    {
        // Full path on purpose: in sessions spawned by the service (multi-seat
        // seats included) PATH may resolve differently or not at all, and a
        // bare "shutdown" then silently does nothing.
        string exe = Environment.ExpandEnvironmentVariables(
            Environment.SystemDirectory is { Length: > 0 } sysDir
                ? Path.Combine(sysDir, "shutdown.exe")
                : @"C:\Windows\System32\shutdown.exe");

        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p == null)
            {
                ServiceHost.Log("power: shutdown.exe " + arguments + " returned no process");
                return;
            }
            // short exit wait so a refused shutdown (exit code != 0) is visible
            // in install-log.txt instead of vanishing silently
            if (!p.WaitForExit(3000))
            {
                ServiceHost.Log("power: shutdown.exe " + arguments + " still running after 3s");
            }
            else
            {
                ServiceHost.Log("power: shutdown.exe " + arguments + " exit=" + p.ExitCode);
            }
        }
        catch (Exception ex)
        {
            ServiceHost.Log("power: shutdown.exe " + arguments + " failed: " + ex.Message);
        }
    }
}
