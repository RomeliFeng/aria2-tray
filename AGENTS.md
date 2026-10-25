# aria2-tray

远程 aria2 的 Windows 托盘客户端（WinUI 3 + .NET 10，不打包）。aria2 跑在别的机器上，程序经 WebSocket JSON-RPC 连它：看速度和进度、暂停、继续、移除、新建下载，样子接近 macOS 控制中心，和 HA Tray（`..\ha-tray`）同一套风格。准备在 GitHub 开源。设计见 `docs/design.md`，进度见 `docs/plan.md`，决策见 `docs/decisions.md`。

## 规则

- **开源仓库：不放任何某一家的数据。** 内网地址、密钥、邮箱、文件名、下载目录都不进仓库，文档里的例子也用编的（演示数据 `Aria2/DemoBackend.cs` 同样）。
  - **推送到 GitHub 前先跑 `pwsh tools\check-privacy.ps1`**，查当前文件、全部提交、提交说明和提交者邮箱。查到就先处理，不推。
  - 关键词清单放在本机 `%LOCALAPPDATA%\Aria2Tray\privacy-patterns.txt`，不进仓库。
- **分界线不越过**：任务、状态、下载选项都归 aria2，程序只管怎么画；不改 aria2 的全局选项（见 `docs/design.md` 第 1 节）。
- **界面文字不是草稿板**：只写名字、读数、状态、动作和要用户知道的错误，不写解释程序做法的说明，提示框也一样。界面文案用 `L.T("中文", "English")`，写在调用处。
- 小项目，文档只做引导：`README.md`（英文）、`README.zh-CN.md`、本文件、`docs/design.md`、`docs/plan.md`、`docs/decisions.md`。代码里读得出来的不写。`plan.md` 做完的条目直接删；`decisions.md` 只增不改，被推翻的标「已取代」。
- 推到 GitHub、发 Release 之前先问用户。

## 代码在哪

| 目录 / 文件 | 管什么 |
|---|---|
| `Aria2/Aria2Connection.cs` | WebSocket：调用按 id 配对、收通知、断线重连、`system.multicall` |
| `Aria2/Aria2Session.cs` | 定时取全局速度和任务列表，事件触发通知；暂停、继续、移除、新建 |
| `Aria2/DemoBackend.cs` | `--demo` 的假数据，接口和 `Aria2Session` 一样 |
| `Model/` | `Download` 一个任务（aria2 原样给的字段 + 派生的名字、进度、剩余时间）；`Format` 大小、速度、时长的写法；`SpeedHistory` 最近 60 秒 |
| `Ui/Kit.cs`、`Ui/Palette.cs` | 控件套件和颜色 |
| `Ui/PanelView.cs` | 主界面、详情页、新建下载 |
| `Ui/TrayArt.cs` | 托盘图标：箭头和进度环，现画成 `HICON` |
| `FlyoutWindow.xaml(.cs)` | 窗口、顶栏、页面切换、拖入种子、调试通道 |
| `App.xaml(.cs)` | 托盘、单实例、后端切换、通知 |
| `tools/` | `make-icons.ps1` 生成程序图标，`check-privacy.ps1` 推送前查个人资料 |

## 开发

- 构建：`dotnet build src\Aria2Tray -c Debug -p:Platform=x64`。改代码前先关掉正在跑的 Aria2Tray，否则 exe 被占用，构建失败。
- `Aria2Tray.exe --demo`：用内置假数据跑，不连 aria2。`--lang=zh|en` 只在这次运行里固定界面语言。
- `Aria2Tray.exe --debug`：文字调试通道，命令见 `docs/design.md` 第 7 节。写一次命令文件常常触发两次，命令都要能重复执行。**改了布局或文字，每一页都跑一遍 `layout` 和 `texts` 再交给用户看。不截图**，界面效果请用户看。
- 密钥由用户自己在设置窗口里填，程序用 DPAPI 加密存在 `%LOCALAPPDATA%\Aria2Tray\settings.json`。不要去读、解密或打印它。
- 程序图标由 `tools\make-icons.ps1` 生成，产物提交进仓库。
