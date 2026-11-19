using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using FontBook.Management;

namespace FontBook.Workbench;

public partial class MainWindow
{
    private AdminReviewSelection? _incomingReview;
    private bool _applyingReview;
    private Task? _pendingUac;
    internal static string CurrentReviewSid()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User?.Value ?? throw new InvalidOperationException("无法读取账户 SID。");
    }
    internal static bool IsReviewAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
    internal void SetIncomingReview(AdminReviewSelection incoming) => _incomingReview = incoming;

    private async Task OpenAdminReview(Func<AdminReviewSelection> create)
    {
        if (_session.ActivePaths.Count != 0 || _sessionBusy)
        {
            StatusText.Text = "请先释放本窗口的会话试用，再在管理员窗口复核。没有执行。";
            return;
        }
        if (_pendingUac is { IsCompleted: false })
        {
            StatusText.Text = "Windows 正在等待上一次管理员授权。当前窗口没有执行。";
            return;
        }
        try
        {
            var review = create();
            string exe = Path.Combine(AppContext.BaseDirectory, "FontWorkbench.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException("找不到同一发行目录的 FontWorkbench.exe，请解压完整发行包。");
            var launch = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory, ErrorDialog = false };
            foreach (var arg in review.Arguments())
                launch.ArgumentList.Add(arg);
            StatusText.Text = "等待 Windows 的管理员授权。原窗口不会执行任何字体更改。";
            _pendingUac = Task.Run(() => { using var child = Process.Start(launch) ?? throw new InvalidOperationException("管理员窗口未启动。"); });
            // If consent arrives late, its sole effect is opening another read-only review window.
            _ = _pendingUac.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            await _pendingUac.WaitAsync(TimeSpan.FromSeconds(90));
            if (!_closed)
                Close(); // Explicit restart: the old process must not retain font mappings.
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { if (!_closed) StatusText.Text = "已取消 Windows 管理员授权，未更改任何字体。"; }
        catch (TimeoutException) { if (!_closed) StatusText.Text = "Windows 授权尚未返回，本窗口未执行。若稍后打开新窗口，仍需重新确认。"; }
        catch (Exception ex) { if (!_closed) StatusText.Text = "无法打开管理员复核窗口，未执行：" + ex.Message; }
    }

    // Run once after the fresh installed catalogue has been published. There is no entry point
    // from arguments to Execute; the same ordinary review/confirmation UI is mandatory in both windows.
    private async Task ResumeAdminReview(FontCatalog catalog, AdminReviewSelection request)
    {
        var source = FamilyList.ItemsSource;
        var initialSelection = FamilyList.SelectedItems.OfType<FamilyRow>().Select(r => r.Id).ToArray();
        bool Current() => !_closed && ReferenceEquals(_catalog, catalog) && ReferenceEquals(FamilyList.ItemsSource, source) && _filePaths is null && _scope is null && SearchBox.Text.Length == 0;
        try
        {
            if (!Current())
                return;
            StatusText.Text = "管理员复核：重新核对选区和物理文件…";
            var inventory = request.Mode == "history" ? null : await Task.Run(() => catalog.ReadManagementInventory(_managementLifetime.Token));
            if (!Current() || !initialSelection.SequenceEqual(FamilyList.SelectedItems.OfType<FamilyRow>().Select(r => r.Id)))
                return;
            if (!request.Matches(CurrentReviewSid(), inventory))
            {
                FamilyList.UnselectAll();
                StatusText.Text = "启动期间选区或文件归属已改变。未继续，请手动重新选择。";
                return;
            }
            if (request.Mode == "history")
            {
                await ManageHistory();
                return;
            }
            _managementInventoryOwner = catalog;
            _managementInventory = inventory;
            _applyingReview = true;
            try
            {
                FamilyList.UnselectAll();
                if (request.Ids.Count == catalog.Rows.Length)
                    FamilyList.SelectAll();
                else
                foreach (var id in request.Ids)
                    FamilyList.SelectedItems.Add(catalog.Rows[id]);
            }
            finally { _applyingReview = false; }
            _shownFamily = FamilyList.SelectedItem as FamilyRow;
            LoadStyles();
            UpdateShelfButtons();
            UpdateManagementCommands();
            UpdateSelectionSummary();
            if (FamilyList.SelectedItem != null)
                FamilyList.ScrollIntoView(FamilyList.SelectedItem);
            await ManageSelection(request.Mode == "uninstall" ? ManagementAction.Uninstall : ManagementAction.Disable);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) { FamilyList.UnselectAll(); StatusText.Text = "无法恢复复核选区。未执行任何操作：" + ex.Message; } }
    }
    private void PublishIncomingReview(FontCatalog catalog)
    {
        if (_incomingReview is not { } request)
            return;
        _incomingReview = null;
        _ = Dispatcher.InvokeAsync(() => _ = ResumeAdminReview(catalog, request), DispatcherPriority.Loaded);
    }
}
