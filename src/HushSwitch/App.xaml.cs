// 起動の入口。同時に動くのは1プロセスだけにし、2つ目が起動されたら
// 既にあるウィンドウを前面に出して、2つ目は終了する(FR-12)。
using System.Runtime.InteropServices;
using System.Windows;

namespace HushSwitch;

public partial class App : Application
{
    private EventWaitHandle? showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HushSwitch.Show", out bool first);
        if (!first)
        {
            // 前面に出す権利を持っているのは、いま起動された側なので、先に譲っておく
            AllowSetForegroundWindow(-1);
            showSignal.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        ThreadPool.RegisterWaitForSingleObject(
            showSignal, (_, _) => Dispatcher.InvokeAsync(window.BringToFront), null, Timeout.Infinite, executeOnlyOnce: false);
        window.Show();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
