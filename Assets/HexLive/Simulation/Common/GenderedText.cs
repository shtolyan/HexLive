using System;
using System.Text;

namespace HexLive.Simulation.Common
{
    /// <summary>
    /// §58.9: грамматический род в переводах. На острове не только девушки, а
    /// термины I2 десять лет писались в женском роде («{0} заметила», «я пошла»).
    /// Мужской вариант живёт ВНУТРИ того же термина разметкой
    /// <c>[[слот:женская|мужская]]</c>, а не отдельным ключом: в одной строке
    /// дневника бывают два рода сразу («Я ждала {0}. Она не пришла.»), и четыре
    /// параллельных ключа на каждую такую строку никто бы не поддержал.
    /// <para>
    /// Слоты: <c>s</c> — тот, о ком или от чьего лица строка (автор дневника,
    /// носитель эффекта, выбранный персонаж); <c>0</c>/<c>1</c>/<c>2</c> — люди,
    /// подставляемые в <c>{0}</c>/<c>{1}</c>/<c>{2}</c>. Неизвестный пол —
    /// женская форма: так строка читается ровно как до разметки.
    /// </para>
    /// <para>
    /// Разрешать нужно ДО <c>string.Format</c>: квадратные скобки формату не
    /// мешают, но в готовом тексте их видеть игроку нельзя.
    /// </para>
    /// </summary>
    public static class GenderedText
    {
        private const string Open = "[[";
        private const string Close = "]]";

        /// <summary>
        /// §84: пол выводится из тела, как и одежда. Единственная копия правила —
        /// и <c>NPCState.Sex</c>, и снапшоты в презентации спрашивают отсюда.
        /// </summary>
        public static bool IsMaleBody(string actorMesh) => actorMesh == "Kshishtof" || actorMesh == "Tonny";

        /// <summary>Строка содержит разметку рода — иначе её можно не трогать.</summary>
        public static bool HasMarkup(string text) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(Open, StringComparison.Ordinal) >= 0;

        /// <summary>Все слоты женские: текст ровно такой, каким был до §58.9.</summary>
        public static string Feminine(string text) => Resolve(text, GenderSlots.Feminine);

        public static string Resolve(string text, GenderSlots slots)
        {
            if (!HasMarkup(text))
            {
                return text;
            }

            var sb = new StringBuilder(text.Length);
            var at = 0;
            while (at < text.Length)
            {
                var open = text.IndexOf(Open, at, StringComparison.Ordinal);
                if (open < 0)
                {
                    break;
                }

                var close = text.IndexOf(Close, open + Open.Length, StringComparison.Ordinal);
                var colon = text.IndexOf(':', open + Open.Length);
                if (close < 0 || colon < 0 || colon > close)
                {
                    break;
                }

                var slot = text.Substring(open + Open.Length, colon - open - Open.Length).Trim();
                var body = text.Substring(colon + 1, close - colon - 1);
                var bar = body.IndexOf('|');
                if (!TrySlot(slot, slots, out var male) || bar < 0)
                {
                    // Битая разметка видна как есть — как и пропущенный ключ (§58.2).
                    sb.Append(text, at, close + Close.Length - at);
                    at = close + Close.Length;
                    continue;
                }

                sb.Append(text, at, open - at);
                sb.Append(male ? body.Substring(bar + 1) : body.Substring(0, bar));
                at = close + Close.Length;
            }

            sb.Append(text, at, text.Length - at);
            return sb.ToString();
        }

        private static bool TrySlot(string slot, GenderSlots slots, out bool male)
        {
            switch (slot)
            {
                case "s": male = slots.Self; return true;
                case "0": male = slots.Arg0; return true;
                case "1": male = slots.Arg1; return true;
                case "2": male = slots.Arg2; return true;
                default: male = false; return false;
            }
        }
    }

    /// <summary>§58.9: кто из участников строки мужчина. По умолчанию — никто.</summary>
    public readonly struct GenderSlots
    {
        public GenderSlots(bool self, bool arg0 = false, bool arg1 = false, bool arg2 = false)
        {
            Self = self;
            Arg0 = arg0;
            Arg1 = arg1;
            Arg2 = arg2;
        }

        public static GenderSlots Feminine => default;

        public bool Self { get; }
        public bool Arg0 { get; }
        public bool Arg1 { get; }
        public bool Arg2 { get; }

        /// <summary>Только «о ком строка» — для эффектов, тостов, состояний.</summary>
        public static GenderSlots Of(bool selfMale) => new GenderSlots(selfMale);
    }
}
