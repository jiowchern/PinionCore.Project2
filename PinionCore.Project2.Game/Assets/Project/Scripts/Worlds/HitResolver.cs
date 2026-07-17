using System;
using System.Collections.Generic;
using PinionCore.Project2.Shared;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 攻擊命中判定:World.Update 每幀驅動,對「動作進行中且帶 HitSegments」的攻擊者
    /// 掃描其他角色做 hitbox vs 目標圓判定,命中即呼叫目標的 ICharacter.Damage()。
    /// 判定區間 = [上次結清時刻, now]:窗夾在兩幀之間也會被結清一次,
    /// Sweep 扇形的角度覆蓋因此無縫;動作在幀間被打斷/結束則剩餘窗片段作廢
    /// (揮擊被打斷即不再造成傷害,自然語意)。
    /// 位置取樣是幀粒度:攻擊者與目標都取本幀取樣位置。
    /// </summary>
    internal class HitResolver
    {
        class Track
        {
            public long Instance;                              // 動作實例序號:換揮擊即重建
            public long LastEvalTicks;                         // 已結清到的時刻
            public readonly HashSet<Guid> Victims = new HashSet<Guid>();   // 本次揮擊已命中(涵蓋所有 hit 段)
        }

        readonly Dictionary<Guid, Track> _Tracks = new Dictionary<Guid, Track>();

        public void Tick(IEnumerable<PlayerController> controllers, long now)
        {
            foreach (var attacker in controllers)
            {
                var player = attacker.Player;
                var config = player.CurrentActionConfig;
                Guid attackerId = attacker.ActorId;
                if (config == null || config.HitSegments == null || config.HitSegments.Length == 0)
                {
                    _Tracks.Remove(attackerId);
                    continue;
                }

                if (!_Tracks.TryGetValue(attackerId, out var track) || track.Instance != player.ActionInstance)
                {
                    // 新揮擊:LastEvalTicks 從動作開始起算,首幀即覆蓋「開始→現在」的完整掃掠區間
                    track = new Track { Instance = player.ActionInstance, LastEvalTicks = player.ActionStartTicks };
                    _Tracks[attackerId] = track;
                }

                var anchor = player.SamplePositionNow();
                var right = player.ActionRight;
                var forward = player.ActionForward;
                foreach (var segment in config.HitSegments)
                {
                    var segStart = player.ActionStartTicks + (long)(Math.Max(0.0, segment.StartTime) * TimeSpan.TicksPerSecond);
                    var segEnd = segStart + (long)(Math.Max(0.0, segment.Duration) * TimeSpan.TicksPerSecond);
                    if (now < segStart || track.LastEvalTicks >= segEnd)
                        continue;
                    var evalFrom = Math.Max(track.LastEvalTicks, segStart);
                    var evalTo = Math.Min(now, segEnd);

                    foreach (var victim in controllers)
                    {
                        if (ReferenceEquals(victim, attacker))
                            continue;
                        Guid victimId = victim.ActorId;
                        if (track.Victims.Contains(victimId))
                            continue;
                        if (!HitGeometry.Test(segment, anchor, right, forward,
                                segStart, segEnd, evalFrom, evalTo,
                                victim.Player.SamplePositionNow(), victim.Player.Radius))
                            continue;

                        track.Victims.Add(victimId);
                        // Damage 只 push 狀態機、下一次 Update 生效,不動 controllers 集合,迭代中呼叫安全
                        ((ICharacter)victim).Damage();
                    }
                }
                track.LastEvalTicks = now;
            }
        }

        /// <summary>玩家離開世界:清掉其攻擊追蹤(受害者集合裡的 id 不需清,揮擊結束自然作廢)。</summary>
        public void Forget(Guid actorId)
        {
            _Tracks.Remove(actorId);
        }
    }
}
