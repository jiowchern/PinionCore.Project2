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
    /// 另維護 int 參數 ActionAnimatorParameter.Name(值 = (int)ActionType)與對應的 AnyState 轉換:
    /// 編輯器時期在 Animator 視窗改參數即可切到對應動作 state 做測試;
    /// runtime 由 ActorShell 切換動作時寫入同一參數(同一套約定)。
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

            // 參數 ActionType(int,值 = enum 值):編輯器時期的測試入口 ——
            // 在 Animator 視窗改參數,經下方 AnyState 轉換切到對應動作 state。
            // 型別不符(手改成 float/bool)視為配置錯誤,移除重建;其他參數不動
            var parameter = controller.parameters.FirstOrDefault(p => p.name == ActionAnimatorParameter.Name);
            if (parameter != null && parameter.type != AnimatorControllerParameterType.Int)
            {
                Debug.Log($"[ActionAnimatorGenerator] {controller.name}: 參數 '{ActionAnimatorParameter.Name}' 型別 {parameter.type} → Int");
                controller.RemoveParameter(parameter);
                parameter = null;
            }
            if (parameter == null)
            {
                controller.AddParameter(ActionAnimatorParameter.Name, AnimatorControllerParameterType.Int);
                changed = true;
            }

            // AnyState → 動作 state 的轉換:一個 state 一條,條件 ActionType == (int)enum,
            // 無 exit time、固定 0.1s 淡入(與 ActorShell 的 CrossFade 同值)、
            // canTransitionToSelf=false(runtime 由 code 先行切入時參數轉換不重複觸發)。
            // 約定之外(手加/條件不符/重複)一律移除,與 state 同樣視 controller 為純產物
            var actionByStateName = actions.Keys.ToDictionary(a => a.ToString());
            var covered = new HashSet<AnimatorState>();
            foreach (var transition in machine.anyStateTransitions)
            {
                var destination = transition.destinationState;
                var valid = destination != null &&
                    actionByStateName.TryGetValue(destination.name, out var action) &&
                    !covered.Contains(destination) &&
                    !transition.hasExitTime && !transition.canTransitionToSelf &&
                    transition.hasFixedDuration && Mathf.Approximately(transition.duration, 0.1f) &&
                    transition.conditions.Length == 1 &&
                    transition.conditions[0].parameter == ActionAnimatorParameter.Name &&
                    transition.conditions[0].mode == AnimatorConditionMode.Equals &&
                    (int)transition.conditions[0].threshold == (int)action;
                if (!valid)
                {
                    machine.RemoveAnyStateTransition(transition);
                    changed = true;
                }
                else
                    covered.Add(destination);
            }
            foreach (var action in actions.Keys.OrderBy(a => (int)a))
            {
                var state = states.FirstOrDefault(s => s.name == action.ToString());
                if (state == null || covered.Contains(state))
                    continue;
                var transition = machine.AddAnyStateTransition(state);
                transition.hasExitTime = false;
                transition.hasFixedDuration = true;
                transition.duration = 0.1f;
                transition.canTransitionToSelf = false;
                transition.AddCondition(AnimatorConditionMode.Equals, (int)action, ActionAnimatorParameter.Name);
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
