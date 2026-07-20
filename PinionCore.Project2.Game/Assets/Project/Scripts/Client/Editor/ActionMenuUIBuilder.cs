using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace PinionCore.Project2.Client.Editor
{
    /// <summary>
    /// 一鍵在 Client 場景建出行動選單 uGUI(獨立 Canvas,不與 DemoUI 互相覆蓋):
    /// RMF_RadialMenu(元件 disabled,佈局由 PlayerActionMenuHandler 驅動)+
    /// inactive 元素模板 + 中心 Current 動作標籤,並自動接好 handler 參照;
    /// 可重複執行(先清掉舊的 ActionMenuUI 再重建)。
    /// </summary>
    public static class ActionMenuUIBuilder
    {
        const string SceneName = "Client";

        [MenuItem("PinionCore/Build Action Menu UI")]
        public static void Build()
        {
            var scene = SceneManager.GetSceneByName(SceneName);
            if (!scene.isLoaded)
            {
                Debug.LogError($"ActionMenuUIBuilder: 場景 {SceneName} 未載入");
                return;
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "ActionMenuUI")
                    Object.DestroyImmediate(root);
            }

            var res = _BuiltinResources();
            // WebGL 無 OS 字型回退,內建 LegacyRuntime 不含 CJK,中文會無聲消失;
            // 必須用打進 build 的 Noto Sans TC(找不到就中止,不能默默退回內建字型)
            var font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Project/Fonts/NotoSansTC-Regular.otf");
            if (font == null)
            {
                Debug.LogError("ActionMenuUIBuilder: 找不到 Assets/Project/Fonts/NotoSansTC-Regular.otf,中止(內建字型在 WebGL 顯示不了中文)");
                return;
            }

            var canvasGo = new GameObject("ActionMenuUI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            SceneManager.MoveGameObjectToScene(canvasGo, scene);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // 專案 activeInputHandler = Input System only,EventSystem 要配 InputSystemUIInputModule;
            // DemoLoginUIBuilder 通常已建好,沒有才補
            if (Object.FindFirstObjectByType<EventSystem>() == null)
            {
                var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                SceneManager.MoveGameObjectToScene(eventSystem, scene);
            }

            // ---- 放射選單本體(靠右下,避開置中登入面板與右上操作說明) ----
            var menuGo = new GameObject("ActionMenu", typeof(RectTransform));
            menuGo.transform.SetParent(canvasGo.transform, false);
            var menuRt = (RectTransform)menuGo.transform;
            menuRt.anchorMin = new Vector2(1f, 0f);
            menuRt.anchorMax = new Vector2(1f, 0f);
            menuRt.pivot = new Vector2(0.5f, 0.5f);
            menuRt.anchoredPosition = new Vector2(-260, 300);
            menuRt.sizeDelta = new Vector2(420, 420);

            var menu = menuGo.AddComponent<RMF_RadialMenu>();
            // 元件本體不啟用:其 Update 走舊版 Input 會丟例外;方向選擇由
            // PlayerActionMenuHandler.Update 以 Input System 重實作。
            // useLazySelection = true 讓元素關閉射線,點擊只走 handler 單一路徑
            //(handler.Start 也會強制此值,這裡同步序列化值避免誤導)
            menu.enabled = false;
            menu.useGamepad = false;
            menu.useLazySelection = true;
            menu.useSelectionFollower = false;

            // ---- 中心標籤:hover 顯示元素 label,平時由 handler 填 Current 動作名 ----
            var label = _Text(font, menuGo.transform, "CenterLabel", string.Empty, 22, TextAnchor.MiddleCenter, Vector2.zero, new Vector2(220, 44));
            label.color = Color.white;
            label.fontStyle = FontStyle.Bold;
            var labelOutline = label.gameObject.AddComponent<Outline>();
            labelOutline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            labelOutline.effectDistance = new Vector2(1.5f, -1.5f);
            menu.textLabel = label;

            // ---- 元素模板(inactive):根物件由 RMF 旋轉定位,Button 以 ForceDirection 保持正立 ----
            var templateGo = new GameObject("ElementTemplate", typeof(RectTransform));
            templateGo.transform.SetParent(menuGo.transform, false);
            var templateRt = (RectTransform)templateGo.transform;
            templateRt.anchoredPosition = Vector2.zero;
            templateRt.sizeDelta = Vector2.zero;
            var element = templateGo.AddComponent<RMF_RadialMenuElement>();

            var buttonGo = DefaultControls.CreateButton(res);
            buttonGo.name = "Button";
            buttonGo.transform.SetParent(templateGo.transform, false);
            var buttonRt = (RectTransform)buttonGo.transform;
            buttonRt.anchoredPosition = new Vector2(0, 150);
            buttonRt.sizeDelta = new Vector2(160, 42);
            var buttonText = buttonGo.GetComponentInChildren<Text>();
            buttonText.font = font;
            buttonText.fontSize = 18;
            buttonText.text = string.Empty;
            var forceDirection = buttonGo.AddComponent<RMF_ForceDirection>();
            forceDirection.forcedZRotation = 0f;

            element.button = buttonGo.GetComponent<Button>();
            templateGo.SetActive(false);

            // ---- 接線 ----
            var handler = canvasGo.AddComponent<PlayerActionMenuHandler>();
            handler.Client = _FindInScene<QueryerHost>(scene);
            handler.Provider = _FindInScene<ActorProvider>(scene);
            handler.ClientPlayer = _FindInScene<PlayerRemote>(scene);
            handler.InputHandler = _FindInScene<PlayerInputHandler>(scene);
            handler.Menu = menu;
            handler.ElementTemplate = element;
            handler.IconConfigs = AssetDatabase.LoadAssetAtPath<PinionCore.Project2.Shared.ActionIconConfigSet>(
                "Assets/Project/Configs/ActionIconConfigs/ActionIconConfigSet.asset");
            if (handler.IconConfigs == null)
                Debug.LogWarning("ActionMenuUIBuilder: 找不到 ActionIconConfigSet.asset,圓盤全部走預設文字按鈕");

            if (handler.Client == null || handler.Provider == null || handler.ClientPlayer == null || handler.InputHandler == null)
                Debug.LogWarning("ActionMenuUIBuilder: 場景缺 QueryerHost/ActorProvider/PlayerRemote/PlayerInputHandler,請手動補指定 PlayerActionMenuHandler 參照");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("ActionMenuUIBuilder: 行動選單 UI 已建立並存檔");
        }

        static T _FindInScene<T>(Scene scene) where T : Component
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentInChildren<T>(true);
                if (found != null)
                    return found;
            }
            return null;
        }

        static Text _Text(Font font, Transform parent, string name, string content, int size, TextAnchor anchor, Vector2 pos, Vector2 sizeDelta)
        {
            var go = new GameObject(name, typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = pos;
            rt.sizeDelta = sizeDelta;
            var text = go.GetComponent<Text>();
            text.font = font;
            text.text = content;
            text.fontSize = size;
            text.alignment = anchor;
            text.raycastTarget = false;
            return text;
        }

        static DefaultControls.Resources _BuiltinResources()
        {
            return new DefaultControls.Resources
            {
                standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
                background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
                inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd"),
                knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
                checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
                dropdown = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd"),
                mask = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd"),
            };
        }
    }
}
