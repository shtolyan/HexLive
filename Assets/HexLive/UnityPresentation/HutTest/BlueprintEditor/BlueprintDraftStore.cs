#nullable enable
using System;
using System.IO;
using HexLive.Simulation.Runtime.Blueprints;
using UnityEngine;

namespace HexLive.UnityPresentation.HutTest.BlueprintEditor
{
    public sealed class BlueprintDraftStore
    {
        private const string FolderName = "BlueprintDrafts";

        public string DirectoryPath => Path.Combine(
            Application.persistentDataPath, "HexLive", FolderName);

        public string PathFor(string blueprintId)
        {
            var safe = string.IsNullOrWhiteSpace(blueprintId) ? "draft" : blueprintId;
            foreach (var invalid in Path.GetInvalidFileNameChars()) safe = safe.Replace(invalid, '_');
            return Path.Combine(DirectoryPath, safe + ".json");
        }

        public bool TryLoad(string blueprintId, out BuildingBlueprintDraft draft, out string error)
        {
            var path = PathFor(blueprintId);
            if (!File.Exists(path))
            {
                draft = null!;
                error = string.Empty;
                return false;
            }
            try
            {
                return BuildingBlueprintJson.TryDeserialize(File.ReadAllText(path), out draft!, out error);
            }
            catch (Exception exception)
            {
                draft = null!;
                error = exception.Message;
                return false;
            }
        }

        public string Save(BuildingBlueprintDraft draft)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = PathFor(draft.BlueprintId);
            var temporary = path + ".tmp";
            var backup = path + ".bak";
            File.WriteAllText(temporary, BuildingBlueprintJson.Serialize(draft) + "\n");
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
                    if (File.Exists(backup)) File.Delete(backup);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(path);
                    File.Move(temporary, path);
                }
            }
            else
            {
                File.Move(temporary, path);
            }
            return path;
        }
    }
}
