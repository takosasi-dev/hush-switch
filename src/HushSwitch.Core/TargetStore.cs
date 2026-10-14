// 設定(settings.json)の読み書きと、対象(exe 名)の追加・削除の検証(FR-2, FR-3, FR-11)。
// 保存するのは小文字の exe ファイル名と画面の設定だけで、ミュートの状態は書かない(INV-9)。
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HushSwitch;

/// <summary>カテゴリー 1 つ。名前・まとめての切り替えのホットキー(空なら無し)・一覧で畳んでいるか。</summary>
public sealed class Category
{
    public string Name { get; set; } = "";
    public string Hotkey { get; set; } = "";
    public bool Collapsed { get; set; }
}

public sealed class TargetStore
{
    public const int MaxTargets = 20;

    private readonly string path;
    private string[] targets = [];

    /// <summary>登録順の対象。変更のたびに新しい配列へ差し替えるので、別スレッドから読んでよい。</summary>
    public IReadOnlyList<string> Targets => targets;

    // 画面の設定。値を入れたあと Save を呼ぶ
    public bool CloseToTray { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    /// <summary>ask / leave / unmute。</summary>
    public string OnExit { get; set; } = "ask";
    /// <summary>system / light / dark。</summary>
    public string Theme { get; set; } = "system";
    public bool StartInTray { get; set; }
    /// <summary>まとめトグルのホットキー("Ctrl+Alt+M" の形)。空なら無し。</summary>
    public string Hotkey { get; set; } = "";
    /// <summary>ウィンドウの 左・上・幅・高さ。まだ覚えていなければ null。</summary>
    public double[]? Window { get; set; }

    // 見た目の設定。選べる値は下の配列(先頭が既定値)
    public static readonly string[] Designs = ["standard", "compact", "large", "tiles", "gaming"];
    public static readonly string[] Accents = ["system", "blue", "teal", "green", "purple", "pink", "orange", "red"];
    public static readonly string[] TextSizes = ["medium", "small", "large", "xlarge"];
    public string Design { get; set; } = "standard";
    /// <summary>system は Windows のアクセントの色。</summary>
    public string Accent { get; set; } = "system";
    public string TextSize { get; set; } = "medium";
    /// <summary>メインウィンドウの不透明度(30〜100%)。</summary>
    public int Opacity { get; set; } = 100;
    public bool ShowIcons { get; set; } = true;
    public bool ShowStatus { get; set; } = true;
    public bool ShowSummary { get; set; } = true;
    public bool ShowRemove { get; set; } = true;

    public const int MaxCategories = 10;
    public const int MaxCategoryName = 20;
    /// <summary>どこにも入れていないアプリの見出し。保存せず、この名前のカテゴリーは作れない。</summary>
    public const string OtherCategory = "その他";
    public static readonly string[] DefaultCategories = ["ゲーム", "ブラウザ", "通話", "音楽"];
    /// <summary>並び順のカテゴリー。設定ファイルにキーが無ければ既定の 4 つ、空の配列なら空のまま。</summary>
    public List<Category> Categories { get; private set; } = DefaultCategoryList();
    private readonly Dictionary<string, string> categoryOf = [];   // exe 名 → カテゴリー名

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
            Theme = "system";
            StartInTray = false;
            Hotkey = "";
            Window = null;
            (Design, Accent, TextSize, Opacity) = (Designs[0], Accents[0], TextSizes[0], 100);
            (ShowIcons, ShowStatus, ShowSummary, ShowRemove) = (true, true, true, true);
            Categories = DefaultCategoryList();
            categoryOf.Clear();
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
        categoryOf.Remove(exe);
        Save();
    }

    // ---- カテゴリー(一覧の見出し、まとめての切り替え、ホットキー)----

    /// <summary>そのアプリのカテゴリー名。どこにも入れていなければ null(「その他」に出す)。</summary>
    public string? CategoryOf(string exe) => categoryOf.GetValueOrDefault(exe);

    /// <param name="category">入れるカテゴリーの名前。null なら「その他」に戻す。</param>
    public void Assign(string exe, string? category)
    {
        if (category == null) categoryOf.Remove(exe);
        else if (targets.Contains(exe) && Categories.Any(c => c.Name == category)) categoryOf[exe] = category;
        Save();
    }

    /// <summary>カテゴリーを足す。拒否したら理由を返す。</summary>
    public string? AddCategory(string name)
    {
        name = name.Trim();
        if (Categories.Count >= MaxCategories) return $"カテゴリーは {MaxCategories} 個までです";
        if (CheckCategoryName(name, null) is { } error) return error;
        Categories.Add(new Category { Name = name });
        Save();
        return null;
    }

    /// <summary>カテゴリーの名前を変える(振り分けも付いていく)。拒否したら理由を返す。</summary>
    public string? RenameCategory(string current, string name)
    {
        name = name.Trim();
        if (name == current) return null;
        if (CheckCategoryName(name, current) is { } error) return error;
        Categories.First(c => c.Name == current).Name = name;
        foreach (string exe in categoryOf.Where(p => p.Value == current).Select(p => p.Key).ToList()) categoryOf[exe] = name;
        Save();
        return null;
    }

