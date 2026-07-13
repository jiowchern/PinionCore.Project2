// 讓測試組件可以直接驅動 World.Update / 讀取 Player.CurrentMoveInfo 等 internal 成員,
// 以 WorldTestScript 模式做免場景的權威模擬單元測試。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Tests")]
