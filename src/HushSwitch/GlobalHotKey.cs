// グローバルホットキー(まとめトグルと、カテゴリーごとのまとめトグル)。番号ごとに登録する。Windows にキーの組み合わせを登録し、押されたら知らせてもらう。
// キー入力そのものは見ない(登録した組み合わせの知らせだけが届く)。
// 知らせを受けるために、画面に出ないウィンドウを 1 つ持つ。
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace HushSwitch;

public sealed class GlobalHotKey : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint NoRepeat = 0x4000;   // 押しっぱなしで繰り返さない
    private static readonly KeyGestureConverter Converter = new();
    private readonly HwndSource sink;
    private readonly HashSet<int> registered = [];

    /// <param name="pressed">押されたキーの番号を受け取る。</param>
    public GlobalHotKey(Action<int> pressed)
    {
        // 親を HWND_MESSAGE(-3)にすると、メッセージを受けるだけの見えないウィンドウになる
        sink = new HwndSource(new HwndSourceParameters("HushSwitch.HotKey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        // AddHook は、自分のウィンドウに届いたメッセージを受け取る WPF の口(他のプロセスには何もしない)
        sink.AddHook((IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == WmHotKey && registered.Contains((int)wParam))
            {
                handled = true;
                pressed((int)wParam);
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>その番号のキーを登録し直す。空なら外すだけ。登録できなければ false(他のアプリが使っている、または使えない組み合わせ)。</summary>
    public bool Set(int id, string gesture)
    {
        if (registered.Remove(id)) UnregisterHotKey(sink.Handle, id);
        if (gesture.Length == 0) return true;
        if (Parse(gesture) is not { } keys) return false;
        if (!RegisterHotKey(sink.Handle, id, (uint)keys.Modifiers | NoRepeat, (uint)KeyInterop.VirtualKeyFromKey(keys.Key))) return false;
        registered.Add(id);
        return true;
    }

    /// <summary>登録しているキーを全部外す。</summary>
    public void Clear()
    {
        foreach (int id in registered.ToList()) Set(id, "");
    }

    /// <summary>"Ctrl+Alt+M" の形を読む。使えない組み合わせなら null。</summary>
    public static KeyGesture? Parse(string text)
    {
        try
        {
            return Converter.ConvertFromInvariantString(text) is KeyGesture keys && Allowed(keys.Key, keys.Modifiers) ? keys : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or FormatException) { return null; }
    }

    /// <summary>押されたキーを "Ctrl+Alt+M" の形にする。使えない組み合わせなら null。</summary>
    public static string? Format(Key key, ModifierKeys modifiers)
    {
        if (!Allowed(key, modifiers)) return null;
        try { return Converter.ConvertToInvariantString(new KeyGesture(key, modifiers)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return null; }
    }

    // 登録したキーは、ほかのアプリに届かなくなる。文字のキーや Enter を単独で取ってしまわないよう、
    // Ctrl か Alt を含む組み合わせか、F1〜F24 だけを認める。Windows キーとの組み合わせは Windows が使うので認めない
    private static bool Allowed(Key key, ModifierKeys modifiers) =>
        (modifiers & ModifierKeys.Windows) == 0
        && ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 || key is >= Key.F1 and <= Key.F24);

    public void Dispose()
    {
        Clear();
        sink.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
