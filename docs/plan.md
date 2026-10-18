# 进度和待办

设计见 `design.md`。

## 现在

v0.1 写完了，2026-10-18 推到 GitHub（公开）：https://github.com/RomeliFeng/aria2-tray

- 托盘和进度环、面板（速度曲线、按组收起和分页的任务卡片、排序、组操作、右键菜单、详情页、新建下载）、横幅、通知、设置（含文件位置映射）、缩略图、演示数据、调试通道。
- 用 `--demo` 核对过各页，中英两种语言；接用户的 aria2 核对过（做种组、分页、排序、详情、缩略图、打开文件的路径换算）。

## 待做

- 用户实际操作一遍还没试到的：新建（链接、种子、拖入、剪贴板预填）、通知、托盘进度环（要有真在下的任务）、浅色主题。
- README 截图（照 HA Tray 的 `make-screenshots.ps1`，用演示数据）。
- 发 Release：照 HA Tray 的发版步骤（`dotnet publish … -p:DebugType=none`，扫发布文件里的个人资料，打 zip），先问用户。
- 每次推送前跑 `tools\check-privacy.ps1`，并先问用户。
