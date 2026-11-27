using System.IO.Pipes;
using System.Threading;

namespace SunshineDuoTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "SunshineDuoTray", out var fresh);
        if (!fresh)
        {
            // 已有实例：请求它打开主界面（再次运行 exe 即可呼出窗口）
            SignalExisting();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        Application.Run(new TrayContext());
    }

    private static void SignalExisting()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "SunshineDuoTray-ui", PipeDirection.Out);
            pipe.Connect(1500);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("open");
        }
        catch { }
    }
}
