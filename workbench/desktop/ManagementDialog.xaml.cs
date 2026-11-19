using System.Windows;
using FontBook.Management;

namespace FontBook.Workbench;

internal enum ReviewDecision
{
    Cancel, Apply, ReviewAsAdmin
}

internal partial class ManagementDialog : Window
{
    private sealed record Row(string Title, string Status, string Files, string Detail, ManagementHistoryItem? Record = null);

    private ManagementDialog(Window owner, string heading, string notice)
    {
        InitializeComponent();
        Owner = owner;
        if (!owner.ShowActivated)
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = owner.Left;
            Top = owner.Top;
        }
        Title = heading;
        ManagementHeading.Text = heading;
        ManagementNotice.Text = notice;
        if (owner.ShowActivated)
            Loaded += (_, _) => ManagementCancelButton.Focus(); // Never give default focus to a write.
    }

    internal static IReadOnlyList<ManagementHistoryKey> ReviewHistory(Window owner, IReadOnlyList<ManagementHistoryItem> history, bool canPlan)
    {
        var notice = history.Count == 0 ? "还没有字体操作记录。" : canPlan
            ? "可按 Ctrl 或 Shift 选择多个可恢复的工作台记录。下一步仅预检；旧安全中心记录及不安全的记录只读。"
            : "当前为只读浏览。请回到已安装字体视图，等待会话操作完成后再尝试恢复。";
        var view = new ManagementDialog(owner, "重新启用、恢复与历史", notice);
        view.ManagementHistoryList.Visibility = Visibility.Visible;
        view.ManagementReviewButton.Visibility = canPlan ? Visibility.Visible : Visibility.Collapsed;
        view.ManagementCancelButton.Content = "关闭";
        view.ManagementHistoryList.ItemsSource = history.Select(h => new Row(
            $"{h.Family} · {Label(h.OriginalAction)}",
            $"{(h.Key.Source == ManagementHistorySource.Workbench ? "工作台" : "旧安全中心")} · {(h.CanRestore && canPlan ? "可预检" : "只读")} · {h.Time} · {h.Status}",
            FileSummary(h.Files), h.Detail, h)).ToArray();
        Row[] Selection() => view.ManagementHistoryList.SelectedItems.OfType<Row>().ToArray();
        bool Ready(Row[] rows) => canPlan && rows.Length > 0 && rows.All(r => r.Record?.CanRestore == true);
        view.ManagementReviewButton.IsEnabled = false;
        view.ManagementHistoryList.SelectionChanged += (_, _) => view.ManagementReviewButton.IsEnabled = Ready(Selection());
        IReadOnlyList<ManagementHistoryKey> keys = [];
        view.ManagementReviewButton.Click += (_, _) =>
        {
            var rows = Selection();
            if (!Ready(rows))
                return;
            keys = Array.AsReadOnly(rows.Select(r => r.Record!.Key).ToArray());
            view.DialogResult = true;
        };
        return view.ShowDialog() == true ? keys : [];
    }

    internal static ReviewDecision Confirm(Window owner, ManagementPlan plan, bool allowAdminReview)
    {
        int allowed = plan.Items.Count(x => x.CanExecute);
        var notice = allowed == 0 ? "没有可执行的项目。未写入字体文件、注册表或日志。" :
            $"仅执行下列通过预检的 {allowed}/{plan.Items.Count} 项。取消不会写入。停用保留原文件；卸载先备份，再移除注册及文件。部分失败会保留已有日志。";
        bool elevate = allowAdminReview && plan.Items.Any(item => item.CanRequestElevation);
        if (elevate)
            notice += " 此批含机器范围项目。选择管理员复核，授权成功后关闭当前窗口并重开；不会自动卸载。";
        var view = new ManagementDialog(owner, "逐项预检与确认", notice);
        var decision = ReviewDecision.Cancel;
        if (elevate)
        {
            view.ManagementAdminButton.Visibility = Visibility.Visible;
            view.ManagementAdminButton.Click += (_, _) => { decision = ReviewDecision.ReviewAsAdmin; view.DialogResult = true; };
        }
        view.ManagementItemsList.Visibility = Visibility.Visible;
        view.ManagementItemsList.ItemsSource = plan.Items.Select(x => new Row(
            $"{x.Family} · {Label(x.Action)}{(x.IsWindowsSystemFont ? " · 系统字体" : "")}",
            $"{(x.CanExecute ? "通过" : x.CanRequestElevation ? "需管理员复核" : "不执行")} · {x.Permission}", FileSummary(x.Files), x.Message)).ToArray();
        view.ManagementConfirmButton.Content = $"确认执行 {allowed} 项";
        view.ManagementConfirmButton.Visibility = allowed == 0 ? Visibility.Collapsed : Visibility.Visible;
        var systemUninstalls = plan.Items.Where(x => x.Action == ManagementAction.Uninstall && x.IsWindowsSystemFont).ToArray();
        int systemAllowed = systemUninstalls.Count(x => x.CanExecute);
        if (systemUninstalls.Length > 0)
        {
            view.SystemFontWarning.Visibility = Visibility.Visible;
            var names = string.Join("、", systemUninstalls.Take(3).Select(x => x.Family));
            var total = systemUninstalls.Length > 3 ? $"等 {systemUninstalls.Length} 个家族" : "";
            view.SystemFontWarningText.Text = $"系统字体卸载警告：{names}{total}。移除后，Windows 界面或文档可能缺字或改变排版。" +
                (systemAllowed == 0 ? "当前窗口不会卸载这些系统字体。" : $"本次可卸载其中 {systemAllowed} 项。");
        }
        bool guarded = allowed >= 10 && plan.Items.Any(x => x.CanExecute && x.Action is ManagementAction.Uninstall or ManagementAction.Disable);
        string expected = allowed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool Ready() => allowed != 0 && (!guarded || view.CountGuardText.Text.Trim() == expected) &&
            (systemAllowed == 0 || view.SystemFontAcknowledgement.IsChecked == true);
        void UpdateConfirmation(object sender, RoutedEventArgs e) => view.ManagementConfirmButton.IsEnabled = Ready();
        if (systemAllowed > 0)
        {
            view.SystemFontAcknowledgement.Visibility = Visibility.Visible;
            view.SystemFontAcknowledgement.Checked += UpdateConfirmation;
            view.SystemFontAcknowledgement.Unchecked += UpdateConfirmation;
        }
        if (guarded)
        {
            view.CountGuard.Visibility = Visibility.Visible;
            view.CountGuardHint.Text = $"输入 {expected} 再确认";
            view.CountGuardText.TextChanged += UpdateConfirmation;
        }
        view.ManagementConfirmButton.IsEnabled = Ready();
        view.ManagementConfirmButton.Click += (_, _) => { if (Ready()) { decision = ReviewDecision.Apply; view.DialogResult = true; } };
        return view.ShowDialog() == true ? decision : ReviewDecision.Cancel;
    }

    internal static void ShowResults(Window owner, IReadOnlyList<ManagementResult> results)
    {
        int succeeded = results.Count(r => r.Ok);
        var view = new ManagementDialog(owner, "字体管理结果",
            $"{succeeded}/{results.Count} 项成功。未完成项请查看原因；如有操作编号，请保留相应日志及备份。Windows 或其他应用可能需要刷新字体目录。");
        view.ManagementCancelButton.Content = "关闭";
        view.ManagementItemsList.Visibility = Visibility.Visible;
        view.ManagementItemsList.ItemsSource = results.Select(x => new Row(
            $"{x.Family} · {Label(x.Action)}", (x.Ok ? "成功" : "未完成或未执行") + (x.OperationId is null ? "" : $" · 操作编号 {x.OperationId}"),
            FileSummary(x.Files ?? []), x.Message)).ToArray();
        view.ShowDialog();
    }

    private static string FileSummary(IReadOnlyList<string> files) => files.Count == 0 ? "影响文件：无可验证路径" :
        $"影响文件（{files.Count}）：\n" + string.Join('\n', files);

    private static string Label(ManagementAction? action) => action switch
    {
        ManagementAction.Disable => "停用",
        ManagementAction.Uninstall => "卸载",
        ManagementAction.Enable => "重新启用",
        ManagementAction.Restore => "恢复",
        _ => "未知操作"
    };
}
