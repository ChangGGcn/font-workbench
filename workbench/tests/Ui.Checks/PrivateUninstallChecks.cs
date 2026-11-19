using System.IO;
using System.Windows;
using System.Windows.Controls;
using FontBook.Management;
using FontBook.TestSupport;
using Microsoft.Win32;

namespace FontBook.Workbench.Ui.Checks;

internal static partial class Program
{
    // Exercise the actual Confirm -> release previews -> execute -> refresh -> restore UI.
    // The only enumerable family is the original two-glyph fixture; the only registry
    // roots the service can open are inside this temporary RegLoadAppKey hive.
    private static async Task CheckPrivateUninstallAsync(WpfDiagnostics diagnostics, string output)
    {
        var scratch = Directory.CreateTempSubdirectory("FontWorkbench-HiveUi-");
        try
        {
            using var lab = new PrivateFontHive(scratch.FullName);
            var bytes = PrivateFontHive.FontBytes();
            // A listed filename with deliberately unrelated family metadata exercises the filename fallback.
            string path = Path.Combine(scratch.FullName, "Gabriola.ttf");
            const string slot = "QA [1] (TrueType)";
            File.WriteAllBytes(path, bytes);
            lab.UserFonts.SetValue(slot, path, RegistryValueKind.ExpandString);
            lab.UserFonts.SetValue("sdk_init_timestamp", 7, RegistryValueKind.DWord);
            var window = new MainWindow(Path.Combine(scratch.FullName, "shelves.json"), lab.Service,
                ct => FontCatalog.Open(ct, File.Exists(path) ? [path] : []))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft - 1600,
                Top = SystemParameters.VirtualScreenTop - 1000,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            using var ui = new WorkbenchUi(window, diagnostics, new NoWriteManagement());
            await ui.ShowAsync("私有 hive 与自制字体");
            var row = ui.Rows.Single();
            Require(row.Key == "FontWorkbench QA Hive", "不是测试专属字体，拒绝继续。");
            var views = Find<ComboBox>(window, "ViewPicker");
            foreach (int view in new[] { 1, 2, 0 })
            {
                views.SelectedIndex = view;
                await ui.WaitAsync("三种视图显示系统字体标记", () =>
                    ui.Families.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item &&
                    VisualDescendants<TextBlock>(item).Any(t => t.IsVisible && t.Text.Contains("系统字体")));
            }
            Find<TextBox>(window, "SampleBox").Text = "AAA";
            await ui.SelectPreviewAsync(row);
            await ui.PreviewAsync("确认待卸载的测试字体已经被 DirectWrite 绘制");
            var review = await OpenOwnedWindowAsync(ui, Find<Button>(window, "BatchUninstallButton"), "ManagementDialog");
            var confirm = Find<Button>(review, "ManagementConfirmButton");
            var risk = Find<CheckBox>(review, "SystemFontAcknowledgement");
            Require(risk.IsVisible && risk.IsChecked == false && !confirm.IsEnabled && !confirm.IsDefault &&
                Find<TextBlock>(review, "SystemFontWarningText").Text.Contains(row.Name), "系统字体卸载缺少指名警告或未经确认就可执行。");
            CaptureRedactedWindow(review, Path.Combine(output, "system-font-warning.png"));
            risk.IsChecked = true;
            Require(confirm.IsEnabled, "明确接受系统字体风险后应可继续既有确认。");
            risk.IsChecked = false;
            Require(!confirm.IsEnabled, "取消勾选应撤销执行资格。");
            risk.IsChecked = true;
            var result = await OpenOwnedWindowAsync(ui, confirm, "ManagementDialog");
            try
            {
                Require(!File.Exists(path) && lab.UserFonts.GetValue(slot) == null,
                    "主窗口确认后仍有文件或登记：" + Find<TextBlock>(result, "ManagementNotice").Text + " " + string.Join(" ", Find<ListBox>(result, "ManagementItemsList").Items.Cast<object>()));
            }
            finally { result.Close(); }
            await ui.WaitAsync("卸载结果刷新为空目录", () => ui.Families.Items.Count == 0 && Find<Button>(window, "BatchEnableButton").IsEnabled);
            var history = await OpenOwnedWindowAsync(ui, Find<Button>(window, "BatchEnableButton"), "ManagementDialog");
            var records = Find<ListBox>(history, "ManagementHistoryList");
            Require(records.Items.Count == 1, "没有唯一的测试恢复记录。");
            records.SelectedIndex = 0;
            var undo = await OpenOwnedWindowAsync(ui, Find<Button>(history, "ManagementReviewButton"), "ManagementDialog");
            Require(!Find<StackPanel>(undo, "SystemFontWarning").IsVisible, "恢复操作不应误报删除警告。");
            var restored = await OpenOwnedWindowAsync(ui, Find<Button>(undo, "ManagementConfirmButton"), "ManagementDialog");
            try
            {
                Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes) && lab.UserFonts.GetValueKind(slot) == RegistryValueKind.ExpandString &&
                    Equals(lab.UserFonts.GetValue(slot, null, RegistryValueOptions.DoNotExpandEnvironmentNames), path) && Equals(lab.UserFonts.GetValue("sdk_init_timestamp"), 7),
                    "恢复后的字节、注册值或时间戳不符。");
            }
            finally { restored.Close(); }
            await ui.WaitAsync("恢复后重新出现在目录", () => ui.Rows.Any(r => r.Key == row.Key));
            Pass("主窗口实测：正在预览的自制字体已从私有 hive 注销并从磁盘删除；历史恢复后文件字节和原登记一致。");

            // Exercise the receiving half of UAC navigation without displaying consent or elevating a test.
            var restoredRow = ui.Rows.Single();
            var evidence = new ManagementInventory([new(restoredRow.Id, restoredRow.Name, restoredRow.Key, [new(path, 0)])]);
            var bookmark = AdminReviewSelection.Create("uninstall", MainWindow.CurrentReviewSid(), evidence, [restoredRow.Id]);
            window.SetIncomingReview(bookmark);
            var handoff = await OpenOwnedWindowAsync(ui, Find<Button>(window, "RefreshButton"), "ManagementDialog");
            try
            {
                Require(!Find<Button>(handoff, "ManagementConfirmButton").IsEnabled &&
                    Find<CheckBox>(handoff, "SystemFontAcknowledgement") is { IsVisible: true, IsChecked: false } &&
                    File.Exists(path) && lab.UserFonts.GetValue(slot) != null,
                    "新建管理员复核不能继承上次的风险确认，也不能执行。");
                Click(Find<Button>(handoff, "ManagementCancelButton"));
            }
            finally { if (handoff.IsVisible) handoff.Close(); }
            await ui.WaitAsync("取消管理员窗口的重新预检", () => Find<Button>(window, "BatchUninstallButton").IsEnabled && !handoff.IsVisible);
            Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes) && lab.UserFonts.GetValue(slot) != null, "仅复核改变了字体。");
            Pass("管理员接续选区重新建档、只读预检并取消；未申请真实 UAC。");
        }
        finally
        {
            await FontReadActivity.WhenIdle().WaitAsync(TimeSpan.FromSeconds(10));
            scratch.Delete(recursive: true);
        }
    }
}
