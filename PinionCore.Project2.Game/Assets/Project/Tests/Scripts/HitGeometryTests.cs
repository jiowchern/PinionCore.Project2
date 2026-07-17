using NUnit.Framework;
using PinionCore.Project2.Shared;
using UnityEngine;

namespace PinionCore.Project2.Tests
{
    // HitGeometry 純幾何判定:同步測試、零時鐘依賴。
    // 座標約定:預設基底 right=(1,0)、forward=(0,1);角度 0°=前方、正=偏右。
    public class HitGeometryTests
    {
        static readonly Vector2 Right = new Vector2(1f, 0f);
        static readonly Vector2 Forward = new Vector2(0f, 1f);
        const long Tick0 = 0;
        const long Tick1 = System.TimeSpan.TicksPerSecond;   // 1 秒窗

        static ActionConfig.HitSegment _Circle(Vector2 offset, float radius) => new ActionConfig.HitSegment
        {
            Shape = HitShapeType.Circle,
            LocalOffset = offset,
            Radius = radius,
        };

        static ActionConfig.HitSegment _Box(Vector2 offset, Vector2 halfExtents, float rotation) => new ActionConfig.HitSegment
        {
            Shape = HitShapeType.Box,
            LocalOffset = offset,
            HalfExtents = halfExtents,
            Rotation = rotation,
        };

        static ActionConfig.HitSegment _Sector(float radius, float from, float to, SectorSweepMode sweep = SectorSweepMode.Static) => new ActionConfig.HitSegment
        {
            Shape = HitShapeType.Sector,
            Radius = radius,
            AngleFrom = from,
            AngleTo = to,
            Sweep = sweep,
        };

        static bool _Test(in ActionConfig.HitSegment segment, Vector2 target, float targetRadius,
            Vector2? anchor = null, Vector2? right = null, Vector2? forward = null,
            long evalFrom = Tick0, long evalTo = Tick1)
        {
            return HitGeometry.Test(segment, anchor ?? Vector2.zero, right ?? Right, forward ?? Forward,
                Tick0, Tick1, evalFrom, evalTo, target, targetRadius);
        }

        [Test]
        public void CircleHitMissBoundary()
        {
            var circle = _Circle(new Vector2(0f, 1f), 1f);   // 圓心在前方 1m
            Assert.IsTrue(_Test(circle, new Vector2(0f, 2.2f), 0.3f), "d=1.2 ≤ R+r=1.3 應命中");
            Assert.IsFalse(_Test(circle, new Vector2(0f, 2.4f), 0.3f), "d=1.4 > 1.3 應未中");
            Assert.IsTrue(_Test(circle, new Vector2(0f, 1f), 0.3f), "目標在圓心應命中");
        }

        [Test]
        public void CircleRespectsActionBasis()
        {
            // 動作面向 +X:forward=(1,0)、right=(0,-1);offset(0,1) 的圓心應在 (1,0)
            var circle = _Circle(new Vector2(0f, 1f), 0.5f);
            var hit = _Test(circle, new Vector2(1.5f, 0f), 0.1f,
                right: new Vector2(0f, -1f), forward: new Vector2(1f, 0f));
            var miss = _Test(circle, new Vector2(0f, 1.5f), 0.1f,
                right: new Vector2(0f, -1f), forward: new Vector2(1f, 0f));
            Assert.IsTrue(hit, "基底旋轉後圓心應跟著動作前方");
            Assert.IsFalse(miss, "世界 +Z 方向不再是動作前方,不應命中");
        }

        [Test]
        public void BoxAxisAligned()
        {
            var box = _Box(new Vector2(0f, 1f), new Vector2(0.5f, 1f), 0f);   // 覆蓋 x∈[-0.5,0.5], y∈[0,2]
            Assert.IsTrue(_Test(box, new Vector2(0f, 1.5f), 0.3f), "盒內應命中");
            Assert.IsTrue(_Test(box, new Vector2(0f, 2.2f), 0.3f), "前緣外 0.2m ≤ r=0.3 應命中");
            Assert.IsFalse(_Test(box, new Vector2(0.9f, 1f), 0.3f), "右緣外 0.4m > 0.3 應未中");
            Assert.IsFalse(_Test(box, new Vector2(0.9f, 2.3f), 0.3f), "角外斜距 √(0.4²+0.3²)=0.5 > 0.3 應未中");
        }

        [Test]
        public void BoxRotated45()
        {
            // 細長刀刃盒指向右前 45°
            var box = _Box(Vector2.zero, new Vector2(0.1f, 1f), 45f);
            var diag = new Vector2(Mathf.Sin(45f * Mathf.Deg2Rad), Mathf.Cos(45f * Mathf.Deg2Rad));
            Assert.IsTrue(_Test(box, diag * 0.8f, 0.05f), "沿刀刃方向 0.8m 應在盒內");
            Assert.IsFalse(_Test(box, new Vector2(0f, 0.8f), 0.1f), "正前方偏離刀刃 0.57m > 0.2 應未中");
        }

        [Test]
        public void SectorStaticAngleGate()
        {
            var sector = _Sector(2f, -45f, 45f);
            Assert.IsTrue(_Test(sector, new Vector2(0f, 1.5f), 0.3f), "正前方應命中");
            Assert.IsFalse(_Test(sector, new Vector2(0f, -1.5f), 0.3f), "正後方應未中");
            Assert.IsFalse(_Test(sector, new Vector2(0f, 2.5f), 0.1f), "超出半徑應未中");

            // 60° 方向、距離 1:小目標(tol≈5.7°)未中;大目標(tol≈17.5°)邊緣切入命中
            var at60 = new Vector2(Mathf.Sin(60f * Mathf.Deg2Rad), Mathf.Cos(60f * Mathf.Deg2Rad));
            Assert.IsFalse(_Test(sector, at60, 0.1f), "60° 小目標超出角度容差應未中");
            Assert.IsTrue(_Test(sector, at60, 0.3f), "60° 大目標邊緣切入角度容差內應命中");
        }

