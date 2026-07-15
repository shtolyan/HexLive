using System;
using I2.Loc;
using UnityEngine;

namespace HexLive.UnityPresentation.Localization
{
    public enum Language
    {
        English = 0,
        Russian = 1
    }

    /// <summary>
    /// Lightweight facade over the I2 language source asset. UI subscribes to
    /// <see cref="LanguageChanged"/> and re-reads strings via <see cref="Get"/>.
    /// </summary>
    public static class Loc
    {
        public static event Action LanguageChanged;

        private static Language _language = DetectDefault();

        static Loc()
        {
            ApplyLanguage(_language, remember: false);
        }

        public static Language Current
        {
            get => _language;
            set
            {
                if (_language == value)
                {
                    return;
                }

                _language = value;
                ApplyLanguage(_language);
                LanguageChanged?.Invoke();
            }
        }

        public static void Toggle()
        {
            Current = _language == Language.English ? Language.Russian : Language.English;
        }

        /// <summary>Short two-letter code for the current language (UI badge).</summary>
        public static string Code => _language == Language.Russian ? "RU" : "EN";

        /// <summary>True when a string is authored for this key (either language).</summary>
        public static bool Has(string key)
        {
            return LocalizationManager.TryGetTranslation(key, out _, overrideLanguage: LanguageName(_language))
                || LocalizationManager.TryGetTranslation(key, out _, overrideLanguage: LanguageName(Language.English))
                || LocalizationManager.TryGetTranslation(key, out _, overrideLanguage: LanguageName(Language.Russian));
        }

        public static string Get(string key)
        {
            if (LocalizationManager.TryGetTranslation(key, out var text, overrideLanguage: LanguageName(_language)))
            {
                return text;
            }

            if (_language != Language.English &&
                LocalizationManager.TryGetTranslation(key, out text, overrideLanguage: LanguageName(Language.English)))
            {
                return text;
            }

            return key;
        }

        /// <summary>Localized short "what they want" phrase for a goal enum name.</summary>
        public static string Goal(string goalName)
        {
            var key = "goal." + goalName;
            return Has(key) ? Get(key) : goalName;
        }

        private static Language DetectDefault()
        {
            return Application.systemLanguage == SystemLanguage.Russian
                ? Language.Russian
                : Language.English;
        }

        private static void ApplyLanguage(Language language, bool remember = true)
        {
            LocalizationManager.SetLanguageAndCode(
                LanguageName(language),
                LanguageCode(language),
                RememberLanguage: remember,
                Force: true);
        }

        private static string LanguageName(Language language)
        {
            return language == Language.Russian ? "Russian" : "English";
        }

        private static string LanguageCode(Language language)
        {
            return language == Language.Russian ? "ru" : "en";
        }
    }
}
