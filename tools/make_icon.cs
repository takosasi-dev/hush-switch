// HushSwitch 自身のアイコン(src/HushSwitch/app.ico)を描いて作る(開発用。絵を変えたいときだけ実行する)。
// 絵は図形だけで描く(角の丸い四角に、白いスピーカーと ×)。フォントの字や外の画像は使わない。
// 使い方: dotnet run tools/make_icon.cs -- [見本の PNG の出力先]
#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property PublishAot=false
#:property PublishTrimmed=false
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

string root = Path.Combine(Path.GetDirectoryName(Here())!, "..");
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
var images = sizes.Select(size =>
{
    using var bitmap = Draw(size);
    using var memory = new MemoryStream();
    bitmap.Save(memory, ImageFormat.Png);
    return memory.ToArray();
}).ToList();

// ICO: 見出し 6 バイト、絵 1 つにつき目次 16 バイト、そのあとに PNG を並べる
using (var writer = new BinaryWriter(File.Create(Path.Combine(root, "src", "HushSwitch", "app.ico"))))
{
    writer.Write((short)0);
    writer.Write((short)1);
    writer.Write((short)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        byte side = (byte)(sizes[i] == 256 ? 0 : sizes[i]);   // 256 は 0 と書く決まり
        writer.Write(side);
        writer.Write(side);
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(images[i].Length);
        writer.Write(offset);
        offset += images[i].Length;
    }
    foreach (byte[] image in images) writer.Write(image);
}

if (args.Length > 0)
{
    // 見本: 明るい地と暗い地に、大きさ違いを並べる
    using var sheet = new Bitmap(520, 300);
    using var g = Graphics.FromImage(sheet);
    g.Clear(Color.FromArgb(0xF3, 0xF3, 0xF3));
    g.FillRectangle(Brushes.Black, 260, 0, 260, 300);
    foreach (int left in new[] { 0, 260 })
    {
        using var big = Draw(128);
        g.DrawImageUnscaled(big, left + 16, 16);
        int x = left + 16;
        foreach (int size in new[] { 48, 32, 24, 16 })
        {
            using var small = Draw(size);
            g.DrawImageUnscaled(small, x, 180);
            x += size + 16;
        }
    }
    sheet.Save(args[0], ImageFormat.Png);
}

static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

static Bitmap Draw(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.ScaleTransform(size / 256f, size / 256f);   // 以下は 256×256 のつもりで描く

    using var back = new GraphicsPath();
    const float edge = 8, side = 240, corner = 108;
    back.AddArc(edge, edge, corner, corner, 180, 90);
    back.AddArc(edge + side - corner, edge, corner, corner, 270, 90);
    back.AddArc(edge + side - corner, edge + side - corner, corner, corner, 0, 90);
    back.AddArc(edge, edge + side - corner, corner, corner, 90, 90);
    back.CloseFigure();
    using var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(256, 256),
        Color.FromArgb(0x6C, 0x7B, 0xFF), Color.FromArgb(0x3A, 0x3F, 0xC4));
    g.FillPath(fill, back);

    // スピーカー(角を丸めるために、同じ形を太い線でもなぞる)
    using var speaker = new GraphicsPath();
    speaker.AddPolygon(new PointF[] { new(58, 108), new(88, 108), new(132, 70), new(132, 186), new(88, 148), new(58, 148) });
    using var outline = new Pen(Color.White, 12) { LineJoin = LineJoin.Round };
    g.FillPath(Brushes.White, speaker);
    g.DrawPath(outline, speaker);

    // ×
    using var cross = new Pen(Color.White, 20) { StartCap = LineCap.Round, EndCap = LineCap.Round };
    g.DrawLine(cross, 166, 104, 214, 152);
    g.DrawLine(cross, 214, 104, 166, 152);
    return bitmap;
}
