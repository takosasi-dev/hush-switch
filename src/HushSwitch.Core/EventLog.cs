// 操作ログ(§9.3)。1 イベントを 1 行の JSON で追記する。1 ファイルが 5 MB を超えたら次の番号へ移り、
// 更新から 14 日を過ぎたファイルは起動時に消す(FR-15)。exe 名以外のアプリ固有の情報は書かない(INV-6)。
// 書けなくてもミュートの動作は止めない。
using System.Text.Json;

namespace HushSwitch;

public sealed class EventLog
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int KeepDays = 14;

    private readonly string dir;
    private readonly object gate = new();

    public EventLog(string dir)
    {
        this.dir = dir;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var file in new DirectoryInfo(dir).GetFiles("hushswitch-*.jsonl"))
                if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-KeepDays)) file.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Write(string @event, string? source = null, string? exe = null, string? action = null,
        int? sessions = null, long? latencyMs = null, string? result = null, string? reason = null)
    {
        string line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            @event, source, exe, action, sessions, latency_ms = latencyMs, result, reason,
        });
        try
        {
            lock (gate) File.AppendAllText(CurrentFile(), line + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // 今日のファイルのうち、まだ 5 MB を超えていない最初の番号
    private string CurrentFile()
    {
        for (int n = 1; ; n++)
        {
            var file = new FileInfo(Path.Combine(dir, $"hushswitch-{DateTime.Now:yyyyMMdd}-{n:00}.jsonl"));
            if (!file.Exists || file.Length <= MaxBytes) return file.FullName;
        }
    }
}
