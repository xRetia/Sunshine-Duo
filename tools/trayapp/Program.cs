using System.Threading;

namespace SunshineDuoTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "SunshineDuoTray", out var fresh);
        if (!fresh) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
