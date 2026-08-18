namespace HexLive.Simulation.Bootstrap
{
    // §146: «Выживание на большом острове». Пока фаза 2 — ЗАГЛУШКА: остров
    // Feud без лагеря чужака (и, следовательно, без рейдовых волн §72.14 —
    // RaidWaveSystem сам глохнет без FactionHomes[Outsiders]). Она существует,
    // чтобы весь каркас режима — меню, сейвы, сервер, хендшейк — прошёл
    // насквозь и был проверен до настоящего worldgen'а (§146.4/§146.7),
    // который заменит тело CreateBigIsland в фазе 3.
    public static partial class PrototypeWorldDefinitionFactory
    {
        // §146.2: ревизия генератора большого острова. Пишется в блоб сейва и
        // сверяется при загрузке: рост карты (2.5× → 6×, §146.7) меняет число,
        // и сейв от старой геометрии отклоняется честно, а не портится молча.
        // Остров Feud отгружен и заморожен, ему ревизия не нужна.
        public const int BigIslandWorldGenRevision = 1;

        internal static WorldBootstrapDefinition CreateBigIsland(int seed)
        {
            var definition = CreateFeud(seed, includeOutsiders: false);
            definition.Simulation.Mode = GameMode.BigIsland;
            return definition;
        }
    }
}
