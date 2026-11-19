using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FontBook.Management;

namespace FontBook.Workbench;

public partial class MainWindow
{
    private IManagementService? _managementService;
    private IManagementService Management => _managementService ??= ManagementService.CreateWindows();
    private readonly CancellationTokenSource _managementLifetime = new();
    private FontCatalog? _managementInventoryOwner, _managementStaleCatalog;
    private ManagementInventory? _managementInventory;
    private CancellationTokenSource? _pendingPreflight;
    private bool _managementBusy, _managementExecuting, _managementRefreshNeeded;

    /// <summary>Isolated UI regression can substitute the entire font-management boundary.</summary>
    public MainWindow(string shelfStatePath, IManagementService management) : this(shelfStatePath) =>
        _managementService = management ?? throw new ArgumentNullException(nameof(management));

    private readonly Func<CancellationToken, FontCatalog> _openInstalledCatalog = static ct => FontCatalog.Open(ct);
    internal MainWindow(string shelfPath, IManagementService management, Func<CancellationToken, FontCatalog> installedCatalog)
        : this(shelfPath, management) => _openInstalledCatalog = installedCatalog;

    private void InitializeManagement()
    {
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && _pendingPreflight is { } pending)
            {
                pending.Cancel();
                StatusText.Text = "正在取消只读预检；没有执行字体操作。";
                e.Handled = true;
            }
        };
        BatchDisableButton.Click += async (_, _) => await ManageSelection(ManagementAction.Disable);
        BatchUninstallButton.Click += async (_, _) => await ManageSelection(ManagementAction.Uninstall);
        BatchEnableButton.Click += async (_, _) => await ManageHistory();
        FamilyList.IsVisibleChanged += (_, _) => UpdateManagementCommands();
        SessionButton.IsEnabledChanged += (_, _) => UpdateManagementCommands();
        BatchDisableButton.IsVisibleChanged += (_, _) => UpdateManagementCommands();
        BatchUninstallButton.IsVisibleChanged += (_, _) => UpdateManagementCommands();
        BatchEnableButton.IsVisibleChanged += (_, _) => UpdateManagementCommands();
        Closing += (_, e) =>
        {
            if (!_managementExecuting)
                return;
            e.Cancel = true;
            StatusText.Text = "请等待当前字体事务结束并保存结果。";
        };
        Closed += (_, _) => _managementLifetime.Cancel();
        SourceInitialized += (_, _) => (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(ManagementWindowMessage);
        UpdateManagementCommands();
    }

    private void UpdateManagementCommands()
    {
        if (!ReferenceEquals(_catalog, _managementStaleCatalog))
            _managementStaleCatalog = null;
        if (!ReferenceEquals(_catalog, _managementInventoryOwner))
        {
            _managementInventory = null;
            _managementInventoryOwner = null;
        }
        var canWrite = !_managementBusy && !_sessionBusy && IsVisible && !_closed && _filePaths is null &&
            _catalog is { } catalog && !ReferenceEquals(catalog, _managementStaleCatalog) &&
            FamilyList.IsVisible && FamilyList.SelectedItems.OfType<FamilyRow>().Any(row => IsCurrentManagementRow(catalog, row));
        BatchDisableButton.IsEnabled = canWrite && BatchDisableButton.IsVisible;
        BatchUninstallButton.IsEnabled = canWrite && BatchUninstallButton.IsVisible;
        // This button opens a browser, not a write command. It must work even with an empty installed catalogue.
        BatchEnableButton.IsEnabled = !_managementBusy && !_closed && BatchEnableButton.IsVisible;
    }

    private bool IsCurrentManagementRow(FontCatalog catalog, FamilyRow row) => row.Id < catalog.Rows.Length &&
        ReferenceEquals(catalog.Rows[row.Id], row) && FamilyList.Items.Contains(row);

    private uint[] SelectedManagementIds(FontCatalog catalog)
    {
        // Avoid a linear Items.Contains scan for every selected row at library scale.
        var visible = FamilyList.Items.OfType<FamilyRow>().ToHashSet(ReferenceEqualityComparer.Instance);
        return FamilyList.SelectedItems.OfType<FamilyRow>()
            .Where(r => r.Id < catalog.Rows.Length && ReferenceEquals(catalog.Rows[r.Id], r) && visible.Contains(r))
            .Select(r => r.Id).Distinct().Order().ToArray();
    }
    private async Task ManageSelection(ManagementAction action)
    {
        var button = action == ManagementAction.Disable ? BatchDisableButton : BatchUninstallButton;
        if (!button.IsEnabled || !button.IsVisible || !FamilyList.IsVisible || _managementBusy || _sessionBusy ||
            _filePaths is not null || _catalog is not { } catalog || ReferenceEquals(catalog, _managementStaleCatalog))
            return;
        var ids = SelectedManagementIds(catalog);
        if (ids.Length == 0)
            return;
        var displayed = FamilyList.ItemsSource;
        var query = SearchBox.Text;
        var scope = _scope;
        bool StillSelected() => !_closed && IsVisible && FamilyList.IsVisible && button.IsVisible && _filePaths is null &&
            ReferenceEquals(catalog, _catalog) && !ReferenceEquals(catalog, _managementStaleCatalog) &&
            ReferenceEquals(displayed, FamilyList.ItemsSource) && SearchBox.Text == query && _scope == scope &&
            ids.SequenceEqual(SelectedManagementIds(catalog));
        await WithManagement(async () =>
        {
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(_managementLifetime.Token);
            _pendingPreflight = pending;
            var ct = pending.Token;
            StatusText.Text = $"正在核对 {ids.Length} 个家族的物理文件；Esc 可取消预检。";
            var progress = new Progress<int>(done =>
            {
                if (ReferenceEquals(_pendingPreflight, pending) && !ct.IsCancellationRequested && !_closed)
                    StatusText.Text = $"已预检 {done:N0}/{ids.Length:N0} 个家族　·　Esc 取消";
            });
            var inventory = _managementInventory;
            if (inventory is null || !ReferenceEquals(catalog, _managementInventoryOwner))
            {
                inventory = await Task.Run(() => catalog.ReadManagementInventory(ct), ct);
                if (ReferenceEquals(_catalog, catalog) && !ReferenceEquals(catalog, _managementStaleCatalog))
                {
                    _managementInventoryOwner = catalog;
                    _managementInventory = inventory;
                }
            }
            if (!StillSelected())
            {
                ReportStaleManagementSelection();
                return;
            }
            var service = Management;
            var plan = await Task.Run(() => service is ManagementService production
                ? production.Preflight(action, inventory, ids, ct, progress) : service.Preflight(action, inventory, ids), ct);
            ct.ThrowIfCancellationRequested();
            _pendingPreflight = null; // No cancellation of an in-progress file/registry transaction.
            if (!StillSelected())
            {
                ReportStaleManagementSelection();
                return;
            }
            await ConfirmManagement(service, plan, StillSelected, () => AdminReviewSelection.Create(
                action == ManagementAction.Uninstall ? "uninstall" : "disable", CurrentReviewSid(), inventory, ids));
        });
    }

    private async Task ManageHistory()
    {
        if (_managementBusy || _closed || !IsVisible || !BatchEnableButton.IsVisible)
            return;
        await WithManagement(async () =>
        {
            StatusText.Text = "正在只读读取并验签字体操作历史…";
            var service = Management;
            var ct = _managementLifetime.Token;
            var history = await Task.Run(service.LoadHistory, ct);
            if (_closed || !IsVisible)
                return;
            var canPlan = _filePaths is null && !_sessionBusy;
            var keys = ManagementDialog.ReviewHistory(this, history, canPlan);
            if (keys.Count == 0)
            {
                StatusText.Text = "已关闭历史；未变更字体。";
                return;
            }
            var plan = await Task.Run(() => service.PreflightRestore(keys), ct);
            await ConfirmManagement(service, plan, () => !_closed && IsVisible && _filePaths is null,
                () => AdminReviewSelection.Create("history", CurrentReviewSid(), null, []));
        });
    }

    private async Task ConfirmManagement(IManagementService service, ManagementPlan plan, Func<bool> stillCurrent, Func<AdminReviewSelection> createReview)
    {
        if (_sessionBusy || !stillCurrent())
        {
            ReportStaleManagementSelection();
            return;
        }
        var active = _session.ActivePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var checkedDisplay = plan.Items.Any(x => x.CanExecute && x.Files.Any(active.Contains))
            ? new ManagementPlan(plan.Items.Select(x => x with
            {
                CanExecute = false,
                Message = x.Files.Any(active.Contains) ? "该文件正在会话试用；请先释放试用引用。" : "同批有文件正在试用；请调整选择后重新预检。"
            }).ToArray())
            : plan;
        bool offerElevation = ReferenceEquals(plan, checkedDisplay) && active.Count == 0 && !_sessionBusy;
        var decision = ManagementDialog.Confirm(this, checkedDisplay, offerElevation);
        if (decision == ReviewDecision.ReviewAsAdmin)
        {
            if (!offerElevation || !plan.Items.Any(i => i.CanRequestElevation) || !stillCurrent())
            {
                ReportStaleManagementSelection();
                return;
            }
            await OpenAdminReview(createReview);
            return;
        }
        if (decision != ReviewDecision.Apply)
        {
            if (!_closed)
                StatusText.Text = "未执行字体变更。";
            return;
        }
        if (!ReferenceEquals(checkedDisplay, plan) || !plan.Items.Any(i => i.CanExecute) ||
            !stillCurrent() || _sessionBusy || plan.Items.Where(x => x.CanExecute).SelectMany(x => x.Files)
                .Intersect(_session.ActivePaths, StringComparer.OrdinalIgnoreCase).Any())
        {
            ReportStaleManagementSelection();
            return;
        }
        StatusText.Text = "正在执行已确认的字体事务…";
        _managementExecuting = true;
        var controls = (UIElement)Content;
        bool wasEnabled = controls.IsEnabled;
        controls.SetCurrentValue(IsEnabledProperty, false);
        SessionButton.IsEnabled = false;
        IReadOnlyList<ManagementResult> results;
        try
        {
            if (plan.Items.Any(x => x.CanExecute && x.Action == ManagementAction.Uninstall))
            {
                // DirectWrite may keep a section mapped even though a DELETE handle opened successfully.
                // The sealed plan owns only evidence, never a live font or catalog handle.
                InvalidateManagementCatalog();
                await ReleasePreviewFilesAsync();
            }
            results = await Task.Run(() => service.Execute(plan, confirmed: true));
        }
        finally
        {
            _managementExecuting = false;
            controls.SetCurrentValue(IsEnabledProperty, wasEnabled);
            SessionButton.IsEnabled = !_sessionBusy;
        }
        if (_closed)
            return;
        InvalidateManagementCatalog();
        int success = results.Count(r => r.Ok);
        StatusText.Text = $"字体管理 · {success} 项成功，{results.Count - success} 项未成功或未执行";
        ManagementDialog.ShowResults(this, results);
    }

    private async Task WithManagement(Func<Task> run)
    {
        _managementBusy = true;
        UpdateManagementCommands();
        try
        {
            await run();
        }
        catch (OperationCanceledException) when (_managementLifetime.IsCancellationRequested || _pendingPreflight?.IsCancellationRequested == true) { if (!_closed) StatusText.Text = "只读预检已取消。没有执行字体操作。"; }
        catch (Exception ex)
        {
            if (_closed || !IsVisible)
                return;
            StatusText.Text = "字体管理已停止：" + ex.Message;
            MessageBox.Show(this, StatusText.Text, "字体管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _pendingPreflight = null;
            _managementBusy = false;
            UpdateManagementCommands();
            RefreshInvalidManagementCatalog();
        }
    }

    private async Task ReleasePreviewFilesAsync()
    {
        _scan.Cancel();
        _query.Cancel();
        var catalog = _catalog;
        _catalog = null; // prevents the virtualized preview queue from scheduling replacement reads
        FamilyList.ItemsSource = null;
        ClearDetails();
        InvalidateLibraryPreviews();
        catalog?.Dispose();
        try
        {
            await FontReadActivity.WhenIdle().WaitAsync(TimeSpan.FromSeconds(15), _managementLifetime.Token);
        }
        catch (TimeoutException) { throw new IOException("工作台尚未释放全部预览。未执行卸载，请稍后重试。"); }
    }

    private void ReportStaleManagementSelection()
    {
        if (!_closed)
            StatusText.Text = "字体目录、选区或会话状态已变化；没有执行，请重新预检。";
    }

    private IntPtr ManagementWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x001D && !_closed && _filePaths is null && IsLoaded) // WM_FONTCHANGE
        {
            InvalidateManagementCatalog();
            _ = Dispatcher.InvokeAsync(RefreshInvalidManagementCatalog, DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    private void InvalidateManagementCatalog()
    {
        _managementStaleCatalog = _catalog;
        _managementInventoryOwner = null;
        _managementInventory = null;
        _managementRefreshNeeded = true;
        UpdateManagementCommands();
    }

    private void RefreshInvalidManagementCatalog()
    {
        if (!_managementRefreshNeeded || _managementBusy || _closed)
            return;
        _managementRefreshNeeded = false;
        if (_filePaths is null)
            Refresh();
    }
}
