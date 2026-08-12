using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.HexFlowerTest
{
    /// <summary>
    /// Сеты романтических сцен (HexFlowerTest): сет = парная анимация + точка
    /// гекс-сетки, где стоит героиня, + её поворот (+ высоты цветка и флаг
    /// парня). Позиция парня — производная (§127-офсет от героини), но его
    /// БЛИЖАЙШИЙ УЗЕЛ сетки анализируется и сохраняется в сете — по нему игра
    /// потом решит, какой соседний гекс должен быть свободен/каким быть.
    /// Что сеты запускает в игре — ещё не решено; тул их только создаёт.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Romance Set Catalog", fileName = "RomanceSetCatalog")]
    public sealed class RomanceSetCatalog : ScriptableObject
    {
        public const string ResourcesPath = "HexLive/Romance/RomanceSetCatalog";

        [Serializable]
        public sealed class SceneSet
        {
            public string name = "";
            [Tooltip("Ключ пары §127 (имя клипа без Female_/Male_).")]
            public string clipKey = "";

            [Tooltip("Узел героини: якорный тайл и суб-координата шаблона.")]
            public int girlTileQ, girlTileR, girlSubQ, girlSubR;
            [Tooltip("Поворот героини в сим-градусах (0° = +X, 60° = шаг соседа).")]
            public float facingDeg;

            public bool maleEnabled;
            [Tooltip("Высоты 7 тайлов цветка в порядке: центр, (1,0), (1,-1), (0,-1), (-1,0), (-1,1), (0,1).")]
            public int[] elevations = Array.Empty<int>();

            [Tooltip("АНАЛИЗ: ближайший к парню узел сетки (его якорный тайл + суб).")]
            public int maleTileQ, maleTileR, maleSubQ, maleSubR;
            [Tooltip("АНАЛИЗ: расстояние от парня до этого узла, wu (XZ).")]
            public float maleNodeDistance;
            [Tooltip("АНАЛИЗ: мировая дельта парень-героиня на момент сохранения.")]
            public Vector3 maleWorldOffset;
        }

        public List<SceneSet> sets = new List<SceneSet>();
    }
}
