using System.Collections.Generic;
using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Garments
{
    /// <summary>
    /// Оглавление гардероба: ЧТО вообще бывает, без единого меша и текстуры.
    ///
    /// Зачем отдельный ассет, когда данные уже лежат в GarmentDefinition:
    /// определение живёт в бандле СВОЕЙ вещи, вместе с её артом. Прочитать его
    /// — значит загрузить бандл целиком, то есть притащить меши и текстуры
    /// вещи, которую никто не надел. На старте так пришлось бы поднять весь
    /// гардероб — ровно то, от чего уходили.
    ///
    /// Поэтому деление такое:
    ///   * ЭТОТ ассет — маленький, свой отдельный бандл, грузится на старте
    ///     целиком: id, статы, слоты, строки, адрес иконки;
    ///   * бандл вещи — тяжёлый, грузится когда вещь понадобилась, и кэшируется.
    ///
    /// Собирается меню <b>HexLive ▸ Addressables ▸ Собрать оглавление гардероба</b>
    /// и обязан пересобираться после КАЖДОЙ новой партии одежды: вещь, которой
    /// нет в оглавлении, для игры не существует, сколько бандлов ни положи.
    /// </summary>
    public sealed class WardrobeIndex : ScriptableObject
    {
        public const string Address = "wardrobe/index";

        [System.Serializable]
        public sealed class Row
        {
            public string id = string.Empty;
            public string artId = string.Empty;
            public string displayName = string.Empty;

            public int layer;
            public float warmth;
            public float armor;
            public float thermalDelta;
            public int dressDurationTicks = 8;
            public int capacity;
            public int sex;

            // Хранятся ЧИСЛАМИ, а не именами: enum в строках пришлось бы
            // разбирать на каждой загрузке и падать на опечатке в данных.
            public List<int> covers = new();
            public List<int> slots = new();

            // Локализация едет ВМЕСТЕ с вещью: у новой вещи её нет в билде, а
            // без имени она показалась бы игроку как «item.unknown».
            public string nameEn = string.Empty;
            public string nameRu = string.Empty;
            public string descEn = string.Empty;
            public string descRu = string.Empty;
        }

        public List<Row> items = new();

        public static GarmentParams ToParams(Row row)
        {
            var parts = new BodyPart[row.covers.Count];
            for (var i = 0; i < row.covers.Count; i++)
            {
                parts[i] = (BodyPart)row.covers[i];
            }

            return new GarmentParams(
                row.id,
                string.IsNullOrEmpty(row.displayName) ? row.id : row.displayName,
                (WearLayer)row.layer,
                row.warmth,
                row.armor,
                row.thermalDelta,
                row.dressDurationTicks,
                row.capacity,
                (GarmentSex)row.sex,
                parts)
            {
                PrototypeId = string.IsNullOrEmpty(row.artId) ? row.id : row.artId,
            };
        }
    }
}
