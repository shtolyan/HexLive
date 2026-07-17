using System;
using I2.Loc;

namespace HexLive.UnityPresentation.Localization
{
    public enum Language
    {
        English = 0,
        Russian = 1
    }

    /// <summary>
    /// Spec §58: thin facade over the I2 Localization plugin. ALL translated
    /// strings live in the language source asset
    /// (<c>Assets/Resources/I2Languages.asset</c>, terms keyed like
    /// "panel.health") — this class holds NO string tables and never will;
    /// adding a locale entry to C# code is a spec violation. It only adapts
    /// the tiny API surface the UI already speaks (Get/Has/Toggle/
    /// LanguageChanged) onto <see cref="LocalizationManager"/>, keeping the
    /// RU/EN toggle semantics and the "missing key renders as the key"
    /// contract that makes untranslated UI obvious in-game.
    /// </summary>
    public static class Loc
    {
        private const string English = "English";
        private const string Russian = "Russian";

        public static event Action LanguageChanged;

        static Loc()
        {
            // I2 fires on every language switch (and on source reloads); the
            // UI re-reads its strings off this exact event, as before.
            LocalizationManager.OnLocalizeEvent += () => LanguageChanged?.Invoke();
        }

        public static Language Current
        {
            get => LocalizationManager.CurrentLanguage == Russian
                ? Language.Russian
                : Language.English;
            set => LocalizationManager.CurrentLanguage =
                value == Language.Russian ? Russian : English;
        }

        public static void Toggle()
        {
            Current = Current == Language.English ? Language.Russian : Language.English;
        }

        /// <summary>Short two-letter code for the current language (UI badge).</summary>
        public static string Code => Current == Language.Russian ? "RU" : "EN";

        /// <summary>True when a term is authored for this key (either language).</summary>
        public static bool Has(string key) =>
            LocalizationManager.TryGetTranslation(key, out _);

        public static string Get(string key)
        {
            if (LocalizationManager.TryGetTranslation(key, out var text) &&
                !string.IsNullOrEmpty(text))
            {
                return text;
            }

            // A term with an empty translation in the current language falls
            // back to English (the old table's behaviour).
            if (LocalizationManager.TryGetTranslation(key, out text, overrideLanguage: English) &&
                !string.IsNullOrEmpty(text))
            {
                return text;
            }

            return key; // missing keys stay visible in the UI
        }

        /// <summary>Localized short "what they want" phrase for a goal enum name.</summary>
        public static string Goal(string goalName)
        {
            var key = "goal." + goalName;
            return Has(key) ? Get(key) : goalName;
        }
    }
}