        [Test]
        public void SectorVertexSwallow()
        {
            var sector = _Sector(2f, -45f, 45f);
            // 目標圓吞掉扇形頂點(d ≤ r):即使圓心在角度範圍外也命中
            Assert.IsTrue(_Test(sector, new Vector2(0.05f, -0.05f), 0.3f), "頂點被吞應命中");
        }

        [Test]
        public void SectorCrossesPlusMinus180()
        {
            var sector = _Sector(2f, 150f, 210f);   // 尾扇,跨 ±180°
            Assert.IsTrue(_Test(sector, new Vector2(0f, -1f), 0.1f), "正後方 (180°) 應命中");
            var atMinus175 = new Vector2(-Mathf.Sin(5f * Mathf.Deg2Rad), -Mathf.Cos(5f * Mathf.Deg2Rad));
            Assert.IsTrue(_Test(sector, atMinus175, 0.1f), "-175°(=185°)應命中");
            Assert.IsFalse(_Test(sector, new Vector2(0f, 1f), 0.1f), "正前方應未中");
        }

        [Test]
        public void SectorReversedAndWide()
        {
            // To < From(往左掃的靜態扇)等價於排序後區間
            var reversed = _Sector(2f, 60f, -60f);
            Assert.IsTrue(_Test(reversed, new Vector2(0f, 1f), 0.1f), "反向區間正前方應命中");

            // 寬 >180° 的區間:只剩正後方一條縫
            var wide = _Sector(2f, -170f, 170f);
            Assert.IsTrue(_Test(wide, new Vector2(1f, 0f), 0.05f), "+90° 應命中");
            Assert.IsFalse(_Test(wide, new Vector2(0f, -1f), 0.05f), "正後方縫隙(小目標)應未中");
            Assert.IsTrue(_Test(wide, new Vector2(0f, -1f), 0.3f), "正後方大目標吃到容差應命中");
        }

        [Test]
        public void SweepAngleClampsToWindow()
        {
            var sweep = _Sector(2f, -90f, 90f, SectorSweepMode.Sweep);
            Assert.AreEqual(-90f, HitGeometry.SweepAngleAt(sweep, Tick0, Tick1, -Tick1), 1e-3f, "窗前應夾在起始角");
            Assert.AreEqual(90f, HitGeometry.SweepAngleAt(sweep, Tick0, Tick1, Tick1 * 2), 1e-3f, "窗後應夾在結束角");
            Assert.AreEqual(0f, HitGeometry.SweepAngleAt(sweep, Tick0, Tick1, Tick1 / 2), 1e-3f, "窗中點應是中間角");
        }

        [Test]
        public void SweepOnlyCoversSweptInterval()
        {
            var sweep = _Sector(2f, -90f, 90f, SectorSweepMode.Sweep);
            var at60 = new Vector2(Mathf.Sin(60f * Mathf.Deg2Rad), Mathf.Cos(60f * Mathf.Deg2Rad));

            // 前半窗只掃 [-90, 0]:60° 目標不應命中;後半窗掃 [0, 90]:應命中
            Assert.IsFalse(_Test(sweep, at60, 0.05f, evalFrom: Tick0, evalTo: Tick1 / 2), "前半窗未掃到 60°");
            Assert.IsTrue(_Test(sweep, at60, 0.05f, evalFrom: Tick1 / 2, evalTo: Tick1), "後半窗掃過 60°");
            // 掃掠範圍外的角度永遠不中
            var at120 = new Vector2(Mathf.Sin(120f * Mathf.Deg2Rad), Mathf.Cos(120f * Mathf.Deg2Rad));
            Assert.IsFalse(_Test(sweep, at120, 0.05f, evalFrom: Tick0, evalTo: Tick1), "120° 在掃掠範圍外");
        }

        [Test]
        public void SweepPartitionLeavesNoGap()
        {
            // 把窗隨機切成 N 個首尾相接的判定區段:任何掃掠範圍內的目標角度,
            // 至少要有一個區段判中(聯集無縫)—— 這是 Sweep 逐 tick 判定不漏的核心保證。
            var sweep = _Sector(2f, -90f, 90f, SectorSweepMode.Sweep);
            var random = new System.Random(20260717);
            for (var round = 0; round < 100; round++)
            {
                var angle = (float)(random.NextDouble() * 160.0 - 80.0);   // 掃掠範圍內側,避開端點
                var target = new Vector2(Mathf.Sin(angle * Mathf.Deg2Rad), Mathf.Cos(angle * Mathf.Deg2Rad));

                var cuts = random.Next(1, 12);
                var boundaries = new System.Collections.Generic.List<long> { Tick0, Tick1 };
                for (var i = 0; i < cuts; i++)
                    boundaries.Add((long)(random.NextDouble() * Tick1));
                boundaries.Sort();

                var hits = 0;
                for (var i = 0; i + 1 < boundaries.Count; i++)
                    if (_Test(sweep, target, 0.01f, evalFrom: boundaries[i], evalTo: boundaries[i + 1]))
                        hits++;
                Assert.GreaterOrEqual(hits, 1, $"角度 {angle:0.##}° 的目標在切成 {boundaries.Count - 1} 段後漏判");
            }
        }
    }
}
