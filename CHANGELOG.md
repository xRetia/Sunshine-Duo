# CHANGELOG

## v1.1.1 (2026-11-27)

- GameManager 安装/卸载 CLI 命令（--register-all/--unregister-all/--install-service/--uninstall-service）改为 stdout/stderr 输出，不再弹 MessageBox，消除 setup.exe 等待子进程超时

## v1.1.0 (2026-11-27)

- 托盘仅保留 WebUI：删除 WinForms 界面层（MainForm/全部页面/UiKit 死代码），只走 WebView2
- WebView2 运行时缺失时弹窗提示并跳转官方下载；安装向导强制检测 WebView2
- 关于页：检查更新按钮右对齐，结果用 toast 显示，作者改为开发者，主页按钮改为打开产品主页 (yk.acgdraw.com)
- 修复 R-ALT+L 执行后 R-ALT 粘住导致下次按 L 锁屏的问题
- 国内镜像：在线读取 .GITPROXY，UTC+8 判定，镜像优先直连兜底
- 发布包仅使用 moedesk_latest_win64.zip（更新流程仍保留版本化资产）
