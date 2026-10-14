// 一覧に出す各アプリのアイコン。exe のファイルから読み、写しを %LOCALAPPDATA%\HushSwitch\icons\<exe 名>.png に残す
// (そのアプリが動いていないときにも出せるように)。読むのはファイルだけで、対象アプリのプロセスには触らない(INV-1)。
// exe の場所はここで使うだけで、保存もログ出力もしない(INV-6)。
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HushSwitch;

public sealed class AppIcons(string folder)
{
    private readonly Dictionary<string, ImageSource?> cache = [];
    private readonly HashSet<string> fresh = [];   // この起動のあいだに exe から読み直したもの

    /// <param name="sessionId">そのアプリのセッションの識別子。今セッションが無ければ null。</param>
    /// <returns>アイコン。まだ一度も読めていなければ null(共通の絵を出す)。</returns>
    public ImageSource? Get(string exe, string? sessionId)
    {
        if (sessionId != null && fresh.Add(exe) && FromExe(sessionId) is { } icon)
        {
            cache[exe] = icon;
            Save(icon, exe);
        }
        if (!cache.TryGetValue(exe, out var known)) cache[exe] = known = Load(exe);
        return known;
    }

    private static ImageSource? FromExe(string sessionId)
    {
        try
        {
            if (ExePaths.FromSession(sessionId) is not { } path || !File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractIcon(path, 0, 48);
            if (icon == null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }   // 読めない exe(ストアのアプリ等)。アイコンが出ないだけ
    }

    private void Save(ImageSource icon, string exe)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create((BitmapSource)icon));
            using var file = File.Create(Path.Combine(folder, exe + ".png"));
            encoder.Save(file);
        }
        catch (Exception) { }   // 写しを残せないだけ。次に音を出したときにまた読む
    }

    private ImageSource? Load(string exe)
    {
        try
        {
            string file = Path.Combine(folder, exe + ".png");
            if (!File.Exists(file)) return null;
            using var stream = File.OpenRead(file);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }   // 壊れた写し。exe から読めたときに上書きされる
    }
}
