using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using FontBook.Management;

namespace FontBook.Workbench.Ui.Checks;

internal static partial class Program
{
    private static async Task CheckManagementAsync(WorkbenchUi ui)
    {
        // Fail closed if the constructor stops installing the test service. Do not query the
        // lazy Management property: doing so could instantiate the production implementation.
        var injected = typeof(MainWindow).GetField("_managementService", BindingFlags.Instance | BindingFlags.NonPublic);
        Require(ReferenceEquals(injected?.GetValue(ui.Window), ui.Management), "未安装无写入的管理替身，拒绝点击批量入口。");
        var disable = Find<Button>(ui.Window, "BatchDisableButton");
        var uninstall = Find<Button>(ui.Window, "BatchUninstallButton");
        var enable = Find<Button>(ui.Window, "BatchEnableButton");
        var status = Find<TextBlock>(ui.Window, "StatusText");
        var selected = ui.Families.SelectedItems.OfType<FamilyRow>().ToArray();
        Require(selected.Length > 1, "需要通过 WPF 实际多选后再检查批量按钮。");
        var ids = selected.Select(row => row.Id).Order().ToArray();
        var names = selected.Select(row => row.Name).Order(StringComparer.Ordinal).ToArray();
        foreach (var (button, action) in new[] { (disable, ManagementAction.Disable), (uninstall, ManagementAction.Uninstall) })
        {
            var window = await OpenOwnedWindowAsync(ui, button, "ManagementDialog", LoadTimeoutSeconds);
            try
            {
                Require(ui.Management.Requests.TryDequeue(out var request) && request.Action == action &&
                    request.Ids.SequenceEqual(ids) && request.Names.Order(StringComparer.Ordinal).SequenceEqual(names),
                    $"{action} 未把准确的多选家族集合转交给只读预检替身。");
                CheckPreflight(window, names);
                await CancelAsync(window, "未执行字体变更。");
            }
            finally
            {
                if (window.IsVisible)
                    window.Close();
            }
        }

        ui.Management.ElevationOnly = true;
        Window? gated = null;
        try
        {
            gated = await OpenOwnedWindowAsync(ui, uninstall, "ManagementDialog", LoadTimeoutSeconds);
            Require(ui.Management.Requests.TryDequeue(out var request) && request.Ids.SequenceEqual(ids), "管理员预检丢失选区。");
            var admin = Find<Button>(gated, "ManagementAdminButton");
            Require(admin.IsVisible && admin.IsEnabled && !admin.IsDefault && !Find<Button>(gated, "ManagementConfirmButton").IsEnabled,
                "普通窗口必须显示明确的管理员复核入口，但不能确认执行。");
            Require(Find<StackPanel>(gated, "SystemFontWarning").IsVisible &&
                !Find<CheckBox>(gated, "SystemFontAcknowledgement").IsVisible,
                "仅能提权复核的系统字体应警示，并且不能在普通窗口同意删除。");
            // Do not invoke runas in automated tests. Cancel is the default even on a machine-only batch.
            await CancelAsync(gated, "未执行字体变更。");
            Pass("机器范围批次出现手动管理员复核入口，普通窗口不能执行；取消不弹 UAC、不写字体。");
        }
        finally { ui.Management.ElevationOnly = false; if (gated?.IsVisible == true) gated.Close(); }

        if (ui.Families.Items.Count >= 12)
        {
            ui.Families.UnselectAll();
            foreach (var row in ui.Families.Items.OfType<FamilyRow>().Take(12))
                ui.Families.SelectedItems.Add(row);
            Window? large = null;
            try
            {
                ui.Management.IncludeSystemFont = true;
                large = await OpenOwnedWindowAsync(ui, uninstall, "ManagementDialog", LoadTimeoutSeconds);
                Require(ui.Management.Requests.TryDequeue(out var req) && req.Ids.Length == 12, "未得到 12 项隔离预检。");
                var confirm = Find<Button>(large, "ManagementConfirmButton");
                var count = Find<TextBox>(large, "CountGuardText");
                var risk = Find<CheckBox>(large, "SystemFontAcknowledgement");
                Require(!confirm.IsEnabled && count.IsVisible && risk.IsVisible && risk.IsChecked == false,
                    "混合批次需要独立确认系统字体风险及数量。");
                count.Text = "12";
                Require(!confirm.IsEnabled, "只输入正确数量不能代替接受系统字体警告。");
                count.Text = "11";
                risk.IsChecked = true;
                Require(!confirm.IsEnabled, "接受系统字体警告不能覆盖错误数量。");
                count.Text = "12";
                Require(confirm.IsEnabled, "正确数量应允许用户确认。");
                await CancelAsync(large, "未执行字体变更。");
                Pass("12 项批量卸载预检要求输入正确数量；操作已取消，未调用执行。");
            }
            finally
            {
                ui.Management.IncludeSystemFont = false;
                if (large?.IsVisible == true)
                    large.Close();
                ui.Families.UnselectAll();
                foreach (var row in selected)
                    ui.Families.SelectedItems.Add(row);
            }
        }
        Window? history = null;
        Window? restore = null;
        try
        {
            history = await OpenOwnedWindowAsync(ui, enable, "ManagementDialog");
            var records = Find<ListBox>(history, "ManagementHistoryList");
            var review = Find<Button>(history, "ManagementReviewButton");
            Require(ui.Management.HistoryReads == 1 && records.IsVisible && records.Items.Count == 1 && !review.IsEnabled,
                "重新启用入口没有打开只读的替身记录，或未选择即允许预检。");
            records.SelectedIndex = 0; // The record belongs solely to the fake, not the Windows catalogue.
            restore = await OpenOwnedWindowAsync(ui, review, "ManagementDialog");
            Require(ui.Management.RestoreRequests.TryDequeue(out var keys) && keys.SequenceEqual(new[] { NoWriteManagement.HistoryKey }),
                "重新启用入口没有预检所选的替身记录。");
            CheckPreflight(restore, [NoWriteManagement.HistoryFamily]);
            await CancelAsync(restore, "未执行字体变更。");
        }
        finally
        {
            if (restore is { IsVisible: true })
                restore.Close();
            if (history is { IsVisible: true })
                history.Close();
        }

        Require(ui.Management.ExecuteAttempts == 0 && ui.Management.Requests.IsEmpty && ui.Management.RestoreRequests.IsEmpty &&
            ui.Families.SelectedItems.OfType<FamilyRow>().Select(row => row.Id).Order().SequenceEqual(ids),
            "预检取消后有意外请求、执行尝试或选中家族变化。");
        Pass("批量停用、卸载和重新启用均进入替身预检并实际取消；生产服务未注入，执行次数为零。");

        void CheckPreflight(Window window, string[] families)
        {
            var items = Find<ListBox>(window, "ManagementItemsList");
            var confirm = Find<Button>(window, "ManagementConfirmButton");
            var titles = items.Items.Cast<object>().Select(row => GlyphUi.Property(row, "Title") as string).ToArray();
            Require(items.IsVisible && titles.Length == families.Length && families.All(family =>
                    titles.Any(title => title?.StartsWith($"{family} · ", StringComparison.Ordinal) == true)) &&
                confirm is { IsVisible: true, IsEnabled: true, IsDefault: false },
                "替身预检没有显示全部待确认项目，或确认操作被错误设为默认按钮。");
        }

        async Task CancelAsync(Window window, string expectedStatus)
        {
            var cancel = Find<Button>(window, "ManagementCancelButton");
            Require(cancel.IsCancel && cancel.IsEnabled && cancel.IsVisible && Equals(cancel.Content, "取消"),
                "预检缺少可用的取消按钮。");
            // Invoke on the cancel control itself: no Enter, OK, confirm, or global input.
            var invoke = new ButtonAutomationPeer(cancel).GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("预检取消控件未实现 WPF Invoke。");
            invoke.Invoke();
            await ui.WaitAsync("取消预检后解锁原批量按钮且没有执行", () => !window.IsVisible &&
                status.Text == expectedStatus && disable.IsEnabled && uninstall.IsEnabled && enable.IsEnabled);
        }
    }

