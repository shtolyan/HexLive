using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.TwoPeopleTest
{
    /// <summary>
    /// Authored male-offsets for the paired romance animations (TwoPeopleTest).
    /// The FEMALE clip is always the anchor and plays at the pair root as-is;
    /// the male actor is placed at <see cref="Entry.malePosition"/> /
    /// <see cref="Entry.maleEuler"/> relative to that root. Tuned in the
    /// TwoPeopleTest scene (Save button) and read back on the next open —
    /// and later by the game when it plays a pair for real.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Romance Pose Catalog", fileName = "RomancePoseCatalog")]
    public sealed class RomancePoseCatalog : ScriptableObject
    {
        public const string ResourcesPath = "HexLive/Romance/RomancePoseCatalog";

        [Serializable]
        public sealed class Entry
        {
            [Tooltip("Ключ пары = имя клипа без префикса Female_/Male_, напр. Standing_Doggy_Pose0.")]
            public string key;
            [Tooltip("Смещение мужчины относительно корня пары (женщина стоит в корне).")]
            public Vector3 malePosition;
            [Tooltip("Поворот мужчины (эйлеры) относительно корня пары.")]
            public Vector3 maleEuler;
            [Tooltip("true = офсет настроен и сохранён игроком; false = дефолт, ещё не тюнили.")]
            public bool authored;
            [Header("Runtime paired clips")]
            public AnimationClip femaleLoop;
            public AnimationClip maleLoop;
            public AnimationClip femaleClimax;
            public AnimationClip maleClimax;
            [Tooltip("Веса Dicktator-пресетов гениталий ДЛЯ ЭТОЙ позы (у каждой позы свои).")]
            public List<GenitalShape> genitalShapes = new List<GenitalShape>();
        }

        [Serializable]
        public sealed class GenitalShape
        {
            [Tooltip("Подстрока имени Dicktator-бленд-шейпа, напр. Flacid Preset 01.")]
            public string shape = "";
            [Range(0f, 100f)] public float weight;
        }

        [Tooltip("Последняя выбранная поза — восстанавливается при открытии сцены.")]
        public string lastSelectedKey = "";

        public List<Entry> entries = new List<Entry>();

        public bool TryGet(string key, out Entry entry)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].key == key) { entry = entries[i]; return true; }
            }
            entry = null;
            return false;
        }

        public Entry GetOrAdd(string key)
        {
            if (TryGet(key, out var e)) return e;
            e = new Entry { key = key };
            entries.Add(e);
            return e;
        }
    }
}
