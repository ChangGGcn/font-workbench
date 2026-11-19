using System.ComponentModel;

using System.Windows;
using FontBook.Workbench.Session;

namespace FontBook.Workbench;

public partial class MainWindow
{
    private readonly SessionFontSet _session = new(new WindowsSessionBackend());
    private bool _sessionBusy;

    private async void ManageSession()
    {
        if (_sessionBusy)
            return;
        var active = _session.ActivePaths;
        string? current = _face?.Path;
        bool canAdd = current is not null && _filePaths?.Contains(current, StringComparer.OrdinalIgnoreCase) == true && !active.Contains(current, StringComparer.OrdinalIgnoreCase);
        if (!canAdd && active.Count == 0)
        {
            MessageBox.Show(this, "先用“免安装预览”选择本地字体文件。需要其他桌面应用使用它时，再来这里显式开启试用。只看标本不需要启用。", "会话试用", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        const string add = "启用当前预览文件";
        var choices = new List<string>();
        if (canAdd)
            choices.Add(add);
        choices.AddRange(active.Select(path => "释放：" + path));
        var option = WorkbenchDialog.Prompt(this, "管理会话试用", "只管理本工作台本次成功添加的引用。", choices[0], choices);
        if (option is null)
            return;
        bool adding = option == add;
        string path = adding ? current! : option[3..];
        var message = adding ? $"临时向支持的 Windows 应用提供整个字体文件？集合中的所有字面都会一起启用。\n\n{path}\n\n不复制、不永久安装、不写注册表。某些应用需要重启。正常关闭工作台时会要求释放；异常退出后如果字体仍可用，可能需要重启 Windows。" : $"释放本次工作台对以下文件的试用引用？不会删除文件。已打开的文档可能需要重新载入。\n\n{path}";
        if (MessageBox.Show(this, message, "确认会话字体变更", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        SetSessionBusy(true);
        try
        {
            if (adding)
            {
                var n = await Task.Run(() => _session.Activate(path));
                StatusText.Text = $"Windows 已接受 {n} 个字体资源；其他应用的可见性取决于其字体缓存。";
            }
            else
            {
                bool released = await Task.Run(() => _session.Release(path));
                StatusText.Text = released ? "已释放本次引用，字体文件未被删除。" : "Windows 未释放引用，已保留记录，可稍后重试。";
            }
        }
        catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetSessionBusy(false); }
    }
    private void SetSessionBusy(bool busy)
    {
        _sessionBusy = busy;
        SessionButton.IsEnabled = !busy;
        int count = _session.ActivePaths.Count;
        SessionButton.Content = count == 0 ? "会话试用" : $"会话试用 · {count}";
    }
    private async void OnClosingSession(object? sender, CancelEventArgs e)
    {
        if (_sessionBusy)
        {
            e.Cancel = true;
            StatusText.Text = "请等待当前字体操作完成。";
            return;
        }
        var paths = _session.ActivePaths;
        if (paths.Count == 0)
            return;
        e.Cancel = true;
        if (MessageBox.Show(this, $"释放本工作台临时启用的 {paths.Count} 个字体文件，然后退出？不会删除文件。", "关闭工作台", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        SetSessionBusy(true);
        try
        {
            int failures = await Task.Run(() =>
            {
                int count = 0;
                foreach (var path in paths)
                {
                    try { if (!_session.Release(path)) count++; }
                    catch (Exception) { count++; } // The service retains this pin for a later retry.
                }
                return count;
            });
            if (failures != 0)
            {
                MessageBox.Show(this, $"{failures} 项尚未释放。工作台将保留记录，请关闭使用字体的应用后重试。", "暂不退出", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Do not call Close while the original WPF Closing event is on the stack.
            _ = Dispatcher.InvokeAsync(Close);
        }
        finally { SetSessionBusy(false); }
    }
}
