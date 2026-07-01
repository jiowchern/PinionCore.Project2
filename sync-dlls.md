# sync-dlls.bat

把 `PinionCore.Remote` 編譯後的 DLL,同步到 Unity 端的
`PinionCore.NetSync.Package`。

## 用法

```bat
sync-dlls.bat            :: 預設 Release
sync-dlls.bat Debug      :: 改用 Debug
```

在 repo 根目錄(`D:\develop\ProjectGame2`)執行即可。

## 流程

1. 編譯 `PinionCore.Remote` 下的 13 個專案。
2. 複製 DLL → `PinionCore.NetSync.Package\Runtime\Plugins`
   (Network、Serialization、Utility、Remote、Client、Server、Ghost、Soul、
   Standalone、Gateway、Gateway.Protocols、Protocol.Identify)。
3. 複製 source generator → `PinionCore.NetSync.Package\Analyzers`
   (Tools.Protocol.Sources)。

## 設計重點

- **只覆蓋既有檔案**:NetSync 套件那段,只更新套件裡原本就存在的
  `.dll` / `.pdb` / `.deps.json`,不新增檔案,因此 Unity 的 `.meta`(GUID)保持穩定。
- **不碰 `.meta`**。
- 每個 DLL 都從**自己專案的 bin** 複製,版本不會抓錯。

## 注意

- 需要安裝 .NET SDK(`dotnet` 指令可用)。
