#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class FigmaJsonToUnity
{
    private static readonly Dictionary<string, TMP_FontAsset> FontCache = new Dictionary<string, TMP_FontAsset>();
    private static ImportContext CurrentImportContext;

    private static readonly HashSet<string> ContainerTypes = new HashSet<string>
    {
        "FRAME",
        "GROUP",
        "COMPONENT",
        "INSTANCE",
        "SECTION"
    };

    [MenuItem("Tools/Figma/Select Figma JSON And Build")]
    public static void Build()
    {
        string path = EditorUtility.OpenFilePanel("Select Figma JSON", GetDefaultExportFolder(), "json");
        if (string.IsNullOrEmpty(path)) return;

        int choice = EditorUtility.DisplayDialogComplex(
            "Figma JSON Import",
            "Create a new scene or update the currently open scene?",
            "Create New Scene",
            "Cancel",
            "Update Open Scene");

        if (choice == 1)
            return;

        BuildFromPath(path, choice == 2);
    }

    [MenuItem("Tools/Figma/Select Figma JSON And Update Open Scene")]
    public static void UpdateOpenScene()
    {
        string path = EditorUtility.OpenFilePanel("Select Figma JSON", GetDefaultExportFolder(), "json");
        if (string.IsNullOrEmpty(path)) return;
        BuildFromPath(path, true);
    }

    public static void BuildFromPath(string path, bool updateExistingScene = false)
    {
        JToken document;
        try
        {
            document = JToken.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Debug.LogError($"Could not read Figma JSON: {ex.Message}");
            return;
        }

        if (document is JObject package && (string)package["format"] == "figma-unity-package")
        {
            BuildUnityPackage(package, path, updateExistingScene);
            return;
        }

        List<JObject> roots = GetImportRoots(document);
        if (roots.Count == 0)
        {
            Debug.LogError("No usable Figma node found. Select a frame, group, component, or visible UI node before exporting.");
            return;
        }

        Rect documentBox = GetUnionBox(roots);
        string screenName = Sanitize(Path.GetFileNameWithoutExtension(path));
        if (roots.Count == 1)
            screenName = Sanitize((string)roots[0]["name"] ?? screenName);

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects);
        var canvasTransform = CreateCanvas(documentBox);
        EnsureEventSystem();
        var responsiveRoot = CreateRectObject(screenName, canvasTransform, documentBox, documentBox);
        ConfigureResponsiveRoot(responsiveRoot, documentBox);

        if (roots.Count == 1)
        {
            BuildNode(roots[0], responsiveRoot, documentBox, true);
        }
        else
        {
            foreach (var node in roots)
                BuildNode(node, responsiveRoot, documentBox, false);
        }

        Directory.CreateDirectory("Assets/Scenes");
        string scenePath = AssetDatabase.GenerateUniqueAssetPath($"Assets/Scenes/{screenName}.unity");
        EditorSceneManager.SaveScene(scene, scenePath);
        AssetDatabase.Refresh();
        Debug.Log($"Figma UI scene built from {path} and saved to {scenePath}");
    }

    private sealed class ImportContext
    {
        public bool UpdateExisting;
        public string PackageAssetFolder;
        public string CommonAssetFolder;
        public readonly Dictionary<string, int> AssetFrameUseCount = new Dictionary<string, int>();
        public readonly Dictionary<string, string> AssetPathFingerprints = new Dictionary<string, string>();
        public readonly Dictionary<string, RectTransform> ByFigmaId = new Dictionary<string, RectTransform>();
        public readonly Dictionary<string, RectTransform> ByName = new Dictionary<string, RectTransform>();
        public readonly HashSet<RectTransform> ExistingFigmaObjects = new HashSet<RectTransform>();
        public readonly HashSet<RectTransform> TouchedFigmaObjects = new HashSet<RectTransform>();
    }

    private static void BuildUnityPackage(JObject package, string path, bool updateExistingScene)
    {
        JObject rootData = package["root"] as JObject;
        JArray nodes = package["nodes"] as JArray;
        JArray screens = package["screens"] as JArray;
        JObject assets = package["assets"] as JObject;

        if (rootData == null || (nodes == null && screens == null) || assets == null)
        {
            Debug.LogError("Invalid Figma Unity package. Expected root, nodes/screens, and assets.");
            return;
        }

        Rect documentBox = GetPackageCanvasBox(rootData, screens, nodes);
        string screenName = Sanitize((string)package["name"] ?? Path.GetFileNameWithoutExtension(path));
        string packageAssetFolder = $"Assets/FigmaSprites/{screenName}";
        Directory.CreateDirectory(packageAssetFolder);

        Scene scene = updateExistingScene
            ? SceneManager.GetActiveScene()
            : EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects);

        CurrentImportContext = new ImportContext
        {
            UpdateExisting = updateExistingScene,
            PackageAssetFolder = packageAssetFolder,
            CommonAssetFolder = $"{packageAssetFolder}/Common"
        };

        try
        {
            IndexPackageAssetUsage(screens, nodes, assets);

            if (updateExistingScene)
                IndexExistingFigmaObjects();

            var canvasTransform = CreateCanvas(documentBox);
            EnsureEventSystem();

            var root = GetOrCreateRectObject(screenName, canvasTransform, documentBox, documentBox, rootData, true);
            ConfigureResponsiveRoot(root, documentBox);

            if (screens != null && screens.Count > 0)
            {
                bool first = true;
                foreach (var screenToken in screens)
                {
                    if (!(screenToken is JObject screen)) continue;
                    string frameAssetFolder = GetFrameAssetFolder(packageAssetFolder, screen);
                    BuildPackageScreen(screen, root, documentBox, assets, frameAssetFolder, first);
                    first = false;
                }
            }
            else
            {
                string frameAssetFolder = GetFrameAssetFolder(packageAssetFolder, rootData);
                BuildPackageScreen(rootData, root, documentBox, assets, frameAssetFolder, true, nodes);
            }

            if (updateExistingScene)
                DeactivateMissingFigmaObjects();

            Directory.CreateDirectory("Assets/Scenes");
            string scenePath = updateExistingScene && !string.IsNullOrEmpty(scene.path)
                ? scene.path
                : AssetDatabase.GenerateUniqueAssetPath($"Assets/Scenes/{screenName}.unity");
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.Refresh();
            Debug.Log($"Figma Unity package {(updateExistingScene ? "updated" : "built")} from {path} and saved to {scenePath}");
        }
        finally
        {
            CurrentImportContext = null;
        }
    }

    private static void BuildPackageScreen(JObject screen, Transform root, Rect documentBox, JObject assets, string assetFolder, bool visibleByDefault, JArray fallbackNodes = null)
    {
        Directory.CreateDirectory(assetFolder);
        Rect screenBox = GetBox(screen);
        var panel = GetOrCreateRectObject($"screen_{MakeObjectId((string)screen["name"] ?? "figma_screen")}", root, documentBox, documentBox, screen, true);
        panel.gameObject.SetActive(visibleByDefault);

        var background = EnableComponent(GetOrAddComponent<Image>(panel.gameObject));
        background.color = GetPackageColor(screen["fill"] as JObject, Color.white);
        background.raycastTarget = false;
        EnableComponent(GetOrAddComponent<RectMask2D>(panel.gameObject));

        JArray screenNodes = (screen["nodes"] as JArray) ?? fallbackNodes;
        if (screenNodes == null) return;

        int siblingIndex = 0;
        foreach (var token in screenNodes)
        {
            if (!(token is JObject node)) continue;
            RectTransform built = BuildPackageNode(node, panel, screenBox, assets, assetFolder);
            if (built != null && built.parent == panel)
                built.SetSiblingIndex(siblingIndex++);
        }
    }

    private static string GetFrameAssetFolder(string packageAssetFolder, JObject screen)
    {
        string frameName = FirstNonEmpty((string)screen?["name"], (string)screen?["id"], "Figma Frame");
        string folder = $"{packageAssetFolder}/{Sanitize(frameName)}";
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void IndexPackageAssetUsage(JArray screens, JArray fallbackNodes, JObject assets)
    {
        if (CurrentImportContext == null || assets == null)
            return;

        CurrentImportContext.AssetFrameUseCount.Clear();
        CurrentImportContext.AssetPathFingerprints.Clear();

        if (screens != null && screens.Count > 0)
        {
            foreach (var screenToken in screens)
            {
                if (!(screenToken is JObject screen))
                    continue;

                var frameFingerprints = new HashSet<string>();
                CollectPackageAssetFingerprints(screen["nodes"] as JArray, assets, frameFingerprints);
                foreach (string fingerprint in frameFingerprints)
                    CurrentImportContext.AssetFrameUseCount[fingerprint] = CurrentImportContext.AssetFrameUseCount.TryGetValue(fingerprint, out int count) ? count + 1 : 1;
            }
        }
        else
        {
            var frameFingerprints = new HashSet<string>();
            CollectPackageAssetFingerprints(fallbackNodes, assets, frameFingerprints);
            foreach (string fingerprint in frameFingerprints)
                CurrentImportContext.AssetFrameUseCount[fingerprint] = 1;
        }

        foreach (var item in CurrentImportContext.AssetFrameUseCount)
        {
            if (item.Value > 1)
            {
                Directory.CreateDirectory(CurrentImportContext.CommonAssetFolder);
                break;
            }
        }
    }

    private static void CollectPackageAssetFingerprints(JArray nodes, JObject assets, HashSet<string> frameFingerprints)
    {
        if (nodes == null)
            return;

        foreach (var token in nodes)
        {
            if (!(token is JObject node))
                continue;

            AddPackageAssetFingerprint((string)node["asset"], assets, frameFingerprints);
            AddPackageAssetFingerprint((string)node["track"]?["asset"], assets, frameFingerprints);
            AddPackageAssetFingerprint((string)node["handle"]?["asset"], assets, frameFingerprints);
            CollectPackageAssetFingerprints(node["children"] as JArray, assets, frameFingerprints);
        }
    }

    private static void AddPackageAssetFingerprint(string assetPath, JObject assets, HashSet<string> frameFingerprints)
    {
        if (string.IsNullOrWhiteSpace(assetPath) || assets == null || frameFingerprints == null)
            return;

        if (!TryGetPackageAssetFingerprint(assetPath, assets, out string fingerprint))
            return;

        frameFingerprints.Add(fingerprint);
    }

    private static bool TryGetPackageAssetFingerprint(string assetPath, JObject assets, out string fingerprint)
    {
        fingerprint = null;
        if (CurrentImportContext == null || string.IsNullOrWhiteSpace(assetPath) || assets == null)
            return false;

        if (CurrentImportContext.AssetPathFingerprints.TryGetValue(assetPath, out fingerprint))
            return !string.IsNullOrWhiteSpace(fingerprint);

        string encoded = (string)assets[assetPath];
        if (string.IsNullOrEmpty(encoded))
            return false;

        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            fingerprint = GetVisualFingerprint(bytes);
            CurrentImportContext.AssetPathFingerprints[assetPath] = fingerprint;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static RectTransform BuildPackageNode(JObject node, Transform parent, Rect parentBox, JObject assets, string assetFolder)
    {
        string kind = (string)node["kind"] ?? "image";
        if (kind == "group")
        {
            Rect groupBox = GetBox(node);
            var groupRect = GetOrCreateRectObject(GetPackageObjectName(node), parent, groupBox, parentBox, node, false);
            ApplyRotation(node, groupRect);
            EnsureGroupButtonIfNeeded(node, groupRect);

            if (node["children"] is JArray children)
            {
                int siblingIndex = 0;
                foreach (var childToken in children)
                {
                    if (childToken is JObject child)
                    {
                        RectTransform built = BuildPackageNode(child, groupRect, groupBox, assets, assetFolder);
                        if (built != null && built.parent == groupRect)
                            built.SetSiblingIndex(siblingIndex++);
                    }
                }
            }
            return groupRect;
        }

        if (kind == "text")
        {
            return BuildPackageText(node, parent, parentBox);
        }

        string assetPath = (string)node["asset"];
        string encoded = assetPath != null ? (string)assets[assetPath] : null;
        if (string.IsNullOrEmpty(encoded)) return null;

        Rect box = GetBox(node);
        var rect = GetOrCreateRectObject(GetPackageObjectName(node), parent, box, parentBox, node, false);

        Sprite sprite = CreateSpriteFromBase64(encoded, assetFolder, assetPath);
        if (sprite == null) return rect;

        if (kind == "button")
        {
            BuildPackageButton(node, rect, sprite);
            return rect;
        }

        if (kind == "input-field")
        {
            BuildPackageInputField(node, rect, sprite);
            return rect;
        }

        if (kind == "slider")
        {
            BuildPackageSlider(node, rect, sprite, assets, assetFolder);
            return rect;
        }

        if (kind == "dropdown")
        {
            BuildPackageDropdown(node, rect, sprite);
            return rect;
        }

        if (kind == "masked-image")
        {
            BuildMaskedImage(node, rect, sprite);
            return rect;
        }

        RectTransform imageRect = ShouldFitSpriteToRenderBounds(kind) ? CreateRenderBoundsChild(node, rect, box) : rect;
        var image = EnableComponent(GetOrAddComponent<Image>(imageRect.gameObject));
        image.sprite = sprite;
        image.color = Color.white;
        image.raycastTarget = false;
        image.preserveAspect = false;
        return rect;
    }

    private static string GetPackageObjectName(JObject node)
    {
        string kind = ((string)node["kind"] ?? "image").Replace("-", "");
        string label = FirstNonEmpty(
            (string)node["label"],
            (string)node["placeholder"],
            (string)node["text"],
            (string)node["name"],
            "figma_node");

        if (kind == "maskedimage")
            kind = "maskedimage";
        else if (kind == "inputfield")
            kind = "input";
        else if (kind == "dropdown")
            kind = "dropdown";
        else if (kind == "slider")
            kind = "slider";
        else if (kind == "panelbackground")
            kind = "panelbg";
        else if (kind == "screenbackground")
            kind = "background";

        return $"{kind}_{MakeObjectId(label)}";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "figma_node";
    }

    private static bool ShouldFitSpriteToRenderBounds(string kind)
    {
        return kind == "image" || kind == "logo";
    }

    private static RectTransform CreateRenderBoundsChild(JObject node, RectTransform parent, Rect layoutBox)
    {
        Rect renderBox = GetRenderBox(node);
        if (Mathf.Abs(renderBox.x - layoutBox.x) < 0.01f &&
            Mathf.Abs(renderBox.y - layoutBox.y) < 0.01f &&
            Mathf.Abs(renderBox.width - layoutBox.width) < 0.01f &&
            Mathf.Abs(renderBox.height - layoutBox.height) < 0.01f)
        {
            return parent;
        }

        var child = new GameObject("Sprite", typeof(RectTransform));
        child.transform.SetParent(parent, false);

        var rect = (RectTransform)child.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(Mathf.Max(1f, renderBox.width), Mathf.Max(1f, renderBox.height));
        rect.anchoredPosition = new Vector2(renderBox.x - layoutBox.x, -(renderBox.y - layoutBox.y));
        return rect;
    }

    private static string MakeObjectId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unnamed";

        var chars = new List<char>();
        foreach (char ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                chars.Add(ch);
            else if (chars.Count > 0 && chars[chars.Count - 1] != '_')
                chars.Add('_');
        }

        string result = new string(chars.ToArray()).Trim('_');
        return string.IsNullOrEmpty(result) ? "unnamed" : result;
    }

    private static void EnsureGroupButtonIfNeeded(JObject node, RectTransform rect)
    {
        string name = FirstNonEmpty((string)node?["name"], rect != null ? rect.gameObject.name : null, "");
        if (rect == null || !LooksLikeButton(name))
            return;

        var hitArea = EnableComponent(GetOrAddComponent<Image>(rect.gameObject));
        hitArea.color = new Color(1f, 1f, 1f, 0.001f);
        hitArea.raycastTarget = true;

        var button = EnableComponent(GetOrAddComponent<Button>(rect.gameObject));
        button.targetGraphic = hitArea;
        button.transition = Selectable.Transition.None;
    }

    private static void BuildPackageButton(JObject node, RectTransform rect, Sprite sprite)
    {
        bool alphaHitTest = (bool?)node["alphaHitTest"] == true;
        bool textBaked = (bool?)node["textBaked"] == true;
        bool preserveTextLayout = (bool?)node["preserveTextLayout"] == true;
        bool interactionOnly = (bool?)node["interactionOnly"] == true || (alphaHitTest && preserveTextLayout && !textBaked);

        var image = EnableComponent(GetOrAddComponent<Image>(rect.gameObject));
        image.sprite = sprite;
        image.color = Color.white;
        image.raycastTarget = true;
        image.preserveAspect = false;
        if (alphaHitTest)
        {
            image.alphaHitTestMinimumThreshold = 0.1f;
            AddAlphaRaycastFilter(rect.gameObject, image);
        }

        var button = EnableComponent(GetOrAddComponent<Button>(rect.gameObject));
        if (interactionOnly)
        {
            button.transition = Selectable.Transition.None;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = Color.white;
            colors.pressedColor = Color.white;
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(1f, 1f, 1f, 0.5f);
            button.colors = colors;
        }

        if (textBaked)
            return;

        JObject textStyle = node["textStyle"] as JObject;
        RectTransform labelRect;
        if (interactionOnly && textStyle != null)
        {
            labelRect = CreateInteractionButtonLabelRect(rect, node, textStyle);
            ApplyRotation(textStyle, labelRect);
        }
        else if (preserveTextLayout && textStyle != null)
        {
            labelRect = CreateRectObject("Label", rect, GetBox(textStyle), GetBox(node));
            ApplyRotation(textStyle, labelRect);
        }
        else
        {
            labelRect = CreateStretchChild("Label", rect, new Vector2(8f, 4f), new Vector2(-8f, -4f));
        }

        var label = EnableComponent(GetOrAddComponent<TextMeshProUGUI>(labelRect.gameObject));
        ApplyTextStyle(label, textStyle, (string)node["label"] ?? "");
        if (interactionOnly)
        {
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.enableAutoSizing = false;
        }
        else if (!preserveTextLayout)
            label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
    }

    private static RectTransform CreateInteractionButtonLabelRect(RectTransform buttonRect, JObject buttonNode, JObject textStyle)
    {
        var go = GetOrCreateNamedChild("Label", buttonRect);

        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);

        Rect textBox = GetBox(textStyle);
        Rect buttonBox = GetBox(buttonNode);

        float localCenterX = textBox.center.x - buttonBox.x;
        float localCenterY = -(textBox.center.y - buttonBox.y);
        float labelWidth = Mathf.Clamp(Mathf.Max(textBox.width, textBox.height) * 1.18f, buttonRect.rect.width * 0.62f, buttonRect.rect.height * 0.95f);
        float labelHeight = Mathf.Clamp(GetFloat(textStyle, "fontSize", 24f) * 1.7f, 32f, buttonRect.rect.width * 0.42f);

        rect.anchoredPosition = new Vector2(localCenterX, localCenterY);
        rect.sizeDelta = new Vector2(labelWidth, labelHeight);
        return rect;
    }

    private static void AddAlphaRaycastFilter(GameObject target, Image image)
    {
        Type filterType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            filterType = assembly.GetType("FigmaAlphaRaycastFilter");
            if (filterType != null)
                break;
        }

        if (filterType == null || target.GetComponent(filterType) != null)
            return;

        var component = target.AddComponent(filterType);
        filterType.GetProperty("TargetImage")?.SetValue(component, image);
        filterType.GetProperty("AlphaThreshold")?.SetValue(component, 0.1f);
    }

    private static void BuildPackageInputField(JObject node, RectTransform rect, Sprite sprite)
    {
        var image = EnableComponent(GetOrAddComponent<Image>(rect.gameObject));
        image.sprite = sprite;
        image.color = Color.white;
        image.raycastTarget = true;
        image.preserveAspect = false;

        var input = EnableComponent(GetOrAddComponent<TMP_InputField>(rect.gameObject));
        input.targetGraphic = image;

        JObject textStyle = node["textStyle"] as JObject;
        var textRect = CreateStretchChild("Text", rect, new Vector2(10f, 4f), new Vector2(-10f, -4f));
        var text = EnableComponent(GetOrAddComponent<TextMeshProUGUI>(textRect.gameObject));
        ApplyTextStyle(text, textStyle, "");
        text.raycastTarget = false;
        text.alignment = TextAlignmentOptions.Left;

        var placeholderRect = CreateStretchChild("Placeholder", rect, new Vector2(10f, 4f), new Vector2(-10f, -4f));
        var placeholder = EnableComponent(GetOrAddComponent<TextMeshProUGUI>(placeholderRect.gameObject));
        ApplyTextStyle(placeholder, textStyle, (string)node["placeholder"] ?? "");
        placeholder.color = new Color(placeholder.color.r, placeholder.color.g, placeholder.color.b, Mathf.Min(placeholder.color.a, 0.55f));
        placeholder.raycastTarget = false;
        placeholder.alignment = TextAlignmentOptions.Left;

        input.textComponent = text;
        input.placeholder = placeholder;
    }

    private static void BuildPackageSlider(JObject node, RectTransform rect, Sprite sprite, JObject assets, string assetFolder)
    {
        var slider = EnableComponent(GetOrAddComponent<Slider>(rect.gameObject));
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.value = GetFloat(node, "value", 0.5f);
        slider.direction = rect.sizeDelta.x >= rect.sizeDelta.y ? Slider.Direction.LeftToRight : Slider.Direction.BottomToTop;

        var hitArea = EnableComponent(GetOrAddComponent<Image>(rect.gameObject));
        hitArea.color = new Color(1f, 1f, 1f, 0.01f);
        hitArea.raycastTarget = true;
        slider.targetGraphic = hitArea;

        JObject trackData = node["track"] as JObject;
        JObject handleData = node["handle"] as JObject;
        if (trackData != null && handleData != null)
        {
            Rect rootBox = GetBox(node);
            Sprite trackSprite = LoadPackagePartSprite(trackData, assets, assetFolder) ?? sprite;
            Sprite handleSprite = LoadPackagePartSprite(handleData, assets, assetFolder);

            var trackRect = CreateRectObject("Track", rect, GetBox(trackData), rootBox);
            var trackImage = EnableComponent(GetOrAddComponent<Image>(trackRect.gameObject));
            trackImage.sprite = trackSprite;
            trackImage.color = Color.white;
            trackImage.raycastTarget = false;
            trackImage.preserveAspect = false;

            var handleArea = CreateStretchChild("Handle Slide Area", rect, Vector2.zero, Vector2.zero);
            var handleRect = CreateRectObject("Handle", handleArea, GetBox(handleData), rootBox);
            var handleImage = EnableComponent(GetOrAddComponent<Image>(handleRect.gameObject));
            handleImage.sprite = handleSprite ?? sprite;
            handleImage.color = Color.white;
            handleImage.raycastTarget = true;
            handleImage.preserveAspect = false;

            slider.targetGraphic = handleImage;
            slider.handleRect = handleRect;
            slider.value = GetFloat(node, "value", 0.5f);
            return;
        }

        hitArea.sprite = sprite;
        hitArea.color = Color.white;
        hitArea.raycastTarget = true;
        hitArea.preserveAspect = false;
        slider.targetGraphic = hitArea;

        var fallbackHandleArea = CreateStretchChild("Handle Slide Area", rect, Vector2.zero, Vector2.zero);
        var handle = new GameObject("Handle", typeof(RectTransform));
        handle.transform.SetParent(fallbackHandleArea, false);

        var fallbackHandleRect = (RectTransform)handle.transform;
        fallbackHandleRect.anchorMin = fallbackHandleRect.anchorMax = fallbackHandleRect.pivot = new Vector2(0.5f, 0.5f);
        float handleSize = Mathf.Clamp(Mathf.Min(rect.sizeDelta.x, rect.sizeDelta.y), 12f, 36f);
        fallbackHandleRect.sizeDelta = new Vector2(handleSize, handleSize);

        var fallbackHandleImage = EnableComponent(GetOrAddComponent<Image>(handle));
        fallbackHandleImage.color = new Color(1f, 1f, 1f, 0.01f);
        fallbackHandleImage.raycastTarget = true;

        slider.targetGraphic = fallbackHandleImage;
        slider.handleRect = fallbackHandleRect;
    }

    private static Sprite LoadPackagePartSprite(JObject part, JObject assets, string assetFolder)
    {
        string assetPath = (string)part["asset"];
        string encoded = assetPath != null ? (string)assets[assetPath] : null;
        if (string.IsNullOrEmpty(encoded))
            return null;

        return CreateSpriteFromBase64(encoded, assetFolder, assetPath);
    }

    private static void BuildPackageDropdown(JObject node, RectTransform rect, Sprite sprite)
    {
        var image = EnableComponent(GetOrAddComponent<Image>(rect.gameObject));
        image.sprite = sprite;
        image.color = Color.white;
        image.raycastTarget = true;
        image.preserveAspect = false;

        var dropdown = EnableComponent(GetOrAddComponent<TMP_Dropdown>(rect.gameObject));
        dropdown.targetGraphic = image;

        JObject textStyle = node["textStyle"] as JObject;
        string labelValue = (string)node["label"] ?? "Option";

        var labelRect = CreateStretchChild("Label", rect, new Vector2(10f, 4f), new Vector2(-24f, -4f));
        var label = EnableComponent(GetOrAddComponent<TextMeshProUGUI>(labelRect.gameObject));
        ApplyTextStyle(label, textStyle, labelValue);
        label.alignment = TextAlignmentOptions.Left;
        label.raycastTarget = false;

        dropdown.captionText = label;
        dropdown.options.Clear();
        dropdown.options.Add(new TMP_Dropdown.OptionData(string.IsNullOrWhiteSpace(labelValue) ? "Option" : labelValue));
        dropdown.options.Add(new TMP_Dropdown.OptionData("Option 2"));

        CreateDropdownTemplate(dropdown, rect, textStyle);
    }

    private static void CreateDropdownTemplate(TMP_Dropdown dropdown, RectTransform root, JObject textStyle)
    {
        var template = new GameObject("Template", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
        template.transform.SetParent(root, false);
        template.SetActive(false);

        var templateRect = (RectTransform)template.transform;
        templateRect.anchorMin = new Vector2(0f, 0f);
        templateRect.anchorMax = new Vector2(1f, 0f);
        templateRect.pivot = new Vector2(0.5f, 1f);
        templateRect.anchoredPosition = Vector2.zero;
        templateRect.sizeDelta = new Vector2(0f, Mathf.Max(96f, root.sizeDelta.y * 3f));

        var templateImage = template.GetComponent<Image>();
        templateImage.color = Color.white;
        templateImage.raycastTarget = true;

        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
        viewport.transform.SetParent(templateRect, false);
        var viewportRect = (RectTransform)viewport.transform;
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = Vector2.zero;
        viewportRect.offsetMax = Vector2.zero;
        viewport.GetComponent<Image>().color = Color.white;
        viewport.GetComponent<Mask>().showMaskGraphic = false;

        var content = new GameObject("Content", typeof(RectTransform));
        content.transform.SetParent(viewportRect, false);
        var contentRect = (RectTransform)content.transform;
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = new Vector2(0f, Mathf.Max(64f, root.sizeDelta.y * 2f));

        var item = new GameObject("Item", typeof(RectTransform), typeof(Toggle));
        item.transform.SetParent(contentRect, false);
        var itemRect = (RectTransform)item.transform;
        itemRect.anchorMin = new Vector2(0f, 1f);
        itemRect.anchorMax = new Vector2(1f, 1f);
        itemRect.pivot = new Vector2(0.5f, 1f);
        itemRect.anchoredPosition = Vector2.zero;
        itemRect.sizeDelta = new Vector2(0f, Mathf.Max(24f, root.sizeDelta.y));

        var itemLabelRect = CreateStretchChild("Item Label", itemRect, new Vector2(10f, 2f), new Vector2(-10f, -2f));
        var itemLabel = EnableComponent(GetOrAddComponent<TextMeshProUGUI>(itemLabelRect.gameObject));
        ApplyTextStyle(itemLabel, textStyle, "Option");
        itemLabel.alignment = TextAlignmentOptions.Left;
        itemLabel.raycastTarget = false;

        var scrollRect = template.GetComponent<ScrollRect>();
        scrollRect.content = contentRect;
        scrollRect.viewport = viewportRect;
        scrollRect.horizontal = false;

        dropdown.template = templateRect;
        dropdown.itemText = itemLabel;
    }

    private static RectTransform CreateStretchChild(string name, RectTransform parent, Vector2 offsetMin, Vector2 offsetMax)
    {
        var child = GetOrCreateNamedChild(name, parent);

        var rect = (RectTransform)child.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
        return rect;
    }

    private static Rect GetPackageCanvasBox(JObject rootData, JArray screens, JArray nodes)
    {
        if (screens != null && screens.Count > 0 && screens[0] is JObject firstScreen)
        {
            Rect firstBox = GetBox(firstScreen);
            return new Rect(0f, 0f, Mathf.Max(1f, firstBox.width), Mathf.Max(1f, firstBox.height));
        }

        Rect rootBox = GetBox(rootData);
        return new Rect(0f, 0f, Mathf.Max(1f, rootBox.width), Mathf.Max(1f, rootBox.height));
    }

    private static RectTransform BuildPackageText(JObject node, Transform parent, Rect documentBox)
    {
        Rect box = GetBox(node);
        var rect = GetOrCreateRectObject((string)node["name"] ?? "Figma Text", parent, box, documentBox, node, false);
        ApplyRotation(node, rect);

        var text = EnableComponent(GetOrAddComponent<TextMeshProUGUI>(rect.gameObject));
        ApplyTextStyle(text, node, (string)node["text"] ?? "");
        return rect;
    }

    private static void ApplyTextStyle(TextMeshProUGUI text, JObject node, string value)
    {
        if (node == null)
        {
            text.text = value;
            text.fontSize = 24f;
            text.color = Color.black;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            return;
        }

        text.text = value;
        text.fontSize = GetFloat(node, "fontSize", 24f);
        ApplyPackageTextPaint(text, node);
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        text.alignment = GetPackageTextAlignment(node);

        string fontStyle = (string)node["fontStyle"] ?? "";
        TMP_FontAsset fontAsset = GetMatchingTmpFont((string)node["fontFamily"], fontStyle);
        if (fontAsset != null)
            text.font = fontAsset;

        text.fontStyle = GetSyntheticFontStyle(fontStyle, fontAsset);

        if (node["lineHeight"] is JObject lineHeight)
        {
            string unit = (string)lineHeight["unit"];
            float lineHeightValue = GetFloat(lineHeight, "value", 0f);
            if (unit == "PERCENT" && lineHeightValue > 0f)
                text.lineSpacing = lineHeightValue - 100f;
        }
    }

    private static void BuildMaskedImage(JObject node, RectTransform rect, Sprite sprite)
    {
        var maskGraphic = EnableComponent(GetOrAddComponent<Image>(rect.gameObject));
        maskGraphic.sprite = GetCircleMaskSprite();
        maskGraphic.color = Color.white;
        maskGraphic.raycastTarget = false;

        var mask = EnableComponent(GetOrAddComponent<Mask>(rect.gameObject));
        mask.showMaskGraphic = false;

        var child = GetOrCreateNamedChild("Image", rect);

        var childRect = (RectTransform)child.transform;
        childRect.anchorMin = Vector2.zero;
        childRect.anchorMax = Vector2.one;
        childRect.pivot = new Vector2(0.5f, 0.5f);
        childRect.offsetMin = Vector2.zero;
        childRect.offsetMax = Vector2.zero;

        var image = EnableComponent(GetOrAddComponent<Image>(child));
        image.sprite = sprite;
        image.color = Color.white;
        image.raycastTarget = false;
        image.preserveAspect = false;
    }

    private static Transform CreateCanvas(Rect documentBox)
    {
        Canvas existingCanvas = CurrentImportContext != null && CurrentImportContext.UpdateExisting
            ? UnityEngine.Object.FindFirstObjectByType<Canvas>()
            : null;
        GameObject canvasGO = existingCanvas != null
            ? existingCanvas.gameObject
            : new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

        canvasGO.SetActive(true);

        var canvas = EnableComponent(GetOrAddComponent<Canvas>(canvasGO));
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        var scaler = EnableComponent(GetOrAddComponent<CanvasScaler>(canvasGO));
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(Mathf.Max(1f, documentBox.width), Mathf.Max(1f, documentBox.height));
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        EnableComponent(GetOrAddComponent<GraphicRaycaster>(canvasGO));

        return canvasGO.transform;
    }

    private static void ConfigureResponsiveRoot(RectTransform root, Rect documentBox)
    {
        if (root == null)
            return;

        Type responsiveType = GetTypeByName("FigmaResponsiveRoot");
        if (responsiveType == null)
        {
            Debug.LogWarning("FigmaResponsiveRoot script was not found. Unity may need to refresh scripts before responsive scaling can be attached.");
            return;
        }

        Component responsive = root.GetComponent(responsiveType);
        if (responsive == null)
            responsive = root.gameObject.AddComponent(responsiveType);

        if (responsive is Behaviour behaviour)
            behaviour.enabled = true;

        responsiveType.GetField("designSize")?.SetValue(responsive, new Vector2(Mathf.Max(1f, documentBox.width), Mathf.Max(1f, documentBox.height)));

        var fitModeField = responsiveType.GetField("fitMode");
        if (fitModeField != null && fitModeField.FieldType.IsEnum)
            fitModeField.SetValue(responsive, Enum.Parse(fitModeField.FieldType, "FitInside"));

        responsiveType.GetMethod("Apply")?.Invoke(responsive, null);
    }

    private static void EnsureEventSystem()
    {
        EventSystem eventSystem = UnityEngine.Object.FindFirstObjectByType<EventSystem>();
        GameObject eventSystemObject = eventSystem != null
            ? eventSystem.gameObject
            : new GameObject("EventSystem", typeof(EventSystem));

        foreach (var oldModule in eventSystemObject.GetComponents<StandaloneInputModule>())
            UnityEngine.Object.DestroyImmediate(oldModule);

        var inputModule = eventSystemObject.GetComponent<InputSystemUIInputModule>();
        if (inputModule != null)
            UnityEngine.Object.DestroyImmediate(inputModule);

        inputModule = eventSystemObject.AddComponent<InputSystemUIInputModule>();
        EnableComponent(inputModule);

        InputActionAsset inputActions = LoadUiInputActions();
        if (inputActions != null)
            inputModule.actionsAsset = inputActions;
    }

    private static InputActionAsset LoadUiInputActions()
    {
        string[] candidates =
        {
            "Assets/InputSystem_Actions.inputactions"
        };

        foreach (string candidate in candidates)
        {
            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(candidate);
            if (asset != null && asset.FindActionMap("UI", false) != null)
                return asset;
        }

        Debug.LogWarning("Figma importer could not find a UI InputActionAsset. UI will still build, but interactive controls may not receive clicks until an EventSystem input module is configured.");
        return null;
    }

    private static void BuildNode(JObject node, Transform parent, Rect parentBox, bool rootNode)
    {
        if (IsHidden(node)) return;

        string type = ((string)node["type"] ?? "NODE").ToUpperInvariant();
        string name = (string)node["name"] ?? type;
        Rect box = GetBox(node);

        var rect = CreateRectObject(name, parent, box, rootNode ? box : parentBox);
        ApplyRotation(node, rect);

        if (type == "TEXT")
        {
            BuildText(node, rect.gameObject);
            return;
        }

        Sprite sprite = LoadSpriteForNode(name) ?? CreateSpriteFromEmbeddedImage(node, name);
        bool hasFill = TryGetFill(node, out Color fill);
        if (sprite != null || hasFill || type == "RECTANGLE" || type == "ELLIPSE")
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;

            if (sprite != null)
            {
                image.sprite = sprite;
                image.color = Color.white;
                image.preserveAspect = true;
            }
            else
            {
                image.color = hasFill ? fill : new Color(1f, 1f, 1f, 0f);
            }

            if (LooksLikeButton(name))
            {
                image.raycastTarget = true;
                rect.gameObject.AddComponent<Button>();
            }
        }

        if ((bool?)node["clipsContent"] == true)
            rect.gameObject.AddComponent<RectMask2D>();

        if (node["children"] is JArray children)
        {
            foreach (var child in children)
            {
                if (child is JObject childObject)
                    BuildNode(childObject, rect, box, false);
            }
        }
    }

    private static RectTransform CreateRectObject(string name, Transform parent, Rect box, Rect parentBox)
    {
        string objectName = SanitizeObjectName(name);
        GameObject go;
        if (CurrentImportContext != null && CurrentImportContext.UpdateExisting && parent != null)
        {
            Transform existing = parent.Find(objectName);
            go = existing != null && existing is RectTransform ? existing.gameObject : null;
        }
        else
        {
            go = null;
        }

        if (go == null)
        {
            go = new GameObject(objectName, typeof(RectTransform));
            go.transform.SetParent(parent, false);
        }

        var rect = (RectTransform)go.transform;
        go.SetActive(true);
        ApplyRect(rect, box, parentBox);
        return rect;
    }

    private static RectTransform GetOrCreateRectObject(string name, Transform parent, Rect box, Rect parentBox, JObject node, bool forceParent)
    {
        string objectName = SanitizeObjectName(name);
        RectTransform rect = FindExistingRect(node, objectName);
        bool created = false;
        if (rect == null)
        {
            var go = new GameObject(objectName, typeof(RectTransform));
            rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            created = true;
        }
        else if (forceParent || rect.parent == parent)
        {
            rect.SetParent(parent, false);
        }

        if (created || rect.parent == parent)
            ApplyRect(rect, box, parentBox);
        else
            rect.sizeDelta = new Vector2(Mathf.Max(1f, box.width), Mathf.Max(1f, box.height));

        rect.gameObject.SetActive(true);
        AddFigmaBinding(rect.gameObject, (string)node?["id"], (string)node?["kind"] ?? (string)node?["type"]);
        IndexImportedObject(rect);
        return rect;
    }

    private static void ApplyRect(RectTransform rect, Rect box, Rect parentBox)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(Mathf.Max(1f, box.width), Mathf.Max(1f, box.height));
        rect.anchoredPosition = new Vector2(box.x - parentBox.x, -(box.y - parentBox.y));
    }

    private static RectTransform FindExistingRect(JObject node, string objectName)
    {
        if (CurrentImportContext == null || !CurrentImportContext.UpdateExisting)
            return null;

        string figmaId = (string)node?["id"];
        if (!string.IsNullOrWhiteSpace(figmaId) && CurrentImportContext.ByFigmaId.TryGetValue(figmaId, out RectTransform byId) && byId != null)
            return byId;

        if (!string.IsNullOrWhiteSpace(objectName) && CurrentImportContext.ByName.TryGetValue(objectName, out RectTransform byName) && byName != null)
            return byName;

        return null;
    }

    private static void IndexExistingFigmaObjects()
    {
        if (CurrentImportContext == null)
            return;

        foreach (var rect in UnityEngine.Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            IndexImportedObject(rect, true);
    }

    private static void IndexImportedObject(RectTransform rect, bool existingObject = false)
    {
        if (CurrentImportContext == null || rect == null)
            return;

        if (!CurrentImportContext.ByName.ContainsKey(rect.gameObject.name))
            CurrentImportContext.ByName.Add(rect.gameObject.name, rect);

        Type bindingType = GetFigmaNodeBindingType();
        if (bindingType == null)
            return;

        Component binding = rect.GetComponent(bindingType);
        if (binding == null)
            return;

        string figmaId = bindingType.GetField("figmaId")?.GetValue(binding) as string;
        if (!string.IsNullOrWhiteSpace(figmaId))
        {
            CurrentImportContext.ByFigmaId[figmaId] = rect;
            if (existingObject)
                CurrentImportContext.ExistingFigmaObjects.Add(rect);
        }
    }

    private static void AddFigmaBinding(GameObject target, string figmaId, string kind)
    {
        if (target == null || string.IsNullOrWhiteSpace(figmaId))
            return;

        Type bindingType = GetFigmaNodeBindingType();
        if (bindingType == null)
            return;

        Component binding = target.GetComponent(bindingType);
        if (binding == null)
            binding = target.AddComponent(bindingType);

        bindingType.GetField("figmaId")?.SetValue(binding, figmaId);
        bindingType.GetField("figmaKind")?.SetValue(binding, kind ?? "");

        if (target.transform is RectTransform rect)
            CurrentImportContext?.TouchedFigmaObjects.Add(rect);
    }

    private static void DeactivateMissingFigmaObjects()
    {
        if (CurrentImportContext == null)
            return;

        foreach (RectTransform rect in CurrentImportContext.ExistingFigmaObjects)
        {
            if (rect == null || CurrentImportContext.TouchedFigmaObjects.Contains(rect) || HasTouchedDescendant(rect))
                continue;

            rect.gameObject.SetActive(false);
        }
    }

    private static bool HasTouchedDescendant(RectTransform rect)
    {
        if (CurrentImportContext == null || rect == null)
            return false;

        foreach (RectTransform touched in CurrentImportContext.TouchedFigmaObjects)
        {
            if (touched != null && touched != rect && touched.IsChildOf(rect))
                return true;
        }

        return false;
    }

    private static Type GetFigmaNodeBindingType()
    {
        return GetTypeByName("FigmaNodeBinding");
    }

    private static Type GetTypeByName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(typeName);
            if (type != null)
                return type;
        }

        return null;
    }

    private static T GetOrAddComponent<T>(GameObject target) where T : Component
    {
        T component = target.GetComponent<T>();
        return component != null ? component : target.AddComponent<T>();
    }

    private static T EnableComponent<T>(T component) where T : Behaviour
    {
        if (component != null)
            component.enabled = true;
        return component;
    }

    private static GameObject GetOrCreateNamedChild(string name, Transform parent)
    {
        string objectName = SanitizeObjectName(name);
        if (CurrentImportContext != null && CurrentImportContext.UpdateExisting)
        {
            Transform existing = parent.Find(objectName);
            if (existing != null && existing is RectTransform)
            {
                existing.gameObject.SetActive(true);
                return existing.gameObject;
            }
        }

        var child = new GameObject(objectName, typeof(RectTransform));
        child.transform.SetParent(parent, false);
        child.SetActive(true);
        return child;
    }

    private static void BuildText(JObject node, GameObject go)
    {
        var text = go.AddComponent<TextMeshProUGUI>();
        text.text = (string)node["characters"] ?? "";
        text.fontSize = GetFloat(node, "fontSize", GetFloat(node["style"] as JObject, "fontSize", 24f));
        text.color = TryGetFill(node, out Color fill) ? fill : Color.black;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Overflow;
        text.alignment = GetTextAlignment(node);

        if (node["lineHeight"] is JObject lineHeight)
        {
            string unit = (string)lineHeight["unit"];
            float value = GetFloat(lineHeight, "value", 0f);
            if (unit == "PERCENT" && value > 0f)
                text.lineSpacing = value - 100f;
        }
    }

    private static TextAlignmentOptions GetTextAlignment(JObject node)
    {
        string horizontal = ((string)(node["textAlignHorizontal"] ?? node["style"]?["textAlignHorizontal"]) ?? "LEFT").ToUpperInvariant();
        string vertical = ((string)(node["textAlignVertical"] ?? node["style"]?["textAlignVertical"]) ?? "TOP").ToUpperInvariant();

        if (vertical == "CENTER")
        {
            if (horizontal == "CENTER") return TextAlignmentOptions.Center;
            if (horizontal == "RIGHT") return TextAlignmentOptions.Right;
            return TextAlignmentOptions.Left;
        }

        if (vertical == "BOTTOM")
        {
            if (horizontal == "CENTER") return TextAlignmentOptions.Bottom;
            if (horizontal == "RIGHT") return TextAlignmentOptions.BottomRight;
            return TextAlignmentOptions.BottomLeft;
        }

        if (horizontal == "CENTER") return TextAlignmentOptions.Top;
        if (horizontal == "RIGHT") return TextAlignmentOptions.TopRight;
        return TextAlignmentOptions.TopLeft;
    }

    private static TextAlignmentOptions GetPackageTextAlignment(JObject node)
    {
        string horizontal = ((string)node["textAlignHorizontal"] ?? "LEFT").ToUpperInvariant();
        string vertical = ((string)node["textAlignVertical"] ?? "TOP").ToUpperInvariant();

        if (vertical == "CENTER")
        {
            if (horizontal == "CENTER") return TextAlignmentOptions.Center;
            if (horizontal == "RIGHT") return TextAlignmentOptions.Right;
            return TextAlignmentOptions.Left;
        }

        if (vertical == "BOTTOM")
        {
            if (horizontal == "CENTER") return TextAlignmentOptions.Bottom;
            if (horizontal == "RIGHT") return TextAlignmentOptions.BottomRight;
            return TextAlignmentOptions.BottomLeft;
        }

        if (horizontal == "CENTER") return TextAlignmentOptions.Top;
        if (horizontal == "RIGHT") return TextAlignmentOptions.TopRight;
        return TextAlignmentOptions.TopLeft;
    }

    private static void ApplyPackageTextPaint(TextMeshProUGUI text, JObject node)
    {
        JObject paint = node["paint"] as JObject;
        if (paint == null || (string)paint["kind"] == "solid")
        {
            text.color = GetPackageColor((paint?["color"] as JObject) ?? node["fill"] as JObject, Color.black);
            return;
        }

        if ((string)paint["kind"] == "gradient" && paint["stops"] is JArray stops && stops.Count > 0)
        {
            Color first = GetPackageColor(stops[0]?["color"] as JObject, Color.black);
            Color last = GetPackageColor(stops[stops.Count - 1]?["color"] as JObject, first);
            Color middle = stops.Count > 2 ? GetPackageColor(stops[stops.Count / 2]?["color"] as JObject, first) : Color.Lerp(first, last, 0.5f);

            text.color = Color.white;
            text.enableVertexGradient = true;
            text.colorGradient = new VertexGradient(first, last, middle, last);
            return;
        }

        text.color = GetPackageColor(node["fill"] as JObject, Color.black);
    }

    private static TMP_FontAsset GetMatchingTmpFont(string family, string style)
    {
        if (string.IsNullOrWhiteSpace(family))
            return null;

        string cacheKey = $"{family}|{style}";
        if (FontCache.TryGetValue(cacheKey, out TMP_FontAsset cached))
            return cached;

        TMP_FontAsset importedTmp = FindImportedTmpFont(family, style);
        if (importedTmp != null)
        {
            FontCache[cacheKey] = importedTmp;
            return importedTmp;
        }

        Font importedFont = FindImportedFont(family, style);
        if (importedFont != null)
        {
            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(importedFont);
            asset.name = $"{importedFont.name} TMP";
            FontCache[cacheKey] = asset;
            return asset;
        }

        try
        {
            Font font = Font.CreateDynamicFontFromOSFont(GetFontLookupNames(family, style), 90);
            if (font == null)
                return null;

            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(font);
            asset.name = $"{font.name} TMP";
            FontCache[cacheKey] = asset;
            return asset;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Could not load Figma font '{family} {style}' from OS fonts: {ex.Message}");
            FontCache[cacheKey] = null;
            return null;
        }
    }

    private static FontStyles GetSyntheticFontStyle(string requestedStyle, TMP_FontAsset fontAsset)
    {
        bool wantsBold = WantsSyntheticBold(requestedStyle);
        bool wantsItalic = WantsSyntheticItalic(requestedStyle);

        FontStyles style = FontStyles.Normal;
        if (wantsBold && !FontAssetProvidesBoldStyle(fontAsset, requestedStyle))
            style |= FontStyles.Bold;
        if (wantsItalic && !FontAssetProvidesItalicStyle(fontAsset, requestedStyle))
            style |= FontStyles.Italic;

        return style;
    }

    private static bool FontAssetProvidesBoldStyle(TMP_FontAsset fontAsset, string requestedStyle)
    {
        if (fontAsset == null || string.IsNullOrWhiteSpace(requestedStyle))
            return false;

        string requested = NormalizeFontName(requestedStyle);
        if (string.IsNullOrEmpty(requested))
            return true;

        string assetName = NormalizeFontName(fontAsset.name);
        return assetName.Contains(requested) ||
               assetName.Contains("bold") ||
               assetName.Contains("black") ||
               assetName.Contains("heavy");
    }

    private static bool FontAssetProvidesItalicStyle(TMP_FontAsset fontAsset, string requestedStyle)
    {
        if (fontAsset == null || string.IsNullOrWhiteSpace(requestedStyle))
            return false;

        string requested = NormalizeFontName(requestedStyle);
        if (string.IsNullOrEmpty(requested))
            return true;

        string assetName = NormalizeFontName(fontAsset.name);
        return assetName.Contains(requested) ||
               assetName.Contains("italic") ||
               assetName.Contains("oblique");
    }

    private static bool WantsSyntheticBold(string style)
    {
        string value = (style ?? "").ToLowerInvariant();
        return value.Contains("bold") || value.Contains("black") || value.Contains("heavy");
    }

    private static bool WantsSyntheticItalic(string style)
    {
        string value = (style ?? "").ToLowerInvariant();
        return value.Contains("italic") || value.Contains("oblique");
    }

    private static TMP_FontAsset FindImportedTmpFont(string family, string style)
    {
        string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");
        TMP_FontAsset best = null;
        int bestScore = 0;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            int score = GetFontMatchScore(asset != null ? asset.name : null, family, style);
            if (score > bestScore)
            {
                best = asset;
                bestScore = score;
            }
        }

        return bestScore > 0 ? best : null;
    }

    private static Font FindImportedFont(string family, string style)
    {
        string[] guids = AssetDatabase.FindAssets("t:Font");
        Font best = null;
        int bestScore = 0;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var font = AssetDatabase.LoadAssetAtPath<Font>(path);
            int score = GetFontMatchScore(font != null ? font.name : null, family, style);
            if (score > bestScore)
            {
                best = font;
                bestScore = score;
            }
        }

        return bestScore > 0 ? best : null;
    }

    private static int GetFontMatchScore(string candidateName, string family, string style)
    {
        if (string.IsNullOrWhiteSpace(candidateName) || string.IsNullOrWhiteSpace(family))
            return 0;

        string candidate = NormalizeFontName(candidateName);
        string familyName = NormalizeFontName(family);
        string styleName = NormalizeFontName(style);

        if (candidate == familyName || (!string.IsNullOrEmpty(styleName) && candidate == $"{familyName}{styleName}"))
            return 100;

        if (candidate.Contains(familyName) && !string.IsNullOrEmpty(styleName) && candidate.Contains(styleName))
            return 90;

        if (candidate.Contains(familyName))
            return 70;

        return 0;
    }

    private static string NormalizeFontName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return value
            .ToLowerInvariant()
            .Replace(" ", "")
            .Replace("-", "")
            .Replace("_", "")
            .Replace("regular", "")
            .Replace("sdf", "")
            .Replace("tmp", "")
            .Trim();
    }

    private static string[] GetFontLookupNames(string family, string style)
    {
        var names = new List<string>();
        string trimmedFamily = family.Trim();
        string trimmedStyle = (style ?? "").Trim();

        if (!string.IsNullOrEmpty(trimmedStyle) && !trimmedStyle.Equals("Regular", StringComparison.OrdinalIgnoreCase))
            names.Add($"{trimmedFamily} {trimmedStyle}");

        names.Add(trimmedFamily);
        names.Add($"{trimmedFamily} Regular");
        return names.ToArray();
    }

    private static List<JObject> GetImportRoots(JToken token)
    {
        var roots = new List<JObject>();

        if (token is JArray array)
        {
            foreach (var child in array)
            {
                if (child is JObject childObject && IsUsableNode(childObject))
                    roots.Add(childObject);
            }

            if (roots.Count > 0)
                return roots;
        }

        JObject preferred = FindPreferredRoot(token);
        if (preferred != null)
        {
            roots.Add(preferred);
            return roots;
        }

        JObject fallback = FindAnyUsableNode(token);
        if (fallback != null)
            roots.Add(fallback);

        return roots;
    }

    private static JObject FindPreferredRoot(JToken token)
    {
        if (token is JObject obj)
        {
            string type = ((string)obj["type"] ?? "").ToUpperInvariant();
            if (ContainerTypes.Contains(type) && HasSize(obj))
                return obj;

            foreach (var property in obj.Properties())
            {
                JObject found = FindPreferredRoot(property.Value);
                if (found != null) return found;
            }
        }
        else if (token is JArray array)
        {
            foreach (var child in array)
            {
                JObject found = FindPreferredRoot(child);
                if (found != null) return found;
            }
        }

        return null;
    }

    private static JObject FindAnyUsableNode(JToken token)
    {
        if (token is JObject obj)
        {
            if (IsUsableNode(obj))
                return obj;

            foreach (var property in obj.Properties())
            {
                JObject found = FindAnyUsableNode(property.Value);
                if (found != null) return found;
            }
        }
        else if (token is JArray array)
        {
            foreach (var child in array)
            {
                JObject found = FindAnyUsableNode(child);
                if (found != null) return found;
            }
        }

        return null;
    }

    private static bool IsUsableNode(JObject node)
    {
        return !IsHidden(node) && HasSize(node) && node["type"] != null;
    }

    private static bool IsHidden(JObject node)
    {
        return node["visible"] != null && node["visible"].Type == JTokenType.Boolean && !(bool)node["visible"];
    }

    private static bool HasSize(JObject node)
    {
        Rect box = GetBox(node);
        return box.width > 0f && box.height > 0f;
    }

    private static Rect GetUnionBox(List<JObject> nodes)
    {
        Rect result = GetBox(nodes[0]);
        for (int i = 1; i < nodes.Count; i++)
            result = Union(result, GetBox(nodes[i]));
        return result;
    }

    private static Rect Union(Rect a, Rect b)
    {
        float xMin = Mathf.Min(a.xMin, b.xMin);
        float yMin = Mathf.Min(a.yMin, b.yMin);
        float xMax = Mathf.Max(a.xMax, b.xMax);
        float yMax = Mathf.Max(a.yMax, b.yMax);
        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    private static Rect GetBox(JObject node)
    {
        JToken box = node["absoluteBoundingBox"] ?? node["absoluteRenderBounds"];
        if (box is JObject boxObject)
        {
            return new Rect(
                GetFloat(boxObject, "x", 0f),
                GetFloat(boxObject, "y", 0f),
                GetFloat(boxObject, "width", 100f),
                GetFloat(boxObject, "height", 100f));
        }

        return new Rect(
            GetFloat(node, "x", 0f),
            GetFloat(node, "y", 0f),
            GetFloat(node, "width", 100f),
            GetFloat(node, "height", 100f));
    }

    private static Rect GetRenderBox(JObject node)
    {
        if (node["renderBounds"] is JObject renderBounds)
        {
            return new Rect(
                GetFloat(renderBounds, "x", 0f),
                GetFloat(renderBounds, "y", 0f),
                GetFloat(renderBounds, "width", 100f),
                GetFloat(renderBounds, "height", 100f));
        }

        return GetBox(node);
    }

    private static void ApplyRotation(JObject node, RectTransform rect)
    {
        float rotation = GetFloat(node, "rotation", 0f);
        if (Mathf.Abs(rotation) > 0.001f)
            rect.localEulerAngles = new Vector3(0f, 0f, -rotation);
    }

    private static bool TryGetFill(JObject node, out Color color)
    {
        color = Color.white;
        if (!(node["fills"] is JArray fills)) return false;

        foreach (var fillToken in fills)
        {
            if (!(fillToken is JObject fill)) continue;
            if (((string)fill["type"] ?? "").ToUpperInvariant() != "SOLID") continue;
            if (fill["visible"] != null && fill["visible"].Type == JTokenType.Boolean && !(bool)fill["visible"]) continue;

            JObject source = fill["color"] as JObject;
            if (source == null) continue;

            float alpha = GetFloat(source, "a", 1f) * GetFloat(fill, "opacity", 1f);
            color = new Color(GetFloat(source, "r", 1f), GetFloat(source, "g", 1f), GetFloat(source, "b", 1f), alpha);
            return alpha > 0f;
        }

        return false;
    }

    private static Sprite LoadSpriteForNode(string nodeName)
    {
        string fileName = Sanitize(nodeName) + ".png";
        string[] candidates =
        {
            $"Assets/Sprites/{fileName}",
            $"Assets/FigmaSprites/{fileName}",
            $"Assets/Resources/{fileName}"
        };

        foreach (string candidate in candidates)
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(candidate);
            if (sprite != null)
                return sprite;
        }

        return null;
    }

    private static Sprite CreateSpriteFromEmbeddedImage(JObject node, string nodeName)
    {
        byte[] bytes = GetEmbeddedImageBytes(node);
        if (bytes == null || bytes.Length == 0)
            return null;

        string assetPath = GetSharedSpritePath(bytes);
        WriteSharedSpriteIfNeeded(assetPath, bytes);
        EnsureSpriteImporter(assetPath);
        return AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
    }

    private static Sprite CreateSpriteFromBase64(string encoded, string folder, string assetPath)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Could not decode package asset {assetPath}: {ex.Message}");
            return null;
        }

        string unityPath = GetPackageSpritePath(bytes, folder, assetPath);
        WriteSharedSpriteIfNeeded(unityPath, bytes);
        EnsureSpriteImporter(unityPath);

        return AssetDatabase.LoadAssetAtPath<Sprite>(unityPath);
    }

    private static string GetPackageSpritePath(byte[] bytes, string folder, string assetPath)
    {
        string fingerprint = GetVisualFingerprint(bytes);
        if (CurrentImportContext != null &&
            CurrentImportContext.AssetFrameUseCount.TryGetValue(fingerprint, out int frameUseCount) &&
            frameUseCount > 1 &&
            !string.IsNullOrWhiteSpace(CurrentImportContext.CommonAssetFolder))
        {
            Directory.CreateDirectory(CurrentImportContext.CommonAssetFolder);
            return $"{CurrentImportContext.CommonAssetFolder}/figma_{fingerprint}.png";
        }

        if (string.IsNullOrWhiteSpace(folder))
            return GetSharedSpritePath(bytes);

        Directory.CreateDirectory(folder);
        string sourceName = Path.GetFileNameWithoutExtension(assetPath ?? "figma_asset");
        string safeName = Sanitize(sourceName);
        if (safeName.Length > 42)
            safeName = safeName.Substring(0, 42);

        return $"{folder}/figma_{fingerprint}_{safeName}.png";
    }

    private static string GetSharedSpritePath(byte[] bytes)
    {
        const string folder = "Assets/FigmaSprites/_Shared";
        Directory.CreateDirectory(folder);
        return $"{folder}/figma_{GetVisualFingerprint(bytes)}.png";
    }

    private static void WriteSharedSpriteIfNeeded(string unityPath, byte[] bytes)
    {
        if (!File.Exists(unityPath))
        {
            File.WriteAllBytes(unityPath, bytes);
            AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceUpdate);
            return;
        }

        AssetDatabase.ImportAsset(unityPath);
    }

    private static void EnsureSpriteImporter(string unityPath)
    {
        var importer = AssetImporter.GetAtPath(unityPath) as TextureImporter;
        if (importer == null)
            return;

        bool changed = false;
        if (importer.textureType != TextureImporterType.Sprite)
        {
            importer.textureType = TextureImporterType.Sprite;
            changed = true;
        }

        if (importer.spriteImportMode != SpriteImportMode.Single)
        {
            importer.spriteImportMode = SpriteImportMode.Single;
            changed = true;
        }

        if (!importer.alphaIsTransparency)
        {
            importer.alphaIsTransparency = true;
            changed = true;
        }

        if (importer.mipmapEnabled)
        {
            importer.mipmapEnabled = false;
            changed = true;
        }

        if (!importer.isReadable)
        {
            importer.isReadable = true;
            changed = true;
        }

        if (changed)
            importer.SaveAndReimport();
    }

    private static string GetContentHash(byte[] bytes)
    {
        using (var sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
        }
    }

    private static string GetVisualFingerprint(byte[] bytes)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!texture.LoadImage(bytes))
                return GetContentHash(bytes);

            Color32[] pixels = texture.GetPixels32();
            int minX = texture.width;
            int minY = texture.height;
            int maxX = -1;
            int maxY = -1;
            int visible = 0;

            for (int y = 0; y < texture.height; y++)
            {
                for (int x = 0; x < texture.width; x++)
                {
                    Color32 pixel = pixels[y * texture.width + x];
                    if (pixel.a <= 8)
                        continue;

                    visible++;
                    minX = Mathf.Min(minX, x);
                    minY = Mathf.Min(minY, y);
                    maxX = Mathf.Max(maxX, x);
                    maxY = Mathf.Max(maxY, y);
                }
            }

            if (visible == 0 || maxX < minX || maxY < minY)
                return GetContentHash(bytes);

            int contentWidth = Mathf.Max(1, maxX - minX + 1);
            int contentHeight = Mathf.Max(1, maxY - minY + 1);
            int aspectBucket = Mathf.RoundToInt((contentWidth / (float)contentHeight) * 64f);
            int coverageBucket = Mathf.RoundToInt((visible / (float)(contentWidth * contentHeight)) * 64f);

            unchecked
            {
                ulong hash = 14695981039346656037UL;
                AddHash(ref hash, (byte)(aspectBucket & 0xff));
                AddHash(ref hash, (byte)(coverageBucket & 0xff));

                const int samples = 16;
                for (int sy = 0; sy < samples; sy++)
                {
                    for (int sx = 0; sx < samples; sx++)
                    {
                        float u = samples == 1 ? 0.5f : sx / (float)(samples - 1);
                        float v = samples == 1 ? 0.5f : sy / (float)(samples - 1);
                        int x = Mathf.Clamp(Mathf.RoundToInt(minX + u * (contentWidth - 1)), 0, texture.width - 1);
                        int y = Mathf.Clamp(Mathf.RoundToInt(minY + v * (contentHeight - 1)), 0, texture.height - 1);
                        Color32 pixel = pixels[y * texture.width + x];

                        AddHash(ref hash, QuantizeByte(pixel.r));
                        AddHash(ref hash, QuantizeByte(pixel.g));
                        AddHash(ref hash, QuantizeByte(pixel.b));
                        AddHash(ref hash, QuantizeByte(pixel.a));
                    }
                }

                return hash.ToString("x16");
            }
        }
        catch
        {
            return GetContentHash(bytes);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    private static byte QuantizeByte(byte value)
    {
        return (byte)(value / 32);
    }

    private static void AddHash(ref ulong hash, byte value)
    {
        unchecked
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
    }

    private static Sprite GetCircleMaskSprite()
    {
        const string folder = "Assets/FigmaSprites";
        const string assetPath = "Assets/FigmaSprites/__circle_mask.png";
        Directory.CreateDirectory(folder);

        if (!File.Exists(assetPath))
        {
            const int size = 256;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            float radius = size * 0.5f - 1f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center);
                    texture.SetPixel(x, y, distance <= radius ? Color.white : Color.clear);
                }
            }

            texture.Apply();
            File.WriteAllBytes(assetPath, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer != null && importer.textureType != TextureImporterType.Sprite)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
    }

    private static Color GetPackageColor(JObject color, Color fallback)
    {
        if (color == null)
            return fallback;

        return new Color(
            GetFloat(color, "r", fallback.r),
            GetFloat(color, "g", fallback.g),
            GetFloat(color, "b", fallback.b),
            GetFloat(color, "a", fallback.a));
    }

    private static byte[] GetEmbeddedImageBytes(JObject node)
    {
        if (!(node["fills"] is JArray fills))
            return null;

        foreach (var fillToken in fills)
        {
            if (!(fillToken is JObject fill)) continue;
            if (((string)fill["type"] ?? "").ToUpperInvariant() != "IMAGE") continue;

            JToken bytesToken = fill["intArr"] ?? fill["bytes"];
            byte[] bytes = ReadByteArray(bytesToken);
            if (bytes != null && bytes.Length > 0)
                return bytes;
        }

        return null;
    }

    private static byte[] ReadByteArray(JToken token)
    {
        if (token is JArray array)
        {
            var bytes = new byte[array.Count];
            for (int i = 0; i < array.Count; i++)
                bytes[i] = (byte)Mathf.Clamp((int)array[i], 0, 255);
            return bytes;
        }

        if (token is JObject obj)
        {
            var indexedBytes = new List<KeyValuePair<int, byte>>();
            foreach (var property in obj.Properties())
            {
                if (!int.TryParse(property.Name, out int index))
                    continue;

                indexedBytes.Add(new KeyValuePair<int, byte>(
                    index,
                    (byte)Mathf.Clamp((int)property.Value, 0, 255)));
            }

            if (indexedBytes.Count == 0)
                return null;

            indexedBytes.Sort((left, right) => left.Key.CompareTo(right.Key));
            var bytes = new byte[indexedBytes.Count];
            for (int i = 0; i < indexedBytes.Count; i++)
                bytes[i] = indexedBytes[i].Value;
            return bytes;
        }

        return null;
    }

    private static bool LooksLikeButton(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.Contains("button") || lower.Contains("btn") || lower.Contains("cta");
    }

    private static float GetFloat(JObject obj, string key, float fallback)
    {
        if (obj == null) return fallback;
        JToken value = obj[key];
        return value != null && value.Type != JTokenType.Null ? (float)value : fallback;
    }

    private static string GetDefaultExportFolder()
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string projectExports = Path.Combine(downloads, "figma-to-json", "exports");
        return Directory.Exists(projectExports) ? projectExports : downloads;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "FigmaScreen";

        foreach (char ch in Path.GetInvalidFileNameChars())
            value = value.Replace(ch, '_');

        return value.Trim();
    }

    private static string SanitizeObjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Figma Node";

        return value.Replace("/", "_").Trim();
    }
}
#endif