    /// <summary>カテゴリーを消す。入っていたアプリは「その他」に戻る。</summary>
    public void RemoveCategory(string name)
    {
        Categories.RemoveAll(c => c.Name == name);
        foreach (string exe in categoryOf.Where(p => p.Value == name).Select(p => p.Key).ToList()) categoryOf.Remove(exe);
        Save();
    }

    /// <summary>そのホットキーを、自分以外の誰が使っているか。self は自分(まとめトグルなら ""、カテゴリーならその名前)。</summary>
    /// <returns>使っている物の表示名。誰も使っていなければ null。</returns>
    public string? HotkeyUsedBy(string gesture, string self)
    {
        if (gesture.Length == 0) return null;
        if (self != "" && string.Equals(Hotkey, gesture, StringComparison.OrdinalIgnoreCase)) return "すべてミュート / すべて解除";
        return Categories.FirstOrDefault(c => c.Name != self && string.Equals(c.Hotkey, gesture, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private string? CheckCategoryName(string name, string? current)
    {
        if (name.Length == 0) return "名前を入れてください";
        if (name.Length > MaxCategoryName) return $"名前は {MaxCategoryName} 文字までです";
        if (name == OtherCategory) return $"「{OtherCategory}」は使えません(どこにも入れていないアプリの見出しです)";
        if (Categories.Any(c => c.Name == name && c.Name != current)) return "同じ名前のカテゴリーがあります";
        return null;
    }

    private static List<Category> DefaultCategoryList() => DefaultCategories.Select(n => new Category { Name = n }).ToList();

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
        if (root.TryGetProperty("theme", out var theme) && theme.ValueKind == JsonValueKind.String
            && theme.GetString() is "system" or "light" or "dark")
            Theme = theme.GetString()!;
        if (root.TryGetProperty("start_in_tray", out var start) && start.ValueKind is JsonValueKind.True or JsonValueKind.False)
            StartInTray = start.GetBoolean();
        if (root.TryGetProperty("hotkey", out var hotkey) && hotkey.ValueKind == JsonValueKind.String
            && hotkey.GetString()!.Length <= 64)
            Hotkey = hotkey.GetString()!;
        if (root.TryGetProperty("window", out var window) && window.ValueKind == JsonValueKind.Array
            && window.GetArrayLength() == 4 && window.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number))
        {
            double[] box = window.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (box.All(double.IsFinite) && box[2] is > 0 and < 100000 && box[3] is > 0 and < 100000) Window = box;
        }

        // 知らない値は読み飛ばして、既定値のままにする
        string Pick(string key, string[] allowed, string current) =>
            root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && allowed.Contains(v.GetString()) ? v.GetString()! : current;
        bool Flag(string key, bool current) =>
            root.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : current;
        Design = Pick("design", Designs, Design);
        Accent = Pick("accent", Accents, Accent);
        TextSize = Pick("text_size", TextSizes, TextSize);
        if (root.TryGetProperty("opacity", out var opacity) && opacity.ValueKind == JsonValueKind.Number
            && opacity.TryGetInt32(out int percent) && percent is >= 30 and <= 100)
            Opacity = percent;
        ShowIcons = Flag("show_icons", ShowIcons);
        ShowStatus = Flag("show_status", ShowStatus);
        ShowSummary = Flag("show_summary", ShowSummary);
        ShowRemove = Flag("show_remove", ShowRemove);

        if (root.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
        {
            Categories = [];
            foreach (var element in categories.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String || Categories.Count >= MaxCategories) continue;
                string text = name.GetString()!.Trim();
                if (CheckCategoryName(text, null) != null) continue;
                string hotkeyText = element.TryGetProperty("hotkey", out var key) && key.ValueKind == JsonValueKind.String
                    && key.GetString()!.Length <= 64 ? key.GetString()! : "";
                bool collapsed = element.TryGetProperty("collapsed", out var fold) && fold.ValueKind == JsonValueKind.True;
                Categories.Add(new Category { Name = text, Hotkey = hotkeyText, Collapsed = collapsed });
            }
        }
        // 登録していないアプリや、無いカテゴリーを指す振り分けは捨てる
        if (root.TryGetProperty("category_of", out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var entry in map.EnumerateObject())
                if (targets.Contains(entry.Name) && entry.Value.ValueKind == JsonValueKind.String
                    && Categories.Any(c => c.Name == entry.Value.GetString()))
                    categoryOf[entry.Name] = entry.Value.GetString()!;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(
                new
                {
                    schema = 1, targets, close_to_tray = CloseToTray, always_on_top = AlwaysOnTop, on_exit = OnExit,
                    theme = Theme, start_in_tray = StartInTray, hotkey = Hotkey, window = Window,
                    design = Design, accent = Accent, text_size = TextSize, opacity = Opacity,
                    show_icons = ShowIcons, show_status = ShowStatus, show_summary = ShowSummary, show_remove = ShowRemove,
                    categories = Categories.Select(c => new { name = c.Name, hotkey = c.Hotkey, collapsed = c.Collapsed }),
                    category_of = categoryOf,
                },
                // カテゴリーの名前(日本語)を \uXXXX にせず、そのまま読める形で書く
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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
