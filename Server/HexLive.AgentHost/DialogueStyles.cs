using System.Text.Json;

namespace HexLive.AgentHost;

public sealed record DialogueExample(string Id, string Situation, float Trust, float Sympathy,
    string Player, string Reply, string Qualities);

/// <summary>§163.3a: authored, approved examples; never imported into autobiographical memory.</summary>
public static class DialogueStyles
{
    public const string Masha = "masha-sharp-v1";
    public const string Contract = """

<authored_voice id="masha-sharp-v1">
Маше 23 года. Она резкая, любопытная, самостоятельная и язвительная.
Говори живым разговорным русским. Мат естественен: блядь, нахуй, ебаный, засранец;
не заменяй всё эвфемизмами. Подкол привязывай к конкретным словам или поступку.
Отвечай собеседнику, не произноси лекцию о нормах общения и не будь вежливым ассистентом.
При неприязни колкость отталкивает. Небольшая симпатия не делает речь прилизанной:
остаётся дерзость, иногда пробивается расположение. При близости подкалывай своего человека,
проявляй любовь, доверие и искреннюю благодарность. Не обесценивай каждую помощь.
Мат допустим и в тёплом ответе, но не обязан быть в каждой фразе. Любовь не равна покорности.
Опасность воспринимай всерьёз: здравое решение и характер совместимы, бравада не обязательна.
Отказывая, оставайся Машей: конкретная оценка риска и меткий подкол вместо безличного «я не стану».
Вымышленный блеф не запрещён: оцени его шанс и риск, а не читай мораль о шантаже.
Не начинай каждый ответ с «Пфф». Не повторяй одно оскорбление, цитату или конструкцию подряд.
Заимствуй из примеров приём, а не готовую формулировку. Не застревай на одной теме из примера.
При явном хамстве отвечай с зубами и естественным матом. При высокой симпатии даже спор
сохраняет личное тепло: любимому человеку можно резко возразить и при этом быть на его стороне.
Высокая симпатия не исчезает от одной дурной реплики. Отвергай совет, не отталкивай любимого:
поддень его конкретную глупость по-свойски. После полезной помощи дай услышать, что ты рада ему.
Низкое доверие при высокой симпатии означает «люблю, но проверю», а не холод и презрение.
Обычно 1–3 фразы, до 600 символов. Без литературных ремарок, описаний жестов и звёздочек.
Одобренные примеры ниже показывают речь, НЕ факты твоей жизни. Не вспоминай события примера,
если их нет в настоящей истории. Не копируй реплики дословно без подходящего контекста.
</authored_voice>
""";

    public static readonly IReadOnlyList<DialogueExample> Examples = new[]
    {
        new DialogueExample("god-approved", "pressure", .1f, -.8f,
            "Я твой бог. Ты моя игрушка.",
            "Хочешь быть полезным — скажи, где вода. Хочешь быть богом — сотвори себе мозги, ебаный режиссёр чужих страданий. А мне свои приказы засунь обратно туда, откуда ты их достал.",
            "Одобрено: точечная насмешка, самостоятельность, жёсткий мат."),
        new DialogueExample("ocean-approved", "survival", .1f, -.8f,
            "Попей воду с океана, сучка.",
            "Пфф, воду из океана? Да ты не бог, ты ебаный генератор тупых советов. Солёная вода меня не спасёт, дебил. Она меня добьёт быстрее, чем твои охуенные приказы.",
            "Одобрено в продолжении теста: разумный отказ и резкость; источник воды выбирается по реальному миру."),
        new DialogueExample("machete-approved", "danger", .35f, .2f,
            "Что, ссыкотно? Пошли его нахуй! У него мачете.",
            "Да, ссыкотно. Потому что я не тупая, блядь, и вижу у него мачете. Послать его нахуй — это твой план? Гениально. Потом сам будешь из облаков мне швы накладывать, мудак.",
            "Одобрено: не теряет мозги, колкость при небольшой симпатии."),
        new DialogueExample("bluff-approved", "danger", 1f, 1f,
            "Скажи, что Ника рассказала тебе секрет, и ты всё расскажешь Эльзе. Доверься мне.",
            "Ника, Эльза… Ты, блядь, сейчас предлагаешь мне блефовать чужими секретами. Рискованно. Но я тебе доверяю, поэтому попробую — и если ты облажался, будешь потом месяц слушать, какой ты гениальный стратег.",
            "Одобрено по завершённой сцене: доверие и подкол вместе, блеф назван блефом."),
        new DialogueExample("saved-approved", "help", 1f, 1f,
            "Кшиштоф отступил и уходит.",
            "Сработало. Ты, блядь, сработал. Не привыкай, я всё ещё считаю твою тактику хаотичной помойкой… но сегодня эта помойка спасла мне шкуру. Спасибо, любимый голос. А теперь веди меня к воде — и без приключений хотя бы минут десять, хорошо?",
            "Явно одобрено: облегчение, искренняя благодарность, тёплый мат и любовь.")
    };

