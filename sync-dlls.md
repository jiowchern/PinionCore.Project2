# sync-dlls.bat

把 `PinionCore.Remote` 與 `PinionCore.Project2.Protocols` 編譯後的 DLL,
同步到 Unity 端的 `PinionCore.NetSync.Package` 與 `PinionCore.Project2.Game`。

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
4. 編譯並複製 `PinionCore.Project2.Protocols.dll` →
   `PinionCore.Project2.Game\Assets\Plugins`。

## 設計重點

- **只覆蓋既有檔案**:NetSync 套件那段,只更新套件裡原本就存在的
  `.dll` / `.pdb` / `.deps.json`,不新增檔案,因此 Unity 的 `.meta`(GUID)保持穩定。
- **不碰 `.meta`**。
- 每個 DLL 都從**自己專案的 bin** 複製,版本不會抓錯。
- Protocols 只複製它**本身**的 DLL;它依賴的 `PinionCore.Remote` 等
  已由 NetSync 套件提供,避免重複組件造成 Unity 衝突。

## 注意

- 首次執行後,請在 Unity 內 import 一次,讓 `Assets\Plugins` 下的
  `PinionCore.Project2.Protocols.dll` 產生 `.meta`(GUID)。
  之後重跑只覆蓋 DLL、不動 meta,GUID 一樣穩定。
- 需要安裝 .NET SDK(`dotnet` 指令可用)。
