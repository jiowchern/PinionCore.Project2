namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 集中管理 Addressables 尋址 key(位址字串)。
    ///
    /// 原則(見 Docs/Resource_Configuration_Guide.md §7):
    /// - 伺服器與客戶端共用同一份 key 定義。
    /// - 網路封包只傳這些字串與邏輯資料,絕不傳資源二進位本身。
    /// - 程式端一律用這裡的常數,不要在各處散落硬字串。
    /// </summary>
    public static class AddressKeys
    {
        // Levels group
        public const string LevelTerrain = "level/terrain";
        public const string LevelTestTerrain1 = "level/test/terrain1";
    }
}
