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
        private readonly List<BrowserIconGroup> filteredIcons = new();
        private readonly Dictionary<string, int> selectedVariantIndices = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<IconSetDefinition> iconSetDefinitions = new();
        private readonly HashSet<string> pendingAutoFontAssetGeneration = new();

        private TMP_FontAsset selectedFontAsset;
        private IconSetDefinition activeIconSet;
        private Vector2 scrollPosition;
        private string searchText = string.Empty;
        private GUIStyle iconGlyphStyle;
        private GUIStyle iconLabelStyle;
        private GUIStyle secondaryIconGlyphStyle;
        private bool iconsLoaded;
        private bool duotoneMode;
        private bool searchAcrossAllFontAssets = true;

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
            EnsureFontAssetsForConfiguredMetadata();
            EnsureDefaultFontAsset();
            UpdateActiveIconSet();
            duotoneMode = IsSelectedFontDuotone();
            EnsureIconsLoaded();
            RebuildFilteredIcons();
        }

        private void OnGUI()
        {
            EnsureIconSetDefinitions();
            EnsureFontAssetsForConfiguredMetadata();
            EnsureDefaultFontAsset();
            UpdateActiveIconSet();
            EnsureIconsLoaded();
            EnsureStyles();

            DrawToolbar();
            EditorGUILayout.Space(6f);
            DrawMetadataSettings();
            EditorGUILayout.Space(6f);

            if (!iconsLoaded)
            {
                string metadataPath = activeIconSet != null ? activeIconSet.IconsMetadataPath : "the configured icon metadata";
                EditorGUILayout.HelpBox($"Could not load icon metadata at '{metadataPath}'.", MessageType.Error);
                return;
            }

            if (selectedFontAsset == null)
            {
                EditorGUILayout.HelpBox("Assign a supported icon TMP Font Asset to preview and place icons.", MessageType.Warning);
                return;
            }

            if (activeIconSet == null)
            {
                EditorGUILayout.HelpBox(
                    "The selected TMP font asset is not part of a registered icon set. Use a Font Awesome icon font, or add another IconSetDefinition to this window.",
                    MessageType.Warning);
                return;
            }

            if (selectedFontAsset.atlasPopulationMode != AtlasPopulationMode.Dynamic &&
                selectedFontAsset.atlasPopulationMode != AtlasPopulationMode.DynamicOS)
            {
                EditorGUILayout.HelpBox(
                    $"This {activeIconSet.DisplayName} font asset is static. Matching icons are shown, but icons missing from the atlas will only render after you switch to a dynamic font asset or regenerate the atlas.",
                    MessageType.Info);
            }

            if (filteredIcons.Count == 0)
            {
                EditorGUILayout.HelpBox("No icons matched the current search/font filter.", MessageType.Info);
                return;
            }

            DrawIconGrid();
            EditorGUILayout.Space(6f);
            DrawSelectionHint();
        }

        private void DrawToolbar()
        {
            EditorGUI.BeginChangeCheck();
            TMP_FontAsset newFontAsset = (TMP_FontAsset)EditorGUILayout.ObjectField(
                "Font Asset",
                selectedFontAsset,
                typeof(TMP_FontAsset),
                false);

            if (EditorGUI.EndChangeCheck())
            {
                selectedFontAsset = newFontAsset;
                duotoneMode = IsSelectedFontDuotone();
                UpdateActiveIconSet();
                EnsureIconsLoaded(true);
                RebuildFilteredIcons();
            }

            EditorGUI.BeginChangeCheck();
            string newSearchText = EditorGUILayout.TextField("Search", searchText);
            if (EditorGUI.EndChangeCheck())
            {
                searchText = newSearchText ?? string.Empty;
                RebuildFilteredIcons();
            }

            EditorGUI.BeginChangeCheck();
            bool newSearchAcrossAllFontAssets = EditorGUILayout.ToggleLeft(
                "Search Across All Font Awesome Styles/SDFs",
                searchAcrossAllFontAssets);
            if (EditorGUI.EndChangeCheck())
            {
                searchAcrossAllFontAssets = newSearchAcrossAllFontAssets;
                RebuildFilteredIcons();
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
                selectedFontAsset = null;
                UpdateActiveIconSet();
                EnsureDefaultFontAsset();
                EnsureIconsLoaded(true);
                RebuildFilteredIcons();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Auto-Find"))
            {
                if (settingsIconSet.TryAutoConfigureMetadataPath())
                {
                    EnsureFontAssetsForIconSet(settingsIconSet);
                    selectedFontAsset = null;
                    UpdateActiveIconSet();
                    EnsureDefaultFontAsset();
                    EnsureIconsLoaded(true);
                    RebuildFilteredIcons();
                }
            }

            if (GUILayout.Button("Reset"))
            {
                settingsIconSet.ClearMetadataPathOverride();
                EnsureFontAssetsForIconSet(settingsIconSet);
                selectedFontAsset = null;
                UpdateActiveIconSet();
                EnsureDefaultFontAsset();
                EnsureIconsLoaded(true);
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

        private void DrawIconButton(BrowserIconGroup group)
        {
            BrowserIconResult result = group.GetActiveResult();
            FontAwesomeIconEntry icon = result.Icon;
            TMP_FontAsset previewFontAsset = result.PreviewFontAsset != null ? result.PreviewFontAsset : selectedFontAsset;
            Rect tileRect = GUILayoutUtility.GetRect(TileWidth, TileHeight, GUILayout.Width(TileWidth), GUILayout.Height(TileHeight));
            GUI.Box(tileRect, GUIContent.none);
            Rect buttonRect = new(tileRect.x + 4f, tileRect.y + 4f, tileRect.width - 8f, tileRect.height - 8f);
            string tooltip = BuildIconTooltip(result);
            bool glyphAvailable = FontHasGlyph(previewFontAsset, icon.Unicode);
            Event currentEvent = Event.current;
            Font previewFont = previewFontAsset != null ? previewFontAsset.sourceFontFile : null;
            GUIStyle glyphStyle = new(iconGlyphStyle) { font = previewFont };
            GUIStyle secondaryGlyphStyle = new(secondaryIconGlyphStyle) { font = previewFont };

            Color previousColor = GUI.color;
            if (!glyphAvailable)
            {
                GUI.color = new Color(previousColor.r, previousColor.g, previousColor.b, 0.65f);
            }

            GUI.Label(buttonRect, new GUIContent(string.Empty, tooltip), GUIStyle.none);

            if (currentEvent.type == EventType.MouseDown &&
                currentEvent.button == 0 &&
                currentEvent.clickCount >= 2 &&
                buttonRect.Contains(currentEvent.mousePosition))
            {
                GUI.FocusControl(string.Empty);
                GUIUtility.keyboardControl = 0;
                ApplyIcon(result);
                currentEvent.Use();
            }

            if (currentEvent.type == EventType.ContextClick && buttonRect.Contains(currentEvent.mousePosition))
            {
                ShowIconContextMenu(result);
                currentEvent.Use();
            }

            Rect glyphRect = new(buttonRect.x, buttonRect.y + 2f, buttonRect.width, 34f);
            if (ShouldRenderAsDuotone(icon, previewFontAsset))
            {
                GUI.Label(glyphRect, icon.Glyph, glyphStyle);

                if (TryGetSecondaryUnicodeForFont(icon, previewFontAsset, out uint secondaryUnicode) &&
                    (icon.SecondaryUnicode.HasValue || CanPreviewGlyphInFont(previewFontAsset, secondaryUnicode)))
                {
                    GUI.Label(glyphRect, char.ConvertFromUtf32((int)secondaryUnicode), secondaryGlyphStyle);
                }
            }
            else
            {
                GUI.Label(glyphRect, icon.Glyph, glyphStyle);
            }

            Rect nameRect = new(buttonRect.x + 2f, buttonRect.y + 38f, buttonRect.width - 4f, 16f);
            GUI.Label(nameRect, icon.Name, iconLabelStyle);

            Rect codeRect = new(buttonRect.x + 2f, buttonRect.y + 55f, buttonRect.width - 4f, 14f);
            GUI.Label(codeRect, $"U+{icon.Unicode:X4}", iconLabelStyle);

            if (group.Variants.Count > 1)
            {
                DrawVariantControls(buttonRect, group);
            }

            GUI.color = previousColor;
        }

        private void DrawVariantControls(Rect buttonRect, BrowserIconGroup group)
        {
            BrowserIconResult activeResult = group.GetActiveResult();
            string familyStyleLabel = string.IsNullOrWhiteSpace(activeResult.PreviewFamily) || string.IsNullOrWhiteSpace(activeResult.PreviewStyle)
                ? $"{group.ActiveVariantIndex + 1}/{group.Variants.Count}"
                : $"{activeResult.PreviewFamily}:{activeResult.PreviewStyle}";

            Rect leftRect = new(buttonRect.x + 2f, buttonRect.y + buttonRect.height - 16f, 14f, 14f);
            Rect rightRect = new(buttonRect.xMax - 16f, buttonRect.y + buttonRect.height - 16f, 14f, 14f);
            Rect labelRect = new(leftRect.xMax + 2f, buttonRect.y + buttonRect.height - 17f, buttonRect.width - 36f, 14f);

            if (GUI.Button(leftRect, "<", EditorStyles.miniButtonLeft))
            {
                CycleVariant(group, -1);
            }

            GUI.Label(labelRect, familyStyleLabel, iconLabelStyle);

            if (GUI.Button(rightRect, ">", EditorStyles.miniButtonRight))
            {
                CycleVariant(group, 1);
            }
        }

        private void CycleVariant(BrowserIconGroup group, int delta)
        {
            if (group.Variants.Count <= 1)
            {
                return;
            }

            int nextIndex = (group.ActiveVariantIndex + delta) % group.Variants.Count;
            if (nextIndex < 0)
            {
                nextIndex += group.Variants.Count;
            }

            group.ActiveVariantIndex = nextIndex;
            selectedVariantIndices[group.IconName] = nextIndex;
            Repaint();
        }

        private string BuildIconTooltip(BrowserIconResult result)
        {
            FontAwesomeIconEntry icon = result.Icon;
            if (result.PreviewFontAsset == null)
            {
                return $"{icon.Name}\n{icon.Label}\nU+{icon.Unicode:X4}";
            }

            string familyStyle = string.IsNullOrWhiteSpace(result.PreviewFamily) || string.IsNullOrWhiteSpace(result.PreviewStyle)
                ? result.PreviewFontAsset.name
                : $"{result.PreviewFamily} / {result.PreviewStyle}";
            return $"{icon.Name}\n{icon.Label}\nU+{icon.Unicode:X4}\n{familyStyle}";
        }

        private void ShowIconContextMenu(BrowserIconResult result)
        {
            FontAwesomeIconEntry icon = result.Icon;
            string inlineToken = BuildInlineTokenForCopy(result);
            GenericMenu menu = new();
            menu.AddItem(new GUIContent("Copy Inline Token"), false, () => EditorGUIUtility.systemCopyBuffer = inlineToken);
            menu.AddItem(new GUIContent("Copy Simple Inline Token"), false, () => EditorGUIUtility.systemCopyBuffer = $":fa-{icon.Name}:");
            menu.AddItem(new GUIContent("Copy Glyph"), false, () => EditorGUIUtility.systemCopyBuffer = icon.Glyph);
            menu.AddItem(new GUIContent("Copy Unicode"), false, () => EditorGUIUtility.systemCopyBuffer = $"U+{icon.Unicode:X4}");
            menu.ShowAsContext();
        }

        private string BuildInlineTokenForCopy(BrowserIconResult result)
        {
            FontAwesomeIconEntry icon = result.Icon;
            TMP_FontAsset tokenFontAsset = result.PreviewFontAsset != null ? result.PreviewFontAsset : selectedFontAsset;
            if (icon.IsValid &&
                activeIconSet != null &&
                activeIconSet.TryGetFamilyStyle(tokenFontAsset, out string family, out string style) &&
                !string.IsNullOrWhiteSpace(family) &&
                !string.IsNullOrWhiteSpace(style))
            {
                return $":fa-{family} fa-{style} fa-{icon.Name}:";
            }

            return $":fa-{icon.Name}:";
        }

        private void EnsureStyles()
        {
            if (iconGlyphStyle == null)
            {
                iconGlyphStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 24,
                    clipping = TextClipping.Clip,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.75f) },
                    hover = { textColor = new Color(1f, 1f, 1f, 1f) }
                };
            }

            if (iconLabelStyle == null)
            {
                iconLabelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
            }

            if (secondaryIconGlyphStyle == null)
            {
                secondaryIconGlyphStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 24,
                    clipping = TextClipping.Clip,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.25f) },
                    hover = { textColor = new Color(1f, 1f, 1f, 0.35f) }
                };
            }

            iconGlyphStyle.font = selectedFontAsset != null ? selectedFontAsset.sourceFontFile : null;
            secondaryIconGlyphStyle.font = iconGlyphStyle.font;
        }

        private void EnsureDefaultFontAsset()
        {
            if (selectedFontAsset != null)
            {
                return;
            }

            foreach (IconSetDefinition iconSetDefinition in iconSetDefinitions)
            {
                string[] guids = AssetDatabase.FindAssets(iconSetDefinition.DefaultFontSearchFilter);
                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    TMP_FontAsset fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                    if (fontAsset != null && iconSetDefinition.MatchesFontAsset(fontAsset))
                    {
                        selectedFontAsset = fontAsset;
                        activeIconSet = iconSetDefinition;
                        return;
                    }
                }
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

        private void RebuildFilteredIcons()
        {
            filteredIcons.Clear();

            if (!iconsLoaded)
            {
                return;
            }

            string query = (searchText ?? string.Empty).Trim();
            bool hasQuery = !string.IsNullOrWhiteSpace(query);
            bool broadenSearchScope = hasQuery && searchAcrossAllFontAssets;
            List<TMP_FontAsset> searchableFontAssets = broadenSearchScope
                ? GetCompatibleSearchFontAssets()
                : null;

            foreach (FontAwesomeIconEntry icon in allIcons)
            {
                if (hasQuery && !icon.MatchesSearch(query))
                {
                    continue;
                }

                if (broadenSearchScope)
                {
                    List<BrowserIconResult> searchResults = ResolveCompatibleSearchResults(icon, searchableFontAssets);
                    if (searchResults == null || searchResults.Count == 0)
                    {
                        continue;
                    }

                    filteredIcons.Add(new BrowserIconGroup(icon.Name, searchResults, GetSavedVariantIndex(icon.Name, searchResults.Count)));
                    continue;
                }

                if (!IconMatchesSelectedFont(icon))
                {
                    continue;
                }

                if (!SelectedFontCanRenderIcon(icon))
                {
                    continue;
                }

                if (duotoneMode && IsSelectedFontDuotone() && !icon.SupportsDuotone)
                {
                    continue;
                }

                List<BrowserIconResult> defaultResults = new() { new BrowserIconResult(icon, selectedFontAsset) };
                filteredIcons.Add(new BrowserIconGroup(icon.Name, defaultResults, 0));
            }

            scrollPosition = Vector2.zero;
            Repaint();
        }

        private List<TMP_FontAsset> GetCompatibleSearchFontAssets()
        {
            List<TMP_FontAsset> results = new();
            if (activeIconSet == null)
            {
                return results;
            }

            string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TMP_FontAsset fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (fontAsset == null ||
                    !activeIconSet.MatchesFontAsset(fontAsset) ||
                    results.Contains(fontAsset))
                {
                    continue;
                }

                results.Add(fontAsset);
            }

            return results;
        }

        private List<BrowserIconResult> ResolveCompatibleSearchResults(FontAwesomeIconEntry icon, List<TMP_FontAsset> candidateFontAssets)
        {
            List<BrowserIconResult> results = new();
            if (icon.IsValid == false)
            {
                return results;
            }

            if (candidateFontAssets == null || candidateFontAssets.Count == 0)
            {
                return results;
            }

            HashSet<string> seenVariants = new(StringComparer.OrdinalIgnoreCase);
            foreach (TMP_FontAsset fontAsset in candidateFontAssets)
            {
                if (fontAsset == null ||
                    !activeIconSet.TryGetFamilyStyle(fontAsset, out string family, out string style) ||
                    !icon.Supports(family, style))
                {
                    continue;
                }

                if (FontHasGlyph(fontAsset, icon.Unicode) || FontSourceHasGlyph(fontAsset, icon.Unicode))
                {
                    string variantKey = $"{family}|{style}|{AssetDatabase.GetAssetPath(fontAsset)}";
                    if (!seenVariants.Add(variantKey))
                    {
                        continue;
                    }

                    results.Add(new BrowserIconResult(icon, fontAsset, family, style));
                }
            }

            results.Sort(CompareBrowserResults);
            return results;
        }

        private static int CompareBrowserResults(BrowserIconResult left, BrowserIconResult right)
        {
            int nameComparison = string.Compare(left.Icon.Name, right.Icon.Name, StringComparison.OrdinalIgnoreCase);
            if (nameComparison != 0)
            {
                return nameComparison;
            }

            int familyComparison = string.Compare(left.PreviewFamily, right.PreviewFamily, StringComparison.OrdinalIgnoreCase);
            if (familyComparison != 0)
            {
                return familyComparison;
            }

            int styleComparison = string.Compare(left.PreviewStyle, right.PreviewStyle, StringComparison.OrdinalIgnoreCase);
            if (styleComparison != 0)
            {
                return styleComparison;
            }

            string leftAssetName = left.PreviewFontAsset != null ? left.PreviewFontAsset.name : string.Empty;
            string rightAssetName = right.PreviewFontAsset != null ? right.PreviewFontAsset.name : string.Empty;
            return string.Compare(leftAssetName, rightAssetName, StringComparison.OrdinalIgnoreCase);
        }

        private int GetSavedVariantIndex(string iconName, int variantCount)
        {
            if (!selectedVariantIndices.TryGetValue(iconName, out int savedIndex) || variantCount <= 0)
            {
                return 0;
            }

            return Mathf.Clamp(savedIndex, 0, variantCount - 1);
        }

        private void ApplyIcon(BrowserIconResult result)
        {
            FontAwesomeIconEntry icon = result.Icon;
            TMP_FontAsset targetFontAsset = result.PreviewFontAsset != null ? result.PreviewFontAsset : selectedFontAsset;
            if (targetFontAsset == null)
            {
                return;
            }

            if (ShouldRenderAsDuotone(icon, targetFontAsset))
            {
                ApplyDuotoneIcon(icon, targetFontAsset);
                return;
            }

            TMP_Text targetText = GetSelectedTextComponent();
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
            EnsureGlyphAvailable(targetFontAsset, icon);
            targetText.font = targetFontAsset;
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

        private void ApplyDuotoneIcon(FontAwesomeIconEntry icon, TMP_FontAsset targetFontAsset)
        {
            TMP_Text primaryText = GetSelectedTextComponent();
            bool createdNewObject = primaryText == null;

            if (primaryText == null)
            {
                primaryText = CreateTextObject();
            }

            if (primaryText == null)
            {
                return;
            }

            bool shouldRenderSecondaryGlyph =
                TryGetSecondaryUnicodeForFont(icon, targetFontAsset, true, out uint secondaryUnicode);
            if (!shouldRenderSecondaryGlyph)
            {
                bool shouldRenamePrimaryOnlyObject = createdNewObject || ShouldRenameAutoNamedObject(primaryText);

                Undo.RecordObject(primaryText, "Assign Font Awesome Icon");
                EnsureGlyphAvailable(targetFontAsset, icon);
                primaryText.font = targetFontAsset;
                primaryText.text = icon.Glyph;
                DisableDuotoneSync(primaryText);
                if (shouldRenamePrimaryOnlyObject)
                {
                    primaryText.name = $"{icon.DisplayName} FA Icon";
                }

                ConfigureTextLayer(primaryText, createdNewObject, 1f);
                EditorUtility.SetDirty(primaryText);
                Selection.activeGameObject = primaryText.gameObject;
                EditorGUIUtility.PingObject(primaryText.gameObject);
                return;
            }

            TMP_Text secondaryText = GetOrCreateSecondaryLayer(primaryText);
            if (secondaryText == null)
            {
                return;
            }

            bool shouldRenameObject = createdNewObject || ShouldRenameAutoNamedObject(primaryText);

            EnsureGlyphAvailable(targetFontAsset, icon);
            EnsureGlyphAvailable(targetFontAsset, secondaryUnicode);

            Undo.RecordObject(primaryText, "Assign Font Awesome Duotone Icon");
            Undo.RecordObject(secondaryText, "Assign Font Awesome Duotone Icon");

            primaryText.font = targetFontAsset;
            primaryText.text = icon.Glyph;
            secondaryText.font = targetFontAsset;
            secondaryText.text = char.ConvertFromUtf32((int)secondaryUnicode);

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

        private void EnsureGlyphAvailable(TMP_FontAsset selectedFontAsset, uint value)
        {
            if (selectedFontAsset == null || FontHasGlyph(selectedFontAsset, value))
            {
                return;
            }

            if (selectedFontAsset.atlasPopulationMode == AtlasPopulationMode.Dynamic ||
                selectedFontAsset.atlasPopulationMode == AtlasPopulationMode.DynamicOS)
            {
                selectedFontAsset.TryAddCharacters(new[] { value }, out uint[] _);
                EditorUtility.SetDirty(selectedFontAsset);
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
            TMP_Text secondaryText = GetSecondaryLayer(primaryText);

            if (sync != null)
            {
                sync.SetSecondaryVisible(false);
            }

            if (secondaryText != null)
            {
                secondaryText.text = string.Empty;
                secondaryText.gameObject.SetActive(false);
                EditorUtility.SetDirty(secondaryText);
            }

            if (sync != null)
            {
                sync.SyncNow();
                EditorUtility.SetDirty(sync);
            }
        }

        private static TMP_Text GetSecondaryLayer(TMP_Text primaryText)
        {
            Transform secondaryTransform = primaryText != null ? primaryText.transform.Find(SecondaryLayerObjectName) : null;
            return secondaryTransform != null ? secondaryTransform.GetComponent<TMP_Text>() : null;
        }

        private bool IconMatchesSelectedFont(FontAwesomeIconEntry icon)
        {
            if (selectedFontAsset == null)
            {
                return true;
            }

            if (activeIconSet == null)
            {
                return false;
            }

            if (!activeIconSet.TryGetFamilyStyle(selectedFontAsset, out string family, out string style))
            {
                return true;
            }

            return icon.Supports(family, style);
        }

        private bool CanUseDuotoneMode()
        {
            return selectedFontAsset != null && IsSelectedFontDuotone();
        }

        private bool IsSelectedFontDuotone()
        {
            return activeIconSet != null &&
                   selectedFontAsset != null &&
                   activeIconSet.TryGetFamilyStyle(selectedFontAsset, out string family, out _) &&
                   (family.IndexOf("duotone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    family.IndexOf("duo", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool ShouldRenderAsDuotone(FontAwesomeIconEntry icon)
        {
            return ShouldRenderAsDuotone(icon, selectedFontAsset);
        }

        private bool ShouldRenderAsDuotone(FontAwesomeIconEntry icon, TMP_FontAsset fontAsset)
        {
            return IsFontAssetDuotone(fontAsset) &&
                   TryGetSecondaryUnicodeForFont(icon, fontAsset, false, out _);
        }

        private bool SelectedFontCanRenderIcon(FontAwesomeIconEntry icon)
        {
            if (selectedFontAsset == null)
            {
                return true;
            }

            if (!CanInspectGlyphInFont(selectedFontAsset, icon.Unicode))
            {
                return false;
            }

            return !ShouldRenderAsDuotone(icon) ||
                   TryGetSecondaryUnicodeForFont(icon, selectedFontAsset, false, out _);
        }

        private bool TryGetSecondaryUnicodeForSelectedFont(FontAwesomeIconEntry icon, out uint secondaryUnicode)
        {
            return TryGetSecondaryUnicodeForFont(icon, selectedFontAsset, false, out secondaryUnicode);
        }

        private bool TryGetSecondaryUnicodeForFont(FontAwesomeIconEntry icon, TMP_FontAsset fontAsset, out uint secondaryUnicode)
        {
            return TryGetSecondaryUnicodeForFont(icon, fontAsset, false, out secondaryUnicode);
        }

        private bool TryGetSecondaryUnicodeForFont(FontAwesomeIconEntry icon, TMP_FontAsset fontAsset, bool allowDynamicLoad, out uint secondaryUnicode)
        {
            secondaryUnicode = 0u;
            if (activeIconSet == null ||
                fontAsset == null ||
                !activeIconSet.TryGetFamilyStyle(fontAsset, out string family, out string style) ||
                !icon.SupportsSecondaryLayer(family, style))
            {
                return false;
            }

            if (icon.SecondaryUnicode.HasValue)
            {
                secondaryUnicode = icon.SecondaryUnicode.Value;
                return true;
            }

            uint syntheticSecondaryUnicode = 0x100000u + icon.Unicode;
            if (allowDynamicLoad)
            {
                EnsureGlyphAvailable(fontAsset, syntheticSecondaryUnicode);
            }

            if (!FontHasGlyph(fontAsset, syntheticSecondaryUnicode))
            {
                return false;
            }

            secondaryUnicode = syntheticSecondaryUnicode;
            return true;
        }

        private bool CanRenderGlyphInSelectedFont(uint unicode)
        {
            return CanRenderGlyphInFont(selectedFontAsset, unicode);
        }

        private bool CanInspectGlyphInFont(TMP_FontAsset fontAsset, uint unicode)
        {
            if (fontAsset == null)
            {
                return true;
            }

            return FontHasGlyph(fontAsset, unicode) || FontSourceHasGlyph(fontAsset, unicode);
        }

        private bool CanRenderGlyphInFont(TMP_FontAsset fontAsset, uint unicode)
        {
            if (CanInspectGlyphInFont(fontAsset, unicode))
            {
                return true;
            }

            EnsureGlyphAvailable(fontAsset, unicode);
            return FontHasGlyph(fontAsset, unicode);
        }

        private bool CanPreviewGlyphInFont(TMP_FontAsset fontAsset, uint unicode)
        {
            if (fontAsset == null)
            {
                return false;
            }

            return FontHasGlyph(fontAsset, unicode);
        }

        private bool IsFontAssetDuotone(TMP_FontAsset fontAsset)
        {
            return activeIconSet != null &&
                   fontAsset != null &&
                   activeIconSet.TryGetFamilyStyle(fontAsset, out string family, out _) &&
                   (family.IndexOf("duotone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    family.IndexOf("duo", StringComparison.OrdinalIgnoreCase) >= 0);
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
            string selectedFontName = selectedFontAsset != null ? selectedFontAsset.name : string.Empty;
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
            IconSetDefinition matchedIconSet = null;
            if (selectedFontAsset != null)
            {
                foreach (IconSetDefinition iconSetDefinition in iconSetDefinitions)
                {
                    if (iconSetDefinition.MatchesFontAsset(selectedFontAsset))
                    {
                        matchedIconSet = iconSetDefinition;
                        break;
                    }
                }
            }

            if (!ReferenceEquals(activeIconSet, matchedIconSet))
            {
                activeIconSet = matchedIconSet;
                iconsLoaded = false;
                allIcons.Clear();
                filteredIcons.Clear();
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
                selectedFontAsset = null;
                EnsureDefaultFontAsset();
                UpdateActiveIconSet();
                EnsureIconsLoaded(true);
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

            selectedFontAsset = null;
            EnsureDefaultFontAsset();
            UpdateActiveIconSet();
            EnsureIconsLoaded(true);
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
                    true);

                if (fontAsset == null)
                {
                    retryRequired = true;
                    continue;
                }

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

        private static bool FontSourceHasGlyph(TMP_FontAsset fontAsset, uint unicode)
        {
            Font sourceFont = fontAsset?.sourceFontFile;
            if (sourceFont == null)
            {
                return false;
            }

            if (unicode <= char.MaxValue)
            {
                return sourceFont.HasCharacter((char)unicode);
            }

            System.Reflection.MethodInfo hasCharacterIntMethod = typeof(Font).GetMethod("HasCharacter", new[] { typeof(int) });
            if (hasCharacterIntMethod == null)
            {
                return false;
            }

            object result = hasCharacterIntMethod.Invoke(sourceFont, new object[] { unchecked((int)unicode) });
            return result is bool hasCharacter && hasCharacter;
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
                    List<string> searchTerms = ParseSearchTerms(entryJson);
                    List<FontAwesomeFamilyStyle> familyStyles = iconSet.ParseFreeFamilyStyles(entryJson);
                    uint? secondaryUnicode = iconSet.ParseSecondaryUnicode(entryJson, unicode, familyStyles);
                    List<FontAwesomeFamilyStyle> secondaryLayerStyles = iconSet.ParseRenderableSecondaryStyles(entryJson);
                    results.Add(new FontAwesomeIconEntry(iconName, label, unicode, secondaryUnicode, familyStyles, secondaryLayerStyles, searchTerms));
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

        private static List<string> ParseSearchTerms(string entryJson)
        {
            List<string> terms = new();
            string searchJson = FindJsonObjectProperty(entryJson, "search");
            if (string.IsNullOrWhiteSpace(searchJson))
            {
                return terms;
            }

            return FindJsonStringArrayProperty(searchJson, "terms");
        }

        private static string FindJsonObjectProperty(string json, string propertyName)
        {
            string pattern = $"\"{propertyName}\"";
            int propertyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (propertyIndex < 0)
            {
                return string.Empty;
            }

            int colonIndex = json.IndexOf(':', propertyIndex + pattern.Length);
            if (colonIndex < 0)
            {
                return string.Empty;
            }

            int objectStart = SkipWhitespace(json, colonIndex + 1);
            if (objectStart >= json.Length || json[objectStart] != '{')
            {
                return string.Empty;
            }

            int objectEnd = FindObjectEnd(json, objectStart);
            if (objectEnd <= objectStart)
            {
                return string.Empty;
            }

            return json.Substring(objectStart, objectEnd - objectStart + 1);
        }

        private static List<string> FindJsonStringArrayProperty(string json, string propertyName)
        {
            List<string> values = new();
            string pattern = $"\"{propertyName}\"";
            int propertyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (propertyIndex < 0)
            {
                return values;
            }

            int colonIndex = json.IndexOf(':', propertyIndex + pattern.Length);
            if (colonIndex < 0)
            {
                return values;
            }

            int arrayStart = SkipWhitespace(json, colonIndex + 1);
            if (arrayStart >= json.Length || json[arrayStart] != '[')
            {
                return values;
            }

            int arrayEnd = FindArrayEnd(json, arrayStart);
            if (arrayEnd <= arrayStart)
            {
                return values;
            }

            int index = arrayStart + 1;
            while (index < arrayEnd)
            {
                index = SkipWhitespace(json, index);
                if (index >= arrayEnd || json[index] == ']')
                {
                    break;
                }

                string value = ReadJsonString(json, ref index);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }

                index = SkipWhitespace(json, index);
                if (index < arrayEnd && json[index] == ',')
                {
                    index++;
                }
            }

            return values;
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

        private readonly struct FontAwesomeIconEntry
        {
            public FontAwesomeIconEntry(string name, string label, uint unicode, uint? secondaryUnicode, List<FontAwesomeFamilyStyle> freeFamilyStyles, List<FontAwesomeFamilyStyle> secondaryLayerStyles, List<string> searchTerms)
            {
                Name = name;
                Label = string.IsNullOrWhiteSpace(label) ? name : label;
                Unicode = unicode;
                SecondaryUnicode = secondaryUnicode;
                FreeFamilyStyles = freeFamilyStyles ?? new List<FontAwesomeFamilyStyle>();
                SecondaryLayerStyles = secondaryLayerStyles ?? new List<FontAwesomeFamilyStyle>();
                SearchTerms = searchTerms ?? new List<string>();
            }

            public string Name { get; }
            public string Label { get; }
            public uint Unicode { get; }
            public uint? SecondaryUnicode { get; }
            public List<FontAwesomeFamilyStyle> FreeFamilyStyles { get; }
            public List<FontAwesomeFamilyStyle> SecondaryLayerStyles { get; }
            public List<string> SearchTerms { get; }
            public string Glyph => char.ConvertFromUtf32((int)Unicode);
            public string SecondaryGlyph => SecondaryUnicode.HasValue ? char.ConvertFromUtf32((int)SecondaryUnicode.Value) : string.Empty;
            public string DisplayName => ToDisplayName();
            public bool IsValid => !string.IsNullOrWhiteSpace(Name);
            public bool HasSecondaryGlyph => SecondaryUnicode.HasValue || (SecondaryLayerStyles != null && SecondaryLayerStyles.Count > 0);
            public bool SupportsDuotone =>
                SupportsFamily("duotone") ||
                SupportsFamily("sharp-duotone") ||
                SupportsFamily("jelly-duo") ||
                SupportsFamily("notdog-duo") ||
                SupportsFamily("utility-duo");

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

            public bool SupportsFamily(string family)
            {
                if (FreeFamilyStyles == null || FreeFamilyStyles.Count == 0)
                {
                    return true;
                }

                foreach (FontAwesomeFamilyStyle familyStyle in FreeFamilyStyles)
                {
                    if (string.Equals(familyStyle.Family, family, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool MatchesSearch(string query)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    return true;
                }

                if (Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (SearchTerms == null)
                {
                    return false;
                }

                foreach (string term in SearchTerms)
                {
                    if (!string.IsNullOrWhiteSpace(term) &&
                        term.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool SupportsSecondaryLayer(string family, string style)
            {
                if (SecondaryLayerStyles == null || SecondaryLayerStyles.Count == 0)
                {
                    return false;
                }

                foreach (FontAwesomeFamilyStyle familyStyle in SecondaryLayerStyles)
                {
                    if (string.Equals(familyStyle.Family, family, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(familyStyle.Style, style, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool TryGetSecondaryUnicode(string family, string style, out uint unicode)
            {
                unicode = 0u;
                if (!SupportsSecondaryLayer(family, style) || !SecondaryUnicode.HasValue)
                {
                    return false;
                }

                unicode = SecondaryUnicode.Value;
                return true;
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

        private readonly struct BrowserIconResult
        {
            public BrowserIconResult(FontAwesomeIconEntry icon, TMP_FontAsset previewFontAsset, string previewFamily = null, string previewStyle = null)
            {
                Icon = icon;
                PreviewFontAsset = previewFontAsset;
                PreviewFamily = previewFamily;
                PreviewStyle = previewStyle;
            }

            public FontAwesomeIconEntry Icon { get; }
            public TMP_FontAsset PreviewFontAsset { get; }
            public string PreviewFamily { get; }
            public string PreviewStyle { get; }
            public bool IsValid => Icon.IsValid;
        }

        private sealed class BrowserIconGroup
        {
            public BrowserIconGroup(string iconName, List<BrowserIconResult> variants, int activeVariantIndex)
            {
                IconName = iconName ?? string.Empty;
                Variants = variants ?? new List<BrowserIconResult>();
                ActiveVariantIndex = Variants.Count == 0 ? 0 : Mathf.Clamp(activeVariantIndex, 0, Variants.Count - 1);
            }

            public string IconName { get; }
            public List<BrowserIconResult> Variants { get; }
            public int ActiveVariantIndex { get; set; }

            public BrowserIconResult GetActiveResult()
            {
                if (Variants == null || Variants.Count == 0)
                {
                    return default;
                }

                int clampedIndex = Mathf.Clamp(ActiveVariantIndex, 0, Variants.Count - 1);
                return Variants[clampedIndex];
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
            public abstract List<FontAwesomeFamilyStyle> ParseRenderableSecondaryStyles(string entryJson);
            public abstract uint? ParseSecondaryUnicode(string entryJson, uint primaryUnicode, List<FontAwesomeFamilyStyle> familyStyles);

            public string IconsMetadataPath => ResolveMetadataPath();

            public void SetMetadataPathOverride(string path)
            {
                string normalizedPath = ResolvePreferredMetadataPath(NormalizeMetadataPath(path));
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

            protected virtual string ResolvePreferredMetadataPath(string path)
            {
                return path;
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
                    if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(GetSdfAssetPath(fontAssetPath)) != null)
                    {
                        return false;
                    }
                }

                return true;
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
                string overriddenPath = ResolvePreferredMetadataPath(NormalizeMetadataPath(EditorPrefs.GetString(prefsKey, string.Empty)));
                if (!string.IsNullOrWhiteSpace(overriddenPath))
                {
                    return overriddenPath;
                }

                string discoveredPath = ResolvePreferredMetadataPath(FindMetadataPath());
                if (!string.IsNullOrWhiteSpace(discoveredPath))
                {
                    EditorPrefs.SetString(prefsKey, discoveredPath);
                    return discoveredPath;
                }

                return ResolvePreferredMetadataPath(DefaultIconsMetadataPath);
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
            private const string FontAwesomeMetadataPath = "Assets/Fonts/fontawesome-free-7.2.0-desktop/metadata/icon-families.json";
            private const string FontAwesomeInstallRoot = "Assets/Fonts/fontawesome-free-7.2.0-desktop";
            private const string FontAwesomeDownloadUrl = "https://github.com/FortAwesome/Font-Awesome/releases/download/7.2.0/fontawesome-free-7.2.0-desktop.zip";

            public override string DisplayName => "Font Awesome";
            public override string DefaultFontSearchFilter => "t:TMP_FontAsset Font Awesome";
            protected override string DefaultIconsMetadataPath => FontAwesomeMetadataPath;
            protected override string MetadataSearchFilter => "icon-families t:TextAsset";
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
                return (normalizedPath.EndsWith("/metadata/icon-families.json", StringComparison.OrdinalIgnoreCase) ||
                        normalizedPath.EndsWith("/metadata/icons.json", StringComparison.OrdinalIgnoreCase)) &&
                       normalizedPath.IndexOf("fontawesome", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            protected override string ResolvePreferredMetadataPath(string path)
            {
                string normalizedPath = path.Replace('\\', '/');
                if (normalizedPath.EndsWith("/metadata/icons.json", StringComparison.OrdinalIgnoreCase))
                {
                    string familyMetadataPath = normalizedPath.Replace("/metadata/icons.json", "/metadata/icon-families.json");
                    if (AssetDatabase.LoadAssetAtPath<TextAsset>(familyMetadataPath) != null)
                    {
                        return familyMetadataPath;
                    }
                }

                return normalizedPath;
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
                string combinedName = $"{sourceName} {assetName}";
                string normalizedName = Regex.Replace(combinedName.ToLowerInvariant(), @"[\s_]+", " ").Trim();

                if (normalizedName.Contains("sharp duotone"))
                {
                    family = "sharp-duotone";
                    style = ResolveNamedStyle(normalizedName);
                    return !string.IsNullOrWhiteSpace(style);
                }

                if (normalizedName.Contains("sharp"))
                {
                    family = "sharp";
                    style = ResolveNamedStyle(normalizedName);
                    return !string.IsNullOrWhiteSpace(style);
                }

                if (normalizedName.Contains("duotone"))
                {
                    family = "duotone";
                    style = ResolveNamedStyle(normalizedName);
                    return !string.IsNullOrWhiteSpace(style);
                }

                if (normalizedName.Contains("brands"))
                {
                    family = "classic";
                    style = "brands";
                    return true;
                }

                if (TryResolveNamedFamilyStyle(normalizedName, out family, out style))
                {
                    return true;
                }

                if (normalizedName.Contains("pro") || normalizedName.Contains("classic"))
                {
                    family = "classic";
                    style = ResolveNamedStyle(normalizedName);
                    return !string.IsNullOrWhiteSpace(style);
                }

                return false;
            }

            public override List<FontAwesomeFamilyStyle> ParseFreeFamilyStyles(string entryJson)
            {
                List<FontAwesomeFamilyStyle> styles = new();

                int familyStylesByLicenseIndex = entryJson.IndexOf("\"familyStylesByLicense\"", StringComparison.Ordinal);
                if (familyStylesByLicenseIndex >= 0)
                {
                    int objectStart = entryJson.IndexOf('{', familyStylesByLicenseIndex);
                    if (objectStart >= 0)
                    {
                        int objectEnd = FindObjectEnd(entryJson, objectStart);
                        if (objectEnd > objectStart)
                        {
                            string familyStylesJson = entryJson.Substring(objectStart, objectEnd - objectStart + 1);
                            MatchCollection familyStyleMatches = new Regex(JsonFamilyStylePattern, RegexOptions.Compiled).Matches(familyStylesJson);
                            foreach (Match match in familyStyleMatches)
                            {
                                styles.Add(new FontAwesomeFamilyStyle(
                                    JsonUnescape(match.Groups["family"].Value),
                                    JsonUnescape(match.Groups["style"].Value)));
                            }
                        }
                    }
                }

                if (styles.Count > 0)
                {
                    return styles;
                }

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
                        string family = "classic";
                        string normalizedStyle = style;
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
                                string family = "classic";
                                string normalizedStyle = style;
                                styles.Add(new FontAwesomeFamilyStyle(family, normalizedStyle));
                            }
                        }
                    }
                }

                return styles;
            }

            public override List<FontAwesomeFamilyStyle> ParseRenderableSecondaryStyles(string entryJson)
            {
                List<FontAwesomeFamilyStyle> styles = new();

                int svgsIndex = entryJson.IndexOf("\"svgs\"", StringComparison.Ordinal);
                if (svgsIndex < 0)
                {
                    return styles;
                }

                int svgsObjectStart = entryJson.IndexOf('{', svgsIndex);
                if (svgsObjectStart < 0)
                {
                    return styles;
                }

                int svgsObjectEnd = FindObjectEnd(entryJson, svgsObjectStart);
                if (svgsObjectEnd <= svgsObjectStart)
                {
                    return styles;
                }

                string svgsJson = entryJson.Substring(svgsObjectStart, svgsObjectEnd - svgsObjectStart + 1);
                int familyIndex = SkipWhitespace(svgsJson, 0);
                if (familyIndex >= svgsJson.Length || svgsJson[familyIndex] != '{')
                {
                    return styles;
                }

                familyIndex++;
                while (familyIndex < svgsJson.Length)
                {
                    familyIndex = SkipWhitespace(svgsJson, familyIndex);
                    if (familyIndex >= svgsJson.Length || svgsJson[familyIndex] == '}')
                    {
                        break;
                    }

                    string family = ReadJsonString(svgsJson, ref familyIndex);
                    familyIndex = SkipWhitespace(svgsJson, familyIndex);
                    if (familyIndex >= svgsJson.Length || svgsJson[familyIndex] != ':')
                    {
                        break;
                    }

                    familyIndex++;
                    familyIndex = SkipWhitespace(svgsJson, familyIndex);
                    if (familyIndex >= svgsJson.Length || svgsJson[familyIndex] != '{')
                    {
                        break;
                    }

                    int familyObjectStart = familyIndex;
                    int familyObjectEnd = FindObjectEnd(svgsJson, familyObjectStart);
                    if (familyObjectEnd <= familyObjectStart)
                    {
                        break;
                    }

                    string familyJson = svgsJson.Substring(familyObjectStart, familyObjectEnd - familyObjectStart + 1);
                    ParseRenderableSecondaryStylesForFamily(family, familyJson, styles);

                    familyIndex = familyObjectEnd + 1;
                    familyIndex = SkipWhitespace(svgsJson, familyIndex);
                    if (familyIndex < svgsJson.Length && svgsJson[familyIndex] == ',')
                    {
                        familyIndex++;
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

                return null;
            }

            private static void ParseRenderableSecondaryStylesForFamily(string family, string familyJson, List<FontAwesomeFamilyStyle> styles)
            {
                int styleIndex = SkipWhitespace(familyJson, 0);
                if (styleIndex >= familyJson.Length || familyJson[styleIndex] != '{')
                {
                    return;
                }

                styleIndex++;
                while (styleIndex < familyJson.Length)
                {
                    styleIndex = SkipWhitespace(familyJson, styleIndex);
                    if (styleIndex >= familyJson.Length || familyJson[styleIndex] == '}')
                    {
                        break;
                    }

                    string style = ReadJsonString(familyJson, ref styleIndex);
                    styleIndex = SkipWhitespace(familyJson, styleIndex);
                    if (styleIndex >= familyJson.Length || familyJson[styleIndex] != ':')
                    {
                        break;
                    }

                    styleIndex++;
                    styleIndex = SkipWhitespace(familyJson, styleIndex);
                    if (styleIndex >= familyJson.Length || familyJson[styleIndex] != '{')
                    {
                        break;
                    }

                    int styleObjectStart = styleIndex;
                    int styleObjectEnd = FindObjectEnd(familyJson, styleObjectStart);
                    if (styleObjectEnd <= styleObjectStart)
                    {
                        break;
                    }

                    string styleJson = familyJson.Substring(styleObjectStart, styleObjectEnd - styleObjectStart + 1);
                    if (HasRenderableSecondaryLayer(styleJson))
                    {
                        styles.Add(new FontAwesomeFamilyStyle(family, style));
                    }

                    styleIndex = styleObjectEnd + 1;
                    styleIndex = SkipWhitespace(familyJson, styleIndex);
                    if (styleIndex < familyJson.Length && familyJson[styleIndex] == ',')
                    {
                        styleIndex++;
                    }
                }
            }

            private static bool HasRenderableSecondaryLayer(string entryJson)
            {
                MatchCollection pathMatches = Regex.Matches(
                    entryJson,
                    "\"path\"\\s*:\\s*\\[\\s*\"(?<first>(?:\\\\.|[^\"\\\\])*)\"\\s*,\\s*\"(?<second>(?:\\\\.|[^\"\\\\])*)\"",
                    RegexOptions.Compiled | RegexOptions.Singleline);

                foreach (Match match in pathMatches)
                {
                    string first = JsonUnescape(match.Groups["first"].Value);
                    string second = JsonUnescape(match.Groups["second"].Value);
                    if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static string ResolveNamedStyle(string normalizedName)
            {
                if (normalizedName.Contains("brands"))
                {
                    return "brands";
                }

                if (normalizedName.Contains("semibold"))
                {
                    return "semibold";
                }

                if (normalizedName.Contains("solid"))
                {
                    return "solid";
                }

                if (normalizedName.Contains("regular"))
                {
                    return "regular";
                }

                if (normalizedName.Contains("light"))
                {
                    return "light";
                }

                if (normalizedName.Contains("thin"))
                {
                    return "thin";
                }

                return null;
            }

            private static bool TryResolveNamedFamilyStyle(string normalizedName, out string family, out string style)
            {
                family = null;
                style = null;

                if (normalizedName.Contains("utility duo"))
                {
                    family = "utility-duo";
                }
                else if (normalizedName.Contains("utility fill"))
                {
                    family = "utility-fill";
                }
                else if (normalizedName.Contains("jelly duo"))
                {
                    family = "jelly-duo";
                }
                else if (normalizedName.Contains("jelly fill"))
                {
                    family = "jelly-fill";
                }
                else if (normalizedName.Contains("notdog duo"))
                {
                    family = "notdog-duo";
                }
                else if (normalizedName.Contains("slab press"))
                {
                    family = "slab-press";
                }
                else if (normalizedName.Contains("chisel"))
                {
                    family = "chisel";
                }
                else if (normalizedName.Contains("etch"))
                {
                    family = "etch";
                }
                else if (normalizedName.Contains("graphite"))
                {
                    family = "graphite";
                }
                else if (normalizedName.Contains("jelly"))
                {
                    family = "jelly";
                }
                else if (normalizedName.Contains("notdog"))
                {
                    family = "notdog";
                }
                else if (normalizedName.Contains("slab"))
                {
                    family = "slab";
                }
                else if (normalizedName.Contains("thumbprint"))
                {
                    family = "thumbprint";
                }
                else if (normalizedName.Contains("utility"))
                {
                    family = "utility";
                }
                else if (normalizedName.Contains("whiteboard"))
                {
                    family = "whiteboard";
                }

                if (string.IsNullOrWhiteSpace(family))
                {
                    return false;
                }

                style = ResolveNamedStyle(normalizedName);
                return !string.IsNullOrWhiteSpace(style);
            }
        }
    }
}
