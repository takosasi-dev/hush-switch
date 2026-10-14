// アクセントの色(強調のボタン・チェック・押したボタン等の色)を、選んだ色に差し替える。system なら Windows の色のまま。
// テーマの辞書のブラシのうち、Windows のアクセントの色の濃淡と同じ色のものを、選んだ色の同じ濃淡で上書きする。
// 配色を切り替えると辞書が読み直されるので、配色を当てたあと毎回呼ぶ。
using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace HushSwitch;

public static class Accent
{
    private static readonly List<object> overridden = [];
    private static string? lastName;
    private static ResourceDictionary? lastTheme;

    // ponytail: 配色が「Windows に合わせる」のまま Windows 側でライト/ダークを切り替えると、上書きした濃淡が前の配色のままになる。
    // 気になるなら、Windows の配色の変更を受けて Apply を呼び直す
    public static void Apply(string name)
    {
        var resources = Application.Current.Resources;
        // 設定を 1 つ変えるたびに呼ばれる。色もテーマの辞書も前と同じなら、差し替え直さない(全ウィンドウの見直しが起きるので)
        var theme = resources.MergedDictionaries.FirstOrDefault();
        if (name == lastName && ReferenceEquals(theme, lastTheme)) return;
        (lastName, lastTheme) = (name, theme);

        foreach (object key in overridden) resources.Remove(key);
        overridden.Clear();
        if (Base(name) is not Color color) return;

        // Windows のアクセントの濃淡 → 選んだ色の同じ濃淡
        var shades = new Dictionary<Color, Color>();
        shades.TryAdd(Opaque(SystemColors.AccentColor), color);
        shades.TryAdd(Opaque(SystemColors.AccentColorLight1), Mix(color, Colors.White, 0.25));
        shades.TryAdd(Opaque(SystemColors.AccentColorLight2), Mix(color, Colors.White, 0.5));
        shades.TryAdd(Opaque(SystemColors.AccentColorLight3), Mix(color, Colors.White, 0.7));
        shades.TryAdd(Opaque(SystemColors.AccentColorDark1), Mix(color, Colors.Black, 0.2));
        shades.TryAdd(Opaque(SystemColors.AccentColorDark2), Mix(color, Colors.Black, 0.4));
        shades.TryAdd(Opaque(SystemColors.AccentColorDark3), Mix(color, Colors.Black, 0.6));

        var replaced = new List<(object Key, Brush Brush)>();
        foreach (var dictionary in resources.MergedDictionaries)
            foreach (DictionaryEntry entry in dictionary)
                if (entry.Value is SolidColorBrush brush && shades.TryGetValue(Opaque(brush.Color), out var shade))
                {
                    var next = new SolidColorBrush(Color.FromArgb(brush.Color.A, shade.R, shade.G, shade.B));
                    next.Freeze();
                    replaced.Add((entry.Key, next));
                }
        foreach (var (key, brush) in replaced)
        {
            resources[key] = brush;   // 直下に置いたほうが、合わせた辞書より先に見つかる
            overridden.Add(key);
        }
    }

    /// <summary>選んだ色の元の色。system なら null。</summary>
    public static Color? Base(string name) => name switch
    {
        "blue" => Color.FromRgb(0x00, 0x78, 0xD4),
        "teal" => Color.FromRgb(0x00, 0x99, 0xA8),
        "green" => Color.FromRgb(0x10, 0x89, 0x3E),
        "purple" => Color.FromRgb(0x87, 0x64, 0xB8),
        "pink" => Color.FromRgb(0xE3, 0x00, 0x8C),
        "orange" => Color.FromRgb(0xF7, 0x63, 0x0C),
        "red" => Color.FromRgb(0xE8, 0x11, 0x23),
        _ => null,
    };

    private static Color Opaque(Color c) => Color.FromRgb(c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
