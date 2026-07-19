using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace PinionCore.Project2.Shared.Editor
{
    /// <summary>
    /// ActionConfig 的 hitbox 編輯器:Inspector 時間滑桿 scrub + Scene view 預覽/把手。
    /// Scene 內容:烘焙位移路徑折線(灰)、scrub 時刻的角色圈(白)、各 hit 段形狀
    /// (窗內紅、窗外淡灰;Sweep 依 scrub 進度畫掃掠中間態);選中的 hit 段有拖曳把手
    /// (中心 = LocalOffset、邊緣 = Radius/HalfExtents、弧端 = AngleFrom/AngleTo)。
    /// 模型預覽:ghost 實例(HideAndDontSave)+ PlayableGraph 手動評估,scrub 時刻取樣
    /// clip pose(in-place),位置沿烘焙 Segments 路徑走 —— 與 hit 圖形同一權威路徑。
    /// 注意:ScriptableObject 的 Editor.OnSceneGUI 不會被呼叫,必須掛 SceneView.duringSceneGui。
    /// 無預覽錨點時畫在世界原點:+Z = 動作前方、+X = 右。
    /// </summary>
    [CustomEditor(typeof(ActionConfig))]
    class ActionConfigEditor : UnityEditor.Editor
    {
        float _PreviewTime;
        int _SelectedHit = -1;                 // -1 = 全部顯示、無把手
        float _PreviewActorRadius = 0.3f;      // ActionConfig 不知道擁有者半徑,預覽用
        float _PreviewVolumeHeight = 1.6f;     // hit 形狀柱體高度;0 = 只畫平面
        Transform _PreviewAnchor;

        // 揮擊軌跡快取:key 不變不重取樣(scrub / 拖形狀把手都不觸發,時間窗或探測點變了才重算)
        readonly List<Vector3> _TrailA = new List<Vector3>();
        readonly List<Vector3> _TrailB = new List<Vector3>();
        Transform _TrailTransformA;            // 目前 scrub 時刻的探測點標記用
        Transform _TrailTransformB;
        string _TrailMissing;                  // 找不到的探測點名(Inspector 警告)
        bool _TrailValid;
        AnimationClip _TrailKeyClip;
        float _TrailKeyFrom, _TrailKeyTo;
        string _TrailKeyProbeA, _TrailKeyProbeB;
        Vector3 _TrailKeyOrigin;
        Quaternion _TrailKeyRotation;
        GameObject _TrailKeyInstance;

        GameObject _PreviewModel;              // 預覽模型來源(自動解析,可手動換)
        GameObject _PreviewInstance;           // ghost 實例
        Animator _PreviewAnimator;
        PlayableGraph _PreviewGraph;
        AnimationClipPlayable _PreviewPlayable;
        AnimationClip _PreviewClip;            // graph 目前掛的 clip(換 clip 要重建)
        bool _PreviewPlaying;
        double _LastUpdateTime;

        void OnEnable()
        {
            SceneView.duringSceneGui += _OnSceneGUI;
            EditorApplication.update += _OnEditorUpdate;
            EditorApplication.playModeStateChanged += _OnPlayModeChanged;
            _PreviewModel = _ResolveDefaultModel((ActionConfig)target);
            _RebuildPreviewInstance();
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= _OnSceneGUI;
            EditorApplication.update -= _OnEditorUpdate;
            EditorApplication.playModeStateChanged -= _OnPlayModeChanged;
            _TeardownPreview();
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var config = (ActionConfig)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Hitbox 預覽(Scene view)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                var icon = _PreviewPlaying ? "PauseButton" : "PlayButton";
                if (GUILayout.Button(EditorGUIUtility.IconContent(icon), GUILayout.Width(28f), GUILayout.Height(18f)))
                {
                    _PreviewPlaying = !_PreviewPlaying;
                    _LastUpdateTime = EditorApplication.timeSinceStartup;
                    SceneView.RepaintAll();
                }
                _PreviewTime = EditorGUILayout.Slider("時間(秒)", _PreviewTime, 0f, Mathf.Max(config.Duration, 0.01f));
            }
            var hitCount = config.HitSegments != null ? config.HitSegments.Length : 0;
            using (new EditorGUI.DisabledScope(hitCount == 0))
                _SelectedHit = EditorGUILayout.IntSlider("編輯 hit 段(-1=僅顯示)", _SelectedHit, -1, hitCount - 1);
            _PreviewActorRadius = EditorGUILayout.FloatField("預覽角色半徑", _PreviewActorRadius);
            _PreviewVolumeHeight = EditorGUILayout.FloatField("柱體高度(0=只畫平面)", _PreviewVolumeHeight);
            _PreviewAnchor = (Transform)EditorGUILayout.ObjectField("預覽錨點(選填)", _PreviewAnchor, typeof(Transform), true);
            var model = (GameObject)EditorGUILayout.ObjectField("預覽模型", _PreviewModel, typeof(GameObject), true);
            if (model != _PreviewModel)
            {
                _PreviewModel = model;
                _RebuildPreviewInstance();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("TrailProbe 快選");
                var human = _PreviewAnimator != null && _PreviewAnimator.avatar != null && _PreviewAnimator.avatar.isHuman;
                using (new EditorGUI.DisabledScope(!human))
                {
                    if (GUILayout.Button("右手")) _SetTrailProbe(config, HumanBodyBones.RightHand);
                    if (GUILayout.Button("左手")) _SetTrailProbe(config, HumanBodyBones.LeftHand);
                    if (GUILayout.Button("右腳")) _SetTrailProbe(config, HumanBodyBones.RightFoot);
                    if (GUILayout.Button("左腳")) _SetTrailProbe(config, HumanBodyBones.LeftFoot);
                }
            }
            if (EditorGUI.EndChangeCheck())
                SceneView.RepaintAll();
            if (_PreviewInstance != null && config.Clip != null && config.Clip.isHumanMotion
                && (_PreviewAnimator == null || _PreviewAnimator.avatar == null || !_PreviewAnimator.avatar.isHuman))
                EditorGUILayout.HelpBox(
                    "clip 是 humanoid,但預覽模型的 Animator 沒有 humanoid Avatar,取樣不會動 —— " +
                    "換成有 Avatar 的模型(同 BakeRig 規則)。", MessageType.Warning);
            if (_TrailMissing != null)
                EditorGUILayout.HelpBox(
                    "軌跡探測點找不到:'" + _TrailMissing + "'(以名稱在預覽模型階層搜尋)。" +
                    "骨骼可用快選鍵填入;武器尖端要換成帶武器的預覽模型再填其 Transform 名。", MessageType.Warning);
            EditorGUILayout.HelpBox(
                "灰折線 = 烘焙位移路徑;白圈 = scrub 時刻的角色;紅 = 命中窗生效中(Sweep 畫掃掠進度)。\n" +
                "命中判定忽略高度:紅柱體 = 真實命中範圍(生效中/選中的段才立體化)。\n" +
                "TrailProbe 填探測點名(骨骼/武器尖端)畫青色揮擊軌跡:選中 hit 段 = 該段時間窗,未選 = 整個動作;" +
                "TrailProbeB 加第二點連成揮擊帶。\n" +
                "無錨點時畫在世界原點,+Z = 動作前方。選中 hit 段後可在 Scene 拖曳中心/邊緣/弧端把手。",
                MessageType.Info);
        }

        void _OnSceneGUI(SceneView view)
        {
            var config = target as ActionConfig;
            if (config == null)
                return;

            // 錨點基底:無場景物件時在原點,+Z=前、+X=右
            Vector2 origin, forward;
            if (_PreviewAnchor != null)
            {
                origin = new Vector2(_PreviewAnchor.position.x, _PreviewAnchor.position.z);
                var f = new Vector2(_PreviewAnchor.forward.x, _PreviewAnchor.forward.z);
                forward = f.sqrMagnitude > 1e-6f ? f.normalized : new Vector2(0f, 1f);
            }
            else
            {
                origin = Vector2.zero;
                forward = new Vector2(0f, 1f);
            }
            var right = new Vector2(forward.y, -forward.x);   // 與 Player._ActionRight 同式

            _DrawMotionPath(config, origin, right, forward);

            // scrub 時刻的角色位置(沿 Segments 積分)與朝向箭頭
            var actorLocal = _LocalPositionAt(config, _PreviewTime);
            var actor = origin + right * actorLocal.x + forward * actorLocal.y;
            var actor3 = new Vector3(actor.x, 0.02f, actor.y);
            var forward3 = new Vector3(forward.x, 0f, forward.y);
            Handles.color = Color.white;
            Handles.DrawWireDisc(actor3, Vector3.up, _PreviewActorRadius);
            Handles.ArrowHandleCap(0, actor3, Quaternion.LookRotation(forward3), _PreviewActorRadius * 1.5f, EventType.Repaint);

            if (Event.current.type == EventType.Repaint)
            {
                // 軌跡先取樣(會動 ghost pose),再還原目前 scrub pose,最後畫軌跡與探測點標記
                _UpdateTrail(config, origin, right, forward);
                _SamplePreview(config, new Vector3(actor.x, 0f, actor.y), Quaternion.LookRotation(forward3), _PreviewTime);
                _DrawTrail();
            }

            if (config.HitSegments == null)
                return;
            for (var i = 0; i < config.HitSegments.Length; i++)
            {
                var segment = config.HitSegments[i];
                var inWindow = _PreviewTime >= segment.StartTime && _PreviewTime <= segment.StartTime + segment.Duration;
                var selected = i == _SelectedHit;
                var fill = inWindow ? new Color(1f, 0f, 0f, 0.25f) : new Color(0.5f, 0.5f, 0.5f, 0.06f);
                var outline = inWindow ? Color.red : (selected ? Color.yellow : new Color(0.6f, 0.6f, 0.6f, 0.5f));
                var progress = segment.Sweep == SectorSweepMode.Sweep && segment.Duration > 0f
                    ? Mathf.Clamp01((_PreviewTime - segment.StartTime) / segment.Duration)
                    : float.NaN;
                // 生效中/選中的段才立體化,窗外段維持平面淡灰,多段動作場景才不會滿屏柱子
                HitShapeGizmos.Draw(segment, actor, right, forward, 0.02f, fill, outline,
                    inWindow ? progress : float.NaN,
                    inWindow || selected ? Mathf.Max(0f, _PreviewVolumeHeight) : 0f);

                if (selected && _EditHandles(config, ref segment, actor, right, forward))
                    config.HitSegments[i] = segment;
            }
        }

        /// <summary>預覽模型自動解析:BakeRig 優先,否則反查持有此動作的 ActorConfig 的 ModelPrefab(與烘焙器同規則)。</summary>
        static GameObject _ResolveDefaultModel(ActionConfig config)
        {
            if (config == null)
                return null;
            if (config.BakeRig != null)
                return config.BakeRig;
            foreach (var guid in AssetDatabase.FindAssets("t:ActorConfig"))
            {
                var owner = AssetDatabase.LoadAssetAtPath<ActorConfig>(AssetDatabase.GUIDToAssetPath(guid));
                if (owner == null || owner.Actions == null)
                    continue;
                foreach (var action in owner.Actions)
                    if (action == config)
                        return owner.ModelPrefab != null ? owner.ModelPrefab.editorAsset : null;
            }
            return null;
        }

        void _RebuildPreviewInstance()
        {
            _TeardownPreview();
            if (_PreviewModel == null || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            _PreviewInstance = Instantiate(_PreviewModel);
            _PreviewInstance.name = "[ActionConfig 預覽] " + _PreviewModel.name;
            _PreviewInstance.hideFlags = HideFlags.HideAndDontSave;
            _PreviewAnimator = _PreviewInstance.GetComponentInChildren<Animator>();
            // 點到 ghost 會換掉選取、editor 被拆 → 預覽瞬間消失,禁止 scene 揀選
            SceneVisibilityManager.instance.DisablePicking(_PreviewInstance, true);
        }

        void _TeardownPreview()
        {
            if (_PreviewGraph.IsValid())
                _PreviewGraph.Destroy();
            _PreviewClip = null;
            _PreviewAnimator = null;
            if (_PreviewInstance != null)
                DestroyImmediate(_PreviewInstance);
            _PreviewInstance = null;
            _TrailTransformA = null;
            _TrailTransformB = null;
            _TrailValid = false;
        }

        /// <summary>
        /// ghost 模型放到 scrub 時刻的烘焙路徑位置,取樣 clip 在該時刻的 pose。
        /// applyRootMotion 關閉取 in-place pose:位移一律走 Segments,與 hit 圖形同一權威路徑,
        /// 不會與 clip 原始 root motion 雙重計算(兩者差異 = SimplifyTolerance 的分段失真)。
        /// </summary>
        void _SamplePreview(ActionConfig config, Vector3 position, Quaternion rotation, float time)
        {
            if (_PreviewInstance == null)
                return;
            _PreviewInstance.transform.SetPositionAndRotation(position, rotation);
            if (config.Clip == null || _PreviewAnimator == null)
            {
                if (_PreviewGraph.IsValid())
                    _PreviewGraph.Destroy();
                _PreviewClip = null;
                return;
            }
            if (!_PreviewGraph.IsValid() || _PreviewClip != config.Clip)
            {
                if (_PreviewGraph.IsValid())
                    _PreviewGraph.Destroy();
                _PreviewAnimator.applyRootMotion = false;
                _PreviewAnimator.fireEvents = false;
                // 預設 CullUpdateTransforms 在編輯模式 renderer 未曾可見時會跳過骨骼寫入,取樣全程不動
                _PreviewAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _PreviewGraph = PlayableGraph.Create("ActionConfigPreview");
                _PreviewGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var output = AnimationPlayableOutput.Create(_PreviewGraph, "preview", _PreviewAnimator);
                _PreviewPlayable = AnimationClipPlayable.Create(_PreviewGraph, config.Clip);
                _PreviewPlayable.SetApplyFootIK(false);
                output.SetSourcePlayable(_PreviewPlayable);
                _PreviewGraph.Play();
                _PreviewClip = config.Clip;
            }
            _PreviewPlayable.SetTime(time);
            _PreviewGraph.Evaluate(0f);
        }

        /// <summary>
        /// 重建揮擊軌跡快取:對時間窗(選中 hit 段;未選 = 整個動作)逐點取樣探測點世界座標。
        /// 位移沿烘焙 Segments 路徑(與模型/白圈同一權威路徑);取樣完 pose 停在窗尾,
        /// 呼叫端(Repaint 流程)隨後會以 _PreviewTime 重取樣還原。
        /// </summary>
        void _UpdateTrail(ActionConfig config, Vector2 origin, Vector2 right, Vector2 forward)
        {
            var probeA = config.TrailProbe;
            var probeB = config.TrailProbeB;
            if (string.IsNullOrEmpty(probeA) || _PreviewInstance == null || config.Clip == null || config.Duration <= 0f)
            {
                _TrailValid = false;
                _TrailMissing = null;
                _TrailTransformA = null;
                _TrailTransformB = null;
                return;
            }

            var from = 0f;
            var to = config.Duration;
            if (_SelectedHit >= 0 && config.HitSegments != null && _SelectedHit < config.HitSegments.Length)
            {
                var hit = config.HitSegments[_SelectedHit];
                from = hit.StartTime;
                to = hit.StartTime + hit.Duration;
            }
            var origin3 = new Vector3(origin.x, 0f, origin.y);
            var rotation = Quaternion.LookRotation(new Vector3(forward.x, 0f, forward.y));
            if (_TrailValid && _TrailKeyClip == config.Clip && _TrailKeyFrom == from && _TrailKeyTo == to
                && _TrailKeyProbeA == probeA && _TrailKeyProbeB == probeB
                && _TrailKeyOrigin == origin3 && _TrailKeyRotation == rotation
                && _TrailKeyInstance == _PreviewInstance)
                return;

            _TrailA.Clear();
            _TrailB.Clear();
            _TrailMissing = null;
            _TrailTransformA = _FindInGhost(probeA);
            _TrailTransformB = string.IsNullOrEmpty(probeB) ? null : _FindInGhost(probeB);
            if (_TrailTransformA == null)
                _TrailMissing = probeA;
            else if (!string.IsNullOrEmpty(probeB) && _TrailTransformB == null)
                _TrailMissing = probeB;

            if (_TrailTransformA != null)
            {
                var steps = Mathf.Clamp(Mathf.CeilToInt((to - from) * 30f), 1, 64);
                for (var i = 0; i <= steps; i++)
                {
                    var t = Mathf.Lerp(from, to, i / (float)steps);
                    var local = _LocalPositionAt(config, t);
                    var position = origin + right * local.x + forward * local.y;
                    _SamplePreview(config, new Vector3(position.x, 0f, position.y), rotation, t);
                    _TrailA.Add(_TrailTransformA.position);
                    if (_TrailTransformB != null)
                        _TrailB.Add(_TrailTransformB.position);
                }
            }

            _TrailValid = true;   // 找不到探測點也記住 key,避免每幀重搜階層
            _TrailKeyClip = config.Clip;
            _TrailKeyFrom = from;
            _TrailKeyTo = to;
            _TrailKeyProbeA = probeA;
            _TrailKeyProbeB = probeB;
            _TrailKeyOrigin = origin3;
            _TrailKeyRotation = rotation;
            _TrailKeyInstance = _PreviewInstance;
        }

        Transform _FindInGhost(string name)
        {
            if (_PreviewInstance == null)
                return null;
            foreach (var child in _PreviewInstance.GetComponentsInChildren<Transform>(true))
                if (child.name == name)
                    return child;
            return null;
        }

        void _DrawTrail()
        {
            // 目前 scrub 時刻的探測點標記(pose 已還原,transform 就是當下位置)
            if (_TrailTransformA != null)
                _DrawProbeMarker(_TrailTransformA.position);
            if (_TrailTransformB != null)
                _DrawProbeMarker(_TrailTransformB.position);

            if (!_TrailValid || _TrailA.Count < 2)
                return;
            var ground = new Color(0f, 1f, 1f, 0.9f);
            var air = new Color(0f, 1f, 1f, 0.35f);

            Handles.color = air;
            Handles.DrawAAPolyLine(2f, _TrailA.ToArray());
            var projA = _ProjectToGround(_TrailA);
            Handles.color = ground;
            Handles.DrawAAPolyLine(3f, projA);

            if (_TrailB.Count == _TrailA.Count)
            {
                Handles.color = air;
                Handles.DrawAAPolyLine(2f, _TrailB.ToArray());
                var projB = _ProjectToGround(_TrailB);
                Handles.color = ground;
                Handles.DrawAAPolyLine(3f, projB);
                Handles.color = air;
                for (var i = 0; i < projA.Length; i++)
                    Handles.DrawLine(projA[i], projB[i]);   // 揮擊帶:A/B 投影間逐點連線
            }
        }

        void _DrawProbeMarker(Vector3 position)
        {
            Handles.color = Color.cyan;
            Handles.SphereHandleCap(0, position, Quaternion.identity, 0.05f, EventType.Repaint);
            Handles.DrawDottedLine(position, new Vector3(position.x, 0.02f, position.z), 4f);
        }

        static Vector3[] _ProjectToGround(List<Vector3> points)
        {
            var projected = new Vector3[points.Count];
            for (var i = 0; i < points.Count; i++)
                projected[i] = new Vector3(points[i].x, 0.02f, points[i].z);
            return projected;
        }

        void _SetTrailProbe(ActionConfig config, HumanBodyBones bone)
        {
            var probe = _PreviewAnimator != null ? _PreviewAnimator.GetBoneTransform(bone) : null;
            if (probe == null)
                return;
            Undo.RecordObject(config, "Set Trail Probe");
            config.TrailProbe = probe.name;
            EditorUtility.SetDirty(config);
        }

        void _OnEditorUpdate()
        {
            if (!_PreviewPlaying)
                return;
            var config = target as ActionConfig;
            if (config == null || config.Duration <= 0f)
                return;
            var now = EditorApplication.timeSinceStartup;
            _PreviewTime = Mathf.Repeat(_PreviewTime + (float)(now - _LastUpdateTime), config.Duration);
            _LastUpdateTime = now;
            SceneView.RepaintAll();
            Repaint();
        }

        // 關 domain reload 的 Enter Play Mode 不會重跑 OnEnable/OnDisable,ghost 會漏進 play 場景,顯式收/建
        void _OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
                _TeardownPreview();
            else if (change == PlayModeStateChange.EnteredEditMode)
                _RebuildPreviewInstance();
        }

        /// <summary>選中 hit 段的把手;有變更回傳 true(已包 Undo/SetDirty)。</summary>
        bool _EditHandles(ActionConfig config, ref ActionConfig.HitSegment segment,
            Vector2 anchor, Vector2 right, Vector2 forward)
        {
            var changed = false;
            var right3 = new Vector3(right.x, 0f, right.y);
            var forward3 = new Vector3(forward.x, 0f, forward.y);
            var center2 = anchor + right * segment.LocalOffset.x + forward * segment.LocalOffset.y;
            var center = new Vector3(center2.x, 0.02f, center2.y);
            var size = HandleUtility.GetHandleSize(center) * 0.06f;

            // 中心把手 → LocalOffset(投影回 XZ、逆轉回動作局部座標)
            EditorGUI.BeginChangeCheck();
            var movedCenter = Handles.FreeMoveHandle(center, size, Vector3.zero, Handles.SphereHandleCap);
            if (EditorGUI.EndChangeCheck())
            {
                var delta = new Vector2(movedCenter.x, movedCenter.z) - anchor;
                _Record(config, "Move Hit Offset");
                segment.LocalOffset = new Vector2(Vector2.Dot(delta, right), Vector2.Dot(delta, forward));
                changed = true;
            }

            switch (segment.Shape)
            {
                case HitShapeType.Circle:
                case HitShapeType.Sector:
                    {
                        // 半徑把手:沿「中間角」方向的邊緣點(圓 = 動作前方)
                        var midAngle = segment.Shape == HitShapeType.Sector
                            ? (segment.AngleFrom + segment.AngleTo) * 0.5f
                            : 0f;
                        var midDir = HitShapeGizmos.Dir(right3, forward3, midAngle);
                        EditorGUI.BeginChangeCheck();
                        var movedEdge = Handles.Slider(center + midDir * segment.Radius, midDir, size, Handles.DotHandleCap, 0f);
                        if (EditorGUI.EndChangeCheck())
                        {
                            _Record(config, "Resize Hit Radius");
                            segment.Radius = Mathf.Max(0.01f, Vector3.Dot(movedEdge - center, midDir));
                            changed = true;
                        }

                        if (segment.Shape == HitShapeType.Sector)
                        {
                            // 弧端把手 → AngleFrom / AngleTo(FreeMove 後以 atan2 反解角度)
                            changed |= _AngleHandle(config, ref segment.AngleFrom, segment.Radius, center, right3, forward3, size);
                            changed |= _AngleHandle(config, ref segment.AngleTo, segment.Radius, center, right3, forward3, size);
                        }
                    }
                    break;

                case HitShapeType.Box:
                    {
                        var rad = segment.Rotation * Mathf.Deg2Rad;
                        var u = right3 * Mathf.Cos(rad) - forward3 * Mathf.Sin(rad);
                        var v = forward3 * Mathf.Cos(rad) + right3 * Mathf.Sin(rad);

                        // 面中心把手 → 半寬
                        EditorGUI.BeginChangeCheck();
                        var movedX = Handles.Slider(center + u * segment.HalfExtents.x, u, size, Handles.DotHandleCap, 0f);
                        if (EditorGUI.EndChangeCheck())
                        {
                            _Record(config, "Resize Hit Box");
                            segment.HalfExtents.x = Mathf.Max(0.01f, Vector3.Dot(movedX - center, u));
                            changed = true;
                        }
                        EditorGUI.BeginChangeCheck();
                        var movedY = Handles.Slider(center + v * segment.HalfExtents.y, v, size, Handles.DotHandleCap, 0f);
                        if (EditorGUI.EndChangeCheck())
                        {
                            _Record(config, "Resize Hit Box");
                            segment.HalfExtents.y = Mathf.Max(0.01f, Vector3.Dot(movedY - center, v));
                            changed = true;
                        }

                        // 旋轉盤 → Rotation(相對動作前方;Handles.Disc 繞 +Y 正角與角度慣例同向)
                        EditorGUI.BeginChangeCheck();
                        var baseRotation = Quaternion.LookRotation(forward3) * Quaternion.AngleAxis(segment.Rotation, Vector3.up);
                        var rotated = Handles.Disc(baseRotation, center, Vector3.up,
                            segment.HalfExtents.magnitude + 0.2f, false, 0f);
                        if (EditorGUI.EndChangeCheck())
                        {
                            _Record(config, "Rotate Hit Box");
                            var newForward = rotated * Vector3.forward;
                            segment.Rotation = Vector2.SignedAngle(
                                new Vector2(newForward.x, newForward.z), forward);   // SignedAngle 逆時針為正 → 前方轉向右為正
                            changed = true;
                        }
                    }
                    break;
            }

            if (changed)
                EditorUtility.SetDirty(config);
            return changed;
        }

        bool _AngleHandle(ActionConfig config, ref float angle, float radius, Vector3 center,
            Vector3 right3, Vector3 forward3, float size)
        {
            var dir = HitShapeGizmos.Dir(right3, forward3, angle);
            EditorGUI.BeginChangeCheck();
            var moved = Handles.FreeMoveHandle(center + dir * radius, size, Vector3.zero, Handles.DotHandleCap);
            if (!EditorGUI.EndChangeCheck())
                return false;
            var delta = moved - center;
            var local = new Vector2(Vector3.Dot(delta, right3), Vector3.Dot(delta, forward3));
            if (local.sqrMagnitude < 1e-6f)
                return false;
            _Record(config, "Rotate Hit Sector");
            // 反解為連續角度:取離現值最近的等價角(±360),拖曳跨 ±180° 不跳變
            var raw = Mathf.Atan2(local.x, local.y) * Mathf.Rad2Deg;
            angle = raw + Mathf.Round((angle - raw) / 360f) * 360f;
            return true;
        }

        static void _Record(ActionConfig config, string operation)
        {
            Undo.RecordObject(config, operation);
        }

        /// <summary>沿烘焙 Segments 積分出 time 時刻的局部位置(超出總時長停在終點)。</summary>
        static Vector2 _LocalPositionAt(ActionConfig config, float time)
        {
            var local = Vector2.zero;
            if (config.Segments == null)
                return local;
            var remaining = time;
            foreach (var segment in config.Segments)
            {
                if (segment.Duration <= 0f || remaining >= segment.Duration)
                {
                    local += segment.LocalOffset;
                    remaining -= Mathf.Max(0f, segment.Duration);
                    continue;
                }
                local += segment.LocalOffset * (remaining / segment.Duration);
                break;
            }
            return local;
        }

        void _DrawMotionPath(ActionConfig config, Vector2 origin, Vector2 right, Vector2 forward)
        {
            if (config.Segments == null || config.Segments.Length == 0)
                return;
            Handles.color = new Color(0.7f, 0.7f, 0.7f, 0.8f);
            var cursor = origin;
            var previous = new Vector3(cursor.x, 0.02f, cursor.y);
            Handles.DrawWireDisc(previous, Vector3.up, 0.03f);
            foreach (var segment in config.Segments)
            {
                cursor += right * segment.LocalOffset.x + forward * segment.LocalOffset.y;
                var current = new Vector3(cursor.x, 0.02f, cursor.y);
                Handles.DrawLine(previous, current);
                Handles.DrawWireDisc(current, Vector3.up, 0.03f);
                previous = current;
            }
        }
    }
}
