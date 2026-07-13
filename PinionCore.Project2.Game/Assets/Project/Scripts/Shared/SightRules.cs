namespace PinionCore.Project2.Shared
{
    /// <summary>視野規則常數:供 Worlds 的 Sight 演算法與 Shared 的 gizmo 繪製共用。</summary>
    public static class SightRules
    {
        /// <summary>離開半徑 = SightRadius × 此係數:已可見的配對要超出此距離才算失去,防邊界震盪。</summary>
        public const float ExitRadiusFactor = 1.1f;
    }
}
