namespace HexLive.Simulation.Common
{
    /// <summary>
    /// §170.4: эпоха «кто где стоит и что держит». Растёт на каждую запись
    /// <c>NPCState.CurrentJunction</c>, <c>MobState.Junction</c> и на каждое
    /// изменение <c>ClaimedJunctions</c> (оба места — в ExecutionSystem).
    /// Единственный потребитель — мемо <c>PathfindingSystem.OtherActorJunctions</c>:
    /// множество занятых людьми узлов строилось с нуля на КАЖДУЮ проверку
    /// подхода (сотни раз за средний тик при ста колонистках), хотя между
    /// проверками одного прохода решений никто не двигался. Статик, а не поле
    /// мира, намеренно: сеттеры состояния мира не знают; «любое изменение
    /// где угодно» лишь чаще сбрасывает кэш, но никогда не делает его лживым.
    /// </summary>
    public static class ActorOccupancy
    {
        public static int Epoch;
    }
}
