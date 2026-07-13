using PinionCore.Project2.Shared;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace PinionCore.Project2.Shared.Editor
{
    /// <summary>
    /// Player build 前把各 ActorConfig 的 ActorMetrics 值烘焙進序列化欄位(build 中拿不到 editorAsset)。
    /// 注意:config 目前由場景直接引用、非 Addressable;若未來 config 進 Addressables group,
    /// 單獨的 Addressables content build 不會觸發 IPreprocessBuildWithReport,需先手動執行選單烘焙。
    /// </summary>
    class ActorConfigBaker : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            Bake();
        }

        [MenuItem("PinionCore/Bake Actor Configs")]
        public static void Bake()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:ActorConfig"))
            {
                var config = AssetDatabase.LoadAssetAtPath<ActorConfig>(AssetDatabase.GUIDToAssetPath(guid));
                if (config != null && config.BakeFromMetrics())
                {
                    EditorUtility.SetDirty(config);
                    AssetDatabase.SaveAssetIfDirty(config);
                }
            }
        }
    }
}
