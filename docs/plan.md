# 进度和待办

设计见 `design.md`。

## 现在

v0.1 写完了，2026-10-18 推到 GitHub（公开）：https://github.com/RomeliFeng/aria2-tray

- 托盘和进度环、面板（速度曲线、标签页和分页的任务卡片、排序、组操作、右键菜单、详情页、新建下载）、横幅、通知、设置（含文件位置映射）、缩略图、演示数据、调试通道。
- 用 `--demo` 核对过各页，中英两种语言；接用户的 aria2 核对过（做种组、分页、排序、详情、缩略图、打开文件的路径换算）。

已发布 v0.1.0（2026-10-18）：https://github.com/RomeliFeng/aria2-tray/releases/tag/v0.1.0，x64 和 ARM64 两个 zip。x64 发布版实测能启动；ARM64 包没在实机上试过。

## 待做

- 用户实际操作一遍还没试到的：新建（链接、种子、拖入、剪贴板预填）、通知、托盘进度环（要有真在下的任务）、浅色主题。
- 开机启动：注册表 Run 里有值，但任务管理器的「启动应用」里看不到，原因还没查（HA Tray 一样）。
- README 截图（照 HA Tray 的 `make-screenshots.ps1`，用演示数据）。
- 发版步骤：`dotnet publish src\Aria2Tray -c Release -p:Platform=x64 -r win-x64 --self-contained false -p:DebugType=none -p:DebugSymbols=false -o dist\build-win-x64`（ARM64 同理），实测能启动，按 UTF-8 和 UTF-16 两种编码扫发布文件里的个人资料，LICENSE 放进去，打成「Aria2Tray 文件夹」的 zip，发布说明附 SHA-256。`DebugType=none` 不能省：调试信息会把本机路径写进 exe。
- 每次推送、发布前都先跑 `tools\check-privacy.ps1`，并先问用户。
