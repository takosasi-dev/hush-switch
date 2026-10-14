// セッションの識別子に入っている exe の場所(\Device\HarddiskVolume3\...)を、ドライブ文字のパスに直す。
// 一覧にアイコンを出すためだけに使う。識別子は Core Audio が返す文字列で、対象アプリのプロセスには触らない(INV-1)。
// パスは保存もログ出力もしない(INV-6)。
using System.Runtime.InteropServices;
using System.Text;

namespace HushSwitch;

public static class ExePaths
{
    /// <summary>セッションの識別子から exe のパスを作る。場所が入っていない、またはドライブが分からなければ null。</summary>
    public static string? FromSession(string instanceId)
    {
        if (DevicePath(instanceId) is not { } device) return null;
        foreach (string drive in Environment.GetLogicalDrives())   // "C:\"
        {
            var target = new StringBuilder(260);
            if (QueryDosDevice(drive[..2], target, target.Capacity) == 0) continue;
            string prefix = target + "\\";
            if (device.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return drive + device[prefix.Length..];
        }
        return null;
    }

    /// <summary>識別子の「{デバイス}|\Device\...\app.exe%b{GUID}|1%b PID」から、まん中の場所を取り出す。</summary>
    // ponytail: 名前に % を含むフォルダは識別子の中で書き方が変わるはずで、取り出したパスが合わない(アイコンが出ないだけ)
    public static string? DevicePath(string instanceId)
    {
        int start = instanceId.IndexOf('|') + 1;
        int end = start > 0 ? instanceId.IndexOf("%b", start, StringComparison.Ordinal) : -1;
        if (end <= start) return null;
        string device = instanceId[start..end];
        return device.StartsWith(@"\Device\", StringComparison.Ordinal) ? device : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int max);
}
