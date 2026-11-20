using System.Runtime.InteropServices;
using System.Text.Json;

namespace SunshineDuoTray;

public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DATA_BLOB input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr h);

    public static string Protect(string plain)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(plain);
        var pin = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pin, bytes.Length);
        var input = new DATA_BLOB { cbData = bytes.Length, pbData = pin };
        var output = new DATA_BLOB();
        try
        {
            if (!CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                throw new InvalidOperationException("CryptProtectData failed");
            var buf = new byte[output.cbData];
            Marshal.Copy(output.pbData, buf, 0, buf.Length);
            return Convert.ToBase64String(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(pin);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    public static string Unprotect(string b64)
    {
        var bytes = Convert.FromBase64String(b64);
        var pin = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pin, bytes.Length);
        var input = new DATA_BLOB { cbData = bytes.Length, pbData = pin };
        var output = new DATA_BLOB();
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                throw new InvalidOperationException("CryptUnprotectData failed");
            var buf = new byte[output.cbData];
            Marshal.Copy(output.pbData, buf, 0, buf.Length);
            return System.Text.Encoding.UTF8.GetString(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(pin);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }
}

public class AppConfig
{
    public int ConsolePort { get; set; } = 47984;
    public int GamePort { get; set; } = 48010;
    public string WebUser { get; set; } = "";
    public string WebPassEnc { get; set; } = "";
    public bool AutoStart { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string WebPass => string.IsNullOrEmpty(WebPassEnc) ? "" : Dpapi.Unprotect(WebPassEnc);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCreds => !string.IsNullOrEmpty(WebUser);

    [System.Text.Json.Serialization.JsonIgnore]
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "trayapp.json");

    public void SetPass(string plain) => WebPassEnc = string.IsNullOrEmpty(plain) ? "" : Dpapi.Protect(plain);

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig();
        }
        catch { }
        return new AppConfig();
    }

    public void Save()
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
