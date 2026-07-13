using Unity.Entities;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 空的「標記元件 (tag component)」:標示某顆 entity 是「地形」。
    ///
    /// - 沒有任何欄位 → 不佔資料空間,只是一個型別標記。
    /// - ECS 裡 entity 沒有名字,只能靠「身上帶哪些元件」來查詢;
    ///   有了 TerrainTag,系統/測試就能用 EntityQuery(typeof(TerrainTag)) 精準認出地形。
    /// </summary>
    public struct TerrainTag : IComponentData
    {
    }
}
