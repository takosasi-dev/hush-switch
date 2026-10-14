// Windows の起動時の自動起動。自分のユーザーの起動項目(レジストリ)に 1 行足すだけで、管理者権限は使わない。
// 起動されたときは --tray が付くので、ウィンドウを出さずにトレイに格納した状態で始まる。
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace HushSwitch;

public static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "HushSwitch";

    // exe を別の場所へ移すと、登録した行は古い場所を指したままになる(そのときは Enabled が false になる)
    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    /// <summary>今の exe が登録されているか。</summary>
    public static bool Enabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Key);
                return key?.GetValue(Name) as string == Command;
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
    }

    /// <returns>書けたか。</returns>
    public static bool Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (on) key.SetValue(Name, Command);
            else key.DeleteValue(Name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }
}
