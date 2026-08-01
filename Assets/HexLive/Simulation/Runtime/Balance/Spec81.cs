namespace HexLive.Simulation.Runtime
{

// §81: абьюз — единственная форма общения, доступная чужаку.
//
// ⭐ Это не «ещё одна злая механика», а закрытие дыры в нуждах. Нужда в общении
// у него не закрывается НИЧЕМ: разговор идёт только между союзниками, амбиентное
// общение §49 считает соседей по фракции, а фракция у него из одного человека.
// Social падает монотонно до нуля и там остаётся. С точки зрения симуляции он
// вечно одинокий — и единственный контакт с людьми, который ему доступен, это
// контакт силой.
//
// Отсюда две развилки одной сцены:
//  * есть что отжать → отжимает (и заодно закрывает голод/жажду);
//  * отжать нечего   → просто гнобит.
// Насыщение общением и порча отношений одинаковы в обоих случаях — потому что
// «удовлетворение» он получает не от добычи, а от того, что его боятся.
public static class Spec81
{
    // Kill-switch. false = мир байт-в-байт догёйтовый.
    public static bool AbuseEnabled = true;

    // --- Когда он вообще идёт абьюзить ---------------------------------------

    // Нужда в общении, ниже которой он идёт искать жертву. Social — это
    // СЫТОСТЬ общением (1 = наговорился), поэтому «низкое» значит одиноко.
    public static float AbuseSocialFloor = 0.55f;

    // Голод/жажда, выше которых он готов отжимать припас.
    public static float AbuseSupplyFloor = 0.55f;

    // Выше этого он уже умирает от голода/жажды — тут не до переговоров, надо
    // жрать всё, до чего дотянется (этим занимается кража §40.5).
    public static float AbuseNeedCeiling = 0.90f;

    // База ставки; к ней прибавляется сила нужды, поэтому голодный и одинокий
    // перебивает свои дела, а сытый и наговорившийся — нет.
    public static float AbuseBaseScore = 0.45f;

    public static int AbuseGraceDays = 2;
    public static int AbuseCooldownTicks = 900;

    // §87: на сколько цель абьюза запирается от аукциона после прерывания.
    // Без замка ближайший же пересчёт вернул бы его к «посидеть»: у одинокого
    // человека досуг стоит дорого, и дорога до жертвы длиннее одного тика.
    public static int AbuseLockTicks = 240;

    // Сцена отталкивает налёт: ограбил — значит сегодня не убивает.
    public static int AbuseRaidLockoutTicks = 600;

    // --- Выбор жертвы --------------------------------------------------------

    public static int AbuseScanRadiusTiles = 7;

    // Сколько подруг рядом он ещё терпит. Премиса §72 — от группы держится
    // подальше — держится именно здесь.
    public static int AbuseMaxMarkAllies = 1;

    // Уважает святилище: у порога дома разворачивается, как волк и как налёт.
    public static bool AbuseRespectsSanctuary = true;

    // --- Расклад сил ---------------------------------------------------------

    // Во сколько раз он должен быть сильнее, чтобы она сдалась.
    public static float AbuseSubmitRatio = 1.2f;

    // Вклад оружия/рук против вклада брони и целости в оценку «кто сильнее».
    public static float AbuseForceOffenseWeight = 0.6f;
    public static float AbuseForceDefenseWeight = 0.4f;

    // Каждая подруга рядом добавляет ей столько же силы в её глазах. Это и есть
    // «она отказала, потому что подошли свои» — без единого частного случая.
    public static float AbuseAllyForceShare = 0.6f;

    // --- Сцена ---------------------------------------------------------------

    public static int AbuseDurationTicks = 48;
    public static int AbuseBeatCryTicks = 12;
    public static int AbuseBeatBlowTicks = 20;
    public static int AbuseBeatBlowSecondTicks = 32;
    public static int AbuseBeatVerdictTicks = 40;
    public static int AbuseBeatTakeTicks = 44;

    // «Может пару раз ударить». Кулаками, НЕ оружием: ему нужны её припасы и её
    // страх, а не её труп; два удара ножом загнали бы её в порог бегства, и
    // сцена свалилась бы в обычный налёт.
    public static int AbuseMaxBlows = 2;
    public static float AbuseBlowDamageMult = 0.35f;

    // Ниже этого здоровья удар просто не наносится: пугать умирающую незачем,
    // а добить её сцена не должна.
    public static float AbuseNoBlowHealthFloor = 0.5f;

    // Сколько защитниц рядом заставляют его бросить сцену.
    public static int AbuseBreakOffDefenders = 3;

    // --- Последствия ---------------------------------------------------------

    // ⭐ Ради этого всё и затевалось: сцена НАСЫЩАЕТ его общением.
    public static float AbuseSocialGain = 0.55f;

    // Ей от этого не легче: чужой контакт общением не считается.
    public static float AbuseMarkSocialGain = 0f;

    public static float AbuseMarkStressCost = 0.30f;
    public static float AbuseAffinityLoss = 0.35f;
    public static float AbuseTrustLoss = 0.25f;

    // Пассивная кража §40.5 намеренно не гейтилась по фракции. Теперь у чужака
    // есть настоящая сцена, и тихий грабёж мимо неё только мешал бы: он молча
    // унёс бы еду, пока идёт её же отжимать.
    public static bool AbuseSupersedesPassiveTheft = true;
}

}
