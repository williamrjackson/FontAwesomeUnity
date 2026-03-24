using TMPro;
using UnityEngine;

namespace Wrj.FontAwesome
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TMP_Text))]
    public sealed class FontAwesomeDuotoneSync : MonoBehaviour
    {
        [SerializeField] private TMP_Text primaryText;
        [SerializeField] private TMP_Text secondaryText;
        [SerializeField] private Color secondaryColor = Color.white;
        [SerializeField][Range(0f, 1f)] private float secondaryAlphaMultiplier = 0.25f;
        [SerializeField] private bool secondaryVisible = true;
        [SerializeField] private bool syncSecondaryRgb = true;

        private Color lastAppliedSecondaryColor = new Color(1f, 1f, 1f, 0.25f);
        private bool hasAppliedSecondaryColor;

        public TMP_Text SecondaryText => secondaryText;

        public void SetSecondaryText(TMP_Text value)
        {
            secondaryText = value;
            CachePrimary();
        }

        public void SetSecondaryVisible(bool visible)
        {
            secondaryVisible = visible;
            if (secondaryText != null)
            {
                secondaryText.gameObject.SetActive(visible);
            }
        }

        public void SetSecondaryColor(Color color)
        {
            secondaryColor = new Color(color.r, color.g, color.b, 1f);
            secondaryAlphaMultiplier = Mathf.Clamp01(color.a);
            syncSecondaryRgb = true;
        }


        public void SyncNow()
        {
            CachePrimary();
            CacheSecondary();

            if (primaryText == null || secondaryText == null)
            {
                return;
            }

            secondaryText.gameObject.SetActive(secondaryVisible);
            if (!secondaryVisible)
            {
                return;
            }

            if (syncSecondaryRgb && !RgbApproximatelyEqual(secondaryColor, lastAppliedSecondaryColor))
            {
                syncSecondaryRgb = false;
            }

            secondaryText.font = primaryText.font;
            secondaryText.fontSharedMaterial = primaryText.fontSharedMaterial;
            secondaryText.fontSize = primaryText.fontSize;
            secondaryText.enableAutoSizing = false;
            secondaryText.fontSizeMin = primaryText.fontSizeMin;
            secondaryText.fontSizeMax = primaryText.fontSizeMax;
            secondaryText.alignment = primaryText.alignment;
            secondaryText.characterSpacing = primaryText.characterSpacing;
            secondaryText.wordSpacing = primaryText.wordSpacing;
            secondaryText.lineSpacing = primaryText.lineSpacing;
            secondaryText.paragraphSpacing = primaryText.paragraphSpacing;
            secondaryText.richText = primaryText.richText;
            secondaryText.textWrappingMode = primaryText.textWrappingMode;
            secondaryText.overflowMode = primaryText.overflowMode;
            secondaryText.horizontalMapping = primaryText.horizontalMapping;
            secondaryText.verticalMapping = primaryText.verticalMapping;
            secondaryText.margin = primaryText.margin;
            secondaryText.isRightToLeftText = primaryText.isRightToLeftText;
            secondaryText.extraPadding = primaryText.extraPadding;
            secondaryText.isOrthographic = primaryText.isOrthographic;
            secondaryText.fontFeatures = primaryText.fontFeatures;

            if (syncSecondaryRgb)
            {
                secondaryColor = new Color(primaryText.color.r, primaryText.color.g, primaryText.color.b, 1f);
            }

            Color color = new Color(
                secondaryColor.r,
                secondaryColor.g,
                secondaryColor.b,
                primaryText.color.a * secondaryAlphaMultiplier);
            secondaryText.color = color;
            lastAppliedSecondaryColor = color;

            if (primaryText is TextMeshProUGUI primaryUi && secondaryText is TextMeshProUGUI secondaryUi)
            {
                secondaryUi.raycastTarget = false;

                RectTransform primaryRect = primaryUi.rectTransform;
                RectTransform secondaryRect = secondaryUi.rectTransform;
                secondaryRect.anchorMin = primaryRect.anchorMin;
                secondaryRect.anchorMax = primaryRect.anchorMax;
                secondaryRect.pivot = primaryRect.pivot;
                secondaryRect.anchoredPosition = Vector2.zero;
                secondaryRect.sizeDelta = primaryRect.sizeDelta;
                secondaryRect.localRotation = Quaternion.identity;
                secondaryRect.localScale = Vector3.one;
            }
            else if (primaryText is TextMeshPro && secondaryText is TextMeshPro)
            {
                Transform secondaryTransform = secondaryText.transform;
                secondaryTransform.localPosition = Vector3.zero;
                secondaryTransform.localRotation = Quaternion.identity;
                secondaryTransform.localScale = Vector3.one;
            }
        }

        private void Reset()
        {
            CachePrimary();
            CacheSecondary();
            SyncNow();
        }

        private void OnEnable()
        {
            SyncNow();
        }

        private void OnValidate()
        {
            SyncNow();
        }

        private void LateUpdate()
        {
            SyncNow();
        }

        private void CachePrimary()
        {
            if (primaryText == null)
            {
                primaryText = GetComponent<TMP_Text>();
            }
        }

        private void CacheSecondary()
        {
            if (secondaryText == null)
            {
                Transform child = transform.Find("FA Secondary Layer");
                secondaryText = child != null ? child.GetComponent<TMP_Text>() : null;
            }
        }

        private static bool RgbApproximatelyEqual(Color left, Color right)
        {
            return Mathf.Approximately(left.r, right.r) &&
                   Mathf.Approximately(left.g, right.g) &&
                   Mathf.Approximately(left.b, right.b);
        }
    }
}