using System.IO.Pipes;
using System.Text;

namespace SunshineDuoTray;

public static class GuardPipe
{
    // wsguard 控制管道：一条 JSON 命令 + 换行，返回一行 JSON
    private const string PipeName = "wsguard-ctrl";

    public static async Task<string> SendAsync(string json)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(cts.Token);
            var req = Encoding.UTF8.GetBytes(json + "\n");
            await pipe.WriteAsync(req);
            await pipe.FlushAsync();

            var buf = new MemoryStream();
            var chunk = new byte[1024];
            while (buf.Length < 4096)
            {
                var n = await pipe.ReadAsync(chunk);
                if (n == 0) break;
                var idx = Array.IndexOf(chunk, (byte)'\n', 0, n);
                if (idx >= 0) { buf.Write(chunk, 0, idx); break; }
                buf.Write(chunk, 0, n);
            }
            var resp = Encoding.UTF8.GetString(buf.ToArray());
            using var doc = System.Text.Json.JsonDocument.Parse(resp);
            return doc.RootElement.GetProperty("ok").GetBoolean() ? null! : (doc.RootElement.TryGetProperty("error", out var e) ? e.GetString()! : resp);
        }
        catch (Exception e)
        {
            return "wsguard 控制通道不可用: " + e.Message;
        }
    }

    public static Task<string> RestartApollo(string target) =>
        SendAsync($"{{\"cmd\":\"restart_apollo\",\"target\":\"{target}\"}}");

    public static Task<string> SetDisplayMode(string mode) =>
        SendAsync($"{{\"cmd\":\"set_display_mode\",\"mode\":\"{mode}\"}}");

    public static Task<string> SetResolution(int width, int height, string[]? resolutions = null) =>
        SendAsync($"{{\"cmd\":\"set_resolution\",\"width\":{width},\"height\":{height},\"resolutions\":{System.Text.Json.JsonSerializer.Serialize(resolutions ?? Array.Empty<string>())}}}");
}
