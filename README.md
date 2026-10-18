# Aria2 Tray

Aria2 Tray is a Windows tray client for an [aria2](https://aria2.github.io/) instance that runs on another machine, such as a NAS, a router or a server. Selecting the tray icon opens a panel above the taskbar with the global speed and every download. The panel closes when it loses focus. The visual design follows the macOS Control Center.

Aria2 Tray does not run aria2 itself. It connects to the aria2 JSON-RPC interface over WebSocket.

[中文说明](README.zh-CN.md)

## Features

| Feature | Description |
|---|---|
| Tray icon | Shows a progress ring with the combined progress of all active downloads. The tooltip shows the download and upload speed. |
| Speed chart | Download and upload speed of the last 60 seconds. |
| Downloads | Tabs for active, waiting, seeding and finished downloads; the last tab is remembered. Pages with a configurable number of downloads. Each download shows its progress, size, speed and remaining time. Long names are shortened in the middle; hovering shows the thumbnail and the full name. |
| Thumbnails | Thumbnails of downloaded files, provided by Windows, in the hover tooltip and on the details page. Requires File location in Settings. |
| Context menu | Details, pause or resume, move to the top or bottom of the queue, open the file, show it in File Explorer, copy the name, link or location, remove. |
| Sorting | Default order, name, size, progress or speed, ascending or descending. |
| Actions | Pause, resume and remove a download; pause or resume a whole group; stop seeding; clear finished downloads. |
| Details | Status, size, speed, connections, location, seeders, ratio, info hash, error message and the file list. |
| New download | Links and magnet links, one per line, and `.torrent` files. Files and links can be dropped onto the panel. Links on the clipboard are filled in. |
| Notifications | A system notification when a download finishes or fails. |
| Appearance | Follows the Windows light or dark theme and the Windows accent color. |

## Requirements

| Item | Requirement |
|---|---|
| Operating system | Windows 10 version 2004 (build 19041) or later; Windows 11 recommended |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| aria2 | RPC enabled (`enable-rpc=true`), reachable from this computer (`rpc-listen-all=true`) |

## Setup

1. Start `Aria2Tray.exe`. The settings window opens on first start.
2. Enter the RPC address, for example `http://192.168.1.10:6800/jsonrpc`. `ws://` and `wss://` addresses are accepted as well.
3. Enter the RPC secret (`rpc-secret`), if aria2 has one. The secret is stored only on the local computer, encrypted for the current Windows user (`%LOCALAPPDATA%\Aria2Tray\settings.json`).
4. To open downloaded files from this computer, fill in File location with the download folder as this computer reaches it (for example `\\nas\Download` or a mapped drive). The download folder on the aria2 side is detected automatically; fill it in only if downloads are saved elsewhere.
5. Save the settings. The connection is tested before the settings are saved.

The context menu of the tray icon provides: Open panel, New download, Resume all, Pause all, Settings, Start with Windows, Exit.

On Windows 11, new tray icons may be placed in the overflow area. The icon can be kept visible under Settings → Personalization → Taskbar → Other system tray icons.

## Building

Requires the .NET 10 SDK.

```
dotnet build src\Aria2Tray -c Release -p:Platform=x64
```

| Option | Purpose |
|---|---|
| `--demo` | Runs with built-in sample data, without aria2 |
| `--debug` | Enables the text debug channel (`%LOCALAPPDATA%\Aria2Tray\debug-cmd.txt` and `debug-result.json`) |
| `--lang=en`, `--lang=zh` | Sets the interface language for the current run only |

Design notes are available in [docs/design.md](docs/design.md) (Chinese).

## License

Aria2 Tray is released under the MIT License. It is not affiliated with the aria2 project.
