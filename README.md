# 字体工作台 · Font Workbench

简体中文 | [English](README.en.md)

一款专为大型已安装字体库设计的 Windows x64 字体管理工具。按家族或本地化名称搜索，在紧凑列表、网格和样本视图之间切换；不必逐个安装下载的字体才能比较效果。

## 下载与启动

1. 在 [GitHub Releases](https://github.com/ChangGGcn/font-workbench/releases) 下载 Windows x64 ZIP。此预览包**未签名**，不含安装器或用户字体。
2. 安装 x64 [.NET 10 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。系统需支持 DirectWrite `IDWriteFactory6`。
3. 完整解压，保留 EXE 和 DLL 在同一目录，运行 `FontWorkbench.exe`。日常浏览和预览不需要管理员权限。

## 使用

- **查找与整理：**`Ctrl+F` 搜索，`Ctrl` / `Shift` 多选；用项目清单保存家族引用，不移动原文件。
- **排版预览：**打开本地 TTF、OTF、TTC 或 OTC，无需安装。点击标本直接输入，调整字号、可变轴及字体支持的 OpenType 特性。`Ctrl+G` 打开字形浏览器。「字体信息」显示当前物理字面的版本、命名及许可等元数据。
- **批量管理：**停用保留文件；卸载先备份。预检逐项列出影响文件、权限与拒绝原因。微软 Windows 11 清单内的系统字体会标记；可卸载的系统字体需要另行勾选风险提示。受保护或归属不明的文件不会因确认或提权而放行。
- **恢复与边界：**从「恢复…」恢复工作台自己的操作。批次按家族分别执行，并非整批原子事务。请保留备份及日志。公开 ZIP 不含用于旧版日志的可选安全中心。

只读排查，不修改字体：

```powershell
dotnet ./FontWorkbench.dll --stats
dotnet ./FontWorkbench.dll --check-uninstall "Segoe UI"
```

## 从源码构建

需要 PowerShell 7、.NET 10 SDK、VS 2022 的「使用 C++ 的桌面开发」工作负载和 Windows SDK。在已初始化 **x64 MSVC** 环境的 PowerShell 7 中，从仓库根目录执行：

```powershell
./workbench/Build.ps1 -Check
./artifacts/workbench-win-x64/FontWorkbench.exe
```

`-Check` 运行原生、托管和隔离桌面 UI 回归。UI 测试需要有桌面的 Windows 会话；卸载/恢复测试使用自制字体和进程私有注册表，不会卸载系统字体。

[更新日志](CHANGELOG.md) · [架构](workbench/ARCHITECTURE.md) · [管理与恢复](workbench/management/README.md) · [反馈问题](https://github.com/ChangGGcn/font-workbench/issues)

## 许可

项目原创代码采用 [MIT License](LICENSE)，Copyright (c) 2026 cHAnGe。第三方数据、工具链声明和字体本身不因本项目而改为 MIT，详见 [NOTICE](workbench/NOTICE.txt)。
