using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SunshineDuoTray;

public class Device
{
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public bool Connected { get; set; }
}

public class ApolloClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _user;
    private readonly string _pass;

    public ApolloClient(int port, string user, string pass)
    {
        _base = $"https://127.0.0.1:{port}";
        _user = user;
        _pass = pass;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private void Auth(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(_user))
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_pass}")));
    }

    private async Task<(string body, HttpStatusCode status, string err)> SendAsync(HttpRequestMessage req, string? json = null)
    {
        Auth(req);
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

    public async Task<bool> Probe()
    {
        var (body, status, err) = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/config"));
        return err == "" && (int)status != 0; // 401 也算在线（服务可达，仅未认证）
    }

    public async Task<string> SendPin(string pin)
    {
        var (body, status, err) = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/pin"), $"{{\"pin\":\"{pin}\"}}");
        if (status != HttpStatusCode.OK)
            return err != "" ? err : $"HTTP {(int)status}: {body}";
        // Apollo may require a client name; retry with an empty one if the bare pin failed
        if (body.Contains("false") || body.Contains("error"))
        {
            (body, status, err) = await SendAsync(
                new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/pin"), $"{{\"pin\":\"{pin}\",\"name\":\"\"}}");
            if (status != HttpStatusCode.OK)
                return err != "" ? err : $"HTTP {(int)status}: {body}";
        }
        return null!;
    }

    public async Task<(List<Device> devices, string err)> ListClients()
    {
        var (body, status, err) = await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/clients/list"));
        if (status != HttpStatusCode.OK)
            return (new List<Device>(), err != "" ? err : $"HTTP {(int)status}: {body}");
        var devices = new List<Device>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("named_certs", out var arr))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    devices.Add(new Device
                    {
                        Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Uuid = el.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "",
                        Connected = el.TryGetProperty("connected", out var c) &&
                                    (c.ValueKind == System.Text.Json.JsonValueKind.True ||
                                     (c.ValueKind == System.Text.Json.JsonValueKind.String && c.GetString() == "true")),
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
        var (body, status, err) = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/clients/unpair"),
            $"{{\"uuid\":\"{uuid}\"}}");
        if (status != HttpStatusCode.OK)
            return err != "" ? err : $"HTTP {(int)status}: {body}";
        return null!;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
