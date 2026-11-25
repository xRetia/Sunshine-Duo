using System.IO.Pipes;
using System.Text;

namespace SunshineDuoTray;

public static class GuardPipe
{
    // SunshineDuoCore 控制管道：一条 JSON 命令 + 换行，返回一行 JSON
    private const string PipeName = "SunshineDuoCore-ctrl";

    public class PairEvent
    {
        public string Target { get; set; } = "";
        public string Payload { get; set; } = "";
    }

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
            return Encoding.UTF8.GetString(buf.ToArray());
        }
        catch (Exception e)
        {
            return System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = "SunshineDuoCore 控制通道不可用: " + e.Message });
        }
    }

    private static async Task<System.Text.Json.JsonDocument?> SendForDocAsync(string json)
    {
        var resp = await SendAsync(json);
        try
        {
            return System.Text.Json.JsonDocument.Parse(resp);
        }
        catch
        {
            return null;
        }
    }

    // 取走 SunshineDuoCore 积累的配对事件（pop 即清除）；失败返回 null
    public static async Task<List<PairEvent>?> PollEvents()
    {
        using var doc = await SendForDocAsync("{\"cmd\":\"poll_pair_events\"}");
        if (doc == null) return null;
        try
        {
            if (!doc.RootElement.GetProperty("ok").GetBoolean()) return null;
            var events = new List<PairEvent>();
            if (doc.RootElement.TryGetProperty("events", out var arr) &&
                arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var ev = new PairEvent
                    {
                        Target = el.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "",
                        Payload = el.TryGetProperty("payload", out var p) ? p.GetString() ?? "" : "",
                    };
                    if (ev.Target.Length > 0) events.Add(ev);
                }
            }
            return events;
        }
        catch
        {
            return null;
        }
    }

    public static Task<string> RestartApollo(string target)
    {
        return SendAndCheckAsync($"{{\"cmd\":\"restart_apollo\",\"target\":\"{target}\"}}");
    }

    public static Task<string> SetDisplayMode(string mode)
    {
        return SendAndCheckAsync($"{{\"cmd\":\"set_display_mode\",\"mode\":\"{mode}\"}}");
    }

    public static Task<string> SetHotkeys(bool winFromRAlt, bool winLFromRAltL)
    {
        var win = winFromRAlt ? "true" : "false";
        var winl = winLFromRAltL ? "true" : "false";
        return SendAndCheckAsync($"{{\"cmd\":\"set_hotkeys\",\"win_from_ralt\":{win},\"winl_from_ralt_l\":{winl}}}");
    }

    public static Task<string> SetHideCursor(bool enabled)
    {
        return SendAndCheckAsync($"{{\"cmd\":\"set_hide_cursor\",\"enabled\":{(enabled ? "true" : "false")}}}");
    }

    public static Task<string> SetResolution(int width, int height, string[]? resolutions = null)
    {
        return SendAndCheckAsync($"{{\"cmd\":\"set_resolution\",\"width\":{width},\"height\":{height},\"resolutions\":{System.Text.Json.JsonSerializer.Serialize(resolutions ?? Array.Empty<string>())}}}");
    }

    // 旧调用方语义：ok=true 返回 null（无错误），否则返回 error 字符串
    private static async Task<string> SendAndCheckAsync(string json)
    {
        using var doc = await SendForDocAsync(json);
        if (doc == null) return "SunshineDuoCore 控制通道响应无法解析";
        try
        {
            if (doc.RootElement.GetProperty("ok").GetBoolean()) return null!;
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "未知错误" : "未知错误";
        }
        catch
        {
            return "SunshineDuoCore 控制通道响应缺少 ok 字段";
        }
    }
}
