# 更新日志 / Changelog

## 0.4.0-alpha.5 — 2026-11-20

首次公开预览。以下是本版相对于先前开发预览的主要变化。 / First public preview. Changes since the preceding development preview:

- **系统字体 / System fonts:** 按微软 Windows 11 字体清单（含可选语言字体）标记家族；卸载可执行的系统字体前需要单独确认。 / Families on the Windows 11 list, including optional language fonts, are labeled and require a separate acknowledgment before an eligible uninstall.
- **管理弹窗 / Management dialogs:** 修复深色模式下预检文字与背景同色的问题，覆盖操作结果、恢复历史和选中行。 / Fixed white-on-white preflight rows in dark mode; the same fix covers results, restore history and selection states.
- **标本 / Specimen editor:** 可编辑标本使用横纵双向灰度抗锯齿。已检查深浅主题、100% 和 150% 缩放、中文小字号、选区和撤销。 / Improved grayscale antialiasing in both directions; tested light/dark themes, 100% and 150% scaling, small Chinese text, selection and undo.
- **发行 / Distribution:** 提供从公开源码重建的无签名 Windows x64 ZIP 和 SHA-256 校验文件，不含旧版安全中心或任何签名证书。 / The unsigned Windows x64 ZIP is rebuilt from public source and accompanied by a SHA-256 checksum. No legacy safety-center binaries or signing certificates are included.
