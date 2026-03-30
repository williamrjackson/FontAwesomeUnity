using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Wrj.FontAwesome
{
    public sealed class FontAwesomeIconBrowserWindow : EditorWindow
    {
        private const string WindowTitle = "Font Awesome Icon Browser";
        private const float TileWidth = 92f;
        private const float TileHeight = 96f;
        private const string SecondaryLayerObjectName = "FA Secondary Layer";
        private const int FontAssetGenerationRetryCount = 10;

        private const string JsonStringPropertyPattern = "\"{0}\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"";
        private const string JsonFamilyStylePattern = "\"family\"\\s*:\\s*\"(?<family>(?:\\\\.|[^\"\\\\])*)\"\\s*,\\s*\"style\"\\s*:\\s*\"(?<style>(?:\\\\.|[^\"\\\\])*)\"";

        private readonly List<FontAwesomeIconEntry> allIcons = new();
        private readonly List<FontAwesomeFilteredIconEntry> filteredIcons = new();
        private readonly List<IconSetDefinition> iconSetDefinitions = new();
        private readonly HashSet<string> pendingAutoFontAssetGeneration = new();

        private IconSetDefinition activeIconSet;
        private Vector2 scrollPosition;
        private string searchText = string.Empty;
        private GUIStyle iconGlyphStyle;
        private GUIStyle iconLabelStyle;
        private GUIStyle secondaryIconGlyphStyle;
        private GUIStyle styleFilterButtonStyle;
        private bool iconsLoaded;
        private readonly Dictionary<string, bool> styleFilterStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<TMP_FontAsset>> fontAssetsByStyle = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> availableStyleKeys = new();

        [MenuItem("Tools/Font Awesome Icon Browser...")]
        private static void OpenWindow()
        {
            FontAwesomeIconBrowserWindow window = GetWindow<FontAwesomeIconBrowserWindow>();
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(420f, 320f);
            window.Show();
        }

        private void OnEnable()
        {
            EnsureIconSetDefinitions();
            UpdateActiveIconSet();
            EnsureFontAssetsForConfiguredMetadata();
            RefreshFontAssetsForActiveIconSet();
            EnsureIconsLoaded();
            EnsureStyleFilters(true);
            RebuildFilteredIcons();
        }

        private void OnGUI()
        {
            EnsureIconSetDefinitions();
            UpdateActiveIconSet();
            EnsureFontAssetsForConfiguredMetadata();
            RefreshFontAssetsForActiveIconSet();
            EnsureIconsLoaded();
            EnsureStyleFilters();
            EnsureStyles();

            DrawToolbar();
            EditorGUILayout.Space(6f);
            DrawMetadataSettings();
            EditorGUILayout.Space(6f);
            DrawStyleFilters();
            EditorGUILayout.Space(6f);
            DrawResultsSummary();
            EditorGUILayout.Space(4f);

            if (!iconsLoaded)
            {
                string metadataPath = activeIconSet != null ? activeIconSet.IconsMetadataPath : "the configured icon metadata";
                EditorGUILayout.HelpBox($"Could not load icon metadata at '{metadataPath}'.", MessageType.Error);
                return;
            }

            if (availableStyleKeys.Count == 0)
            {
                EditorGUILayout.HelpBox("No styles were discovered from the configured metadata.", MessageType.Warning);
                return;
            }

            if (activeIconSet == null)
            {
                EditorGUILayout.HelpBox(
                    "No registered icon set is active. Configure the metadata path to continue.",
                    MessageType.Warning);
                return;
            }

            if (!HasAnyResolvedFontAssets())
            {
                EditorGUILayout.HelpBox(
                    $"No matching {activeIconSet.DisplayName} TMP SDF assets were found for the discovered styles. You can still browse icons, but placing them requires generated SDF assets.",
                    MessageType.Warning);
            }

            if (filteredIcons.Count == 0)
            {
                EditorGUILayout.HelpBox("No icons matched the current search/style filters.", MessageType.Info);
                return;
            }

            DrawIconGrid();
            EditorGUILayout.Space(6f);
            DrawSelectionHint();
        }

        private void DrawToolbar()
        {
            EditorGUI.BeginChangeCheck();
            string newSearchText = EditorGUILayout.TextField("Search", searchText);
            if (EditorGUI.EndChangeCheck())
            {
                searchText = newSearchText ?? string.Empty;
                RebuildFilteredIcons();
            }
        }

        private void DrawStyleFilters()
        {
            if (availableStyleKeys.Count == 0)
            {
                return;
            }

            EditorGUILayout.LabelField("Style Filters", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("All", EditorStyles.miniButtonLeft, GUILayout.Width(56f)))
            {
                SetAllStyleFilters(true);
            }

            if (GUILayout.Button("None", EditorStyles.miniButtonMid, GUILayout.Width(56f)))
            {
                SetAllStyleFilters(false);
            }

            GUILayout.Space(8f);
            DrawStyleFilterButtons();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            string styleSummary = GetEnabledStyleSdfSummary();
            if (!string.IsNullOrWhiteSpace(styleSummary))
            {
                EditorGUILayout.LabelField(styleSummary, EditorStyles.miniLabel);
            }
        }

        private void DrawResultsSummary()
        {
            if (!iconsLoaded)
            {
                return;
            }

            int enabledStyleCount = availableStyleKeys.Count(styleKey => IsStyleEnabled(styleKey));
            EditorGUILayout.LabelField($"Results: {filteredIcons.Count}   Styles On: {enabledStyleCount}/{availableStyleKeys.Count}", EditorStyles.miniLabel);
        }

        private void DrawStyleFilterButtons()
        {
            float availableWidth = Mathf.Max(position.width - 180f, 120f);
            float currentRowWidth = 0f;
            bool rowOpen = false;

            foreach (string styleKey in availableStyleKeys)
            {
                string label = ToDisplayLabel(styleKey);
                Vector2 size = styleFilterButtonStyle.CalcSize(new GUIContent(label));
                float buttonWidth = Mathf.Max(56f, size.x + 12f);

                if (!rowOpen || currentRowWidth + buttonWidth > availableWidth)
                {
                    if (rowOpen)
                    {
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUILayout.BeginHorizontal();
                    rowOpen = true;
                    currentRowWidth = 0f;
                }

                bool isEnabled = styleFilterStates.TryGetValue(styleKey, out bool currentValue) && currentValue;
                bool nextValue = GUILayout.Toggle(isEnabled, label, styleFilterButtonStyle, GUILayout.Width(buttonWidth));
                if (nextValue != isEnabled)
                {
                    styleFilterStates[styleKey] = nextValue;
                    RebuildFilteredIcons();
                }

                currentRowWidth += buttonWidth + 4f;
            }

            if (rowOpen)
            {
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawMetadataSettings()
        {
            IconSetDefinition settingsIconSet = activeIconSet ?? GetPreferredSettingsIconSet();
            if (settingsIconSet == null)
            {
                return;
            }

            EditorGUILayout.LabelField($"{settingsIconSet.DisplayName} Metadata", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            string metadataPath = EditorGUILayout.DelayedTextField("Metadata Path", settingsIconSet.IconsMetadataPath);
            if (EditorGUI.EndChangeCheck())
            {
                settingsIconSet.SetMetadataPathOverride(metadataPath);
                EnsureFontAssetsForIconSet(settingsIconSet);
                UpdateActiveIconSet();
                RefreshFontAssetsForActiveIconSet();
                EnsureIconsLoaded(true);
                EnsureStyleFilters(true);
                RebuildFilteredIcons();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Auto-Find"))
            {
                if (settingsIconSet.TryAutoConfigureMetadataPath())
                {
                    EnsureFontAssetsForIconSet(settingsIconSet);
                    UpdateActiveIconSet();
                    RefreshFontAssetsForActiveIconSet();
                    EnsureIconsLoaded(true);
                    EnsureStyleFilters(true);
                    RebuildFilteredIcons();
                }
            }

            if (GUILayout.Button("Reset"))
            {
                settingsIconSet.ClearMetadataPathOverride();
                EnsureFontAssetsForIconSet(settingsIconSet);
                UpdateActiveIconSet();
                RefreshFontAssetsForActiveIconSet();
                EnsureIconsLoaded(true);
                EnsureStyleFilters(true);
                RebuildFilteredIcons();
            }

            EditorGUILayout.EndHorizontal();

            if (settingsIconSet.SupportsPackageInstall && !settingsIconSet.HasInstalledContent())
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(settingsIconSet.InstallButtonLabel))
                {
                    InstallIconSetPackage(settingsIconSet);
                }

                EditorGUILayout.LabelField(settingsIconSet.InstallSummary, EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawSelectionHint()
        {
            TMP_Text selectedText = GetSelectedTextComponent();
            string iconSetName = activeIconSet != null ? activeIconSet.DisplayName : "icon";
            string message = selectedText != null
                ? $"Using selected TMP component on '{selectedText.gameObject.name}' with the {iconSetName} browser."
                : $"Clicking an icon will create a new TMP object if no TMP component is selected.";

            EditorGUILayout.HelpBox(message, MessageType.None);
        }

        private void DrawIconGrid()
        {
            float width = Mathf.Max(position.width - 24f, TileWidth);
            int columns = Mathf.Max(1, Mathf.FloorToInt(width / TileWidth));
            int rows = Mathf.CeilToInt(filteredIcons.Count / (float)columns);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            for (int row = 0; row < rows; row++)
            {
                EditorGUILayout.BeginHorizontal();

                for (int column = 0; column < columns; column++)
                {
                    int index = row * columns + column;
                    if (index >= filteredIcons.Count)
                    {
                        GUILayout.FlexibleSpace();
                        continue;
                    }

                    DrawIconButton(filteredIcons[index]);
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawIconButton(FontAwesomeFilteredIconEntry filteredIcon)
        {
            FontAwesomeIconEntry icon = filteredIcon.Icon;
            Rect tileRect = GUILayoutUtility.GetRect(TileWidth, TileHeight, GUILayout.Width(TileWidth), GUILayout.Height(TileHeight));
            GUI.Box(tileRect, GUIContent.none);
            Rect buttonRect = new(tileRect.x + 4f, tileRect.y + 4f, tileRect.width - 8f, tileRect.height - 8f);
            TMP_FontAsset previewFontAsset = ResolveFontAssetForIcon(icon, null, filteredIcon.StyleKey);
            Font previewFont = ResolvePreviewFont(filteredIcon, previewFontAsset);
            string tooltip = $"{icon.Name}\n{icon.Label}\n{ToDisplayLabel(filteredIcon.StyleKey)}\nU+{icon.Unicode:X4}\n{GetSdfSummaryForStyle(filteredIcon.StyleKey)}";
            bool glyphAvailable = FontHasGlyph(previewFontAsset, icon.Unicode);
            Event currentEvent = Event.current;

            // Color previousColor = GUI.color;
            // if (!glyphAvailable)
            // {
            //     GUI.color = new Color(previousColor.r, previousColor.g, previousColor.b, 1f);
            // }

            GUI.Label(buttonRect, new GUIContent(string.Empty, tooltip), GUIStyle.none);

            if (currentEvent.type == EventType.MouseDown &&
                currentEvent.button == 0 &&
                buttonRect.Contains(currentEvent.mousePosition))
            {
                GUI.FocusControl(string.Empty);
                GUIUtility.keyboardControl = 0;
                ApplyIcon(filteredIcon);
                currentEvent.Use();
            }

            iconGlyphStyle.font = previewFont;
            secondaryIconGlyphStyle.font = iconGlyphStyle.font;

            Rect glyphRect = new(buttonRect.x, buttonRect.y + 2f, buttonRect.width, 34f);
            if (iconGlyphStyle.font == null)
            {
                GUI.Label(glyphRect, "?", iconGlyphStyle);
            }
            else if (ShouldRenderAsDuotone(icon, previewFontAsset))
            {
                GUI.Label(glyphRect, icon.Glyph, iconGlyphStyle);

                if (icon.HasSecondaryGlyph)
                {
                    GUI.Label(glyphRect, icon.SecondaryGlyph, secondaryIconGlyphStyle);
                }
            }
            else
            {
                GUI.Label(glyphRect, icon.Glyph, iconGlyphStyle);
            }

            Rect nameRect = new(buttonRect.x + 2f, buttonRect.y + 38f, buttonRect.width - 4f, 16f);
            GUI.Label(nameRect, icon.Name, iconLabelStyle);

            Rect codeRect = new(buttonRect.x + 2f, buttonRect.y + 55f, buttonRect.width - 4f, 14f);
            GUI.Label(codeRect, ToDisplayLabel(filteredIcon.StyleKey), iconLabelStyle);

            Rect sdfRect = new(buttonRect.x + 2f, buttonRect.y + 69f, buttonRect.width - 4f, 18f);
            GUI.Label(sdfRect, GetSdfSummaryForStyle(filteredIcon.StyleKey), iconLabelStyle);

            // GUI.color = previousColor;
        }

        private void EnsureStyles()
        {
            Color primaryTextColor = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.82f)
                : new Color(0.08f, 0.08f, 0.08f, 0.92f);
            Color primaryHoverColor = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 1f)
                : new Color(0f, 0f, 0f, 1f);
            Color secondaryTextColor = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.3f)
                : new Color(0.08f, 0.08f, 0.08f, 0.35f);
            Color secondaryHoverColor = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.45f)
                : new Color(0.08f, 0.08f, 0.08f, 0.5f);
            Color labelColor = EditorGUIUtility.isProSkin
                ? new Color(0.86f, 0.86f, 0.86f, 0.95f)
                : new Color(0.12f, 0.12f, 0.12f, 0.95f);

            if (iconGlyphStyle == null)
            {
                iconGlyphStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 24,
                    clipping = TextClipping.Clip
                };
            }

            iconGlyphStyle.normal.textColor = primaryTextColor;
            iconGlyphStyle.hover.textColor = primaryHoverColor;

            if (iconLabelStyle == null)
            {
                iconLabelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
            }

            iconLabelStyle.normal.textColor = labelColor;
            iconLabelStyle.hover.textColor = labelColor;

            if (secondaryIconGlyphStyle == null)
            {
                secondaryIconGlyphStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 24,
                    clipping = TextClipping.Clip
                };
            }

            secondaryIconGlyphStyle.normal.textColor = secondaryTextColor;
            secondaryIconGlyphStyle.hover.textColor = secondaryHoverColor;

            if (styleFilterButtonStyle == null)
            {
                styleFilterButtonStyle = new GUIStyle(EditorStyles.miniButton)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fixedHeight = 20f,
                    padding = new RectOffset(8, 8, 3, 3),
                    margin = new RectOffset(2, 2, 1, 1)
                };
            }
        }

        private void EnsureFontAssetsForConfiguredMetadata()
        {
            foreach (IconSetDefinition iconSetDefinition in iconSetDefinitions)
            {
                EnsureFontAssetsForIconSet(iconSetDefinition);
            }
        }

        private void EnsureFontAssetsForIconSet(IconSetDefinition iconSet)
        {
            if (iconSet == null || !iconSet.NeedsFontAssetGeneration())
            {
                return;
            }

            string generationKey = iconSet.GetFontAssetGenerationKey();
            if (!string.IsNullOrWhiteSpace(generationKey) &&
                pendingAutoFontAssetGeneration.Contains(generationKey))
            {
                return;
            }

            FontAssetCreationStatus creationStatus = CreateInstalledFontAssets(iconSet);
            if (creationStatus == FontAssetCreationStatus.Completed)
            {
                if (!string.IsNullOrWhiteSpace(generationKey))
                {
                    pendingAutoFontAssetGeneration.Remove(generationKey);
                }

                AssetDatabase.Refresh();
                return;
            }

            if (string.IsNullOrWhiteSpace(generationKey))
            {
                return;
            }

            pendingAutoFontAssetGeneration.Add(generationKey);
            EditorApplication.delayCall += () =>
            {
                pendingAutoFontAssetGeneration.Remove(generationKey);
                EnsureFontAssetsForIconSet(iconSet);
            };
        }

        private void EnsureIconsLoaded(bool forceReload = false)
        {
            if (iconsLoaded && !forceReload)
            {
                return;
            }

            allIcons.Clear();
            iconsLoaded = false;

            if (activeIconSet == null)
            {
                return;
            }

            TextAsset iconMetadata = AssetDatabase.LoadAssetAtPath<TextAsset>(activeIconSet.IconsMetadataPath);
            if (iconMetadata == null)
            {
                return;
            }

            ParseIconMetadata(iconMetadata.text, allIcons, activeIconSet);
            allIcons.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
            iconsLoaded = allIcons.Count > 0;
        }

        private void EnsureStyleFilters(bool forceReset = false)
        {
            if (!iconsLoaded)
            {
                availableStyleKeys.Clear();
                if (forceReset)
                {
                    styleFilterStates.Clear();
                }

                return;
            }

            HashSet<string> discoveredStyleKeys = new(StringComparer.OrdinalIgnoreCase);
            foreach (FontAwesomeIconEntry icon in allIcons)
            {
                foreach (string styleKey in icon.GetStyleKeys())
                {
                    discoveredStyleKeys.Add(styleKey);
                }
            }

            List<string> orderedStyleKeys = discoveredStyleKeys
                .OrderBy(GetStyleSortOrder)
                .ThenBy(ToDisplayLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();

            availableStyleKeys.Clear();
            availableStyleKeys.AddRange(orderedStyleKeys);

            if (forceReset)
            {
                styleFilterStates.Clear();
            }

            foreach (string styleKey in orderedStyleKeys)
            {
                if (!styleFilterStates.ContainsKey(styleKey))
                {
                    styleFilterStates[styleKey] = true;
                }
            }

            List<string> staleKeys = styleFilterStates.Keys
                .Where(key => !discoveredStyleKeys.Contains(key))
                .ToList();
            foreach (string staleKey in staleKeys)
            {
                styleFilterStates.Remove(staleKey);
            }
        }

        private void RebuildFilteredIcons()
        {
            filteredIcons.Clear();

            if (!iconsLoaded)
            {
                return;
            }

            string query = (searchText ?? string.Empty).Trim();
            bool hasQuery = !string.IsNullOrWhiteSpace(query);
            bool hasEnabledStyles = availableStyleKeys.Any(styleKey => IsStyleEnabled(styleKey));

            if (!hasEnabledStyles && availableStyleKeys.Count > 0)
            {
                foreach (string styleKey in availableStyleKeys)
                {
                    styleFilterStates[styleKey] = true;
                }

                hasEnabledStyles = true;
            }

            foreach (FontAwesomeIconEntry icon in allIcons)
            {
                if (hasQuery &&
                    icon.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    icon.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                foreach (string styleKey in availableStyleKeys)
                {
                    if (!IsStyleEnabled(styleKey) || !icon.SupportsStyleKey(styleKey))
                    {
                        continue;
                    }

                    filteredIcons.Add(new FontAwesomeFilteredIconEntry(icon, styleKey));
                }
            }

            scrollPosition = Vector2.zero;
            Repaint();
        }

        private void ApplyIcon(FontAwesomeFilteredIconEntry filteredIcon)
        {
            FontAwesomeIconEntry icon = filteredIcon.Icon;
            TMP_Text targetText = GetSelectedTextComponent();
            TMP_FontAsset resolvedFontAsset = ResolveFontAssetForIcon(icon, targetText, filteredIcon.StyleKey);
            if (resolvedFontAsset == null)
            {
                EditorUtility.DisplayDialog(
                    WindowTitle,
                    $"No TMP SDF asset was found for the {ToDisplayLabel(filteredIcon.StyleKey)} version of '{icon.DisplayName}'. Generate or import that SDF asset first.",
                    "OK");
                return;
            }

            if (ShouldRenderAsDuotone(icon, resolvedFontAsset))
            {
                ApplyDuotoneIcon(icon, targetText, resolvedFontAsset);
                return;
            }

            bool createdNewObject = targetText == null;
            if (targetText == null)
            {
                targetText = CreateTextObject();
            }

            if (targetText == null)
            {
                return;
            }

            bool shouldRenameObject = createdNewObject || ShouldRenameAutoNamedObject(targetText);

            Undo.RecordObject(targetText, "Assign Font Awesome Icon");
            EnsureGlyphAvailable(resolvedFontAsset, icon);
            targetText.font = resolvedFontAsset;
            targetText.text = icon.Glyph;
            DisableDuotoneSync(targetText);
            if (shouldRenameObject)
            {
                targetText.name = $"{icon.DisplayName} FA Icon";
            }

            if (targetText is TextMeshProUGUI uiText)
            {
                uiText.alignment = TextAlignmentOptions.Center;
                uiText.raycastTarget = false;
                if (uiText.rectTransform.sizeDelta == Vector2.zero)
                {
                    uiText.rectTransform.sizeDelta = new Vector2(120f, 120f);
                }
            }
            else if (targetText is TextMeshPro worldText)
            {
                worldText.alignment = TextAlignmentOptions.Center;
            }

            if (createdNewObject || Mathf.Approximately(targetText.fontSize, 0f))
            {
                targetText.fontSize = 12f;
            }

            EditorUtility.SetDirty(targetText);
            Selection.activeGameObject = targetText.gameObject;
            EditorGUIUtility.PingObject(targetText.gameObject);
        }

        private void ApplyDuotoneIcon(FontAwesomeIconEntry icon, TMP_Text primaryText, TMP_FontAsset fontAsset)
        {
            TMP_Text secondaryText;
            bool createdNewObject = primaryText == null;

            if (primaryText == null)
            {
                primaryText = CreateTextObject();
            }

            if (primaryText == null)
            {
                return;
            }

            secondaryText = GetOrCreateSecondaryLayer(primaryText);
            if (secondaryText == null)
            {
                return;
            }

            bool shouldRenameObject = createdNewObject || ShouldRenameAutoNamedObject(primaryText);

            EnsureGlyphAvailable(fontAsset, icon);
            if (icon.HasSecondaryGlyph)
            {
                EnsureGlyphAvailable(fontAsset, icon.SecondaryUnicode.Value);
            }

            Undo.RecordObject(primaryText, "Assign Font Awesome Duotone Icon");
            Undo.RecordObject(secondaryText, "Assign Font Awesome Duotone Icon");

            primaryText.font = fontAsset;
            primaryText.text = icon.Glyph;
            secondaryText.font = fontAsset;
            secondaryText.text = icon.SecondaryGlyph;

            if (shouldRenameObject)
            {
                primaryText.name = $"{icon.DisplayName} FA Icon";
            }

            ConfigureTextLayer(primaryText, createdNewObject, 1f);
            ConfigureTextLayer(secondaryText, createdNewObject, 0.25f);
            ConfigureDuotoneSync(primaryText, secondaryText);

            EditorUtility.SetDirty(primaryText);
            EditorUtility.SetDirty(secondaryText);
            Selection.activeGameObject = primaryText.gameObject;
            EditorGUIUtility.PingObject(primaryText.gameObject);
        }

        private void EnsureGlyphAvailable(TMP_FontAsset fontAsset, uint value)
        {
            if (fontAsset == null || FontHasGlyph(fontAsset, value))
            {
                return;
            }

            if (fontAsset.atlasPopulationMode == AtlasPopulationMode.Dynamic ||
                fontAsset.atlasPopulationMode == AtlasPopulationMode.DynamicOS)
            {
                string glyph = char.ConvertFromUtf32((int)value);
                fontAsset.TryAddCharacters(glyph, out string _);
                EditorUtility.SetDirty(fontAsset);
            }
        }


        private TMP_Text CreateTextObject()
        {
            GameObject selectedObject = Selection.activeGameObject;
            Transform selectedTransform = selectedObject != null ? selectedObject.transform : null;
            Canvas parentCanvas = selectedObject != null ? selectedObject.GetComponentInParent<Canvas>() : null;

            if (parentCanvas != null)
            {
                GameObject go = new("Font Awesome Icon", typeof(RectTransform), typeof(TextMeshProUGUI));
                Undo.RegisterCreatedObjectUndo(go, "Create Font Awesome UI Icon");

                Transform parent = selectedObject.GetComponent<RectTransform>() != null ? selectedTransform : parentCanvas.transform;
                Undo.SetTransformParent(go.transform, parent, "Parent Font Awesome UI Icon");

                RectTransform rectTransform = go.GetComponent<RectTransform>();
                rectTransform.localScale = Vector3.one;
                rectTransform.localRotation = Quaternion.identity;
                rectTransform.anchoredPosition = Vector2.zero;
                rectTransform.sizeDelta = new Vector2(120f, 120f);

                return go.GetComponent<TextMeshProUGUI>();
            }

            GameObject worldObject = new("Font Awesome Icon", typeof(TextMeshPro));
            Undo.RegisterCreatedObjectUndo(worldObject, "Create Font Awesome Icon");

            if (selectedTransform != null)
            {
                Undo.SetTransformParent(worldObject.transform, selectedTransform, "Parent Font Awesome Icon");
                worldObject.transform.localPosition = Vector3.zero;
                worldObject.transform.localRotation = Quaternion.identity;
            }
            else
            {
                worldObject.transform.position = Vector3.zero;
                worldObject.transform.rotation = Quaternion.identity;
            }

            worldObject.transform.localScale = Vector3.one;
            return worldObject.GetComponent<TextMeshPro>();
        }

        private TMP_Text GetSelectedTextComponent()
        {
            if (Selection.activeObject is TMP_Text selectedText)
            {
                return selectedText;
            }

            return Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<TMP_Text>()
                : null;
        }

        private TMP_Text GetOrCreateSecondaryLayer(TMP_Text primaryText)
        {
            Transform secondaryTransform = primaryText.transform.Find(SecondaryLayerObjectName);
            TMP_Text secondaryText = secondaryTransform != null ? secondaryTransform.GetComponent<TMP_Text>() : null;

            if (secondaryText != null)
            {
                return secondaryText;
            }

            if (primaryText is TextMeshProUGUI uiText)
            {
                GameObject go = new(SecondaryLayerObjectName, typeof(RectTransform), typeof(TextMeshProUGUI));
                Undo.RegisterCreatedObjectUndo(go, "Create Font Awesome Secondary Layer");
                Undo.SetTransformParent(go.transform, uiText.transform, "Parent Font Awesome Secondary Layer");

                RectTransform sourceRect = uiText.rectTransform;
                RectTransform targetRect = go.GetComponent<RectTransform>();
                targetRect.anchorMin = sourceRect.anchorMin;
                targetRect.anchorMax = sourceRect.anchorMax;
                targetRect.pivot = sourceRect.pivot;
                targetRect.anchoredPosition = Vector2.zero;
                targetRect.sizeDelta = sourceRect.sizeDelta;
                targetRect.localScale = Vector3.one;
                targetRect.localRotation = Quaternion.identity;

                return go.GetComponent<TextMeshProUGUI>();
            }

            GameObject worldObject = new(SecondaryLayerObjectName, typeof(TextMeshPro));
            Undo.RegisterCreatedObjectUndo(worldObject, "Create Font Awesome Secondary Layer");
            Undo.SetTransformParent(worldObject.transform, primaryText.transform, "Parent Font Awesome Secondary Layer");
            worldObject.transform.localPosition = Vector3.zero;
            worldObject.transform.localRotation = Quaternion.identity;
            worldObject.transform.localScale = Vector3.one;
            return worldObject.GetComponent<TextMeshPro>();
        }

        private void ConfigureDuotoneSync(TMP_Text primaryText, TMP_Text secondaryText)
        {
            FontAwesomeDuotoneSync sync = primaryText.GetComponent<FontAwesomeDuotoneSync>();
            if (sync == null)
            {
                sync = Undo.AddComponent<FontAwesomeDuotoneSync>(primaryText.gameObject);
                sync.SetSecondaryColor(secondaryText.color);
            }

            sync.SetSecondaryText(secondaryText);
            sync.SetSecondaryVisible(true);
            sync.SyncNow();
        }

        private static void DisableDuotoneSync(TMP_Text primaryText)
        {
            if (primaryText == null)
            {
                return;
            }

            FontAwesomeDuotoneSync sync = primaryText.GetComponent<FontAwesomeDuotoneSync>();
            if (sync == null)
            {
                return;
            }

            sync.SetSecondaryVisible(false);
            if (sync.SecondaryText != null)
            {
                sync.SecondaryText.text = string.Empty;
                EditorUtility.SetDirty(sync.SecondaryText);
            }

            sync.SyncNow();
            EditorUtility.SetDirty(sync);
        }

        private bool IconMatchesEnabledStyles(FontAwesomeIconEntry icon)
        {
            if (availableStyleKeys.Count == 0)
            {
                return false;
            }

            foreach (string styleKey in availableStyleKeys)
            {
                if (!styleFilterStates.TryGetValue(styleKey, out bool isEnabled) || !isEnabled)
                {
                    continue;
                }

                if (icon.SupportsStyleKey(styleKey))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasAnyResolvedFontAssets()
        {
            return fontAssetsByStyle.Values.Any(fontAssets => fontAssets.Count > 0);
        }

        private bool IsDuotoneFontAsset(TMP_FontAsset fontAsset)
        {
            return activeIconSet != null &&
                   fontAsset != null &&
                   activeIconSet.TryGetFamilyStyle(fontAsset, out _, out string style) &&
                   string.Equals(style, "duotone", StringComparison.OrdinalIgnoreCase);
        }

        private bool ShouldRenderAsDuotone(FontAwesomeIconEntry icon, TMP_FontAsset fontAsset)
        {
            return fontAsset != null && IsDuotoneFontAsset(fontAsset) && icon.HasSecondaryGlyph;
        }

        private void ConfigureTextLayer(TMP_Text targetText, bool createdNewObject, float alpha)
        {
            if (targetText is TextMeshProUGUI uiText)
            {
                uiText.alignment = TextAlignmentOptions.Center;
                uiText.raycastTarget = false;
                if (uiText.rectTransform.sizeDelta == Vector2.zero)
                {
                    uiText.rectTransform.sizeDelta = new Vector2(120f, 120f);
                }

                if (createdNewObject)
                {
                    uiText.enableAutoSizing = true;
                    uiText.fontSizeMax = 500f;
                }
            }
            else if (targetText is TextMeshPro worldText)
            {
                worldText.alignment = TextAlignmentOptions.Center;
            }

            if (createdNewObject || Mathf.Approximately(targetText.fontSize, 0f))
            {
                targetText.fontSize = 12f;
            }

            Color color = targetText.color;
            color.a = alpha;
            targetText.color = color;
        }

        private bool ShouldRenameAutoNamedObject(TMP_Text targetText)
        {
            if (targetText == null)
            {
                return false;
            }

            FontAwesomeIconEntry previousIcon = FindIconByGlyph(targetText.text);
            if (!previousIcon.IsValid)
            {
                return false;
            }

            string currentName = targetText.gameObject.name;
            if (string.Equals(currentName, $"{previousIcon.DisplayName} FA Icon", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(currentName, previousIcon.DisplayName, StringComparison.Ordinal))
            {
                return true;
            }

            string currentFontName = targetText.font != null ? targetText.font.name : string.Empty;
            TMP_FontAsset resolvedFontAsset = ResolveFontAssetForIcon(previousIcon, targetText);
            string selectedFontName = resolvedFontAsset != null ? resolvedFontAsset.name : string.Empty;
            if (string.Equals(currentName, $"{currentFontName} {previousIcon.Name}", StringComparison.Ordinal) ||
                string.Equals(currentName, $"{selectedFontName} {previousIcon.Name}", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private FontAwesomeIconEntry FindIconByGlyph(string glyph)
        {
            if (string.IsNullOrEmpty(glyph))
            {
                return default;
            }

            foreach (FontAwesomeIconEntry icon in allIcons)
            {
                if (string.Equals(icon.Glyph, glyph, StringComparison.Ordinal))
                {
                    return icon;
                }
            }

            return default;
        }

        private void EnsureIconSetDefinitions()
        {
            if (iconSetDefinitions.Count > 0)
            {
                return;
            }

            iconSetDefinitions.Add(FontAwesomeIconSetDefinition.Create());
        }

        private void UpdateActiveIconSet()
        {
            IconSetDefinition matchedIconSet = GetPreferredSettingsIconSet();

            if (!ReferenceEquals(activeIconSet, matchedIconSet))
            {
                activeIconSet = matchedIconSet;
                iconsLoaded = false;
                allIcons.Clear();
                filteredIcons.Clear();
                styleFilterStates.Clear();
                availableStyleKeys.Clear();
                fontAssetsByStyle.Clear();
            }
        }

        private IconSetDefinition GetPreferredSettingsIconSet()
        {
            if (activeIconSet != null)
            {
                return activeIconSet;
            }

            return iconSetDefinitions.Count > 0 ? iconSetDefinitions[0] : null;
        }

        private void InstallIconSetPackage(IconSetDefinition iconSet)
        {
            if (iconSet == null || !iconSet.SupportsPackageInstall)
            {
                return;
            }

            PackageInstallInfo installInfo = iconSet.ResolvePackageInstallInfo();
            string installRoot = installInfo.InstallRootPath;
            string existingInstallRoot = installInfo.ExistingInstallRootPath;
            string replaceRoot = AssetDatabase.IsValidFolder(existingInstallRoot) ? existingInstallRoot : installRoot;
            if (AssetDatabase.IsValidFolder(replaceRoot))
            {
                bool overwriteConfirmed = EditorUtility.DisplayDialog(
                    $"Replace {iconSet.DisplayName}?",
                    $"{iconSet.DisplayName} already exists at '{replaceRoot}'. Replace the existing files with a fresh download?",
                    "Replace",
                    "Cancel");

                if (!overwriteConfirmed)
                {
                    return;
                }
            }

            string tempZipPath = Path.Combine(Path.GetTempPath(), $"{iconSet.DisplayName.Replace(" ", string.Empty)}.zip");
            string tempExtractPath = Path.Combine(Path.GetTempPath(), $"{iconSet.DisplayName.Replace(" ", string.Empty)}-Extract");

            try
            {
                EditorUtility.DisplayProgressBar(WindowTitle, $"Downloading {iconSet.DisplayName} package...", 0.15f);
                DownloadFile(installInfo.DownloadUrl, tempZipPath);

                if (Directory.Exists(tempExtractPath))
                {
                    Directory.Delete(tempExtractPath, true);
                }

                EditorUtility.DisplayProgressBar(WindowTitle, $"Extracting {iconSet.DisplayName} package...", 0.45f);
                ZipFile.ExtractToDirectory(tempZipPath, tempExtractPath);

                string extractedRoot = iconSet.FindExtractedPackageRoot(tempExtractPath);
                if (string.IsNullOrWhiteSpace(extractedRoot) || !Directory.Exists(extractedRoot))
                {
                    throw new InvalidOperationException($"Could not find the extracted {iconSet.DisplayName} package root.");
                }

                if (AssetDatabase.IsValidFolder(existingInstallRoot) &&
                    !string.Equals(existingInstallRoot, installRoot, StringComparison.OrdinalIgnoreCase))
                {
                    FileUtil.DeleteFileOrDirectory(existingInstallRoot);
                    FileUtil.DeleteFileOrDirectory($"{existingInstallRoot}.meta");
                }

                if (AssetDatabase.IsValidFolder(installRoot))
                {
                    FileUtil.DeleteFileOrDirectory(installRoot);
                    FileUtil.DeleteFileOrDirectory($"{installRoot}.meta");
                }

                Directory.CreateDirectory(installRoot);

                EditorUtility.DisplayProgressBar(WindowTitle, $"Installing {iconSet.DisplayName} into Assets...", 0.75f);
                CopyRequiredPackageContents(iconSet, extractedRoot, installRoot);

                AssetDatabase.Refresh();

                EditorUtility.DisplayProgressBar(WindowTitle, $"Generating {iconSet.DisplayName} TMP font assets...", 0.9f);

                string metadataPath = installInfo.InstalledMetadataPath;
                if (!string.IsNullOrWhiteSpace(metadataPath))
                {
                    iconSet.SetMetadataPathOverride(metadataPath);
                }

                BeginInstalledFontAssetCreation(iconSet);
                UpdateActiveIconSet();
                RefreshFontAssetsForActiveIconSet();
                EnsureIconsLoaded(true);
                EnsureStyleFilters(true);
                RebuildFilteredIcons();

                EditorUtility.DisplayDialog(
                    $"{iconSet.DisplayName} Installed",
                    $"{iconSet.DisplayName} was downloaded and installed to '{installRoot}'.",
                    "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog(
                    $"{iconSet.DisplayName} Install Failed",
                    $"Could not install {iconSet.DisplayName}.\n\n{exception.Message}",
                    "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();

                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }

                if (Directory.Exists(tempExtractPath))
                {
                    Directory.Delete(tempExtractPath, true);
                }
            }
        }

        private void BeginInstalledFontAssetCreation(IconSetDefinition iconSet)
        {
            if (iconSet == null)
            {
                return;
            }

            AssetDatabase.Refresh();

            FontAssetCreationStatus creationStatus = CreateInstalledFontAssets(iconSet);
            if (creationStatus == FontAssetCreationStatus.Completed)
            {
                OnInstalledFontAssetsGenerated();
                return;
            }

            ScheduleInstalledFontAssetCreationRetry(iconSet, FontAssetGenerationRetryCount);
        }

        private void ScheduleInstalledFontAssetCreationRetry(IconSetDefinition iconSet, int attemptsRemaining)
        {
            EditorApplication.delayCall += () => RetryInstalledFontAssetCreation(iconSet, attemptsRemaining);
        }

        private void RetryInstalledFontAssetCreation(IconSetDefinition iconSet, int attemptsRemaining)
        {
            if (iconSet == null)
            {
                return;
            }

            AssetDatabase.Refresh();

            FontAssetCreationStatus creationStatus = CreateInstalledFontAssets(iconSet);
            if (creationStatus == FontAssetCreationStatus.Completed)
            {
                OnInstalledFontAssetsGenerated();
                return;
            }

            if (attemptsRemaining > 0)
            {
                ScheduleInstalledFontAssetCreationRetry(iconSet, attemptsRemaining - 1);
                return;
            }

            Debug.LogWarning(
                $"Delayed generation of {iconSet.DisplayName} TMP font assets did not complete before retrying timed out. " +
                "Reopen the Font Awesome browser or reimport the installed fonts after TextMeshPro finishes initializing.");
        }

        private void OnInstalledFontAssetsGenerated()
        {
            AssetDatabase.Refresh();

            UpdateActiveIconSet();
            RefreshFontAssetsForActiveIconSet();
            EnsureIconsLoaded(true);
            EnsureStyleFilters(true);
            RebuildFilteredIcons();
            Repaint();
        }

        private static FontAssetCreationStatus CreateInstalledFontAssets(IconSetDefinition iconSet)
        {
            if (iconSet == null)
            {
                return FontAssetCreationStatus.Completed;
            }

            if (TMP_Settings.instance == null)
            {
                return FontAssetCreationStatus.RetryRequired;
            }

            bool retryRequired = false;
            bool savedAssets = false;
            List<FontAwesomeIconEntry> iconEntries = LoadIconEntries(iconSet);

            foreach (string fontAssetPath in iconSet.GetFontSourceAssetPaths())
            {
                Font sourceFont = AssetDatabase.LoadAssetAtPath<Font>(fontAssetPath);
                if (sourceFont == null)
                {
                    retryRequired = true;
                    continue;
                }

                string sdfAssetPath = IconSetDefinition.GetSdfAssetPath(fontAssetPath);

                TMP_FontAsset existingAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(sdfAssetPath);
                if (existingAsset != null)
                {
                    continue;
                }

                TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(
                    sourceFont,
                    90,
                    9,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                    1024,
                    1024,
                    AtlasPopulationMode.Dynamic,
                    false);

                if (fontAsset == null)
                {
                    retryRequired = true;
                    continue;
                }

                PopulateFontAssetWithIcons(fontAsset, iconSet, iconEntries);
                ConfigureDefaultFontFallback(fontAsset);
                AssetDatabase.CreateAsset(fontAsset, sdfAssetPath);

                if (fontAsset.material != null)
                {
                    AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
                }

                if (fontAsset.atlasTextures != null)
                {
                    foreach (Texture2D atlasTexture in fontAsset.atlasTextures)
                    {
                        if (atlasTexture != null)
                        {
                            AssetDatabase.AddObjectToAsset(atlasTexture, fontAsset);
                        }
                    }
                }

                EditorUtility.SetDirty(fontAsset);
                savedAssets = true;
            }

            if (savedAssets)
            {
                AssetDatabase.SaveAssets();
            }

            return retryRequired ? FontAssetCreationStatus.RetryRequired : FontAssetCreationStatus.Completed;
        }

        private static List<FontAwesomeIconEntry> LoadIconEntries(IconSetDefinition iconSet)
        {
            List<FontAwesomeIconEntry> iconEntries = new();
            if (iconSet == null)
            {
                return iconEntries;
            }

            TextAsset iconMetadata = AssetDatabase.LoadAssetAtPath<TextAsset>(iconSet.IconsMetadataPath);
            if (iconMetadata == null)
            {
                return iconEntries;
            }

            ParseIconMetadata(iconMetadata.text, iconEntries, iconSet);
            return iconEntries;
        }

        private static bool PopulateFontAssetWithIcons(TMP_FontAsset fontAsset, IconSetDefinition iconSet, List<FontAwesomeIconEntry> iconEntries)
        {
            if (fontAsset == null || iconSet == null || iconEntries == null || iconEntries.Count == 0)
            {
                return false;
            }

            if (!iconSet.TryGetFamilyStyle(fontAsset, out string family, out string style))
            {
                return false;
            }

            List<uint> unicodes = GetUnicodesForFontStyle(iconEntries, family, style);
            if (unicodes.Count == 0)
            {
                return false;
            }

            fontAsset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            fontAsset.ClearFontAssetData(true);
            fontAsset.TryAddCharacters(unicodes.ToArray(), out uint[] _);
            fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
            fontAsset.isMultiAtlasTexturesEnabled = false;
            return true;
        }

        private static List<uint> GetUnicodesForFontStyle(List<FontAwesomeIconEntry> iconEntries, string family, string style)
        {
            HashSet<uint> unicodes = new();
            foreach (FontAwesomeIconEntry icon in iconEntries)
            {
                if (!icon.Supports(family, style))
                {
                    continue;
                }

                unicodes.Add(icon.Unicode);

                if (string.Equals(style, "duotone", StringComparison.OrdinalIgnoreCase) && icon.SecondaryUnicode.HasValue)
                {
                    unicodes.Add(icon.SecondaryUnicode.Value);
                }
            }

            return unicodes.OrderBy(unicode => unicode).ToList();
        }

        private static bool ConfigureDefaultFontFallback(TMP_FontAsset fontAsset)
        {
            if (fontAsset == null)
            {
                return false;
            }

            TMP_FontAsset defaultFontAsset = TMP_Settings.defaultFontAsset;
            if (defaultFontAsset == null || ReferenceEquals(defaultFontAsset, fontAsset))
            {
                return false;
            }

            fontAsset.fallbackFontAssetTable ??= new List<TMP_FontAsset>();
            if (fontAsset.fallbackFontAssetTable.Contains(defaultFontAsset))
            {
                return false;
            }

            fontAsset.fallbackFontAssetTable.Add(defaultFontAsset);
            return true;
        }

        private enum FontAssetCreationStatus
        {
            Completed,
            RetryRequired
        }

        private sealed class PackageInstallInfo
        {
            public PackageInstallInfo(
                string downloadUrl,
                string installRootPath,
                string installedMetadataPath,
                string existingInstallRootPath = "")
            {
                DownloadUrl = downloadUrl ?? string.Empty;
                InstallRootPath = installRootPath ?? string.Empty;
                InstalledMetadataPath = installedMetadataPath ?? string.Empty;
                ExistingInstallRootPath = existingInstallRootPath ?? string.Empty;
            }

            public string DownloadUrl { get; }
            public string InstallRootPath { get; }
            public string InstalledMetadataPath { get; }
            public string ExistingInstallRootPath { get; }
        }

        private static void CopyRequiredPackageContents(IconSetDefinition iconSet, string extractedRoot, string installRoot)
        {
            foreach (string relativePath in iconSet.GetRequiredPackagePaths())
            {
                string sourcePath = Path.Combine(extractedRoot, relativePath);
                string destinationPath = Path.Combine(installRoot, relativePath);

                if (Directory.Exists(sourcePath))
                {
                    CopyDirectoryContents(sourcePath, destinationPath);
                    continue;
                }

                if (File.Exists(sourcePath))
                {
                    string destinationDirectory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }

                    FileUtil.CopyFileOrDirectory(sourcePath.Replace('\\', '/'), destinationPath.Replace('\\', '/'));
                }
            }
        }

        private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (string directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relativeDirectory = Path.GetRelativePath(sourceDirectory, directory);
                string targetDirectory = Path.Combine(destinationDirectory, relativeDirectory);
                Directory.CreateDirectory(targetDirectory);
            }

            foreach (string filePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relativeFilePath = Path.GetRelativePath(sourceDirectory, filePath);
                string destinationPath = Path.Combine(destinationDirectory, relativeFilePath);
                string destinationParent = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationParent))
                {
                    Directory.CreateDirectory(destinationParent);
                }

                File.Copy(filePath, destinationPath, true);
            }
        }

        private static void DownloadFile(string url, string destinationPath)
        {
            using UnityWebRequest request = UnityWebRequest.Get(url);
            string directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            request.downloadHandler = new DownloadHandlerFile(destinationPath);
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            while (!operation.isDone)
            {
            }

#if UNITY_2020_1_OR_NEWER
            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new InvalidOperationException($"Download failed: {request.error}");
            }
#else
        if (request.isHttpError || request.isNetworkError)
        {
            throw new InvalidOperationException($"Download failed: {request.error}");
        }
#endif
        }

        private static bool FontHasGlyph(TMP_FontAsset fontAsset, uint unicode)
        {
            return fontAsset != null &&
                   fontAsset.characterLookupTable != null &&
                   fontAsset.characterLookupTable.ContainsKey(unicode);
        }

        private static void EnsureGlyphAvailable(TMP_FontAsset fontAsset, FontAwesomeIconEntry icon)
        {
            if (fontAsset == null || FontHasGlyph(fontAsset, icon.Unicode))
            {
                return;
            }

            if (fontAsset.atlasPopulationMode == AtlasPopulationMode.Dynamic ||
                fontAsset.atlasPopulationMode == AtlasPopulationMode.DynamicOS)
            {
                fontAsset.TryAddCharacters(icon.Glyph, out string _);
                EditorUtility.SetDirty(fontAsset);
            }
        }

        private void RefreshFontAssetsForActiveIconSet()
        {
            fontAssetsByStyle.Clear();

            if (activeIconSet == null)
            {
                return;
            }

            string[] guids = AssetDatabase.FindAssets(activeIconSet.DefaultFontSearchFilter);
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                TMP_FontAsset fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
                if (fontAsset == null || !activeIconSet.MatchesFontAsset(fontAsset))
                {
                    continue;
                }

                if (!activeIconSet.TryGetFamilyStyle(fontAsset, out string family, out string style))
                {
                    continue;
                }

                string styleKey = ToStyleKey(family, style);
                if (string.IsNullOrWhiteSpace(styleKey))
                {
                    continue;
                }

                if (!fontAssetsByStyle.TryGetValue(styleKey, out List<TMP_FontAsset> fontAssets))
                {
                    fontAssets = new List<TMP_FontAsset>();
                    fontAssetsByStyle[styleKey] = fontAssets;
                }

                if (!fontAssets.Contains(fontAsset))
                {
                    fontAssets.Add(fontAsset);
                }
            }

            foreach ((string _, List<TMP_FontAsset> fontAssets) in fontAssetsByStyle)
            {
                fontAssets.Sort((left, right) => string.Compare(left.name, right.name, StringComparison.OrdinalIgnoreCase));
            }
        }

        private TMP_FontAsset ResolveFontAssetForIcon(FontAwesomeIconEntry icon, TMP_Text preferredTargetText = null, string preferredStyleKey = null)
        {
            if (activeIconSet == null)
            {
                return null;
            }

            if (preferredTargetText != null &&
                preferredTargetText.font != null &&
                activeIconSet.MatchesFontAsset(preferredTargetText.font) &&
                activeIconSet.TryGetFamilyStyle(preferredTargetText.font, out string preferredFamily, out string preferredStyle) &&
                IsStyleEnabled(ToStyleKey(preferredFamily, preferredStyle)) &&
                icon.Supports(preferredFamily, preferredStyle))
            {
                return preferredTargetText.font;
            }

            if (!string.IsNullOrWhiteSpace(preferredStyleKey) &&
                fontAssetsByStyle.TryGetValue(preferredStyleKey, out List<TMP_FontAsset> preferredStyleAssets))
            {
                foreach (TMP_FontAsset fontAsset in preferredStyleAssets)
                {
                    if (activeIconSet.TryGetFamilyStyle(fontAsset, out string family, out string style) &&
                        icon.Supports(family, style))
                    {
                        return fontAsset;
                    }
                }
            }

            foreach (string styleKey in availableStyleKeys)
            {
                if (!IsStyleEnabled(styleKey) ||
                    !fontAssetsByStyle.TryGetValue(styleKey, out List<TMP_FontAsset> fontAssets))
                {
                    continue;
                }

                foreach (TMP_FontAsset fontAsset in fontAssets)
                {
                    if (activeIconSet.TryGetFamilyStyle(fontAsset, out string family, out string style) &&
                        icon.Supports(family, style))
                    {
                        return fontAsset;
                    }
                }
            }

            return null;
        }

        private Font ResolvePreviewFont(FontAwesomeFilteredIconEntry filteredIcon, TMP_FontAsset previewFontAsset)
        {
            if (previewFontAsset != null && previewFontAsset.sourceFontFile != null)
            {
                return previewFontAsset.sourceFontFile;
            }

            Font fontFromSiblingAsset = ResolvePreviewFontFromSiblingAsset(previewFontAsset);
            if (fontFromSiblingAsset != null)
            {
                return fontFromSiblingAsset;
            }

            if (activeIconSet == null)
            {
                return null;
            }

            foreach (string fontAssetPath in activeIconSet.GetFontSourceAssetPaths())
            {
                if (!TryInferStyleKeyFromName(fontAssetPath, out string styleKey) ||
                    !string.Equals(styleKey, filteredIcon.StyleKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Font sourceFont = AssetDatabase.LoadAssetAtPath<Font>(fontAssetPath);
                if (sourceFont != null)
                {
                    return sourceFont;
                }
            }

            return null;
        }

        private static Font ResolvePreviewFontFromSiblingAsset(TMP_FontAsset fontAsset)
        {
            if (fontAsset == null)
            {
                return null;
            }

            string assetPath = AssetDatabase.GetAssetPath(fontAsset);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return null;
            }

            string basePath = assetPath.EndsWith(" SDF.asset", StringComparison.OrdinalIgnoreCase)
                ? assetPath[..^" SDF.asset".Length]
                : Path.ChangeExtension(assetPath, null);

            Font otfFont = AssetDatabase.LoadAssetAtPath<Font>($"{basePath}.otf");
            if (otfFont != null)
            {
                return otfFont;
            }

            return AssetDatabase.LoadAssetAtPath<Font>($"{basePath}.ttf");
        }

        private void SetAllStyleFilters(bool value)
        {
            foreach (string styleKey in availableStyleKeys)
            {
                styleFilterStates[styleKey] = value;
            }

            RebuildFilteredIcons();
        }

        private bool IsStyleEnabled(string styleKey)
        {
            return !string.IsNullOrWhiteSpace(styleKey) &&
                   styleFilterStates.TryGetValue(styleKey, out bool isEnabled) &&
                   isEnabled;
        }

        private string GetSdfSummaryForStyle(string styleKey)
        {
            if (!fontAssetsByStyle.TryGetValue(styleKey, out List<TMP_FontAsset> fontAssets) || fontAssets.Count == 0)
            {
                return "none found";
            }

            return string.Join(", ", fontAssets.Select(fontAsset => fontAsset.name));
        }

        private string GetEnabledStyleSdfSummary()
        {
            List<string> summaries = new();
            foreach (string styleKey in availableStyleKeys)
            {
                if (!IsStyleEnabled(styleKey))
                {
                    continue;
                }

                summaries.Add($"{ToDisplayLabel(styleKey)}: {GetSdfSummaryForStyle(styleKey)}");
            }

            if (summaries.Count == 0)
            {
                return "No styles enabled.";
            }

            return string.Join("   |   ", summaries);
        }

        private string GetSdfSummaryForStyleKeys(IReadOnlyList<string> styleKeys)
        {
            List<string> summaries = new();
            foreach (string styleKey in styleKeys)
            {
                string summary = $"{ToDisplayLabel(styleKey)}: {GetSdfSummaryForStyle(styleKey)}";
                summaries.Add(summary);
            }

            return summaries.Count > 0 ? string.Join("\n", summaries) : "No associated SDFs";
        }

        private string GetSdfSummaryForIcon(FontAwesomeIconEntry icon)
        {
            List<string> matchingAssets = new();
            foreach (string styleKey in icon.GetStyleKeys())
            {
                if (!IsStyleEnabled(styleKey) ||
                    !fontAssetsByStyle.TryGetValue(styleKey, out List<TMP_FontAsset> fontAssets))
                {
                    continue;
                }

                foreach (TMP_FontAsset fontAsset in fontAssets)
                {
                    if (!matchingAssets.Contains(fontAsset.name))
                    {
                        matchingAssets.Add(fontAsset.name);
                    }
                }
            }

            if (matchingAssets.Count == 0)
            {
                return "No SDF";
            }

            return matchingAssets.Count == 1
                ? matchingAssets[0]
                : $"{matchingAssets[0]} +{matchingAssets.Count - 1}";
        }

        private static int GetStyleSortOrder(string styleKey)
        {
            return styleKey.ToLowerInvariant() switch
            {
                "solid" => 0,
                "regular" => 1,
                "light" => 2,
                "thin" => 3,
                "duotone" => 4,
                "brands" => 5,
                _ => 10
            };
        }

        private static string ToDisplayLabel(string styleKey)
        {
            if (string.IsNullOrWhiteSpace(styleKey))
            {
                return "Unknown";
            }

            return styleKey.Equals("brands", StringComparison.OrdinalIgnoreCase)
                ? "Brands"
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(styleKey.Replace('-', ' '));
        }

        private static string ToStyleKey(string family, string style)
        {
            if (string.Equals(family, "brands", StringComparison.OrdinalIgnoreCase))
            {
                return "brands";
            }

            return style?.Trim().ToLowerInvariant() ?? string.Empty;
        }

        private static bool TryInferStyleKeyFromName(string value, out string styleKey)
        {
            styleKey = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string normalized = value.ToLowerInvariant();
            if (normalized.Contains("brands"))
            {
                styleKey = "brands";
                return true;
            }

            if (normalized.Contains("duotone"))
            {
                styleKey = "duotone";
                return true;
            }

            if (normalized.Contains("solid"))
            {
                styleKey = "solid";
                return true;
            }

            if (normalized.Contains("regular"))
            {
                styleKey = "regular";
                return true;
            }

            if (normalized.Contains("light"))
            {
                styleKey = "light";
                return true;
            }

            if (normalized.Contains("thin"))
            {
                styleKey = "thin";
                return true;
            }

            return false;
        }

        private static void ParseIconMetadata(string json, List<FontAwesomeIconEntry> results, IconSetDefinition iconSet)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            int index = SkipWhitespace(json, 0);
            if (index >= json.Length || json[index] != '{')
            {
                return;
            }

            index++;
            while (index < json.Length)
            {
                index = SkipWhitespace(json, index);
                if (index >= json.Length || json[index] == '}')
                {
                    break;
                }

                string iconName = ReadJsonString(json, ref index);
                index = SkipWhitespace(json, index);
                if (index >= json.Length || json[index] != ':')
                {
                    break;
                }

                index++;
                index = SkipWhitespace(json, index);
                if (index >= json.Length || json[index] != '{')
                {
                    break;
                }

                int objectStart = index;
                int objectEnd = FindObjectEnd(json, objectStart);
                if (objectEnd <= objectStart)
                {
                    break;
                }

                string entryJson = json.Substring(objectStart, objectEnd - objectStart + 1);
                string unicodeHex = FindJsonStringProperty(entryJson, "unicode");
                if (!string.IsNullOrWhiteSpace(unicodeHex) &&
                    uint.TryParse(unicodeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint unicode))
                {
                    string label = FindJsonStringProperty(entryJson, "label");
                    List<FontAwesomeFamilyStyle> familyStyles = iconSet.ParseFreeFamilyStyles(entryJson);
                    uint? secondaryUnicode = iconSet.ParseSecondaryUnicode(entryJson, unicode, familyStyles);
                    results.Add(new FontAwesomeIconEntry(iconName, label, unicode, secondaryUnicode, familyStyles));
                }

                index = objectEnd + 1;
                index = SkipWhitespace(json, index);
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                }
            }
        }

        private static string FindJsonStringProperty(string json, string propertyName)
        {
            Regex propertyRegex = new(string.Format(JsonStringPropertyPattern, Regex.Escape(propertyName)), RegexOptions.Compiled);
            Match match = propertyRegex.Match(json);
            return match.Success ? JsonUnescape(match.Groups["value"].Value) : string.Empty;
        }

        private static int SkipWhitespace(string text, int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            return index;
        }

        private static string ReadJsonString(string text, ref int index)
        {
            index = SkipWhitespace(text, index);
            if (index >= text.Length || text[index] != '"')
            {
                return string.Empty;
            }

            index++;
            StringBuilder builder = new();
            bool escaping = false;

            while (index < text.Length)
            {
                char current = text[index++];
                if (escaping)
                {
                    builder.Append(current switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        '/' => '/',
                        'b' => '\b',
                        'f' => '\f',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        'u' when index + 4 <= text.Length => (char)Convert.ToInt32(text.Substring(index, 4), 16),
                        _ => current
                    });

                    if (current == 'u' && index + 4 <= text.Length)
                    {
                        index += 4;
                    }

                    escaping = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (current == '"')
                {
                    break;
                }

                builder.Append(current);
            }

            return builder.ToString();
        }

        private static int FindObjectEnd(string text, int objectStart)
        {
            int depth = 0;
            bool insideString = false;
            bool escaping = false;

            for (int i = objectStart; i < text.Length; i++)
            {
                char current = text[i];

                if (insideString)
                {
                    if (escaping)
                    {
                        escaping = false;
                        continue;
                    }

                    if (current == '\\')
                    {
                        escaping = true;
                    }
                    else if (current == '"')
                    {
                        insideString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    insideString = true;
                    continue;
                }

                if (current == '{')
                {
                    depth++;
                }
                else if (current == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static int FindArrayEnd(string text, int arrayStart)
        {
            int depth = 0;
            bool insideString = false;
            bool escaping = false;

            for (int i = arrayStart; i < text.Length; i++)
            {
                char current = text[i];

                if (insideString)
                {
                    if (escaping)
                    {
                        escaping = false;
                        continue;
                    }

                    if (current == '\\')
                    {
                        escaping = true;
                    }
                    else if (current == '"')
                    {
                        insideString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    insideString = true;
                    continue;
                }

                if (current == '[')
                {
                    depth++;
                }
                else if (current == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static string JsonUnescape(string value)
        {
            return value
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\/", "/")
                .Replace("\\b", "\b")
                .Replace("\\f", "\f")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }

        private readonly struct FontAwesomeFilteredIconEntry
        {
            public FontAwesomeFilteredIconEntry(FontAwesomeIconEntry icon, string styleKey)
            {
                Icon = icon;
                StyleKey = styleKey;
            }

            public FontAwesomeIconEntry Icon { get; }
            public string StyleKey { get; }
        }

        private readonly struct FontAwesomeIconEntry
        {
            public FontAwesomeIconEntry(string name, string label, uint unicode, uint? secondaryUnicode, List<FontAwesomeFamilyStyle> freeFamilyStyles)
            {
                Name = name;
                Label = string.IsNullOrWhiteSpace(label) ? name : label;
                Unicode = unicode;
                SecondaryUnicode = secondaryUnicode;
                FreeFamilyStyles = freeFamilyStyles ?? new List<FontAwesomeFamilyStyle>();
            }

            public string Name { get; }
            public string Label { get; }
            public uint Unicode { get; }
            public uint? SecondaryUnicode { get; }
            public List<FontAwesomeFamilyStyle> FreeFamilyStyles { get; }
            public string Glyph => char.ConvertFromUtf32((int)Unicode);
            public string SecondaryGlyph => SecondaryUnicode.HasValue ? char.ConvertFromUtf32((int)SecondaryUnicode.Value) : string.Empty;
            public string DisplayName => ToDisplayName();
            public bool IsValid => !string.IsNullOrWhiteSpace(Name);
            public bool HasSecondaryGlyph => SecondaryUnicode.HasValue;
            public bool SupportsDuotone => Supports("classic", "duotone");

            public List<string> GetStyleKeys()
            {
                if (FreeFamilyStyles == null || FreeFamilyStyles.Count == 0)
                {
                    return new List<string>();
                }

                HashSet<string> styleKeys = new(StringComparer.OrdinalIgnoreCase);
                foreach (FontAwesomeFamilyStyle familyStyle in FreeFamilyStyles)
                {
                    styleKeys.Add(ToStyleKey(familyStyle.Family, familyStyle.Style));
                }

                return styleKeys.OrderBy(GetStyleSortOrder).ThenBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();
            }

            public bool SupportsStyleKey(string styleKey)
            {
                if (FreeFamilyStyles == null || FreeFamilyStyles.Count == 0)
                {
                    return true;
                }

                foreach (FontAwesomeFamilyStyle familyStyle in FreeFamilyStyles)
                {
                    if (string.Equals(ToStyleKey(familyStyle.Family, familyStyle.Style), styleKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool Supports(string family, string style)
            {
                if (FreeFamilyStyles == null || FreeFamilyStyles.Count == 0)
                {
                    return true;
                }

                foreach (FontAwesomeFamilyStyle familyStyle in FreeFamilyStyles)
                {
                    if (string.Equals(familyStyle.Family, family, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(familyStyle.Style, style, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            private string ToDisplayName()
            {
                if (!string.IsNullOrWhiteSpace(Label))
                {
                    return Label;
                }

                if (string.IsNullOrWhiteSpace(Name))
                {
                    return "Icon";
                }

                string[] parts = Name.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    return Name;
                }

                TextInfo textInfo = CultureInfo.InvariantCulture.TextInfo;
                for (int i = 0; i < parts.Length; i++)
                {
                    parts[i] = textInfo.ToTitleCase(parts[i]);
                }

                return string.Join(" ", parts);
            }
        }

        private readonly struct FontAwesomeFamilyStyle
        {
            public FontAwesomeFamilyStyle(string family, string style)
            {
                Family = family;
                Style = style;
            }

            public string Family { get; }
            public string Style { get; }
        }

        private abstract class IconSetDefinition
        {
            public abstract string DisplayName { get; }
            public abstract string DefaultFontSearchFilter { get; }
            protected abstract string DefaultIconsMetadataPath { get; }
            protected abstract string MetadataSearchFilter { get; }
            public virtual bool SupportsPackageInstall => false;
            public virtual string InstallButtonLabel => $"Install {DisplayName}";
            public virtual string InstallSummary => string.Empty;
            public virtual string PackageDownloadUrl => string.Empty;

            public abstract bool MatchesFontAsset(TMP_FontAsset fontAsset);
            public abstract bool TryGetFamilyStyle(TMP_FontAsset fontAsset, out string family, out string style);
            public abstract List<FontAwesomeFamilyStyle> ParseFreeFamilyStyles(string entryJson);
            public abstract uint? ParseSecondaryUnicode(string entryJson, uint primaryUnicode, List<FontAwesomeFamilyStyle> familyStyles);

            public string IconsMetadataPath => ResolveMetadataPath();

            public void SetMetadataPathOverride(string path)
            {
                string normalizedPath = NormalizeMetadataPath(path);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    EditorPrefs.DeleteKey(GetMetadataPrefsKey());
                    return;
                }

                EditorPrefs.SetString(GetMetadataPrefsKey(), normalizedPath);
            }

            public void ClearMetadataPathOverride()
            {
                EditorPrefs.DeleteKey(GetMetadataPrefsKey());
            }

            public bool TryAutoConfigureMetadataPath()
            {
                string discoveredPath = FindMetadataPath();
                if (string.IsNullOrWhiteSpace(discoveredPath))
                {
                    return false;
                }

                SetMetadataPathOverride(discoveredPath);
                return true;
            }

            protected virtual bool IsMatchingMetadataPath(string assetPath)
            {
                return string.Equals(Path.GetFileName(assetPath), Path.GetFileName(DefaultIconsMetadataPath), StringComparison.OrdinalIgnoreCase);
            }

            public virtual string GetInstallRootPath()
            {
                return Path.GetDirectoryName(DefaultIconsMetadataPath)?.Replace('\\', '/') ?? "Assets";
            }

            public virtual string GetInstalledMetadataPath()
            {
                return DefaultIconsMetadataPath;
            }

            public virtual PackageInstallInfo ResolvePackageInstallInfo()
            {
                return new PackageInstallInfo(
                    PackageDownloadUrl,
                    GetInstallRootPath(),
                    GetInstalledMetadataPath());
            }

            public virtual string FindExtractedPackageRoot(string extractedRootPath)
            {
                return extractedRootPath;
            }

            public virtual IEnumerable<string> GetRequiredPackagePaths()
            {
                yield break;
            }

            public virtual IEnumerable<string> GetFontSourceAssetPaths()
            {
                yield break;
            }

            public virtual bool HasInstalledContent()
            {
                return AssetDatabase.LoadAssetAtPath<TextAsset>(IconsMetadataPath) != null;
            }

            public virtual bool NeedsFontAssetGeneration()
            {
                if (AssetDatabase.LoadAssetAtPath<TextAsset>(IconsMetadataPath) == null)
                {
                    return false;
                }

                List<string> sourceFontAssetPaths = GetFontSourceAssetPaths().ToList();
                if (sourceFontAssetPaths.Count == 0)
                {
                    return false;
                }

                foreach (string fontAssetPath in sourceFontAssetPaths)
                {
                    if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(GetSdfAssetPath(fontAssetPath)) == null)
                    {
                        return true;
                    }
                }

                return false;
            }

            public virtual string GetFontAssetGenerationKey()
            {
                return $"{GetType().FullName}:{IconsMetadataPath}";
            }

            public static string GetSdfAssetPath(string fontAssetPath)
            {
                string directory = Path.GetDirectoryName(fontAssetPath)?.Replace('\\', '/') ?? "Assets";
                string fontName = Path.GetFileNameWithoutExtension(fontAssetPath);
                return $"{directory}/{fontName} SDF.asset";
            }

            private string ResolveMetadataPath()
            {
                string prefsKey = GetMetadataPrefsKey();
                string overriddenPath = NormalizeMetadataPath(EditorPrefs.GetString(prefsKey, string.Empty));
                if (!string.IsNullOrWhiteSpace(overriddenPath))
                {
                    return overriddenPath;
                }

                string discoveredPath = FindMetadataPath();
                if (!string.IsNullOrWhiteSpace(discoveredPath))
                {
                    EditorPrefs.SetString(prefsKey, discoveredPath);
                    return discoveredPath;
                }

                return DefaultIconsMetadataPath;
            }

            private string FindMetadataPath()
            {
                string[] guids = AssetDatabase.FindAssets(MetadataSearchFilter);
                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (IsMatchingMetadataPath(assetPath))
                    {
                        return assetPath;
                    }
                }

                return AssetDatabase.LoadAssetAtPath<TextAsset>(DefaultIconsMetadataPath) != null
                    ? DefaultIconsMetadataPath
                    : string.Empty;
            }

            private string GetMetadataPrefsKey()
            {
                return $"{GetType().FullName}.MetadataPath";
            }

            private static string NormalizeMetadataPath(string path)
            {
                return string.IsNullOrWhiteSpace(path)
                    ? string.Empty
                    : path.Replace('\\', '/').Trim();
            }
        }

        private sealed class FontAwesomeIconSetDefinition : IconSetDefinition
        {
            private const string FallbackFontAwesomeVersion = "7.2.0";
            private const string FontAwesomeLatestReleaseUrl = "https://github.com/FortAwesome/Font-Awesome/releases/latest";
            private const string FontAwesomePackageRootPattern = @"(^|/)fontawesome-free-[^/]+-desktop$";
            private const string FontAwesomeMetadataPath = "Assets/Fonts/fontawesome-free-7.2.0-desktop/metadata/icons.json";
            private const string FontAwesomeInstallRoot = "Assets/Fonts/fontawesome-free-7.2.0-desktop";
            private const string FontAwesomeDownloadUrl = "https://github.com/FortAwesome/Font-Awesome/releases/download/7.2.0/fontawesome-free-7.2.0-desktop.zip";

            public override string DisplayName => "Font Awesome";
            public override string DefaultFontSearchFilter => "t:TMP_FontAsset Font Awesome";
            protected override string DefaultIconsMetadataPath => FontAwesomeMetadataPath;
            protected override string MetadataSearchFilter => "icons t:TextAsset";
            public override bool SupportsPackageInstall => true;
            public override string InstallButtonLabel => "Download Latest Font Awesome Free";
            public override string InstallSummary => "Downloads the latest official free desktop package into Assets/Fonts.";
            public override string PackageDownloadUrl => FontAwesomeDownloadUrl;

            public static FontAwesomeIconSetDefinition Create()
            {
                return new FontAwesomeIconSetDefinition();
            }

            protected override bool IsMatchingMetadataPath(string assetPath)
            {
                string normalizedPath = assetPath.Replace('\\', '/');
                return normalizedPath.EndsWith("/metadata/icons.json", StringComparison.OrdinalIgnoreCase) &&
                       normalizedPath.IndexOf("fontawesome", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            public override string GetInstallRootPath()
            {
                return FontAwesomeInstallRoot;
            }

            public override string GetInstalledMetadataPath()
            {
                return FontAwesomeMetadataPath;
            }

            public override PackageInstallInfo ResolvePackageInstallInfo()
            {
                FontAwesomePackageInfo packageInfo = ResolveLatestPackageInfo();
                return new PackageInstallInfo(
                    packageInfo.DownloadUrl,
                    packageInfo.InstallRootPath,
                    packageInfo.MetadataPath,
                    GetExistingInstallRootPath());
            }

            public override IEnumerable<string> GetRequiredPackagePaths()
            {
                yield return "metadata";
                yield return "otfs";
                yield return "LICENSE.txt";
            }

            public override IEnumerable<string> GetFontSourceAssetPaths()
            {
                string otfDirectory = GetFontRootPath();
                if (!AssetDatabase.IsValidFolder(otfDirectory))
                {
                    yield break;
                }

                string[] guids = AssetDatabase.FindAssets("t:Font", new[] { otfDirectory });
                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid).Replace('\\', '/');
                    if (assetPath.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return assetPath;
                    }
                }
            }

            public override bool HasInstalledContent()
            {
                if (AssetDatabase.LoadAssetAtPath<TextAsset>(IconsMetadataPath) == null)
                {
                    return false;
                }

                foreach (string _ in GetFontSourceAssetPaths())
                {
                    return true;
                }

                return false;
            }

            private string GetFontRootPath()
            {
                string metadataPath = IconsMetadataPath.Replace('\\', '/');
                string metadataDirectory = Path.GetDirectoryName(metadataPath)?.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(metadataDirectory))
                {
                    return Path.Combine(GetInstallRootPath(), "otfs").Replace('\\', '/');
                }

                string packageRoot = Path.GetDirectoryName(metadataDirectory)?.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(packageRoot))
                {
                    return Path.Combine(GetInstallRootPath(), "otfs").Replace('\\', '/');
                }

                return $"{packageRoot}/otfs";
            }

            public override string FindExtractedPackageRoot(string extractedRootPath)
            {
                string[] directories = Directory.GetDirectories(extractedRootPath);
                foreach (string directory in directories)
                {
                    string normalizedPath = directory.Replace('\\', '/');
                    if (Regex.IsMatch(normalizedPath, FontAwesomePackageRootPattern, RegexOptions.IgnoreCase))
                    {
                        return normalizedPath;
                    }
                }

                return extractedRootPath;
            }

            private FontAwesomePackageInfo ResolveLatestPackageInfo()
            {
                try
                {
                    string latestVersion = ResolveLatestVersionFromRedirect();
                    if (!string.IsNullOrWhiteSpace(latestVersion))
                    {
                        return FontAwesomePackageInfo.Create(latestVersion);
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Could not resolve the latest Font Awesome release from GitHub. Falling back to {FallbackFontAwesomeVersion}. {exception.Message}");
                }

                return FontAwesomePackageInfo.Create(FallbackFontAwesomeVersion);
            }

            private static string ResolveLatestVersionFromRedirect()
            {
                using UnityWebRequest request = UnityWebRequest.Get(FontAwesomeLatestReleaseUrl);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.redirectLimit = 8;
                request.SetRequestHeader("User-Agent", "FontAwesomeUnity");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                }

#if UNITY_2020_1_OR_NEWER
                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new InvalidOperationException($"Release lookup failed: {request.error}");
                }
#else
                if (request.isHttpError || request.isNetworkError)
                {
                    throw new InvalidOperationException($"Release lookup failed: {request.error}");
                }
#endif

                string redirectedUrl = request.url ?? string.Empty;
                string tag = redirectedUrl.TrimEnd('/').Split('/').LastOrDefault();
                if (string.IsNullOrWhiteSpace(tag))
                {
                    throw new InvalidOperationException("Release lookup did not return a tag.");
                }

                return tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    ? tag.Substring(1)
                    : tag;
            }

            private string GetExistingInstallRootPath()
            {
                string metadataPath = IconsMetadataPath.Replace('\\', '/');
                string metadataDirectory = Path.GetDirectoryName(metadataPath)?.Replace('\\', '/');
                string packageRoot = Path.GetDirectoryName(metadataDirectory ?? string.Empty)?.Replace('\\', '/');
                return IsFontAwesomePackageRoot(packageRoot) ? packageRoot : string.Empty;
            }

            private static bool IsFontAwesomePackageRoot(string path)
            {
                return !string.IsNullOrWhiteSpace(path) &&
                       Regex.IsMatch(path.Replace('\\', '/'), FontAwesomePackageRootPattern, RegexOptions.IgnoreCase);
            }

            private readonly struct FontAwesomePackageInfo
            {
                public FontAwesomePackageInfo(string version, string directoryName, string downloadUrl, string installRootPath, string metadataPath)
                {
                    Version = version;
                    DirectoryName = directoryName;
                    DownloadUrl = downloadUrl;
                    InstallRootPath = installRootPath;
                    MetadataPath = metadataPath;
                }

                public string Version { get; }
                public string DirectoryName { get; }
                public string DownloadUrl { get; }
                public string InstallRootPath { get; }
                public string MetadataPath { get; }

                public static FontAwesomePackageInfo Create(string version)
                {
                    string normalizedVersion = string.IsNullOrWhiteSpace(version)
                        ? FallbackFontAwesomeVersion
                        : version.Trim().TrimStart('v', 'V');
                    string directoryName = $"fontawesome-free-{normalizedVersion}-desktop";
                    string installRoot = $"Assets/Fonts/{directoryName}";
                    string downloadUrl = $"https://github.com/FortAwesome/Font-Awesome/releases/download/{normalizedVersion}/{directoryName}.zip";
                    string metadataPath = $"{installRoot}/metadata/icons.json";
                    return new FontAwesomePackageInfo(normalizedVersion, directoryName, downloadUrl, installRoot, metadataPath);
                }
            }

            public override bool MatchesFontAsset(TMP_FontAsset fontAsset)
            {
                if (fontAsset == null)
                {
                    return false;
                }

                string sourceName = fontAsset.sourceFontFile != null ? fontAsset.sourceFontFile.name : string.Empty;
                string assetName = fontAsset.name ?? string.Empty;
                string combinedName = $"{sourceName} {assetName}";
                return combinedName.IndexOf("Font Awesome", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            public override bool TryGetFamilyStyle(TMP_FontAsset fontAsset, out string family, out string style)
            {
                family = null;
                style = null;

                if (fontAsset == null)
                {
                    return false;
                }

                string sourceName = fontAsset.sourceFontFile != null ? fontAsset.sourceFontFile.name : string.Empty;
                string assetName = fontAsset.name ?? string.Empty;
                string combinedName = $"{sourceName} {assetName}".ToLowerInvariant();

                if (combinedName.Contains("brands"))
                {
                    family = "brands";
                    style = "regular";
                    return true;
                }

                if (combinedName.Contains("duotone"))
                {
                    family = "classic";
                    style = "duotone";
                    return true;
                }

                if (combinedName.Contains("solid"))
                {
                    family = "classic";
                    style = "solid";
                    return true;
                }

                if (combinedName.Contains("regular"))
                {
                    family = "classic";
                    style = "regular";
                    return true;
                }

                if (combinedName.Contains("light"))
                {
                    family = "classic";
                    style = "light";
                    return true;
                }

                return false;
            }

            public override List<FontAwesomeFamilyStyle> ParseFreeFamilyStyles(string entryJson)
            {
                List<FontAwesomeFamilyStyle> styles = new();

                Regex familyStyleRegex = new(JsonFamilyStylePattern, RegexOptions.Compiled);
                MatchCollection explicitMatches = familyStyleRegex.Matches(entryJson);
                foreach (Match match in explicitMatches)
                {
                    styles.Add(new FontAwesomeFamilyStyle(
                        JsonUnescape(match.Groups["family"].Value),
                        JsonUnescape(match.Groups["style"].Value)));
                }

                if (styles.Count > 0)
                {
                    return styles;
                }

                Match stylesMatch = Regex.Match(entryJson, "\"styles\"\\s*:\\s*\\[(?<styles>.*?)\\]", RegexOptions.Singleline);
                if (stylesMatch.Success)
                {
                    MatchCollection styleMatches = Regex.Matches(stylesMatch.Groups["styles"].Value, "\"(?<style>[^\"]+)\"");
                    foreach (Match match in styleMatches)
                    {
                        string style = JsonUnescape(match.Groups["style"].Value);
                        string family = style == "brands" ? "brands" : "classic";
                        string normalizedStyle = style == "brands" ? "regular" : style;
                        styles.Add(new FontAwesomeFamilyStyle(family, normalizedStyle));
                    }
                }

                if (styles.Count > 0)
                {
                    return styles;
                }

                int freeIndex = entryJson.IndexOf("\"free\"", StringComparison.Ordinal);
                if (freeIndex >= 0)
                {
                    int arrayStart = entryJson.IndexOf('[', freeIndex);
                    if (arrayStart >= 0)
                    {
                        int arrayEnd = FindArrayEnd(entryJson, arrayStart);
                        if (arrayEnd > arrayStart)
                        {
                            string freeJson = entryJson.Substring(arrayStart, arrayEnd - arrayStart + 1);
                            MatchCollection matches = new Regex(JsonFamilyStylePattern, RegexOptions.Compiled).Matches(freeJson);
                            foreach (Match match in matches)
                            {
                                styles.Add(new FontAwesomeFamilyStyle(
                                    JsonUnescape(match.Groups["family"].Value),
                                    JsonUnescape(match.Groups["style"].Value)));
                            }

                            if (styles.Count > 0)
                            {
                                return styles;
                            }

                            Regex legacyStyleRegex = new("\"(?<style>solid|regular|brands)\"", RegexOptions.Compiled);
                            MatchCollection legacyMatches = legacyStyleRegex.Matches(freeJson);
                            foreach (Match match in legacyMatches)
                            {
                                string style = JsonUnescape(match.Groups["style"].Value);
                                string family = style == "brands" ? "brands" : "classic";
                                string normalizedStyle = style == "brands" ? "regular" : style;
                                styles.Add(new FontAwesomeFamilyStyle(family, normalizedStyle));
                            }
                        }
                    }
                }

                return styles;
            }

            public override uint? ParseSecondaryUnicode(string entryJson, uint primaryUnicode, List<FontAwesomeFamilyStyle> familyStyles)
            {
                Match secondaryMatch = Regex.Match(
                    entryJson,
                    "\"secondary\"\\s*:\\s*(?:\\[\\s*)?\"(?<unicode>[0-9a-fA-F]+)\"",
                    RegexOptions.Compiled | RegexOptions.Singleline);

                if (secondaryMatch.Success &&
                    uint.TryParse(secondaryMatch.Groups["unicode"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsedSecondaryUnicode))
                {
                    return parsedSecondaryUnicode;
                }

                foreach (FontAwesomeFamilyStyle familyStyle in familyStyles)
                {
                    if (string.Equals(familyStyle.Family, "classic", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(familyStyle.Style, "duotone", StringComparison.OrdinalIgnoreCase))
                    {
                        return 0x100000u + primaryUnicode;
                    }
                }

                return null;
            }
        }
    }
}
