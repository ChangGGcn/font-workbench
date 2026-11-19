using System.Windows;

namespace FontBook.Workbench;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == FontBook.Management.AdminReviewSelection.Switch)
        {
            if (!FontBook.Management.AdminReviewSelection.TryParse(e.Args, out var request) || request is null ||
                !FontBook.Workbench.MainWindow.IsReviewAdministrator() || request.UserSid != FontBook.Workbench.MainWindow.CurrentReviewSid())
            {
                MessageBox.Show("管理员复核参数无效，或 UAC 使用了另一个 Windows 账户。为避免改动错误账户的字体和恢复日志，未执行。", "无法接续字体复核", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(2);
                return;
            }
            var review = new MainWindow();
            review.SetIncomingReview(request);
            MainWindow = review;
            review.Show();
            return;
        }
        if (e.Args.Length != 0)
        {
            Shutdown(CatalogCommands.Run(e.Args));
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
