using System.Diagnostics;
using System.IO;
using System.Windows;
using FontBook.Workbench.Collections;

namespace FontBook.Workbench;

public partial class MainWindow
{
    private void UpdateShelfButtons()
    {
        NewShelfButton.IsEnabled = !_shelfBusy && _shelvesReady;
        RenameShelfButton.IsEnabled = DeleteShelfButton.IsEnabled = NewShelfButton.IsEnabled && _scope != null;
        var family = FamilyList.SelectedItem as FamilyRow;
        ToggleMemberButton.IsEnabled = _filePaths is null && NewShelfButton.IsEnabled && family is { Key.Length: > 0 and <= 512 } && FamilyList.SelectedItems.OfType<FamilyRow>().All(r => r.Key.Length is > 0 and <= 512);
        ToggleMemberButton.Content = _scope is null ? "加入清单" : "移出清单";
        System.Windows.Automation.AutomationProperties.SetName(ToggleMemberButton, (string)ToggleMemberButton.Content);
        ToggleMemberButton.ToolTip = family is { Key.Length: 0 } ? "家族标识不唯一，禁止归入清单。" : "只更改清单，不启用或停用系统字体。";
    }
    private async Task UseStore(Func<ShelfSnapshot, ShelfSnapshot> work, string? desiredScope = null, bool changeScope = false)
    {
        if (_shelfBusy || _closed)
            return;
        _shelfBusy = true;
        UpdateShelfButtons();
        var prior = _shelves;
        try
        {
            var next = await Task.Run(() => work(prior));
            if (_closed)
                return;
            _shelves = next;
            _shelvesReady = true;
            var target = changeScope ? desiredScope : _scope;
            if (target != null && !next.Shelves.ContainsKey(target))
                target = null;
            _syncingShelves = true;
            try
            {
                ShelfList.ItemsSource = next.Shelves.Keys.ToArray();
                ShelfList.SelectedItem = target;
            }
            finally { _syncingShelves = false; }
            ShelfHint.Text = next.Shelves.Count == 0 ? "新建清单，按项目收藏字体。" : "清单只保存引用，不改变系统字体。";
            if (_scope != target || target != null)
                ShowInstalled(target);
        }
        catch (Exception ex)
        {
            if (_closed)
                return;
            // A damaged or concurrently modified file is never overwritten by a stale in-memory view.
            _shelvesReady = false;
            ShelfHint.Text = $"清单未更新：{ex.Message}。请刷新重新读取。";
            RetryButton.Visibility = Visibility.Visible;
        }
        finally { _shelfBusy = false; if (!_closed) UpdateShelfButtons(); }
    }
    private void AddShelf()
    {
        var name = WorkbenchDialog.Prompt(this, "新建字体清单", "清单名称", "")?.Trim();
        if (string.IsNullOrEmpty(name))
            return;
        _ = UseStore(s => _store.Create(name, s.Revision), name, true);
    }
    private void RenameShelf()
    {
        if (_scope is not { } old)
            return;
        var name = WorkbenchDialog.Prompt(this, "重命名清单", "新名称", old)?.Trim();
        if (string.IsNullOrEmpty(name))
            return;
        _ = UseStore(s => _store.Rename(old, name, s.Revision), name, true);
    }
    private void DeleteShelf()
    {
        if (_scope is not { } old)
            return;
        if (MessageBox.Show(this, $"删除清单“{old}”？字体文件、系统注册和其他清单不会改变。", "删除清单", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            _ = UseStore(s => _store.Delete(old, s.Revision), null, true);
    }
    private void ToggleMembership()
    {
        if (_filePaths != null)
            return;
        var selected = FamilyList.SelectedItems.OfType<FamilyRow>().ToArray();
        if (selected.Length == 0 || selected.Any(row => row.Key.Length is 0 or > 512))
            return;
        var keys = selected.Select(row => row.Key).ToArray();
        if (_scope is { } old)
        {
            _ = UseStore(s => _store.SetMembers(old, keys, false, s.Revision));
            return;
        }
        var names = _shelves.Shelves.Keys.ToArray();
        var target = names.Length == 0 ? WorkbenchDialog.Prompt(this, "收藏到新清单", "第一个清单的名称", "")?.Trim()
            : WorkbenchDialog.Prompt(this, "加入字体清单", $"将选中的 {keys.Length} 个家族加入：", names[0], names);
        if (!string.IsNullOrEmpty(target))
            _ = UseStore(s => _store.SetMembers(target, keys, true, s.Revision, names.Length == 0), target, true);
    }
    private void SetSpecimen()
    {
        string[] presets = ["永 字 Aa 123", "Hamburgefontsiv 0123456789", "春风又绿江南岸，明月何时照我还。", "العربية  •  עברית  •  हिन्दी"];
        var text = WorkbenchDialog.Prompt(this, "标本设置", "选择本次会话的样例；也可直接在标本文字框编辑。", presets.Contains(SampleBox.Text) ? SampleBox.Text : presets[0], presets);
        if (text != null)
            SampleBox.Text = text;
    }
    private void OpenSafetyCenter()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "safety-center", "FontBook.exe");
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "此构建未附安全操作中心。请用带已校验安全包的发行目录。工作台不会尝试修改系统字体或原版保险库。", "安全操作中心", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, "打开独立安全操作中心？请在那里重新核对字体和操作历史。工作台不会传递命令、修改字体或请求提权。", "安全操作中心", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "安全操作中心未启动", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
