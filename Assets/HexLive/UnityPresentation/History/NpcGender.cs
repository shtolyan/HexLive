#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;

namespace HexLive.UnityPresentation.History
{
    /// <summary>
    /// §58.9: кто из встреченных за сессию — мужчина. Лента колонии и дневник
    /// хранят только id или id имени, а тело к моменту чтения уже может лежать
    /// в могиле, поэтому книга помнит всех, кого видела хоть в одном снапшоте.
    /// Не видела — женская форма, так строка читается ровно как до разметки.
    /// </summary>
    public static class NpcGender
    {
        private static readonly Dictionary<int, bool> ById = new();
        private static readonly Dictionary<string, bool> ByName = new();

        public static bool IsMale(NpcSnapshot? npc) => npc != null && GenderedText.IsMaleBody(npc.ActorMesh);

        public static void Remember(WorldSnapshot? snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            Remember(snapshot.Npcs);
            Remember(snapshot.Corpses);
        }

        public static bool IsMale(int? id) => id.HasValue && ById.TryGetValue(id.Value, out var male) && male;

        public static bool IsMaleName(string? nameId) =>
            !string.IsNullOrEmpty(nameId) && ByName.TryGetValue(nameId!, out var male) && male;

        private static void Remember(List<NpcSnapshot> npcs)
        {
            foreach (var npc in npcs)
            {
                var male = IsMale(npc);
                ById[npc.Id.Value] = male;
                if (!string.IsNullOrEmpty(npc.DisplayName))
                {
                    ByName[npc.DisplayName] = male;
                }
            }
        }
    }
}
