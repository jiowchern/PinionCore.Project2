using System.Collections.Generic;
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
    /// 一鍵在 Client 場景建出演示用 uGUI:登入面板+操作說明框+EventSystem,
    /// 並自動接好 DemoLoginUI 參照;可重複執行(先清掉舊的 DemoUI/EventSystem 再重建)。
    /// </summary>
    public static class DemoLoginUIBuilder
    {
        const string SceneName = "Client";

        [MenuItem("PinionCore/Build Demo Login UI")]
        public static void Build()
        {
            var scene = SceneManager.GetSceneByName(SceneName);
            if (!scene.isLoaded)
            {
                Debug.LogError($"DemoLoginUIBuilder: 場景 {SceneName} 未載入");
                return;
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "DemoUI" || root.name == "EventSystem")
                    Object.DestroyImmediate(root);
            }

            var res = _BuiltinResources();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var canvasGo = new GameObject("DemoUI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            SceneManager.MoveGameObjectToScene(canvasGo, scene);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // 專案 activeInputHandler = Input System only,EventSystem 要配 InputSystemUIInputModule
            var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            SceneManager.MoveGameObjectToScene(eventSystem, scene);

            // ---- 登入面板(置中) ----
            var loginPanel = _Panel(res, canvasGo.transform, "LoginPanel",
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(460, 340), new Color(0.08f, 0.09f, 0.12f, 0.92f));

            var title = _Text(font, loginPanel.transform, "Title", "演示登入", 30, TextAnchor.MiddleCenter, new Vector2(0, 122), new Vector2(400, 44));
            title.color = Color.white;
            title.fontStyle = FontStyle.Bold;

            // 內容由 DemoLoginUI 依平台推導後填入
            var mode = _Text(font, loginPanel.transform, "Mode", string.Empty, 16, TextAnchor.MiddleCenter, new Vector2(0, 90), new Vector2(400, 24));
            mode.color = new Color(0.6f, 0.75f, 0.9f, 1f);

            var nameInput = _InputField(res, font, loginPanel.transform, new Vector2(0, 52), new Vector2(340, 46), "輸入角色名稱");
            var dropdown = _Dropdown(res, font, loginPanel.transform, new Vector2(0, -8), new Vector2(340, 46),
                new List<string> { "Cube", "Unitychan" }, 1); // 選項順序 = ModelType enum 值
            var button = _Button(res, font, loginPanel.transform, new Vector2(0, -74), new Vector2(220, 52), "登入");
            var status = _Text(font, loginPanel.transform, "Status", string.Empty, 20, TextAnchor.MiddleCenter, new Vector2(0, -134), new Vector2(420, 36));
            status.color = new Color(1f, 0.75f, 0.35f, 1f);

            // ---- 操作說明框(世界畫面旁,靠右上) ----
            var controls = _Panel(res, canvasGo.transform, "ControlsPanel",
                new Vector2(1f, 1f), new Vector2(-190, -150), new Vector2(340, 260), new Color(0.08f, 0.09f, 0.12f, 0.72f));
            var controlsTitle = _Text(font, controls.transform, "Title", "操作說明", 26, TextAnchor.MiddleCenter, new Vector2(0, 100), new Vector2(300, 40));
            controlsTitle.color = Color.white;
            controlsTitle.fontStyle = FontStyle.Bold;
            var controlsBody = _Text(font, controls.transform, "Body",
                "W A S D  移動\n\nR  切換姿態(冒險 ⇄ 戰鬥)\n\n滑鼠左鍵  攻擊(戰鬥姿態)",
                22, TextAnchor.UpperLeft, new Vector2(10, -40), new Vector2(300, 170));
            controlsBody.color = new Color(0.92f, 0.94f, 1f, 1f);

            // ---- Ping(操作說明正下方) ----
            // 掛在 ControlsPanel 底下跟著一起顯示/隱藏;背景同款深色面板+文字描邊,
            // 疊在任何世界背景上都可讀
            var pingPanel = _Panel(res, controls.transform, "PingPanel",
                new Vector2(0.5f, 0f), new Vector2(0, -34), new Vector2(340, 44), new Color(0.08f, 0.09f, 0.12f, 0.72f));
            var pingText = _Text(font, pingPanel.transform, "Label", "Ping --", 22, TextAnchor.MiddleCenter, Vector2.zero, new Vector2(320, 36));
            pingText.color = Color.white;
            pingText.fontStyle = FontStyle.Bold;
            var pingOutline = pingText.gameObject.AddComponent<Outline>();
            pingOutline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            pingOutline.effectDistance = new Vector2(1.5f, -1.5f);

            var pingDisplay = pingPanel.AddComponent<PingDisplay>();
            pingDisplay.Label = pingText;

            // ---- 接線 ----
            var ui = canvasGo.AddComponent<DemoLoginUI>();
            ui.QueryerHost = _FindQueryerHost(scene);
            ui.LoginPanel = loginPanel;
            ui.ControlsPanel = controls;
            ui.NameInput = nameInput;
            ui.ModelDropdown = dropdown;
            ui.LoginButton = button;
            ui.StatusText = status;
            ui.ModeText = mode;
            pingDisplay.QueryerHost = ui.QueryerHost;

            if (ui.QueryerHost == null)
                Debug.LogWarning("DemoLoginUIBuilder: 找不到 QueryerHost(wrapper),請手動指定 DemoLoginUI.QueryerHost");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("DemoLoginUIBuilder: Demo UI 已建立並存檔");
        }

        // 專案的轉發型 QueryerHost(handlers/Console 同款接點),全場景唯一
        static QueryerHost _FindQueryerHost(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var host = root.GetComponentInChildren<QueryerHost>(true);
                if (host != null)
                    return host;
            }
            return null;
        }

        static GameObject _Panel(DefaultControls.Resources res, Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, Color color)
        {
            var go = DefaultControls.CreatePanel(res);
            go.name = name;
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            go.GetComponent<Image>().color = color;
            return go;
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

        static InputField _InputField(DefaultControls.Resources res, Font font, Transform parent, Vector2 pos, Vector2 size, string placeholder)
        {
            var go = DefaultControls.CreateInputField(res);
            go.name = "NameInput";
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            foreach (var t in go.GetComponentsInChildren<Text>(true))
            {
                t.font = font;
                t.fontSize = 22;
            }
            var input = go.GetComponent<InputField>();
            input.placeholder.GetComponent<Text>().text = placeholder;
            input.text = "Player";
            return input;
        }

        static Dropdown _Dropdown(DefaultControls.Resources res, Font font, Transform parent, Vector2 pos, Vector2 size, List<string> options, int value)
        {
            var go = DefaultControls.CreateDropdown(res);
            go.name = "ModelDropdown";
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            foreach (var t in go.GetComponentsInChildren<Text>(true))
            {
                t.font = font;
                t.fontSize = 22;
            }
            var dd = go.GetComponent<Dropdown>();
            dd.options.Clear();
            foreach (var option in options)
                dd.options.Add(new Dropdown.OptionData(option));
            dd.value = value;
            dd.RefreshShownValue();
            return dd;
        }

        static Button _Button(DefaultControls.Resources res, Font font, Transform parent, Vector2 pos, Vector2 size, string label)
        {
            var go = DefaultControls.CreateButton(res);
            go.name = "LoginButton";
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var text = go.GetComponentInChildren<Text>();
            text.font = font;
            text.text = label;
            text.fontSize = 24;
            return go.GetComponent<Button>();
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
