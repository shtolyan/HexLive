#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Debug;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>§119 tabletop/ground ingredient layout and Sims-like progress capsule.</summary>
    public sealed class CraftProjectVisual : MonoBehaviour
    {
        private const float TableTopY = 0.805f;
        private readonly List<GameObject> _ingredients = new();
        private Transform? _output;
        private Transform? _capsule;
        private Transform? _fill;
        private Renderer[] _fillRenderers = System.Array.Empty<Renderer>();
        private string _ingredientSignature = string.Empty;
        private bool _outputSized;
        private Material? _idleMaterial;
        private Material? _activeMaterial;

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

            EnsureCapsule();
            var complete = snapshot.CraftWorkRequired <= 0 ||
                snapshot.CraftWorkDone >= snapshot.CraftWorkRequired;
            if (_capsule != null)
            {
                _capsule.gameObject.SetActive(!complete);
                _capsule.localPosition = new Vector3(0f, onTable ? 1.16f : 0.54f, 0f);
            }
            if (complete || _fill == null) return;

            var progress = Mathf.Clamp01(snapshot.CraftWorkDone /
                (float)Mathf.Max(1, snapshot.CraftWorkRequired));
            var width = 0.46f * progress;
            _fill.localScale = new Vector3(Mathf.Max(0.001f, width), 0.065f, 0.055f);
            _fill.localPosition = new Vector3(-0.23f + width * 0.5f, 0f, -0.012f);
            var material = snapshot.CraftActive ? _activeMaterial : _idleMaterial;
            foreach (var renderer in _fillRenderers) renderer.sharedMaterial = material;
            var pulse = snapshot.CraftActive ? 1f + Mathf.Sin(Time.time * 6f) * 0.06f : 1f;
            _capsule.localScale = Vector3.one * pulse;
        }

        private void LateUpdate()
        {
            if (_capsule == null || !_capsule.gameObject.activeSelf || Camera.main == null) return;
            var direction = _capsule.position - Camera.main.transform.position;
            if (direction.sqrMagnitude > 0.001f)
            {
                _capsule.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
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
            if (_outputSized || !ObjectFit.WorldBounds(_output.gameObject, out var bounds)) return;
            var current = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (current > 0.24f) _output.localScale *= 0.24f / current;
            _outputSized = true;
        }

        private void RebuildIngredients(IReadOnlyList<string> ids, bool onTable)
        {
            foreach (var go in _ingredients) if (go != null) Destroy(go);
            _ingredients.Clear();
            for (var i = 0; i < ids.Count && i < 6; i++)
            {
                var model = LowPolyToolFactory.Build(ids[i]);
                if (model == null)
                {
                    var prefab = WorldPropResources.Load(ids[i]);
                    model = prefab != null ? Instantiate(prefab) : GameObject.CreatePrimitive(PrimitiveType.Cube);
                }
                model.name = $"Craft Ingredient {i:00} {ids[i]}";
                model.transform.SetParent(transform, false);
                if (ObjectFit.WorldBounds(model, out var bounds))
                {
                    var horizontal = Mathf.Max(bounds.size.x, bounds.size.z);
                    if (horizontal > 0.16f) model.transform.localScale *= 0.16f / horizontal;
                }

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

        private void EnsureCapsule()
        {
            if (_capsule != null) return;
            var root = new GameObject("Craft Progress Capsule");
            root.transform.SetParent(transform, false);
            _capsule = root.transform;

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var shellMaterial = new Material(shader) { color = new Color(0.08f, 0.10f, 0.11f, 0.92f) };
            _idleMaterial = new Material(shader) { color = new Color(0.23f, 0.48f, 0.35f, 0.78f) };
            _activeMaterial = new Material(shader) { color = new Color(0.31f, 0.92f, 0.56f, 1f) };
            SetBaseColor(shellMaterial, shellMaterial.color);
            SetBaseColor(_idleMaterial, _idleMaterial.color);
            SetBaseColor(_activeMaterial, _activeMaterial.color);

            var shell = GameObject.CreatePrimitive(PrimitiveType.Cube);
            shell.name = "Craft Capsule Shell";
            shell.transform.SetParent(root.transform, false);
            shell.transform.localScale = new Vector3(0.56f, 0.11f, 0.075f);
            shell.GetComponent<Renderer>().sharedMaterial = shellMaterial;
            Destroy(shell.GetComponent<Collider>());

            var fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fill.name = "Craft Capsule Fill";
            fill.transform.SetParent(root.transform, false);
            _fill = fill.transform;
            _fillRenderers = fill.GetComponentsInChildren<Renderer>();
            Destroy(fill.GetComponent<Collider>());

            AddCap(root.transform, -0.28f, shellMaterial);
            AddCap(root.transform, 0.28f, shellMaterial);
        }

        private static void AddCap(Transform parent, float x, Material material)
        {
            var cap = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            cap.name = "Craft Capsule Cap";
            cap.transform.SetParent(parent, false);
            cap.transform.localPosition = new Vector3(x, 0f, 0f);
            cap.transform.localScale = new Vector3(0.11f, 0.11f, 0.075f);
            cap.GetComponent<Renderer>().sharedMaterial = material;
            Destroy(cap.GetComponent<Collider>());
        }

        private static void SetBaseColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.28f);
        }
    }
}
