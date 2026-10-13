// 試験用のコンソール(製品には含めない)。セッションの列挙ダンプ、指定 exe のミュート切替、
// セッション作成通知の実測、音の出ない試験用セッションの作成、自己点検を行う。
using System.Diagnostics;
using System.Text;
using HushSwitch;
using NAudio.CoreAudioApi;
using NAudio.Wave;

Console.OutputEncoding = Encoding.UTF8;
var service = new AudioSessionService();

return args switch
{
    ["dump"] => Dump(),
    ["mute", var exe, var mode and ("on" or "off")] => Mute(exe, mode == "on"),
    ["watch", var seconds] => Watch(int.Parse(seconds)),
    ["silence", var seconds] => Silence(int.Parse(seconds), null),
    ["silence", var seconds, var device] => Silence(int.Parse(seconds), int.Parse(device)),
    ["selfcheck"] => SelfCheck(),
    _ => Usage(),
};

int Usage()
{
    Console.WriteLine("""
        dump                      全再生デバイスのセッションを列挙する(§8.4 の採取)
        mute <exe> on|off         指定した exe の全セッションのミュートを切り替える
        watch <秒>                セッション作成通知を全デバイスで購読し、届いたスレッドを表示する
        silence <秒> [デバイス番号] 無音を再生して自分のセッションを作る(番号は dump のデバイス一覧)
        selfcheck                 exe 名の解決と列挙の自己点検
        """);
    return 2;
}

string Format(SessionInfo s) =>
    $"{s.DeviceName} / {s.Pid} / {s.Exe ?? "(解決できず)"} / {s.State} / {(s.Muted ? "ミュート" : "ミュートなし")} / {(s.IsSystemSounds ? "システム音" : "-")}";

string ThreadInfo() =>
    $"thread={Environment.CurrentManagedThreadId} apartment={Thread.CurrentThread.GetApartmentState()} pool={Thread.CurrentThread.IsThreadPoolThread}";

int Dump()
{
    Console.WriteLine($"# {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}");
    Console.WriteLine("# 有効な再生デバイス(* は既定)");
    using (var enumerator = new MMDeviceEnumerator())
    using (var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        string? defaultId = null;
        if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var defaultDevice))
            using (defaultDevice) defaultId = defaultDevice.ID;
        for (int i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            Console.WriteLine($"#  [{i}]{(device.ID == defaultId ? "*" : " ")} {device.FriendlyName}");
        }
    }

    var watch = Stopwatch.StartNew();
    var sessions = service.ListSessions();
    watch.Stop();
    Console.WriteLine("# デバイス名 / PID / exe 名 / State / ミュート / システム音か");
    foreach (var s in sessions) Console.WriteLine(Format(s));
    foreach (var error in service.DeviceErrors) Console.WriteLine($"# 読めなかったデバイス: {error}");
    Console.WriteLine($"# {sessions.Count} セッション、列挙 {watch.ElapsedMilliseconds} ms");
    return 0;
}

int Mute(string exe, bool mute)
{
    var watch = Stopwatch.StartNew();
    exe = exe.ToLowerInvariant();
    var result = service.SetMute(s => s.Exe == exe, mute);
    watch.Stop();
    var own = result.Sessions.Where(s => s.Exe == exe && !s.IsSystemSounds).ToList();
    foreach (var s in own) Console.WriteLine(Format(s));
    foreach (var error in service.DeviceErrors) Console.WriteLine($"# 読めなかったデバイス: {error}");
    Console.WriteLine($"# {exe} を {(mute ? "ミュート" : "解除")}: 対象 {own.Count} 件、失敗 {result.Failed.Count} 件、{watch.ElapsedMilliseconds} ms");
    return own.Count > 0 && result.Failed.Count == 0 ? 0 : 1;
}

int Watch(int seconds)
{
    Console.WriteLine($"# 購読側: {ThreadInfo()}");
    using var enumerator = new MMDeviceEnumerator();
    using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
    var held = new List<MMDevice>();
    foreach (var device in devices)
    {
        string name = device.FriendlyName;
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        manager.OnSessionCreated += (_, session) => // session は借り物。所有しないので Dispose しない
        {
            var watch = Stopwatch.StartNew();
            uint pid = session.GetProcessID;
            bool muted = session.SimpleAudioVolume.Mute;
            ProcessNames.Snapshot().TryGetValue(pid, out var exe);
            Console.WriteLine($"created {DateTimeOffset.Now:HH:mm:ss.fff} / {name} / {pid} / {exe ?? "(解決できず)"} / {(muted ? "ミュート" : "ミュートなし")} / {ThreadInfo()} / 読み取り {watch.ElapsedMilliseconds} ms");
        };
        held.Add(device);
    }
    Console.WriteLine($"# {held.Count} デバイスで購読中。{seconds} 秒待つ");
    Thread.Sleep(seconds * 1000);
    foreach (var device in held) device.Dispose();
    return 0;
}

int Silence(int seconds, int? deviceIndex)
{
    using var enumerator = new MMDeviceEnumerator();
    using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
    using var device = deviceIndex is int index
        ? devices[index]
        : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    using var client = device.CreateAudioClient();
#pragma warning disable CS0618 // 無音を流すだけなので、旧来の WasapiOut で足りる
    using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
#pragma warning restore CS0618
    output.Init(new SilenceProvider(client.MixFormat));
    output.Play();
    Console.WriteLine($"# 無音を再生中: {device.FriendlyName} / PID {Environment.ProcessId} / {seconds} 秒");
    Thread.Sleep(seconds * 1000);
    return 0;
}

int SelfCheck()
{
    int failures = 0;
    void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "ok  " : "NG  ")}{what}");
        if (!ok) failures++;
    }

    var names = ProcessNames.Snapshot();
    string self = Path.GetFileName(Environment.ProcessPath!).ToLowerInvariant();
    Check(names.GetValueOrDefault((uint)Environment.ProcessId) == self, $"自分の PID が自分の exe 名({self})に解決される");
    Check(!names.ContainsKey(0), "PID 0 は表に入れない");
    Check(names.Values.All(n => n == n.ToLowerInvariant()), "exe 名はすべて小文字");
    Check(names.ContainsValue("csrss.exe"), "保護されたプロセス(csrss.exe)の名前も取れる");

    var first = service.ListSessions();
    Check(first.All(s => s.Exe != null || s.IsSystemSounds || !names.ContainsKey(s.Pid)), "解決できないのは一覧に無い PID だけ");

    const int rounds = 100;
    using var me = Process.GetCurrentProcess();
    long handlesBefore = me.HandleCount;
    var watch = Stopwatch.StartNew();
    for (int i = 0; i < rounds; i++) service.ListSessions();
    watch.Stop();
    me.Refresh();
    Console.WriteLine($"# 列挙 {rounds} 回: 平均 {watch.Elapsed.TotalMilliseconds / rounds:F1} ms、セッション {first.Count} 件、ハンドル数 {handlesBefore} → {me.HandleCount}");
    return failures;
}
