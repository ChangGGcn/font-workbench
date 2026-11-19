# Windows 界面与文字排版：本次实现依据

本轮核对 Microsoft Learn 的原始文档，并用 VS 2022、Windows SDK 和运行中的 WPF 窗口验证实现。界面沿用 WPF；没有为了得到 Windows 外观而把字体管理引擎迁到浏览器。

| 微软资料 | 在本工具中的决定 |
|---|---|
| [WPF .NET 9：Fluent theme 与 ThemeMode](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90) | 用 .NET 10 WPF 自带的 Fluent 控件与系统/浅/深三种主题。主题改变只影响应用，不改 Windows 设置。 |
| [Color in Windows](https://learn.microsoft.com/en-us/windows/apps/design/style/color) | 主面板为无彩色底，选择、焦点及关键操作使用 Windows 强调色。文本、边界和错误提示不单靠颜色区分。 |
| [Acrylic](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic) · [Mica](https://learn.microsoft.com/en-us/windows/apps/design/style/mica) | 采用用户允许的纯色路线。长期显示的小号字体表格、编辑画布不叠半透明材质，不把 Win11 的材质当作 Win10 必需功能。 |
| [ClearType 与 WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/cleartype-registry-settings) | UI 正文遵循 Windows 文字呈现和像素对齐；不改用户 ClearType 注册表。原生字形图是中性、多级灰度 alpha，按当前主题着墨，不在透明背景上预烘焙 RGB 彩边。 |
| [SetTypography](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-settypography) | 从所选物理字面的 GSUB/GPOS 读取可用 tag。仅为用户显式指定的特性建立 override，未指定项保留字体默认。 |
| [HitTestPoint](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittestpoint) · [HitTestTextPosition](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittesttextposition) · [HitTestTextRange](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittesttextrange) | 真正的文字与插入点、鼠标命中、折行及选择矩形共用同一个原生布局；WPF TextBox 仅负责文字、撤销、输入法和辅助功能。没有用另一款字体的平均字宽估算光标。 |

资料不构成对不同 Windows 版本、显示器或 IME 的真机验收。高对比、不同缩放或远程桌面应保留实色回退。
