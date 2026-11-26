using System.Management;

namespace SunshineDuoSetup;

// 物理显卡枚举（排除虚拟显卡），对齐旧版 Get-PhysicalGpus
internal static class GpuEnum
{
    private static readonly System.Text.RegularExpressions.Regex VirtualRx =
        new(@"virtual|paravirtual|basic display|remote|mirage|llvmpipe|hyper-v|indirect|parsec|sunshine|splashtop|todesk|oray|向日葵",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static List<string> Physical()
    {
        var list = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var mo in searcher.Get())
            {
                var name = mo["Name"] as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (VirtualRx.IsMatch(name)) continue;
                if (!list.Contains(name)) list.Add(name);
            }
        }
        catch { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public static string Vendor(string name)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"nvidia|geforce|quadro|rtx|tesla", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "NVIDIA";
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"amd|radeon", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "AMD";
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"intel|iris|uhd|hd graphics", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "Intel";
        return "Other";
    }
}
