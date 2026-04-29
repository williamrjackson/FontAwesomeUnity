using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;

namespace Wrj.FontAwesome
{
    [AddComponentMenu("TextMeshPro/Font Awesome Inline Text")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TMP_Text))]
    public sealed class FontAwesomeInlineText : MonoBehaviour, ITextPreprocessor
    {
        private const string DefaultMetadataPath = "Assets/Fonts/fontawesome-free-7.2.0-desktop/metadata/icon-families.json";
        private const string TokenPrefix = ":fa-";
        private const string SecondaryLayerObjectName = "FA Secondary Layer";
        private const string HiddenAlphaTag = "<alpha=#00>";
        private const string HiddenAlphaCloseTag = "</alpha>";

        [SerializeField] private TMP_Text targetText;
        [SerializeField] private TextAsset metadata;
        [SerializeField] private TMP_FontAsset defaultIconFontAsset;
        [SerializeField] private TMP_FontAsset brandsFontAsset;
        [SerializeField] private List<TMP_FontAsset> availableIconFontAssets = new();
        [SerializeField] private List<TMP_FontAsset> additionalFallbackFontAssets = new();

        private readonly Dictionary<string, InlineIconMetadata> iconLookup = new(StringComparer.OrdinalIgnoreCase);
        private bool lookupBuilt;
        private string cachedMetadataText;
        private TMP_FontAsset lastConfiguredPrimaryFontAsset;

        private static readonly string[] KnownFamilySelectors =
        {
            "classic",
            "duotone",
            "sharp",
            "sharp-duotone",
            "chisel",
            "etch",
            "graphite",
            "jelly",
            "jelly-duo",
            "jelly-fill",
            "notdog",
            "notdog-duo",
            "slab",
            "slab-press",
            "thumbprint",
            "utility",
            "utility-duo",
            "utility-fill",
            "whiteboard"
        };

        private static readonly string[] KnownStyleSelectors =
        {
            "brands",
            "semibold",
            "solid",
            "regular",
            "light",
            "thin"
        };

        public string PreprocessText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            EnsureLookupBuilt();
            if (iconLookup.Count == 0)
            {
                return text;
            }

            StringBuilder builder = null;
            int index = 0;

            while (index < text.Length)
            {
                int tokenStart = text.IndexOf(TokenPrefix, index, StringComparison.OrdinalIgnoreCase);
                if (tokenStart < 0)
                {
                    break;
                }

                int nameStart = tokenStart + TokenPrefix.Length;
                int tokenEnd = text.IndexOf(':', nameStart);
                if (tokenEnd < 0)
                {
                    break;
                }

                string tokenContent = text.Substring(nameStart, tokenEnd - nameStart);
                if (!TryParseInlineToken(tokenContent, out InlineTokenSpec token) ||
                    !iconLookup.TryGetValue(token.IconName, out InlineIconMetadata iconMetadata))
                {
                    index = tokenStart + 1;
                    continue;
                }

                builder ??= new StringBuilder(text.Length + 32);
                builder.Append(text, index, tokenStart - index);
                AppendResolvedToken(builder, token, iconMetadata.PrimaryUnicode);
                index = tokenEnd + 1;
            }

            if (builder == null)
            {
                return text;
            }

            if (index < text.Length)
            {
                builder.Append(text, index, text.Length - index);
            }

            return builder.ToString();
        }

        private void Reset()
        {
            CacheTargetText();
            AutoConfigureDefaults();
            ApplyConfiguration();
        }

        private void OnEnable()
        {
            CacheTargetText();
            AutoConfigureDefaults();
            ApplyConfiguration();
        }

        private void OnValidate()
        {
            CacheTargetText();
            AutoConfigureDefaults();
            ApplyConfiguration();
        }

        private void LateUpdate()
        {
            if (targetText == null)
            {
                CacheTargetText();
            }

            if (targetText == null)
            {
                return;
            }

            if (!ReferenceEquals(targetText.textPreprocessor, this))
            {
                targetText.textPreprocessor = this;
            }

            if (targetText.font != lastConfiguredPrimaryFontAsset)
            {
                ConfigureFallbackFontAssets();
            }

            UpdateSecondaryLayer();
        }

        private void OnDisable()
        {
            if (targetText != null && ReferenceEquals(targetText.textPreprocessor, this))
            {
                targetText.textPreprocessor = null;
                targetText.havePropertiesChanged = true;
                targetText.SetAllDirty();
            }
        }

        private void CacheTargetText()
        {
            if (targetText == null)
            {
                targetText = GetComponent<TMP_Text>();
            }
        }

        private void ApplyConfiguration()
        {
            if (targetText == null)
            {
                return;
            }

            if (!ReferenceEquals(targetText.textPreprocessor, this))
            {
                targetText.textPreprocessor = this;
            }

            lookupBuilt = false;
            RegisterKnownFontAssets();
            ConfigureFallbackFontAssets();
            UpdateSecondaryLayer();
            targetText.havePropertiesChanged = true;
            targetText.SetAllDirty();
            targetText.ForceMeshUpdate();
        }

        private void ConfigureFallbackFontAssets()
        {
            if (targetText == null || targetText.font == null)
            {
                return;
            }

            List<TMP_FontAsset> fallbackTable = targetText.font.fallbackFontAssetTable;
            if (fallbackTable == null)
            {
                targetText.font.fallbackFontAssetTable = new List<TMP_FontAsset>();
                fallbackTable = targetText.font.fallbackFontAssetTable;
            }

            EnsureFallbackFontAsset(fallbackTable, brandsFontAsset);
            EnsureFallbackFontAsset(fallbackTable, defaultIconFontAsset);

            for (int i = 0; i < additionalFallbackFontAssets.Count; i++)
            {
                EnsureFallbackFontAsset(fallbackTable, additionalFallbackFontAssets[i]);
            }

            lastConfiguredPrimaryFontAsset = targetText.font;
        }

        private void AppendResolvedToken(StringBuilder builder, InlineTokenSpec token, uint unicode)
        {
            string glyph = char.ConvertFromUtf32((int)unicode);
            TMP_FontAsset resolvedFontAsset = ResolveInlineFontAsset(token);
            if (resolvedFontAsset == null)
            {
                builder.Append(glyph);
                return;
            }

            EnsureGlyphAvailable(resolvedFontAsset, unicode);
            MaterialReferenceManager.AddFontAsset(resolvedFontAsset);
            builder.Append("<font=\"");
            builder.Append(resolvedFontAsset.name);
            builder.Append("\">");
            builder.Append(glyph);
            builder.Append("</font>");
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

        private static void EnsureGlyphAvailable(TMP_FontAsset fontAsset, uint unicode)
        {
            if (fontAsset == null || FontHasGlyph(fontAsset, unicode))
            {
                return;
            }

            if (fontAsset.atlasPopulationMode != AtlasPopulationMode.Dynamic &&
                fontAsset.atlasPopulationMode != AtlasPopulationMode.DynamicOS)
            {
                return;
            }

            fontAsset.TryAddCharacters(new[] { unicode }, out uint[] _);
        }

        private TMP_FontAsset ResolveInlineFontAsset(InlineTokenSpec token)
        {
            if (string.IsNullOrWhiteSpace(token.Family) && string.IsNullOrWhiteSpace(token.Style))
            {
                return null;
            }

            string requestedFamily = token.Family;
            string requestedStyle = token.Style;

            if (string.IsNullOrWhiteSpace(requestedStyle))
            {
                if (TryGetFamilyStyle(defaultIconFontAsset, out string defaultFamily, out string defaultStyle) &&
                    string.Equals(defaultFamily, requestedFamily, StringComparison.OrdinalIgnoreCase))
                {
                    requestedStyle = defaultStyle;
                }
            }

            if (string.Equals(requestedStyle, "brands", StringComparison.OrdinalIgnoreCase))
            {
                return brandsFontAsset != null ? brandsFontAsset : FindMatchingAvailableFontAsset("classic", "brands");
            }

            if (string.IsNullOrWhiteSpace(requestedFamily) && !string.IsNullOrWhiteSpace(requestedStyle))
            {
                requestedFamily = "classic";
            }

            if (string.IsNullOrWhiteSpace(requestedFamily))
            {
                return defaultIconFontAsset;
            }

            TMP_FontAsset exactMatch = FindMatchingAvailableFontAsset(requestedFamily, requestedStyle);
            if (exactMatch != null)
            {
                return exactMatch;
            }

            if (defaultIconFontAsset != null &&
                TryGetFamilyStyle(defaultIconFontAsset, out string defaultMatchFamily, out string defaultMatchStyle) &&
                string.Equals(defaultMatchFamily, requestedFamily, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(requestedStyle) ||
                 string.Equals(defaultMatchStyle, requestedStyle, StringComparison.OrdinalIgnoreCase)))
            {
                return defaultIconFontAsset;
            }

            return null;
        }

        private TMP_FontAsset FindMatchingAvailableFontAsset(string family, string style)
        {
            foreach (TMP_FontAsset fontAsset in EnumerateKnownFontAssets())
            {
                if (fontAsset == null || !TryGetFamilyStyle(fontAsset, out string assetFamily, out string assetStyle))
                {
                    continue;
                }

                if (!string.Equals(assetFamily, family, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(style) &&
                    !string.Equals(assetStyle, style, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return fontAsset;
            }

            return null;
        }

        private IEnumerable<TMP_FontAsset> EnumerateKnownFontAssets()
        {
            if (brandsFontAsset != null)
            {
                yield return brandsFontAsset;
            }

            if (defaultIconFontAsset != null)
            {
                yield return defaultIconFontAsset;
            }

            for (int i = 0; i < availableIconFontAssets.Count; i++)
            {
                TMP_FontAsset fontAsset = availableIconFontAssets[i];
                if (fontAsset != null)
                {
                    yield return fontAsset;
                }
            }

            for (int i = 0; i < additionalFallbackFontAssets.Count; i++)
            {
                TMP_FontAsset fontAsset = additionalFallbackFontAssets[i];
                if (fontAsset != null)
                {
                    yield return fontAsset;
                }
            }
        }

        private void RegisterKnownFontAssets()
        {
            foreach (TMP_FontAsset fontAsset in EnumerateKnownFontAssets())
            {
                if (fontAsset != null)
                {
                    MaterialReferenceManager.AddFontAsset(fontAsset);
                }
            }
        }

        private static bool TryParseInlineToken(string tokenContent, out InlineTokenSpec token)
        {
            token = default;
            if (string.IsNullOrWhiteSpace(tokenContent))
            {
                return false;
            }

            string[] segments = tokenContent.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return false;
            }

            string iconName = null;
            string family = null;
            string style = null;

            for (int i = 0; i < segments.Length; i++)
            {
                string normalizedSegment = NormalizeTokenSegment(segments[i]);
                if (string.IsNullOrWhiteSpace(normalizedSegment))
                {
                    continue;
                }

                if (TryNormalizeFamilySelector(normalizedSegment, out string normalizedFamily))
                {
                    family ??= normalizedFamily;
                    continue;
                }

                if (TryNormalizeStyleSelector(normalizedSegment, out string normalizedStyle))
                {
                    style ??= normalizedStyle;
                    continue;
                }

                iconName ??= normalizedSegment;
            }

            if (string.IsNullOrWhiteSpace(iconName))
            {
                return false;
            }

            if (string.Equals(style, "brands", StringComparison.OrdinalIgnoreCase))
            {
                family ??= "classic";
            }
            else if (string.IsNullOrWhiteSpace(family) && !string.IsNullOrWhiteSpace(style))
            {
                family = "classic";
            }

            token = new InlineTokenSpec(iconName, family, style);
            return true;
        }

        private static string NormalizeTokenSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return string.Empty;
            }

            string normalized = segment.Trim();
            if (normalized.StartsWith("fa-", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(3);
            }

            return normalized.Trim().ToLowerInvariant();
        }

        private static bool TryNormalizeFamilySelector(string value, out string family)
        {
            for (int i = 0; i < KnownFamilySelectors.Length; i++)
            {
                if (string.Equals(KnownFamilySelectors[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    family = KnownFamilySelectors[i];
                    return true;
                }
            }

            family = null;
            return false;
        }

        private static bool TryNormalizeStyleSelector(string value, out string style)
        {
            for (int i = 0; i < KnownStyleSelectors.Length; i++)
            {
                if (string.Equals(KnownStyleSelectors[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    style = KnownStyleSelectors[i];
                    return true;
                }
            }

            style = null;
            return false;
        }

        private static bool TryGetFamilyStyle(TMP_FontAsset fontAsset, out string family, out string style)
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
            string normalizedName = NormalizeFontAssetName(combinedName);

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

        private static string NormalizeFontAssetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            StringBuilder builder = new(name.Length);
            bool previousWasWhitespace = false;

            for (int i = 0; i < name.Length; i++)
            {
                char current = char.ToLowerInvariant(name[i]);
                if (char.IsLetterOrDigit(current))
                {
                    builder.Append(current);
                    previousWasWhitespace = false;
                    continue;
                }

                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }
            }

            return builder.ToString().Trim();
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

        private static void EnsureFallbackFontAsset(List<TMP_FontAsset> fallbackTable, TMP_FontAsset fontAsset)
        {
            if (fallbackTable == null || fontAsset == null)
            {
                return;
            }

            if (!fallbackTable.Contains(fontAsset))
            {
                fallbackTable.Add(fontAsset);
            }
        }

        private void EnsureLookupBuilt()
        {
            string metadataText = metadata != null ? metadata.text : string.Empty;
            if (lookupBuilt && string.Equals(cachedMetadataText, metadataText, StringComparison.Ordinal))
            {
                return;
            }

            iconLookup.Clear();
            cachedMetadataText = metadataText;
            lookupBuilt = true;

            if (string.IsNullOrWhiteSpace(metadataText))
            {
                return;
            }

            ParseIconLookup(metadataText, iconLookup);
        }

        private void UpdateSecondaryLayer()
        {
            if (targetText == null)
            {
                return;
            }

            string rawText = targetText.text ?? string.Empty;
            if (!TryBuildSecondaryText(rawText, out string secondaryTextValue, out bool hasVisibleSecondary))
            {
                DisableSecondaryLayer();
                return;
            }

            TMP_Text secondaryText = GetOrCreateSecondaryLayer();
            if (secondaryText == null)
            {
                return;
            }

            secondaryText.text = secondaryTextValue;
            ConfigureDuotoneSync(secondaryText);
            targetText.havePropertiesChanged = true;
            targetText.SetAllDirty();
            secondaryText.havePropertiesChanged = true;
            secondaryText.SetAllDirty();
        }

        private bool TryBuildSecondaryText(string rawText, out string secondaryText, out bool hasVisibleSecondary)
        {
            secondaryText = string.Empty;
            hasVisibleSecondary = false;

            if (string.IsNullOrEmpty(rawText))
            {
                return false;
            }

            EnsureLookupBuilt();
            if (iconLookup.Count == 0)
            {
                return false;
            }

            StringBuilder builder = null;
            int index = 0;

            while (index < rawText.Length)
            {
                int tokenStart = rawText.IndexOf(TokenPrefix, index, StringComparison.OrdinalIgnoreCase);
                if (tokenStart < 0)
                {
                    break;
                }

                int nameStart = tokenStart + TokenPrefix.Length;
                int tokenEnd = rawText.IndexOf(':', nameStart);
                if (tokenEnd < 0)
                {
                    break;
                }

                builder ??= new StringBuilder(rawText.Length + 64);
                AppendHiddenSegment(builder, rawText, index, tokenStart - index);

                string tokenContent = rawText.Substring(nameStart, tokenEnd - nameStart);
                string tokenText = rawText.Substring(tokenStart, tokenEnd - tokenStart + 1);
                if (!TryParseInlineToken(tokenContent, out InlineTokenSpec token) ||
                    !iconLookup.TryGetValue(token.IconName, out InlineIconMetadata iconMetadata))
                {
                    AppendHiddenSegment(builder, tokenText, 0, tokenText.Length);
                    index = tokenEnd + 1;
                    continue;
                }

                TMP_FontAsset resolvedFontAsset = ResolveInlineFontAsset(token);
                if (TryResolveSecondaryGlyph(token, iconMetadata, resolvedFontAsset, out uint secondaryUnicode))
                {
                    AppendResolvedToken(builder, token, secondaryUnicode);
                    hasVisibleSecondary = true;
                }
                else
                {
                    AppendHiddenGlyph(builder, token, iconMetadata.PrimaryUnicode, resolvedFontAsset);
                }

                index = tokenEnd + 1;
            }

            if (builder == null)
            {
                return false;
            }

            if (index < rawText.Length)
            {
                AppendHiddenSegment(builder, rawText, index, rawText.Length - index);
            }

            secondaryText = builder.ToString();
            return hasVisibleSecondary;
        }

        private bool TryResolveSecondaryGlyph(InlineTokenSpec token, InlineIconMetadata iconMetadata, TMP_FontAsset resolvedFontAsset, out uint secondaryUnicode)
        {
            secondaryUnicode = 0u;
            if (!TryResolveTokenFamilyStyle(token, resolvedFontAsset, out string family, out string style))
            {
                return false;
            }

            if (iconMetadata.TryGetSecondaryUnicode(family, style, out secondaryUnicode))
            {
                EnsureGlyphAvailable(resolvedFontAsset, secondaryUnicode);
                return resolvedFontAsset == null || FontHasGlyph(resolvedFontAsset, secondaryUnicode);
            }

            if (!iconMetadata.SupportsSecondaryLayer(family, style) || resolvedFontAsset == null)
            {
                return false;
            }

            uint syntheticSecondaryUnicode = 0x100000u + iconMetadata.PrimaryUnicode;
            EnsureGlyphAvailable(resolvedFontAsset, syntheticSecondaryUnicode);
            if (!FontHasGlyph(resolvedFontAsset, syntheticSecondaryUnicode))
            {
                return false;
            }

            secondaryUnicode = syntheticSecondaryUnicode;
            return true;
        }

        private static bool TryResolveTokenFamilyStyle(InlineTokenSpec token, TMP_FontAsset resolvedFontAsset, out string family, out string style)
        {
            family = token.Family;
            style = token.Style;

            if (resolvedFontAsset != null && TryGetFamilyStyle(resolvedFontAsset, out string resolvedFamily, out string resolvedStyle))
            {
                family = resolvedFamily;
                style = resolvedStyle;
            }

            if (string.Equals(style, "brands", StringComparison.OrdinalIgnoreCase))
            {
                family ??= "classic";
            }
            else if (string.IsNullOrWhiteSpace(family) && !string.IsNullOrWhiteSpace(style))
            {
                family = "classic";
            }

            return !string.IsNullOrWhiteSpace(family) && !string.IsNullOrWhiteSpace(style);
        }

        private static void AppendHiddenSegment(StringBuilder builder, string text, int startIndex, int length)
        {
            if (builder == null || string.IsNullOrEmpty(text) || length <= 0)
            {
                return;
            }

            builder.Append(HiddenAlphaTag);
            builder.Append(text, startIndex, length);
            builder.Append(HiddenAlphaCloseTag);
        }

        private void AppendHiddenGlyph(StringBuilder builder, InlineTokenSpec token, uint unicode, TMP_FontAsset resolvedFontAsset)
        {
            builder.Append(HiddenAlphaTag);

            string glyph = char.ConvertFromUtf32((int)unicode);
            if (resolvedFontAsset == null)
            {
                builder.Append(glyph);
                builder.Append(HiddenAlphaCloseTag);
                return;
            }

            MaterialReferenceManager.AddFontAsset(resolvedFontAsset);
            builder.Append("<font=\"");
            builder.Append(resolvedFontAsset.name);
            builder.Append("\">");
            builder.Append(glyph);
            builder.Append("</font>");
            builder.Append(HiddenAlphaCloseTag);
        }

        private TMP_Text GetOrCreateSecondaryLayer()
        {
            Transform secondaryTransform = targetText != null ? targetText.transform.Find(SecondaryLayerObjectName) : null;
            TMP_Text secondaryText = secondaryTransform != null ? secondaryTransform.GetComponent<TMP_Text>() : null;
            if (secondaryText != null)
            {
                secondaryText.gameObject.SetActive(true);
                return secondaryText;
            }

            if (targetText is TextMeshProUGUI uiText)
            {
                GameObject go = new(SecondaryLayerObjectName, typeof(RectTransform), typeof(TextMeshProUGUI));
                go.transform.SetParent(uiText.transform, false);

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
            worldObject.transform.SetParent(targetText.transform, false);
            worldObject.transform.localPosition = Vector3.zero;
            worldObject.transform.localRotation = Quaternion.identity;
            worldObject.transform.localScale = Vector3.one;
            return worldObject.GetComponent<TextMeshPro>();
        }

        private void ConfigureDuotoneSync(TMP_Text secondaryText)
        {
            if (targetText == null || secondaryText == null)
            {
                return;
            }

            FontAwesomeDuotoneSync sync = targetText.GetComponent<FontAwesomeDuotoneSync>();
            if (sync == null)
            {
                sync = targetText.gameObject.AddComponent<FontAwesomeDuotoneSync>();
                sync.SetSecondaryColor(new Color(targetText.color.r, targetText.color.g, targetText.color.b, 0.25f));
            }

            secondaryText.gameObject.SetActive(true);
            sync.SetSecondaryText(secondaryText);
            sync.SetSecondaryVisible(true);
            sync.SyncNow();
        }

        private void DisableSecondaryLayer()
        {
            if (targetText == null)
            {
                return;
            }

            FontAwesomeDuotoneSync sync = targetText.GetComponent<FontAwesomeDuotoneSync>();
            TMP_Text secondaryText = GetExistingSecondaryLayer();

            if (sync != null)
            {
                sync.SetSecondaryVisible(false);
                sync.SyncNow();
            }

            if (secondaryText != null)
            {
                secondaryText.text = string.Empty;
                secondaryText.gameObject.SetActive(false);
                secondaryText.havePropertiesChanged = true;
                secondaryText.SetAllDirty();
            }
        }

        private TMP_Text GetExistingSecondaryLayer()
        {
            Transform secondaryTransform = targetText != null ? targetText.transform.Find(SecondaryLayerObjectName) : null;
            return secondaryTransform != null ? secondaryTransform.GetComponent<TMP_Text>() : null;
        }

        private static void ParseIconLookup(string json, Dictionary<string, InlineIconMetadata> results)
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
                if (!string.IsNullOrWhiteSpace(iconName) &&
                    !string.IsNullOrWhiteSpace(unicodeHex) &&
                    uint.TryParse(unicodeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint unicode))
                {
                    uint? secondaryUnicode = FindSecondaryUnicode(entryJson);
                    List<FontAwesomeFamilyStyle> secondaryLayerStyles = ParseRenderableSecondaryStyles(entryJson);
                    results[iconName] = new InlineIconMetadata(unicode, secondaryUnicode, secondaryLayerStyles);
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

            int valueIndex = SkipWhitespace(json, colonIndex + 1);
            if (valueIndex >= json.Length || json[valueIndex] != '"')
            {
                return string.Empty;
            }

            return ReadJsonString(json, ref valueIndex);
        }

        private static uint? FindSecondaryUnicode(string json)
        {
            int aliasesIndex = json.IndexOf("\"aliases\"", StringComparison.Ordinal);
            if (aliasesIndex < 0)
            {
                return null;
            }

            int aliasesObjectStart = json.IndexOf('{', aliasesIndex);
            if (aliasesObjectStart < 0)
            {
                return null;
            }

            int aliasesObjectEnd = FindObjectEnd(json, aliasesObjectStart);
            if (aliasesObjectEnd <= aliasesObjectStart)
            {
                return null;
            }

            string aliasesJson = json.Substring(aliasesObjectStart, aliasesObjectEnd - aliasesObjectStart + 1);
            string secondaryHex = FindFirstStringInArrayProperty(aliasesJson, "secondary");
            if (uint.TryParse(secondaryHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint secondaryUnicode))
            {
                return secondaryUnicode;
            }

            return null;
        }

        private static string FindFirstStringInArrayProperty(string json, string propertyName)
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

            int arrayStart = SkipWhitespace(json, colonIndex + 1);
            if (arrayStart >= json.Length || json[arrayStart] != '[')
            {
                return string.Empty;
            }

            int index = SkipWhitespace(json, arrayStart + 1);
            if (index >= json.Length || json[index] == ']')
            {
                return string.Empty;
            }

            return json[index] == '"' ? ReadJsonString(json, ref index) : string.Empty;
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
                    builder.Append(current);
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

        private static List<FontAwesomeFamilyStyle> ParseRenderableSecondaryStyles(string entryJson)
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
            int pathIndex = entryJson.IndexOf("\"path\"", StringComparison.Ordinal);
            if (pathIndex < 0)
            {
                return false;
            }

            int arrayStart = entryJson.IndexOf('[', pathIndex);
            if (arrayStart < 0)
            {
                return false;
            }

            int firstIndex = SkipWhitespace(entryJson, arrayStart + 1);
            if (firstIndex >= entryJson.Length || entryJson[firstIndex] != '"')
            {
                return false;
            }

            string firstPath = ReadJsonString(entryJson, ref firstIndex);
            int secondIndex = entryJson.IndexOf('"', firstIndex);
            if (secondIndex < 0)
            {
                return false;
            }

            string secondPath = ReadJsonString(entryJson, ref secondIndex);
            return !string.IsNullOrWhiteSpace(firstPath) && !string.IsNullOrWhiteSpace(secondPath);
        }

#if UNITY_EDITOR
        private void AutoConfigureDefaults()
        {
            if (metadata == null)
            {
                metadata = LoadTextAssetByPreferredPath(DefaultMetadataPath);
            }

            if (brandsFontAsset == null)
            {
                brandsFontAsset = FindFontAsset("brands");
            }

            if (defaultIconFontAsset == null)
            {
                defaultIconFontAsset = FindFontAsset("pro regular") ??
                                       FindFontAsset("regular") ??
                                       FindFontAsset("solid");
            }

            PopulateAvailableIconFontAssets();
        }

        private static TextAsset LoadTextAssetByPreferredPath(string preferredPath)
        {
            string normalizedPreferredPath = preferredPath.Replace('\\', '/');
            TextAsset preferredAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(normalizedPreferredPath);
            if (preferredAsset != null)
            {
                return preferredAsset;
            }

            string[] guids = UnityEditor.AssetDatabase.FindAssets("icon-families t:TextAsset");
            foreach (string guid in guids)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid).Replace('\\', '/');
                if (path.IndexOf("fontawesome", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    TextAsset asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                    if (asset != null)
                    {
                        return asset;
                    }
                }
            }

            return null;
        }

        private static TMP_FontAsset FindFontAsset(string requiredNameFragment)
        {
            string[] guids = UnityEditor.AssetDatabase.FindAssets("t:TMP_FontAsset Font Awesome");
            foreach (string guid in guids)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                TMP_FontAsset asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (asset == null)
                {
                    continue;
                }

                string sourceName = asset.sourceFontFile != null ? asset.sourceFontFile.name : string.Empty;
                string assetName = asset.name ?? string.Empty;
                string combinedName = $"{sourceName} {assetName}";
                if (combinedName.IndexOf(requiredNameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return asset;
                }
            }

            return null;
        }

        private void PopulateAvailableIconFontAssets()
        {
            availableIconFontAssets ??= new List<TMP_FontAsset>();
            availableIconFontAssets.Clear();

            string[] guids = UnityEditor.AssetDatabase.FindAssets("t:TMP_FontAsset Font Awesome");
            foreach (string guid in guids)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                TMP_FontAsset asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (asset == null || availableIconFontAssets.Contains(asset))
                {
                    continue;
                }

                availableIconFontAssets.Add(asset);
            }
        }
#endif

        private readonly struct InlineTokenSpec
        {
            public InlineTokenSpec(string iconName, string family, string style)
            {
                IconName = iconName;
                Family = family;
                Style = style;
            }

            public string IconName { get; }
            public string Family { get; }
            public string Style { get; }
        }

        private readonly struct InlineIconMetadata
        {
            public InlineIconMetadata(uint primaryUnicode, uint? secondaryUnicode, List<FontAwesomeFamilyStyle> secondaryLayerStyles)
            {
                PrimaryUnicode = primaryUnicode;
                SecondaryUnicode = secondaryUnicode;
                SecondaryLayerStyles = secondaryLayerStyles ?? new List<FontAwesomeFamilyStyle>();
            }

            public uint PrimaryUnicode { get; }
            public uint? SecondaryUnicode { get; }
            public List<FontAwesomeFamilyStyle> SecondaryLayerStyles { get; }

            public bool TryGetSecondaryUnicode(string family, string style, out uint unicode)
            {
                unicode = 0u;
                if (SecondaryLayerStyles == null || SecondaryLayerStyles.Count == 0 || !SecondaryUnicode.HasValue)
                {
                    return false;
                }

                return SupportsSecondaryLayer(family, style, out unicode);
            }

            public bool SupportsSecondaryLayer(string family, string style)
            {
                return SupportsSecondaryLayer(family, style, out _);
            }

            private bool SupportsSecondaryLayer(string family, string style, out uint unicode)
            {
                unicode = 0u;
                for (int i = 0; i < SecondaryLayerStyles.Count; i++)
                {
                    FontAwesomeFamilyStyle familyStyle = SecondaryLayerStyles[i];
                    if (string.Equals(familyStyle.Family, family, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(familyStyle.Style, style, StringComparison.OrdinalIgnoreCase))
                    {
                        if (SecondaryUnicode.HasValue)
                        {
                            unicode = SecondaryUnicode.Value;
                        }

                        return true;
                    }
                }

                return false;
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
    }
}
