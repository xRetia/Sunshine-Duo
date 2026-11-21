using System.Net;
using System.Text;
using System.Text.Json;

namespace SunshineDuoTray;

public class Device
{
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public bool Connected { get; set; }
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

