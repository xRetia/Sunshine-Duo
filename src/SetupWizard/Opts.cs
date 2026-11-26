namespace SunshineDuoSetup;

// 一次安装/卸载运行的全部选项（UI 收集后传入 Ops）
internal sealed class Opts
{
    // 安装
    public bool SingleSession = true;
    public string Adapter = "";
    public string User = "";            // 游戏用户（单会话 = 当前用户）
    public string Password = "";
    public string WebUser = "";
    public string WebUiPassword = "";
    public bool AutoLogon = true;
    public int PortMain = 48100;
    public int PortGame = 48200;
    public int Vw;
    public int Vh;
    public List<string> SudovdaResolutions = [];
    public bool Gamepad = true;
    public bool GpuAccel = true;
    public bool GameManager = true;
    public bool Reset;
    public bool TrayDesktop = true;
    public bool TrayStart = true;
    public bool TrayAuto = true;

    public bool GameEnabled => !SingleSession;
    public bool TrayAny => TrayDesktop || TrayStart || TrayAuto;

    // 卸载
    public bool UnFirewall = true;
    public bool UnGamepad = true;
    public bool UnRdpWrap = true;
    public bool UnGpuAccel = true;
    public bool UnCore = true;
    public bool UnVda;
    public bool UnGameManager = true;
    public bool UnTray = true;
}
