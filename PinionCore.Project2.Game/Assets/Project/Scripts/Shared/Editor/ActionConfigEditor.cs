using UnityEditor;
using UnityEngine;

namespace PinionCore.Project2.Shared.Editor
{
    /// <summary>
    /// ActionConfig 的 hitbox 編輯器:Inspector 時間滑桿 scrub + Scene view 預覽/把手。
    /// Scene 內容:烘焙位移路徑折線(灰)、scrub 時刻的角色圈(白)、各 hit 段形狀
    /// (窗內紅、窗外淡灰;Sweep 依 scrub 進度畫掃掠中間態);選中的 hit 段有拖曳把手
    /// (中心 = LocalOffset、邊緣 = Radius/HalfExtents、弧端 = AngleFrom/AngleTo)。
    /// 注意:ScriptableObject 的 Editor.OnSceneGUI 不會被呼叫,必須掛 SceneView.duringSceneGui。
    /// 無預覽錨點時畫在世界原點:+Z = 動作前方、+X = 右。
    /// </summary>
    [CustomEditor(typeof(ActionConfig))]
    class ActionConfigEditor : UnityEditor.Editor
    {
        float _PreviewTime;
        int _SelectedHit = -1;                 // -1 = 全部顯示、無把手
        float _PreviewActorRadius = 0.3f;      // ActionConfig 不知道擁有者半徑,預覽用
        Transform _PreviewAnchor;

        void OnEnable()
        {
            SceneView.duringSceneGui += _OnSceneGUI;
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= _OnSceneGUI;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var config = (ActionConfig)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Hitbox 預覽(Scene view)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _PreviewTime = EditorGUILayout.Slider("時間(秒)", _PreviewTime, 0f, Mathf.Max(config.Duration, 0.01f));
            var hitCount = config.HitSegments != null ? config.HitSegments.Length : 0;
            using (new EditorGUI.DisabledScope(hitCount == 0))
                _SelectedHit = EditorGUILayout.IntSlider("編輯 hit 段(-1=僅顯示)", _SelectedHit, -1, hitCount - 1);
            _PreviewActorRadius = EditorGUILayout.FloatField("預覽角色半徑", _PreviewActorRadius);
            _PreviewAnchor = (Transform)EditorGUILayout.ObjectField("預覽錨點(選填)", _PreviewAnchor, typeof(Transform), true);
            if (EditorGUI.EndChangeCheck())
                SceneView.RepaintAll();
            EditorGUILayout.HelpBox(
                "灰折線 = 烘焙位移路徑;白圈 = scrub 時刻的角色;紅 = 命中窗生效中(Sweep 畫掃掠進度)。\n" +
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
                HitShapeGizmos.Draw(segment, actor, right, forward, 0.02f, fill, outline,
                    inWindow ? progress : float.NaN);

                if (selected && _EditHandles(config, ref segment, actor, right, forward))
                    config.HitSegments[i] = segment;
            }
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
