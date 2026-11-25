using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SunshineDuoTray;

public class Device
{
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public bool Connected { get; set; }
    public long Perm { get; set; }
    public JsonElement Raw { get; set; }
}

public class ApolloClient : IDisposable
{
    private static readonly object CookieLock = new();
    private static readonly Dictionary<int, string> Cookies = new();

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private readonly Uri _uri;
    private readonly string _base;
    private readonly int _port;
    private readonly string _user;
    private readonly string _pass;

    public ApolloClient(int port, string user, string pass)
    {
        _port = port;
        _base = $"https://127.0.0.1:{port}";
        _uri = new Uri(_base);
        _user = user;
        _pass = pass;
        lock (CookieLock)
        {
            if (Cookies.TryGetValue(port, out var auth))
                _cookies.Add(_uri, new Cookie("auth", auth, "/", "127.0.0.1") { Secure = true });
        }
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            CookieContainer = _cookies,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public static void InvalidateCookies(int port)
    {
        lock (CookieLock) Cookies.Remove(port);
    }

    public string? AuthCookie => _cookies.GetCookies(_uri)["auth"]?.Value;

    private async Task<(string body, HttpStatusCode status, string err)> SendAsync(
        HttpRequestMessage req, string? json = null)
    {
        if (json != null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            return (body, resp.StatusCode, "");
        }
        catch (Exception e)
        {
            return ("", 0, e.Message);
        }
    }

    private async Task<string?> LoginAsync()
    {
        var payload = JsonSerializer.Serialize(new { username = _user, password = _pass });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/login")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var (_, status, err) = await SendAsync(req);
        if (err != "") return "无法连接 WebUI: " + err;
        if (status != HttpStatusCode.OK) return $"WebUI 登录失败（HTTP {(int)status}），请检查账号密码";
        var cookie = _cookies.GetCookies(_uri)["auth"]?.Value;
        if (cookie == null) return "WebUI 登录未返回会话 Cookie";
        lock (CookieLock) Cookies[_port] = cookie;
        return null;
    }

    public async Task<bool> Probe()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync("127.0.0.1", _port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> EnsureLoginAsync()
    {
        if (string.IsNullOrEmpty(_user)) return "尚未配置 WebUI 账号，请先在「设置」里填写";
        if (_cookies.GetCookies(_uri)["auth"] != null) return null!;
        return await LoginAsync() ?? null!;
    }

    public async Task<string> SendPin(string pin, string name = "")
    {
        var err = await EnsureLoginAsync();
        if (err != null) return err;
        var payload = JsonSerializer.Serialize(new { pin, name });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/pin");
        var (body, status, sendErr) = await SendAsync(req, payload);
        if (sendErr != "") return sendErr;
        if (status != HttpStatusCode.OK) return $"HTTP {(int)status}: {body}";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var ok = doc.RootElement.TryGetProperty("status", out var s) &&
                     (s.ValueKind == JsonValueKind.True ||
                      (s.ValueKind == JsonValueKind.String && s.GetString() == "true"));
            return ok ? null! : "设备未在等待配对，或 PIN 不正确";
        }
        catch
        {
            return body.Contains("false") ? "设备未在等待配对，或 PIN 不正确" : null!;
        }
    }

    public async Task<(List<Device> devices, string err)> ListClients()
    {
        var err = await EnsureLoginAsync();
        if (err != null) return (new List<Device>(), err);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/clients/list");
        var (body, status, sendErr) = await SendAsync(req);
        if (sendErr != "") return (new List<Device>(), sendErr);
        if (status != HttpStatusCode.OK)
            return (new List<Device>(), $"HTTP {(int)status}: {body}");
        var devices = new List<Device>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("named_certs", out var arr))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    devices.Add(new Device
                    {
                        Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Uuid = el.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "",
                        Connected = el.TryGetProperty("connected", out var c) &&
                                    (c.ValueKind == JsonValueKind.True ||
                                     (c.ValueKind == JsonValueKind.String && c.GetString() == "true")),
                        Perm = ParsePerm(el),
                        Raw = el.Clone(),
                    });
                }
            }
        }
        catch (Exception e)
        {
            return (devices, "解析失败: " + e.Message);
        }
        return (devices, "");
    }

    private static long ParsePerm(JsonElement el)
    {
        if (!el.TryGetProperty("perm", out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var sv)) return sv;
        return 0;
    }

    // Apollo 设备权限位，与 WebUI pin 页常量一致：全部输入 + 全部操作 + list/view/launch
    public const long PermAll = 119480064;

    private static JsonNode? RawField(JsonElement e, string prop, JsonNode? fallback)
    {
        if (e.ValueKind != JsonValueKind.Object) return fallback;
        if (!e.TryGetProperty(prop, out var v)) return fallback;
        if (v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return fallback;
        return JsonNode.Parse(v.GetRawText());
    }

    // Apollo 新配对设备默认仅 list|view（不能启动应用、无输入权限），
    // trayapp 配对不走 WebUI 的授权页，这里补一次 /api/clients/update 授全权。
    public async Task<string> GrantAllPermissions(Device d)
    {
        var payload = new JsonObject
        {
            ["uuid"] = d.Uuid,
            ["name"] = d.Name,
            ["display_mode"] = RawField(d.Raw, "display_mode", ""),
            ["allow_client_commands"] = RawField(d.Raw, "allow_client_commands", false),
            ["enable_legacy_ordering"] = RawField(d.Raw, "enable_legacy_ordering", false),
            ["always_use_virtual_display"] = RawField(d.Raw, "always_use_virtual_display", false),
            ["perm"] = PermAll,
            ["do"] = RawField(d.Raw, "do", new JsonArray()),
            ["undo"] = RawField(d.Raw, "undo", new JsonArray()),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/clients/update")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var (body, status, sendErr) = await SendAsync(req);
        if (sendErr != "") return sendErr;
        if (status != HttpStatusCode.OK) return $"HTTP {(int)status}: {body}";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("status", out var s) &&
                     (s.ValueKind == JsonValueKind.True ||
                      (s.ValueKind == JsonValueKind.String && s.GetString() == "true"));
            if (ok) return null!;
            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            return string.IsNullOrEmpty(msg) ? "Apollo 拒绝授权" : msg;
        }
        catch
        {
            return "授权响应解析失败: " + body;
        }
    }

    public async Task<string> Unpair(string uuid)
    {
        var err = await EnsureLoginAsync();
        if (err != null) return err;
        var payload = JsonSerializer.Serialize(new { uuid });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/clients/unpair");
        var (body, status, sendErr) = await SendAsync(req, payload);
        if (sendErr != "") return sendErr;
        if (status != HttpStatusCode.OK) return $"HTTP {(int)status}: {body}";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("status", out var s) &&
                (s.ValueKind == JsonValueKind.False ||
                 (s.ValueKind == JsonValueKind.String && s.GetString() == "false")))
                return "Apollo 拒绝解除配对";
        }
        catch { }
        return null!;
    }

    public void Dispose() => _http.Dispose();
}

