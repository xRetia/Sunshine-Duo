// Per-user single instance with window activation:
//   - a named mutex guards one UI process per user account
//   - a second launch connects to the running instance's pipe and sends ACTIVATE
//   - the running instance brings its window to the front (foreground rights exist there)
//   - the service path also uses ACTIVATE, so repeated steam://open/bigpicture calls
//     bring the existing window forward instead of piling up new windows

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace GameManager;

internal static class SingleInstance
{
    private static Mutex? _mutex;
    private static NamedPipeServerStream? _server;

    public static event Action? ActivateRequested;

    /// <summary>True when this process owns the single-instance slot for the current user.</summary>
    public static bool Acquire(Action onActivate)
    {
        string name = @"Local\GameManager." + Environment.UserName;
        _mutex = new Mutex(true, name, out bool createdNew);
        if (!createdNew)
        {
            SignalActivate();
            return false;
        }

        ActivateRequested += onActivate;
        _server = new NamedPipeServerStream(
            @"Local\GameManager.pipe." + Environment.UserName,
            PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        ListenLoop();
        return true;
    }

    private static void ListenLoop()
    {
        var ar = _server!.BeginWaitForConnection(_ =>
        {
            try
            {
                if (_server is not { IsConnected: true }) return;
                using var reader = new StreamReader(_server, Encoding.UTF8);
                string? line = reader.ReadLine();
                if (line == "ACTIVATE")
                {
                    ActivateRequested?.Invoke();
                }
                _server.Disconnect();
            }
            catch
            {
                // Client vanished - just restart listening.
            }
            // recreate the server for the next client
            try
            {
                _server = new NamedPipeServerStream(
                    @"Local\GameManager.pipe." + Environment.UserName,
                    PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                ListenLoop();
            }
            catch
            {
                // Shutting down.
            }
        }, null);
    }

    private static void SignalActivate()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", @"Local\GameManager.pipe." + Environment.UserName,
                PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine("ACTIVATE");
            client.WaitForPipeDrain();
        }
        catch
        {
            // Owner process is busy or gone.
        }
    }

    public static void Release()
    {
        try { _server?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
    }
}