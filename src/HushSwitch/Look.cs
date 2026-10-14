// 見た目の設定(デザイン・配色・アクセントの色・文字の大きさ・表示する物)をメインウィンドウに当てる。
// 行の形はメインウィンドウの資源 Row.<デザイン> にあり、ここでは差し替えるだけ。
// tools/shoot_ui.cs からも呼ぶので、メインウィンドウの実体(音の処理)には触らない。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HushSwitch;

public static class Look
{
    private const double BaseMinWidth = 340;   // MainWindow.xaml の MinWidth と同じ

    /// <param name="root">文字の大きさに合わせて拡大する、ウィンドウの中身。</param>
    public static void Apply(Window window, ItemsControl list, FrameworkElement root, TargetStore store)
    {
        // ゲーミングは暗い地のデザインなので、配色はダークに決める
        App.ApplyTheme(store.Design == "gaming" ? "dark" : store.Theme);
        Accent.Apply(store.Accent);

        list.ItemTemplate = (DataTemplate)window.FindResource("Row." + store.Design);
        list.ItemsPanel = (ItemsPanelTemplate)window.FindResource(store.Design == "tiles" ? "Panel.tiles" : "Panel.list");
        if (store.Design == "gaming") window.SetResourceReference(Control.BackgroundProperty, "GamingBackground");
        else window.ClearValue(Control.BackgroundProperty);

        // テーマのスタイルがボタン等の文字の大きさを決めているので、文字だけでなく中身ごと拡大する
        double scale = store.TextSize switch { "small" => 0.9, "large" => 1.15, "xlarge" => 1.3, _ => 1.0 };
        root.LayoutTransform = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);
        // 拡大したぶん最小の幅も広げる(狭いままだと、名前の列が潰れる)
        window.MinWidth = BaseMinWidth * scale;

        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        window.Resources["RowIconVisibility"] = Show(store.ShowIcons);
        window.Resources["RowStatusVisibility"] = Show(store.ShowStatus);
        window.Resources["RowRemoveVisibility"] = Show(store.ShowRemove);
        window.Resources["SummaryVisibility"] = Show(store.ShowSummary);
    }
}