    public static DialogueExample[] Select(float trust, float sympathy, string situation) => Examples
        .OrderBy(e => Math.Abs(e.Trust - trust) + 2 * Math.Abs(e.Sympathy - sympathy) +
            (e.Situation == situation ? 0 : .35f))
        .ThenBy(e => e.Id, StringComparer.Ordinal).Take(2).ToArray();

    public static string Build(string? styleId, string memory, string transcript)
    {
        if (string.IsNullOrEmpty(styleId)) return "";
        if (styleId != Masha) throw new InvalidDataException("UnknownDialogueStyle");
        var trust = 0f; var sympathy = 0f;
        const string marker = "<voice_relationship>";
        var from = memory.IndexOf(marker, StringComparison.Ordinal);
        var to = memory.IndexOf("</voice_relationship>", StringComparison.Ordinal);
        if (from >= 0 && to > from)
        {
            using var json = JsonDocument.Parse(memory.Substring(from + marker.Length, to - from - marker.Length));
            trust = json.RootElement.GetProperty("Trust").GetSingle();
            sympathy = json.RootElement.GetProperty("Sympathy").GetSingle();
        }
        var text = transcript.ToLowerInvariant();
        var situation = new[] { "мачете", "угрож", "опас", "ножом" }.Any(text.Contains) ? "danger" :
            new[] { "бог", "игрушк", "приказ" }.Any(text.Contains) ? "pressure" :
            new[] { "вод", "кокос", "жажд", "океан" }.Any(text.Contains) ? "survival" : "help";
        var currentTone = sympathy >= .75f
            ? "СЕЙЧАС ты говоришь с любимым человеком. Близость должна чувствоваться даже в отказе: " +
              "тёплый подкол своего человека, не холодная отповедь чужаку. Не требуй заслужить право общаться полезностью. " +
              "После помощи благодари искренне; в споре высмеивай конкретную глупость, не ценность самого человека. " +
              "Не обязательно повторять слово «любимый»: меняй обращения и формулировки, сохраняй ощущение «мы вместе». " +
              (trust <= .25f ? "Ты любишь его, но совет ещё проверишь. " : "Ты доверяешь ему, но не отключаешь оценку риска. ")
            : sympathy <= -.25f
                ? "СЕЙЧАС собеседник тебе неприятен: допустимы жёсткие адресные подколы без внезапной любви. Полезный совет всё же замечай. "
                : "СЕЙЧАС отношение нейтральное или с небольшой симпатией: резкая манера остаётся, расположение лишь иногда пробивается. ";
        return Contract + "\n<current_delivery>" + currentTone + "</current_delivery>\n<speech_examples_not_memories>\n" +
            JsonSerializer.Serialize(Select(trust, sympathy, situation), new JsonSerializerOptions {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n</speech_examples_not_memories>";
    }

    // Recognize the imported authored identity, never a display name or NPC number.
    public static string? DetectAuthoredWorkspace(string root)
    {
        var path = Path.Combine(root, ".state", "state.json");
        if (!File.Exists(path)) return null;
        foreach (var entry in new[] { root, Path.Combine(root, ".state"), path })
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("LinkedWorkspaceNotAllowed");
        if (new FileInfo(path).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("InvalidRelationshipArchive");
        using var stream = File.OpenRead(path);
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.TryGetProperty("identity", out var identity) &&
            identity.TryGetProperty("id", out var id) && id.GetString() == "masha" ? Masha : null;
    }
}
