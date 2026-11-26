using System.Runtime.InteropServices;

namespace SunshineDuoSetup;

// LogonUser 密码验证（对齐旧版 PwCheck）
internal static class WinLogon
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string pass, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    public static async Task<bool> VerifyAsync(string user, string password)
    {
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password)) return false;
        return await Task.Run(() =>
        {
            var domain = Environment.UserDomainName;
            if (string.IsNullOrEmpty(domain)) domain = ".";
            var ok = LogonUser(user, domain, password, 3 /*LOGON32_LOGON_NETWORK*/, 0, out var tok);
            if (ok) CloseHandle(tok);
            return ok;
        });
    }
}
