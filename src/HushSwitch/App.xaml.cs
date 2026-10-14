// 起動の入口。同時に動くのは1プロセスだけにし、2つ目が起動されたら
// 既にあるウィンドウを前面に出して、2つ目は終了する(FR-12)。
// 配色(システム / ライト / ダーク)の切り替えもここに置く。
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
        // 自動起動(--tray)か、設定で選ばれていれば、ウィンドウを出さずにトレイに格納した状態で始める
        if (!e.Args.Contains("--tray") && !window.StartInTray) window.Show();
    }

    /// <param name="theme">system / light / dark。</param>
    public static void ApplyTheme(string theme)
    {
#pragma warning disable WPF0001 // ThemeMode は「試験的」の印が付いている。App.xaml で使っているのと同じもの
        var mode = theme switch { "light" => ThemeMode.Light, "dark" => ThemeMode.Dark, _ => ThemeMode.System };
        if (Current.ThemeMode != mode) Current.ThemeMode = mode;
#pragma warning restore WPF0001
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
