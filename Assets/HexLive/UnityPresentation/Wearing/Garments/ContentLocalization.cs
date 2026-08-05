using I2.Loc;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Garments
{

// Строки вещей, приехавших КОНТЕНТОМ.
//
// §58 требует, чтобы строки жили в I2, а не в C#, — и это правило тут не
// нарушается: строки не пишутся в код, они регистрируются в I2 ОТДЕЛЬНЫМ
// источником, ровно тем механизмом, который для этого в I2 и есть. Просто
// источник теперь приезжает вместе с вещью, а не лежит в билде.
//
// Иначе никак: у вещи, которой не было в момент сборки игры, терминов в
// I2Languages.asset нет и взяться им неоткуда — игрок увидел бы «item.unknown».
internal static class ContentLocalization
{
    public static void Register(WardrobeIndex index)
    {
        if (index == null || index.items.Count == 0)
        {
            return;
        }

        var source = FindOrCreate();
        if (source == null)
        {
            return;
        }

        var english = source.GetLanguageIndex("English");
        var russian = source.GetLanguageIndex("Russian");
        if (english < 0 || russian < 0)
        {
            Debug.LogWarning("[Гардероб] в источнике строк нет English/Russian — строки вещей пропущены.");
            return;
        }

        var added = 0;
        foreach (var row in index.items)
        {
            if (row == null || string.IsNullOrEmpty(row.id))
            {
                continue;
            }

            var slug = HexLive.Simulation.Content.ItemInfo.Slug(row.id);
            added += Put(source, $"item.{slug}.name", english, row.nameEn, russian, row.nameRu) ? 1 : 0;
            added += Put(source, $"item.{slug}.desc", english, row.descEn, russian, row.descRu) ? 1 : 0;
        }

        LocalizationManager.LocalizeAll(true);
        Debug.Log($"[Гардероб] строк из контента: {added}.");
    }

    // Термин, который УЖЕ есть в билде, не трогаем: там он выверен человеком, а
    // в контенте мог остаться прошлой редакцией.
    private static bool Put(LanguageSourceData source, string term,
                            int english, string en, int russian, string ru)
    {
        if (string.IsNullOrEmpty(en) && string.IsNullOrEmpty(ru))
        {
            return false;
        }

        if (LocalizationManager.TryGetTranslation(term, out _))
        {
            return false;
        }

        var data = source.GetTermData(term) ?? source.AddTerm(term, eTermType.Text);
        if (data == null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(en))
        {
            data.Languages[english] = en;
        }

        if (!string.IsNullOrEmpty(ru))
        {
            data.Languages[russian] = ru;
        }

        return true;
    }

    // Свой источник строк, добавленный в LocalizationManager.Sources. Именно
    // так I2 и задуман: источников может быть много, и они складываются.
    // Отдельный источник (а не дописывание в I2Languages) важен потому, что
    // ассет в билде править на лету нельзя, а контент приезжает после сборки.
    private static LanguageSourceData _content;

    private static LanguageSourceData FindOrCreate()
    {
        if (_content != null)
        {
            return _content;
        }

        // Языки берутся у уже загруженного источника: порядок колонок в I2 —
        // это ИНДЕКСЫ, и свой собственный порядок развёл бы русский с
        // английским местами.
        var shipped = LocalizationManager.Sources.Count > 0 ? LocalizationManager.Sources[0] : null;
        if (shipped == null)
        {
            Debug.LogWarning("[Гардероб] I2 ещё не поднят — строки вещей из контента пропущены.");
            return null;
        }

        _content = new LanguageSourceData();
        foreach (var language in shipped.mLanguages)
        {
            _content.AddLanguage(language.Name, language.Code);
        }

        LocalizationManager.Sources.Add(_content);
        return _content;
    }
}

}
