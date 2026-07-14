using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Spec 20.16 / attach-points: the saved home for how each carried item sits
    /// in the hand. One entry per item id holds the prop's local pose in the
    /// acting hand's space (rHand-local, right-handed). The ItemAttach test scene
    /// lets you drop any item in the hand, nudge it live, and "Save" writes the
    /// pose back here; the asset is committed to git so tuning is shared.
    ///
    /// One asset lives in Resources/HexLive so runtime code (NpcActorView.SetHandProp)
    /// can load it. Missing asset or missing entry → SetHandProp falls back to a
    /// built-in default table, then to an automatic palm-fit, so the game never
    /// breaks before the asset is authored.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Item Attach Config", fileName = "ItemAttachConfig")]
    public sealed class ItemAttachConfig : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            [Tooltip("id предмета (ObjectDefinition.Id), напр. tool.axe_stone")]
            public string itemId;

            [Header("Правая рука (rHand-local)")]
            [Tooltip("Позиция предмета в системе координат ладони.")]
            public Vector3 localPosition;

            [Tooltip("Поворот предмета (эйлеровы углы, градусы).")]
            public Vector3 localEuler;

            [Tooltip("Масштаб предмета.")]
            public Vector3 localScale;

            [Header("Левая рука (опционально)")]
            [Tooltip("Если выключено — лево-рукий хват зеркалит правый автоматически.")]
            public bool hasLeft;

            public Vector3 leftLocalPosition;

            public Vector3 leftLocalEuler;
        }

        [Tooltip("По одной записи на предмет. Порядок не важен — поиск по itemId.")]
        public List<Entry> entries = new();

        private Dictionary<string, Entry> _index;

        private void BuildIndex()
        {
            _index = new Dictionary<string, Entry>(entries.Count);
            foreach (var e in entries)
            {
                if (!string.IsNullOrEmpty(e.itemId))
                {
                    _index[e.itemId] = e;
                }
            }
        }

        /// <summary>Tuned pose for an item, in the acting hand's local space.</summary>
        public bool TryGet(string itemId, bool leftHanded,
            out Vector3 localPosition, out Quaternion localRotation, out Vector3 localScale)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            localScale = Vector3.one;

            if (string.IsNullOrEmpty(itemId))
            {
                return false;
            }

            if (_index == null || _index.Count != entries.Count)
            {
                BuildIndex();
            }

            if (!_index.TryGetValue(itemId, out var e))
            {
                return false;
            }

            localScale = e.localScale == Vector3.zero ? Vector3.one : e.localScale;

            if (leftHanded && e.hasLeft)
            {
                localPosition = e.leftLocalPosition;
                localRotation = Quaternion.Euler(e.leftLocalEuler);
            }
            else if (leftHanded)
            {
                // Mirror the right-hand pose across the body's sagittal plane.
                localPosition = new Vector3(-e.localPosition.x, e.localPosition.y, e.localPosition.z);
                localRotation = Quaternion.Euler(e.localEuler.x, -e.localEuler.y, -e.localEuler.z);
            }
            else
            {
                localPosition = e.localPosition;
                localRotation = Quaternion.Euler(e.localEuler);
            }

            return true;
        }

        /// <summary>Write (or replace) a right-hand pose for an item.</summary>
        public void Set(string itemId, Vector3 localPosition, Vector3 localEuler, Vector3 localScale)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].itemId == itemId)
                {
                    var kept = entries[i];
                    kept.localPosition = localPosition;
                    kept.localEuler = localEuler;
                    kept.localScale = localScale;
                    entries[i] = kept;
                    _index = null;
                    return;
                }
            }

            entries.Add(new Entry
            {
                itemId = itemId,
                localPosition = localPosition,
                localEuler = localEuler,
                localScale = localScale,
            });
            _index = null;
        }

        // ---- shared runtime instance ----

        private static ItemAttachConfig _instance;
        private static bool _loaded;

        public static ItemAttachConfig Instance
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _instance = Resources.Load<ItemAttachConfig>("HexLive/ItemAttachConfig");
                }

                return _instance;
            }
        }

        // Forget the cached instance so a freshly saved asset is re-read (used by
        // the test scene after a Save).
        public static void InvalidateCache()
        {
            _loaded = false;
            _instance = null;
        }
    }
}
