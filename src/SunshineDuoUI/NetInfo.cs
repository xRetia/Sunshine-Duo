using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SunshineDuoTray;

// 本机/公网地址查询：本机 IP 走路由探测，WAN IP 取 ip.sb（带缓存，失败不重试轰炸）
internal static class NetInfo
{
    private const string WanUrl = "https://api.ip.sb/ip";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly object Gate = new();
    private static string _wan = "";
    private static string _wanErr = "";
    private static DateTime _wanAt = DateTime.MinValue;

    public static string LocalIp()
    {
        try
        {
            using var udp = new UdpClient();
            udp.Connect("8.8.8.8", 53);
            var ep = (IPEndPoint)udp.Client.LocalEndPoint!;
            if (ep.Address.Equals(IPAddress.Any)) throw new InvalidOperationException();
            return ep.Address.ToString();
        }
        catch
        {
            return FallbackLocalIp();
        }
    }

    private static string FallbackLocalIp()
    {
        try
        {
            var best = "";
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    if (ua.Address.GetAddressBytes()[0] == 169) continue; // APIPA
                    if (ni.GetIPProperties().GatewayAddresses.Count > 0) return ua.Address.ToString();
                    if (best.Length == 0) best = ua.Address.ToString();
                }
            }
            return best.Length > 0 ? best : "未联网";
        }
        catch
        {
            return "未联网";
        }
    }

    public static async Task<(string ip, string err)> WanIpAsync(bool force)
    {
        lock (Gate)
        {
            if (!force && _wan.Length > 0 && (DateTime.Now - _wanAt).TotalSeconds < 300)
                return (_wan, "");
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, WanUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "SunshineDuoTray/1.0");
            using var resp = await Http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var body = (await resp.Content.ReadAsStringAsync()).Trim();
            var ip = body.Split('\n')[0].Trim();
            if (!IPAddress.TryParse(ip, out _))
                throw new InvalidOperationException("ip.sb 返回内容无法识别");
            lock (Gate)
            {
                _wan = ip;
                _wanErr = "";
                _wanAt = DateTime.Now;
            }
            return (ip, "");
        }
        catch (Exception e)
        {
            var msg = e is TaskCanceledException ? "获取超时" : e.Message;
            lock (Gate)
            {
                _wanErr = msg;
                _wanAt = DateTime.Now; // 失败也节流，避免离线时反复请求
            }
            // 离线时仍回吐上一次成功结果，界面不至于空白
            lock (Gate) return (_wan, msg);
        }
    }
}
