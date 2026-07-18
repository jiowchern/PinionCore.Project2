using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PinionCore.Project2.Build
{
    /// <summary>
    /// 發布建置入口:Linux 統一伺服器(Server/User/World/Bot,Dedicated Server subtarget)
    /// 與 WebGL client(Client),輸出至 repo 根的 publish/。
    /// 兩端執行期都依賴 Addressables(client 角色模型、server 地形碰撞),
    /// 因此每次切換目標後必須重跑 Addressables content build。
    /// scenes 由 BuildPlayerOptions 傳入,不動全域 Build Settings 與既有 WindowsClient profile。
    /// CLI:Unity.exe -batchmode -quit -executeMethod PinionCore.Project2.Build.PublishBuilder.BuildAll
    /// </summary>
    public static class PublishBuilder
    {
        static readonly string _PublishRoot =
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "publish"));

        [MenuItem("Project/Publish/Build Linux Server")]
        public static void BuildLinuxServer()
        {
            _Build(
                BuildTarget.StandaloneLinux64,
                StandaloneBuildSubtarget.Server,
                new[]
                {
                    "Assets/Project/Scenes/Server.unity",
                    "Assets/Project/Scenes/World.unity",
                    "Assets/Project/Scenes/User.unity",
                    "Assets/Project/Scenes/Bot.unity",
                },
                Path.Combine(_PublishRoot, "linux-server", "ProjectGame2Server.x86_64"));
        }

        [MenuItem("Project/Publish/Build WebGL Client")]
        public static void BuildWebGLClient()
        {
            _ApplyWebGLSettings();
            _Build(
                BuildTarget.WebGL,
                StandaloneBuildSubtarget.Player,
                new[] { "Assets/Project/Scenes/Client.unity" },
                Path.Combine(_PublishRoot, "webgl-client"));
        }

        /// <summary>
        /// 診斷用 development build:內嵌符號 + 完整例外堆疊,wasm 崩潰時 stack 帶函式名。
        /// </summary>
        [MenuItem("Project/Publish/Build WebGL Client (Dev Diagnostics)")]
        public static void BuildWebGLClientDev()
        {
            _ApplyWebGLSettings();
            PlayerSettings.WebGL.debugSymbolMode = WebGLDebugSymbolMode.Embedded;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithStacktrace;
            _Build(
                BuildTarget.WebGL,
                StandaloneBuildSubtarget.Player,
                new[] { "Assets/Project/Scenes/Client.unity" },
                Path.Combine(_PublishRoot, "webgl-client-dev"),
                BuildOptions.Development);
        }

        const string _DisableEcsBootstrapDefine = "UNITY_DISABLE_AUTOMATIC_SYSTEM_BOOTSTRAP";

        static void _ApplyWebGLSettings()
        {
            // 顯式設回 release 值:dev 診斷 build 會覆寫這兩項,不收斂的話殘留設定
            // 會讓下次 release build 帶著內嵌符號與完整例外(體積大、變慢)
            PlayerSettings.WebGL.debugSymbolMode = WebGLDebugSymbolMode.Off;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;

            // 本地 nginx 靜態托管免壓縮 header 設定;要縮小體積時再改 Brotli 並補 header
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            // 預設 32MB 初始 heap 在啟動期大量配置時觸發 wasm memory OOB,加大避開成長路徑
            PlayerSettings.WebGL.initialMemorySize = 256;

            // 本地/內網 demo 走 HTTP;預設 NotAllowed 會讓 connection.json 的
            // UnityWebRequest 丟 Insecure connection not allowed(localhost 豁免、LAN IP 不豁免)
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;

            // Entities/DOTS 不支援 WebGL:AutomaticWorldBootstrap 在啟動期建立預設 ECS World
            // 時無限配置直到 wasm memory OOB(client 不使用 ECS,僅 server 的 Unity.Physics 需要)
            var webgl = NamedBuildTarget.WebGL;
            var defines = PlayerSettings.GetScriptingDefineSymbols(webgl);
            if (!defines.Contains(_DisableEcsBootstrapDefine))
                PlayerSettings.SetScriptingDefineSymbols(
                    webgl,
                    string.IsNullOrEmpty(defines) ? _DisableEcsBootstrapDefine : defines + ";" + _DisableEcsBootstrapDefine);
        }

        public static void BuildAll()
        {
            BuildLinuxServer();
            BuildWebGLClient();
        }

        public static void BuildWebGLBoth()
        {
            BuildWebGLClient();
            BuildWebGLClientDev();
        }

        static void _Build(BuildTarget target, StandaloneBuildSubtarget subtarget, string[] scenes, string output,
            BuildOptions buildOptions = BuildOptions.None)
        {
            // 先設 subtarget 再切換目標,讓重編譯帶到正確 define(UNITY_SERVER)
            EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;
            var group = BuildPipeline.GetBuildTargetGroup(target);
            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
                    throw new BuildFailedException($"切換建置目標失敗:{target}(模組未安裝?)");
            }

            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult contentResult);
            if (!string.IsNullOrEmpty(contentResult.Error))
                throw new BuildFailedException($"Addressables content build 失敗:{contentResult.Error}");

            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                target = target,
                targetGroup = group,
                subtarget = (int)subtarget,
                locationPathName = output,
                options = buildOptions,
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new BuildFailedException(
                    $"{target}({subtarget})建置失敗:{report.summary.result},errors={report.summary.totalErrors}");

            Debug.Log($"[PublishBuilder] {target}({subtarget})完成 → {output}");
        }
    }
}