    private sealed class NoWriteManagement : IManagementService
    {
        internal static readonly ManagementHistoryKey HistoryKey = new(ManagementHistorySource.Workbench, "ui-check-only");
        internal const string HistoryFamily = "UI test fixture family";
        private const string FixtureFile = @"C:\FontBook-UiChecks-fixture\never-installed.ttf";
        private int _historyReads, _executeAttempts;
        public bool ElevationOnly
        {
            get; set;
        }
        public bool IncludeSystemFont { get; set; }
        public int HistoryReads => Volatile.Read(ref _historyReads);
        public int ExecuteAttempts => Volatile.Read(ref _executeAttempts);
        public ConcurrentQueue<(ManagementAction Action, uint[] Ids, string[] Names)> Requests { get; } = new();
        public ConcurrentQueue<ManagementHistoryKey[]> RestoreRequests { get; } = new();

        public ManagementPlan Preflight(ManagementAction action, ManagementInventory inventory, IReadOnlyList<uint> familyIds)
        {
            var ids = familyIds.ToArray();
            var byId = inventory.Families.ToDictionary(family => family.Id);
            var names = ids.Where(byId.ContainsKey).Select(id => byId[id].Name).ToArray();
            Requests.Enqueue((action, ids, names));
            return new ManagementPlan(names.Select((name, index) => ElevationOnly
                ? PlanItem(action, name) with
                {
                    CanExecute = false,
                    CanRequestElevation = true,
                    IsWindowsSystemFont = true,
                    Permission = "测试：需管理员"
                } : PlanItem(action, name) with { IsWindowsSystemFont = IncludeSystemFont && index == 0 }).ToArray());
        }

        public IReadOnlyList<ManagementHistoryItem> LoadHistory()
        {
            Interlocked.Increment(ref _historyReads);
            return [new(HistoryKey, ManagementAction.Disable, HistoryFamily, "fixture", [FixtureFile], "test", true, "预检替身")];
        }

        public ManagementPlan PreflightRestore(IReadOnlyList<ManagementHistoryKey> keys)
        {
            RestoreRequests.Enqueue(keys.ToArray());
            return new ManagementPlan([PlanItem(ManagementAction.Enable, HistoryFamily)]);
        }

        public IReadOnlyList<ManagementResult> Execute(ManagementPlan plan, bool confirmed)
        {
            Interlocked.Increment(ref _executeAttempts);
            // Even if a regression bypasses cancel, this fake cannot touch fonts, registry, or any files.
            return [];
        }

        private static ManagementCheck PlanItem(ManagementAction action, string family) =>
            new(action, family, [FixtureFile], true, "测试权限", "仅测试数据，禁止执行。");
    }
}
