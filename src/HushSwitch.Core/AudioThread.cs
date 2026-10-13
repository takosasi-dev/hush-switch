// Core Audio を呼ぶ専用の1スレッド(D-11)。頼まれた仕事を順番に実行し、
// 手が空いていれば 1000ms ごとに同期(tick)を呼ぶ。UI スレッドはここへ仕事を渡すだけ(INV-7)。
using System.Collections.Concurrent;
using System.Diagnostics;

namespace HushSwitch;

public sealed class AudioThread : IDisposable
{
    private readonly BlockingCollection<Action> queue = [];

    public AudioThread(Action tick, int intervalMs = 1000)
    {
        var thread = new Thread(() =>
        {
            // 周期は単調増加の時計で測る(§10: システム時刻の変更に影響されない)
            long next = Environment.TickCount64;
            while (!queue.IsAddingCompleted)
            {
                int wait = (int)Math.Max(0, next - Environment.TickCount64);
                if (!queue.TryTake(out var work, wait))
                {
                    if (queue.IsAddingCompleted) break;
                    work = tick;
                    next = Environment.TickCount64 + intervalMs;
                }
                try { work(); }
                catch (Exception e) { Trace.WriteLine(e); } // 次の周期で再開する(§10)。ログとエラー表示は Phase 3
            }
        }) { IsBackground = true, Name = "HushSwitch.Audio" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public void Post(Action work)
    {
        if (!queue.IsAddingCompleted) queue.Add(work);
    }

    public void Dispose() => queue.CompleteAdding();
}
