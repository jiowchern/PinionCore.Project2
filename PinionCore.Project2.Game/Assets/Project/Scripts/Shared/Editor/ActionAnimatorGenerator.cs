using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PinionCore.Project2.Shared.Editor
{
    /// <summary>
    /// 由 ActorConfig.Actions 重生模型 AnimatorController 的動作 states:
    /// state 名 = ActionType 的 enum 名(ActorShell 以同一約定 CrossFade),motion = ActionConfig.Clip。
    /// controller 是產物不是來源 —— 換動作的視覺 clip 一律改 ActionConfig.Clip 再跑本產生器,
    /// 不要手動編輯 controller(下次產生會被蓋掉)。
    /// 比照 ActorConfigBaker:build 前自動跑,也可從選單手動執行。
    /// </summary>
    class ActionAnimatorGenerator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 2;   // 在 ActionMotionBaker(1)之後:同一次 build 先烘段再同步視覺

        public void OnPreprocessBuild(BuildReport report)
        {
            Generate();
        }

        [MenuItem("PinionCore/Generate Action Animator States")]
        public static void Generate()
        {
            // 多顆 ActorConfig 可共用同一顆 controller(TestActor1/unitychan 現況):
            // 先聚合每顆 controller 應有的 ActionType → config,同型別不同 clip 視為配置錯誤
            var plans = new Dictionary<AnimatorController, Dictionary<ActionType, ActionConfig>>();
            foreach (var guid in AssetDatabase.FindAssets("t:ActorConfig"))
            {
                var config = AssetDatabase.LoadAssetAtPath<ActorConfig>(AssetDatabase.GUIDToAssetPath(guid));
                if (config == null || config.Actions == null)
                    continue;
                var model = config.ModelPrefab != null ? config.ModelPrefab.editorAsset : null;
                if (model == null)
                    continue;   // 無模型的 config(測試用)沒有表現層要同步

                var animator = model.GetComponentInChildren<Animator>();
                var controller = animator != null ? animator.runtimeAnimatorController as AnimatorController : null;
                if (controller == null)
                {
                    Debug.LogError(
                        $"[ActionAnimatorGenerator] {config.name}: 模型 '{model.name}' 沒有 Animator," +
                        "或掛的不是 AnimatorController(OverrideController 不支援)");
                    continue;
                }

                if (!plans.TryGetValue(controller, out var actions))
                    plans[controller] = actions = new Dictionary<ActionType, ActionConfig>();
                foreach (var action in config.Actions)
                {
                    if (action == null)
                        continue;
                    if (actions.TryGetValue(action.Action, out var existing))
                    {
                        if (existing != action && existing.Clip != action.Clip)
                            Debug.LogError(
                                $"[ActionAnimatorGenerator] controller '{controller.name}': {action.Action} 有兩份不同 clip 的 config" +
                                $"('{existing.name}' vs '{action.name}')—— 共用 controller 的模型必須共用同一顆 ActionConfig");
                        continue;
                    }
                    actions[action.Action] = action;
                }
            }

            foreach (var plan in plans)
                Sync(plan.Key, plan.Value);
        }

        static void Sync(AnimatorController controller, Dictionary<ActionType, ActionConfig> actions)
        {
            var machine = controller.layers[0].stateMachine;
            var changed = false;

            // 約定之外的 state 一律移除:controller 是純產物,手加的 state 沒有人會 CrossFade 到
            foreach (var child in machine.states)
            {
                var keep = actions.Keys.Any(a => a.ToString() == child.state.name);
                if (!keep)
                {
                    Debug.Log($"[ActionAnimatorGenerator] {controller.name}: 移除 state '{child.state.name}'(無對應 ActionConfig)");
                    machine.RemoveState(child.state);
                    changed = true;
                }
            }

            var index = 0;
            foreach (var action in actions.Keys.OrderBy(a => (int)a))
            {
                var config = actions[action];
                var stateName = action.ToString();
                var state = machine.states
                    .Select(c => c.state)
                    .FirstOrDefault(s => s.name == stateName);
                if (state == null)
                {
                    state = machine.AddState(stateName, new Vector3(300f, 60f * index, 0f));
                    changed = true;
                }
                index++;

                if (config.Clip == null)
                    Debug.LogWarning(
                        $"[ActionAnimatorGenerator] {config.name}: 無 Clip(手填段資料?),state '{stateName}' 沒有動畫可播");
                else if (config.Clip.isLooping != config.Loop)
                    Debug.LogWarning(
                        $"[ActionAnimatorGenerator] {config.name}: clip '{config.Clip.name}' 的匯入 Loop Time={config.Clip.isLooping} " +
                        $"與 ActionConfig.Loop={config.Loop} 不一致 —— 循環動作會停格 / 一次性動作會鬼畜,請修匯入設定");

                if (state.motion != config.Clip)
                {
                    Debug.Log(
                        $"[ActionAnimatorGenerator] {controller.name}: state '{stateName}' motion " +
                        $"'{(state.motion != null ? state.motion.name : "無")}' → '{(config.Clip != null ? config.Clip.name : "無")}'");
                    state.motion = config.Clip;
                    changed = true;
                }
            }

            // 預設 state:訂閱 replay 抵達前的墊底動畫,選 AdventureIdle(登入初始 stance),沒有就取第一個
            var states = machine.states.Select(c => c.state).ToArray();
            var fallback = states.FirstOrDefault(s => s.name == ActionType.AdventureIdle.ToString())
                        ?? states.FirstOrDefault();
            if (fallback != null && machine.defaultState != fallback)
            {
                machine.defaultState = fallback;
                changed = true;
            }

            if (changed)
            {
                EditorUtility.SetDirty(machine);
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssetIfDirty(controller);
                Debug.Log($"[ActionAnimatorGenerator] {controller.name}: 已同步 {actions.Count} 個動作 state");
            }
        }
    }
}
