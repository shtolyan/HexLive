#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Debug;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>§119 tabletop/ground ingredient layout. Progress is shown by the active NPC.</summary>
    public sealed class CraftProjectVisual : MonoBehaviour
    {
        private const float TableTopY = 0.805f;
        private readonly List<GameObject> _ingredients = new();
        private Transform? _output;
        private string _ingredientSignature = string.Empty;

        public void Sync(ObjectSnapshot snapshot)
        {
            EnsureOutput();
            var onTable = snapshot.CraftStationObjectId.HasValue;
            PositionOutput(onTable);

            var signature = string.Join("|", snapshot.CraftIngredients);
            if (signature != _ingredientSignature)
            {
                RebuildIngredients(snapshot.CraftIngredients, onTable);
                _ingredientSignature = signature;
            }

        }

        private void EnsureOutput()
        {
            if (_output != null) return;
            for (var i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (!child.name.StartsWith("Craft "))
                {
                    _output = child;
                    break;
                }
            }
        }

        private void PositionOutput(bool onTable)
        {
            if (_output == null) return;
            _output.localPosition = new Vector3(0f, onTable ? TableTopY + 0.035f : 0.035f, 0f);
            _output.localRotation = Quaternion.Euler(onTable ? 0f : 86f, 0f, 0f);
        }

        private void RebuildIngredients(IReadOnlyList<string> ids, bool onTable)
        {
            foreach (var go in _ingredients) if (go != null) Destroy(go);
            _ingredients.Clear();
            for (var i = 0; i < ids.Count && i < 6; i++)
            {
                // §119.1 / bug #137: use the authored Player-safe model first,
                // exactly like a world drop. Crafting used to call the
                // procedural factory first, silently replacing valid native
                // resources, then clamp everything to 0.16 wu.
                var model = WorldPropResources.Build(ids[i]) ??
                            GameObject.CreatePrimitive(PrimitiveType.Cube);
                model.name = $"Craft Ingredient {i:00} {ids[i]}";
                model.transform.SetParent(transform, false);
                model.transform.localScale *= ObjectFit.FitScaleFactor(model, ids[i]);

                if (onTable)
                {
                    var side = i % 2 == 0 ? -0.31f : 0.31f;
                    var row = i / 2;
                    model.transform.localPosition = new Vector3(
                        side, TableTopY + 0.025f, -0.22f + row * 0.22f);
                    model.transform.localRotation = Quaternion.Euler(0f, i * 37f, 0f);
                }
                else
                {
                    var angle = i * Mathf.PI * 2f / Mathf.Max(1, ids.Count);
                    model.transform.localPosition = new Vector3(
                        Mathf.Cos(angle) * 0.28f, 0.025f, Mathf.Sin(angle) * 0.28f);
                    model.transform.localRotation = Quaternion.Euler(86f, i * 47f, 0f);
                }
                foreach (var collider in model.GetComponentsInChildren<Collider>()) Destroy(collider);
                _ingredients.Add(model);
            }
        }

    }
}
