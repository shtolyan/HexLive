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

        // PERF: ключи термов собираются конкатенацией на каждый вызов, а зовут
        // эти хелперы панели КАЖДЫЙ кадр/тик на каждую девушку. Сам ключ от
        // языка не зависит — кэш живёт вечно (наборы имён/целей конечны).
        private static readonly System.Collections.Generic.Dictionary<string, string> _goalKeys = new();
        private static readonly System.Collections.Generic.Dictionary<string, string> _dreamKeys = new();
        private static readonly System.Collections.Generic.Dictionary<string, string> _npcNameKeys = new();

        /// <summary>Localized short "what they want" phrase for a goal enum name.</summary>
        public static string Goal(string goalName)
        {
            if (!_goalKeys.TryGetValue(goalName, out var key))
            {
                key = "goal." + goalName;
                _goalKeys[goalName] = key;
            }

            return Has(key) ? Get(key) : goalName;
        }

        /// <summary>Spec §64: localized "what she dreams of" phrase for a
        /// DreamType enum name (e.g. "Campfire" → "Dreams of a campfire").</summary>
        public static string Dream(string dreamName)
        {
            if (!_dreamKeys.TryGetValue(dreamName, out var key))
            {
                key = "dream." + dreamName;
                _dreamKeys[dreamName] = key;
            }

            return Has(key) ? Get(key) : dreamName;
        }

        /// <summary>
        /// §74: a colonist's name. <c>NPCState.DisplayName</c> is now an ID out
        /// of <c>ColonistAppearance.NameIds</c>, not a label — the latin id is
        /// what travels through traces, saves and GameObject names, while the
        /// player reads the term <c>npc.&lt;id&gt;.name</c> (EN + RU).
        /// An unknown id renders as itself, so a name added to the pool without
        /// its term is visible rather than blank.
        /// </summary>
        public static string NpcName(string nameId)
        {
            if (string.IsNullOrEmpty(nameId))
            {
                return string.Empty;
            }

            if (!_npcNameKeys.TryGetValue(nameId, out var key))
            {
                key = "npc." + nameId.ToLowerInvariant() + ".name";
                _npcNameKeys[nameId] = key;
            }

            return Has(key) ? Get(key) : nameId;
        }
    }
}
