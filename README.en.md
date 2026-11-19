# Font Workbench

[简体中文](README.md) | English

A Windows x64 font manager for people with large installed font libraries. Search localized family names and switch between compact lists, grids and specimens. Compare fonts from disk without installing them first.

## Download and run

1. Download the Windows x64 ZIP from [GitHub Releases](https://github.com/ChangGGcn/font-workbench/releases). This preview is **unsigned**; it contains neither an installer nor user fonts.
2. Install the x64 [.NET 10 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). The system must support DirectWrite `IDWriteFactory6`.
3. Extract the entire ZIP. Keep the EXE and DLLs together and run `FontWorkbench.exe`. Browsing and previewing fonts do not require elevation.

## What you can do

- **Find and organize:** Press `Ctrl+F` to search. Use `Ctrl` or `Shift` to select multiple families. Project shelves hold references rather than moving your files.
- **Try type:** Open local TTF, OTF, TTC and OTC files without installing them. Click the specimen to edit it, adjust size and variable axes, and toggle supported OpenType features. `Ctrl+G` opens the glyph browser. Font details describe the actual selected face, including names, version and licensing metadata.
- **Manage batches:** Disabling keeps the file; uninstalling backs it up first. Preflight lists affected files, permissions and refusals for each family. Fonts on Microsoft's Windows 11 list are labeled; uninstalling an eligible listed font requires an extra acknowledgment. Neither confirmation nor elevation overrides protected files or uncertain ownership.
- **Recover:** Use the restore dialog to undo eligible Workbench operations. A batch consists of separate family transactions, not one atomic change. Keep your backups and logs. The optional safety center for older journals is not part of the public ZIP.

Read-only diagnostics:

```powershell
dotnet ./FontWorkbench.dll --stats
dotnet ./FontWorkbench.dll --check-uninstall "Segoe UI"
```

## Build from source

Install PowerShell 7, the .NET 10 SDK, the Visual Studio 2022 *Desktop development with C++* workload and a Windows SDK. In PowerShell 7 with an initialized **x64 MSVC** environment, run from the repository root:

```powershell
./workbench/Build.ps1 -Check
./artifacts/workbench-win-x64/FontWorkbench.exe
```

`-Check` runs native, managed and isolated desktop UI checks. The UI suite needs a Windows desktop session. Uninstall and restore tests use a synthetic font and a process-private registry hive, not installed system fonts.

[Changelog](CHANGELOG.md) · [Architecture (Chinese)](workbench/ARCHITECTURE.md) · [Management and recovery](workbench/management/README.md) · [Report an issue](https://github.com/ChangGGcn/font-workbench/issues)

## License

Original project code is licensed under the [MIT License](LICENSE), Copyright (c) 2026 cHAnGe. Third-party data, toolchain notices and fonts retain their own terms; see [NOTICE](workbench/NOTICE.txt).
