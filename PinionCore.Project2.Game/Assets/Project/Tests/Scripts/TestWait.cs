using System;
using PinionCore.Project2.Shared;
using UniRx;

namespace PinionCore.Project2.Tests
{
    /// <summary>
    /// 測試共用的訊息通知等待工具:取代 realtimeSinceStartup deadline 輪詢。
    ///
    /// 時鐘前提:所有 Timeout/Throttle/Timer 走 UniRx 預設 MainThreadScheduler
    /// (frame-driven scaled time,單幀推進量受 Time.maximumDeltaTime 截斷)——
    /// 編輯器卡頓(失焦節流、GC、asset import)時等待預算幾乎不消耗,
    /// timeout 公平地跟著遊戲暫停;wall-clock 最後保險由測試的 [Timeout] 屬性承擔。
    /// 若未來測試操作 Time.timeScale,需改用 Scheduler.MainThreadIgnoreTimeScale。
    ///
    /// 慣例:回傳 ObservableYieldInstruction(throwOnError:false),
    /// 「先建 wait → 觸發動作 → yield → AssertDone」;
    /// 等待 NetSync SupplyEvent(不 replay)或 ghost 事件回應時,wait 必須建立在觸發動作之前。
    /// 注意:ghost 事件每個新訂閱必收到一筆當下狀態的 replay(晚一個網路往返),
    /// predicate 與收集斷言都要把這筆算進去。
    /// </summary>
    public static class TestWait
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

        // 等第一個事件(現有 First+Timeout 慣例的薄封裝)
        public static ObservableYieldInstruction<T> First<T>(IObservable<T> source, TimeSpan? timeout = null)
        {
            return source.First().Timeout(timeout ?? DefaultTimeout).ToYieldInstruction(throwOnError: false);
        }

        // 等第一個符合條件的事件
        public static ObservableYieldInstruction<T> First<T>(IObservable<T> source, Func<T, bool> predicate, TimeSpan? timeout = null)
        {
            return source.Where(predicate).First().Timeout(timeout ?? DefaultTimeout).ToYieldInstruction(throwOnError: false);
        }

        // 等 N 個事件。perGap=false:timeout 界定總時間;perGap=true:界定每筆事件的間距
        // (UniRx Timeout 是 per-gap 語意——每個 OnNext 重置計時,故位置決定語意)
        public static ObservableYieldInstruction<T[]> Count<T>(IObservable<T> source, int count, TimeSpan timeout, bool perGap = false)
        {
            IObservable<T[]> collected = perGap
                ? source.Timeout(timeout).Take(count).ToArray()
                : source.Take(count).ToArray().Timeout(timeout);
            return collected.ToYieldInstruction(throwOnError: false);
        }

        // 事件靜默窗:收到新事件就重置窗,靜默 quiet 後發出「最後一筆」。
        // StartWith(seed) 讓「訂閱起就無事件」也能在 quiet 後完成;Result==seed 代表窗內無事件。
        public static ObservableYieldInstruction<T> Quiet<T>(IObservable<T> source, T seed, TimeSpan quiet, TimeSpan? timeout = null)
        {
            return source.StartWith(seed).Throttle(quiet)
                .First().Timeout(timeout ?? DefaultTimeout)
                .ToYieldInstruction(throwOnError: false);
        }

        // frame-based 條件等待(殼位移、相機狀態這類無事件來源的狀態)。
        // 輪詢節奏與舊 while 迴圈相同(每幀一次),差別在逾時時鐘是 frame-driven 而非 wall-clock。
        public static ObservableYieldInstruction<Unit> Until(Func<bool> condition, TimeSpan? timeout = null)
        {
            return Observable.EveryUpdate().Where(_ => condition()).AsUnitObservable()
                .First().Timeout(timeout ?? DefaultTimeout)
                .ToYieldInstruction(throwOnError: false);
        }

        // 條件連續 stableFrames 幀成立(相機穩定這類)
        public static ObservableYieldInstruction<Unit> UntilStable(Func<bool> condition, int stableFrames, TimeSpan? timeout = null)
        {
            return Observable.EveryUpdate()
                .Select(_ => condition())
                .Scan(0, (acc, ok) => ok ? acc + 1 : 0)
                .Where(n => n >= stableFrames).AsUnitObservable()
                .First().Timeout(timeout ?? DefaultTimeout)
                .ToYieldInstruction(throwOnError: false);
        }

        // 負向斷言窗(事件):收集 window 內抵達的所有事件,事後由呼叫端斷言內容
        public static ObservableYieldInstruction<T[]> CollectFor<T>(IObservable<T> source, TimeSpan window)
        {
            return source.TakeUntil(Observable.Timer(window)).ToArray()
                .ToYieldInstruction(throwOnError: false);
        }

        // 負向斷言窗(逐幀不變量):窗內每幀檢查,違規即刻結束。
        // Result==false 代表整窗不變量成立;Result==true 代表出現違規(fail-fast)。
        public static ObservableYieldInstruction<bool> HoldFrames(Func<bool> invariant, TimeSpan window)
        {
            return Observable.Amb(
                    Observable.EveryUpdate().Where(_ => !invariant()).Select(_ => true).Take(1),
                    Observable.Timer(window).Select(_ => false))
                .First().ToYieldInstruction(throwOnError: false);
        }

        // 逾時重訂閱+重送:每次嘗試 = onAttempt(重送指令,可為 null)+ 重新訂閱
        // (Defer 重掛 ghost 事件 → 觸發 replay 取回當下狀態);單次逾時 → Retry 重跑;
        // attempts 次全失敗 → TimeoutException 進 HasError。
        // Timeout 失敗會先 dispose 上游(-=)再重訂閱,維持同一時刻單一註冊。
        public static ObservableYieldInstruction<T> FirstWithRetry<T>(
            Func<IObservable<T>> sourceFactory, Action onAttempt, TimeSpan perAttempt, int attempts)
        {
            return Observable.Defer(() =>
                {
                    onAttempt?.Invoke();
                    return sourceFactory();
                })
                .First().Timeout(perAttempt)
                .Retry(attempts)
                .ToYieldInstruction(throwOnError: false);
        }

        // 統一斷言:逾時與其他錯誤分開呈現,方便從失敗訊息直接定位等待點
        public static void AssertDone<T>(ObservableYieldInstruction<T> wait, string message)
        {
            if (wait.HasError)
            {
                NUnit.Framework.Assert.Fail(wait.Error is TimeoutException
                    ? $"{message}(等待逾時:{wait.Error.Message})"
                    : $"{message}(等待失敗:{wait.Error})");
            }
            NUnit.Framework.Assert.IsTrue(wait.HasResult, message);
        }

        // ghost MoveEvent 的標準 stream(集中 FromEvent 樣板)
        public static IObservable<MoveInfo> MoveEvents(IActor ghost)
        {
            return Observable.FromEvent<MoveInfo>(h => ghost.MoveEvent += h, h => ghost.MoveEvent -= h);
        }

        // ghost StatusEvent 的標準 stream(與 MoveEvent 同為訂閱即 replay)
        public static IObservable<StatusType> StatusEvents(IActor ghost)
        {
            return Observable.FromEvent<StatusType>(h => ghost.StatusEvent += h, h => ghost.StatusEvent -= h);
        }
    }
}
