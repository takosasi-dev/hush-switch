// トレイアイコン(FR-16, FR-17)。クリックでウィンドウを出し、右クリックのメニューに 表示 / まとめトグル / カテゴリーごとの切り替え / 設定 / 終了 を置く。
// ミュートのある対象が 1 つでもあれば絵を切り替える。
// WPF にトレイの部品が無いので、Windows フォームの NotifyIcon を使う。
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Forms = System.Windows.Forms;

namespace HushSwitch;

public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon = new();
    private readonly Forms.ToolStripMenuItem all = new();
    // 絵は丸地に白のスピーカー。タスクバーが明るくても暗くても見えるよう、地の色で区別する
    private readonly Icon normal = Draw("", Color.FromArgb(0x60, 0x60, 0x60));
    private readonly Icon muted = Draw("", Color.FromArgb(0xC4, 0x2B, 0x1C));

    private readonly List<Forms.ToolStripItem> categoryItems = [];
    private readonly Action<string> toggleCategory;
    private string categoryKey = "";

    /// <param name="toggleCategory">カテゴリーの名前を受け取って、そのカテゴリーをまとめて切り替える。</param>
    public TrayIcon(Action show, Action toggleAll, Action<string> toggleCategory, Action settings, Action exit)
    {
        this.toggleCategory = toggleCategory;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("表示", null, (_, _) => show());
        all.Click += (_, _) => toggleAll();
        menu.Items.Add(all);
        menu.Items.Add("設定…", null, (_, _) => settings());
        menu.Items.Add("終了", null, (_, _) => exit());
        icon.ContextMenuStrip = menu;
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) show();
        };
        Update(false, "すべてミュート", false);
        icon.Visible = true;
    }

    /// <param name="allText">まとめトグルの文言(ウィンドウのボタンと同じ)。</param>
    public void Update(bool hasMute, string allText, bool allEnabled)
    {
        // 毎秒呼ばれるので、変わったときだけ書く
        var next = hasMute ? muted : normal;
        if (icon.Icon != next)
        {
            icon.Icon = next;
            icon.Text = hasMute ? "HushSwitch(ミュートあり)" : "HushSwitch";
        }
        if (all.Text != allText) all.Text = allText;
        if (all.Enabled != allEnabled) all.Enabled = allEnabled;
    }

    /// <summary>まとめトグルの下に、カテゴリーごとの切り替えを並べる(名前と文言)。変わったときだけ組み直す。</summary>
    public void UpdateCategories(IReadOnlyList<(string Name, string Text)> categories, bool enabled)
    {
        string key = string.Join("\n", categories.Select(c => c.Text)) + enabled;
        if (key == categoryKey) return;
        categoryKey = key;
        var menu = icon.ContextMenuStrip!;
        foreach (var item in categoryItems) menu.Items.Remove(item);
        categoryItems.Clear();
        int at = menu.Items.IndexOf(all) + 1;
        foreach (var (name, text) in categories)
        {
            var item = new Forms.ToolStripMenuItem(text) { Enabled = enabled };
            item.Click += (_, _) => toggleCategory(name);
            categoryItems.Add(item);
            menu.Items.Insert(at++, item);
        }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }

    // 絵はアイコン用フォントの字から作る(画像ファイルを持たない)。2 つだけ作って終了まで使う
    private static Icon Draw(string glyph, Color back)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(back))
        using (var font = new Font("Segoe MDL2 Assets", 18, GraphicsUnit.Pixel))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.FillEllipse(brush, 0, 0, 31, 31);
            g.DrawString(glyph, font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
