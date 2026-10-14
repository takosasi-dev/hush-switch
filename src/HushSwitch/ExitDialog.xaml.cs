// 終了の確認の動き(FR-13)。選んだ結果を Unmute と Remember で返す。
// キャンセル(DialogResult が true でない)のときは、どちらも使わない。
using System.Windows;

namespace HushSwitch;

public partial class ExitDialog : Window
{
    /// <summary>「解除して終了」を選んだか(false は「このまま終了」)。</summary>
    public bool Unmute { get; private set; }

    /// <summary>「今後確認しない」がチェックされていたか。</summary>
    public bool Remember => RememberCheck.IsChecked == true;

    public ExitDialog() => InitializeComponent();

    private void OnUnmute(object sender, RoutedEventArgs e)
    {
        Unmute = true;
        DialogResult = true;
    }

    private void OnLeave(object sender, RoutedEventArgs e) => DialogResult = true;
}
