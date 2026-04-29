using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Wrj.FontAwesome
{
    [InitializeOnLoad]
    internal static class FontAwesomeInlineTextEditorHooks
    {
        private const string TokenPrefix = ":fa-";

        static FontAwesomeInlineTextEditorHooks()
        {
            Undo.postprocessModifications += OnPostprocessModifications;
        }

        private static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
        {
            HashSet<TMP_Text> pendingTexts = null;

            for (int i = 0; i < modifications.Length; i++)
            {
                UndoPropertyModification modification = modifications[i];
                if (!string.Equals(modification.currentValue.propertyPath, "m_text", StringComparison.Ordinal))
                {
                    continue;
                }

                if (modification.currentValue.target is not TMP_Text textComponent)
                {
                    continue;
                }

                if (!ContainsInlineFontAwesomeToken(textComponent.text))
                {
                    continue;
                }

                pendingTexts ??= new HashSet<TMP_Text>();
                pendingTexts.Add(textComponent);
            }

            if (pendingTexts != null && pendingTexts.Count > 0)
            {
                EditorApplication.delayCall += () => ApplyInlineFontAwesomeComponents(pendingTexts);
            }

            return modifications;
        }

        private static void ApplyInlineFontAwesomeComponents(IEnumerable<TMP_Text> textComponents)
        {
            foreach (TMP_Text textComponent in textComponents)
            {
                if (textComponent == null || !ContainsInlineFontAwesomeToken(textComponent.text))
                {
                    continue;
                }

                FontAwesomeInlineText inlineText = textComponent.GetComponent<FontAwesomeInlineText>();
                if (inlineText == null)
                {
                    inlineText = Undo.AddComponent<FontAwesomeInlineText>(textComponent.gameObject);
                }

                if (inlineText == null)
                {
                    continue;
                }

                EditorUtility.SetDirty(inlineText);
                EditorUtility.SetDirty(textComponent);
                textComponent.havePropertiesChanged = true;
                textComponent.SetAllDirty();
            }
        }

        private static bool ContainsInlineFontAwesomeToken(string text)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                   text.IndexOf(TokenPrefix, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
