// 設定(settings.json)の読み書きと、対象(exe 名)の追加・削除の検証(FR-2, FR-3, FR-11)。
// 保存するのは小文字の exe ファイル名と画面の設定だけで、ミュートの状態は書かない(INV-9)。
using System.Text.Json;

namespace HushSwitch;

public sealed class TargetStore
{
    public const int MaxTargets = 20;

    private readonly string path;
    private string[] targets = [];

    /// <summary>登録順の対象。変更のたびに新しい配列へ差し替えるので、別スレッドから読んでよい。</summary>
    public IReadOnlyList<string> Targets => targets;
    public bool CloseToTray { get; private set; } = true;
    public bool AlwaysOnTop { get; private set; }
    public string OnExit { get; private set; } = "ask";

    /// <summary>読み込み時に知らせること(§9.5)。無ければ空。</summary>
    public List<string> Notices { get; } = [];

    /// <summary>直前の保存に失敗したか。次の設定変更でまた保存を試す(§10)。</summary>
    public bool SaveFailed { get; private set; }

    public TargetStore(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        try
        {
            Load();
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            targets = [];
            CloseToTray = true;
            AlwaysOnTop = false;
            OnExit = "ask";
            Notices.Clear();
            string bad = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Move(path, bad); }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException) { }
            Notices.Add($"設定ファイルを読み込めなかったため、初期設定で起動しました(元のファイルは {Path.GetFileName(bad)} に退避しました)");
        }
    }

    /// <summary>ファイル名を対象に追加する。拒否したら理由(§9.5)を返し、追加できたら null を返す。</summary>
    public string? Add(string fileName)
    {
        string exe = fileName.ToLowerInvariant();
        if (exe.Length > 128) return "ファイル名が長すぎます";
        if (!IsValidName(exe)) return "exe ファイルを選んでください";
        if (targets.Contains(exe)) return "すでに登録されています";
        if (targets.Length >= MaxTargets) return $"登録できるのは {MaxTargets} 件までです";
        targets = [.. targets, exe];
        Save();
        return null;
    }

    public void Remove(string exe)
    {
        targets = targets.Where(t => t != exe).ToArray();
        Save();
    }

    /// <summary>§9.2 の targets の要素として通るか。</summary>
    public static bool IsValidName(string name) =>
        name.Length is >= 1 and <= 128
        && name == name.ToLowerInvariant()
        && name.EndsWith(".exe", StringComparison.Ordinal)
        && name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0;

    private void Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schema", out var schema)
            || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1)
            throw new InvalidDataException();

        if (root.TryGetProperty("targets", out var array))
        {
            if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
            var list = new List<string>();
            int skipped = 0;
            foreach (var element in array.EnumerateArray())
            {
                string? name = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                if (name != null && IsValidName(name) && !list.Contains(name) && list.Count < MaxTargets) list.Add(name);
                else skipped++;
            }
            targets = [.. list];
            if (skipped > 0) Notices.Add($"設定ファイルの targets に使えない要素が {skipped} 件あったため、読み飛ばしました");
        }

        if (root.TryGetProperty("close_to_tray", out var tray) && tray.ValueKind is JsonValueKind.True or JsonValueKind.False)
            CloseToTray = tray.GetBoolean();
        if (root.TryGetProperty("always_on_top", out var top) && top.ValueKind is JsonValueKind.True or JsonValueKind.False)
            AlwaysOnTop = top.GetBoolean();
        if (root.TryGetProperty("on_exit", out var exit) && exit.ValueKind == JsonValueKind.String
            && exit.GetString() is "ask" or "leave" or "unmute")
            OnExit = exit.GetString()!;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(
                new { schema = 1, targets, close_to_tray = CloseToTray, always_on_top = AlwaysOnTop, on_exit = OnExit },
                new JsonSerializerOptions { WriteIndented = true });
            // 途中で切れたファイルを残さないよう、別名に書いてから置き換える(FR-11)
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            SaveFailed = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SaveFailed = true;
        }
    }
}
