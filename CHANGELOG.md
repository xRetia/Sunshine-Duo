# CHANGELOG

## v1.1.1 (2026-11-27)

- GameManager 安装/卸载 CLI 命令（--register-all/--unregister-all/--install-service/--uninstall-service）改为 stdout/stderr 输出，不再弹 MessageBox，消除 setup.exe 等待子进程超时
- 更新清场覆盖 GameViewerService（gv_hook 注入宿主）与 GameManagerService，停服后循环验证全部目标 PID 退出，30 秒未清空即中止替换，避免半更新
- 修复托盘启动 setup.exe 更新时清场必然失败的问题：托盘是 setup.exe 父进程，Kill(entireProcessTree) 抛"调用者在目标树内"异常；祖先进程改为只终止自身（ProcGuard）
- 更新后按更新前状态恢复 GameViewerService/GameManagerService/旧服务，核心服务与托盘照常拉起（easytier/sunshine 由核心自动拉起）
- 同一进程终止失败只告警一次，不再刷屏

## v1.1.0 (2026-11-27)

- 托盘仅保留 WebUI：删除 WinForms 界面层（MainForm/全部页面/UiKit 死代码），只走 WebView2
- WebView2 运行时缺失时弹窗提示并跳转官方下载；安装向导强制检测 WebView2
- 关于页：检查更新按钮右对齐，结果用 toast 显示，作者改为开发者，主页按钮改为打开产品主页 (yk.acgdraw.com)
- 修复 R-ALT+L 执行后 R-ALT 粘住导致下次按 L 锁屏的问题
- 国内镜像：在线读取 .GITPROXY，UTC+8 判定，镜像优先直连兜底
- 发布包仅使用 moedesk_latest_win64.zip（更新流程仍保留版本化资产）
