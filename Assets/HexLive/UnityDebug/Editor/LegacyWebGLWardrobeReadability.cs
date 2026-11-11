#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    // Output-only normalization. Never changes model input fingerprints or any
    // geometry byte. Imported native authoring is outside the allowed root.
    static byte[] ReadableMeshBytes(byte[] before)
    {
        var marker = Encoding.ASCII.GetBytes("  m_IsReadable: ");
        if (before.Length < 20 || Encoding.ASCII.GetString(before,0,Math.Min(20,before.Length)).IndexOf("%YAML",StringComparison.Ordinal) < 0)
            throw new InvalidDataException("Expected serialized YAML Mesh asset");
        string text = Encoding.UTF8.GetString(before);
        if (!text.Contains("--- !u!43 &") || Regex.Matches(text,@"(?m)^--- !u!").Count != 1)
            throw new InvalidDataException("Expected exactly one serialized Mesh object");
        int at = -1;
        for (int i=0;i<=before.Length-marker.Length;i++)
        {
            bool match = i == 0 || before[i-1] == 10;
            for (int j=0;match && j<marker.Length;j++) match = before[i+j] == marker[j];
            if (!match) continue;
            if (at >= 0) throw new InvalidDataException("Duplicate readability field");
            at=i+marker.Length;
        }
        if (at < 0 || at+1 >= before.Length || (before[at] != 48 && before[at] != 49) ||
            (before[at+1] != 13 && before[at+1] != 10)) throw new InvalidDataException("Missing/nonboolean readability field");
        var after=(byte[])before.Clone(); after[at]=49;
        int differences=Enumerable.Range(0,before.Length).Count(i=>before[i]!=after[i]);
        if (differences != (before[at] == 48 ? 1 : 0)) throw new InvalidDataException("Unexpected geometry byte modification");
        return after;
    }

    public static void NormalizePreparedMeshReadability()
    {
        string cachePath=PreparedRoot+"/preparation.json";
        if (!File.Exists(cachePath)) { Debug.Log("LEGACY_READABILITY_NORMALIZED: no prepared cache yet"); return; }
        var cache=JObject.Parse(File.ReadAllText(cachePath));
        var protectedFiles=new Dictionary<string,string>(StringComparer.Ordinal);
        void Protect(string path) { if (File.Exists(path)) protectedFiles[path]=FileHash(path); }
        Protect(cachePath); Protect(PreparedRoot+"/catalog.json"); Protect(PeopleRoot+"/catalog.json");
        foreach(string file in new[]{"LegacyWebGLWardrobeAssets.cs","LegacyWebGLWardrobeGeometry.cs","LegacyWardrobeTriangleTree.cs","LegacyWebGLWardrobePreparation.cs","LegacyWardrobeQuality.cs"})
            Protect("Assets/HexLive/UnityDebug/Editor/"+file);
        var inputs=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var property in cache.Properties())
        {
            string source=(string)property.Value["source"];
            if (!string.IsNullOrEmpty(source)) inputs[source]=AssetDatabase.GetAssetDependencyHash(source).ToString();
        }
        foreach(string path in FittingUnderlayerPaths.Concat(new[]{PeopleRoot+"/Prefabs/Actors/Marta.prefab",PeopleRoot+"/Prefabs/Actors/Kshishtof.prefab"}))
            inputs[path]=AssetDatabase.GetAssetDependencyHash(path).ToString();
        string simplifier=SimplifierInputFingerprint();
        var pending=new Dictionary<string,(byte[] before,byte[] after,byte[] meta)>(StringComparer.Ordinal);
        var affected=new HashSet<string>(StringComparer.Ordinal);
        var allPrefabs=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in cache.Properties())
        {
            string prefabPath=(string)property.Value["prepared"];
            if (string.IsNullOrEmpty(prefabPath) || !prefabPath.StartsWith(PreparedRoot+"/Prefabs/",StringComparison.Ordinal))
                throw new InvalidDataException("Unowned prepared prefab path");
            allPrefabs.Add(prefabPath); Protect(prefabPath); Protect(prefabPath+".meta");
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) continue; // A genuinely absent artifact remains a normal cache miss for PrepareAll.
            var wear=prefab.GetComponent<Wear>();
            if (wear == null) continue;
            var configs=new SerializedObject(wear).FindProperty("configs");
            for(int i=0;i<configs.arraySize;i++)
            {
                var mesh=configs.GetArrayElementAtIndex(i).FindPropertyRelative("mesh").objectReferenceValue as Mesh;
                if (mesh == null || mesh.isReadable) continue;
                string path=AssetDatabase.GetAssetPath(mesh);
                if (!path.StartsWith(PreparedRoot+"/Meshes/",StringComparison.Ordinal) || Path.GetExtension(path)!=".asset" || !File.Exists(path+".meta"))
                    throw new InvalidDataException("Refusing non-owned Mesh normalization: "+path);
                if (mesh.vertexCount<=0 || mesh.subMeshCount<=0) throw new InvalidDataException("Unreadable mesh is also empty: "+path);
                var before=File.ReadAllBytes(path);var after=ReadableMeshBytes(before);
                if (before.SequenceEqual(after)) throw new InvalidDataException("Loaded/serialized readability disagree: "+path);
                if (!pending.ContainsKey(path)) pending.Add(path,(before,after,File.ReadAllBytes(path+".meta")));
                affected.Add(prefabPath);
            }
        }
        string backup=OutputRoot+"/readability-repair-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
        if (Directory.Exists(backup)) throw new IOException("Repair evidence directory already exists");
        Directory.CreateDirectory(backup);
        var rows=new JArray();
        foreach(var pair in pending)
        {
            string relative=pair.Key.Substring(PreparedRoot.Length+1);
            string copy=backup+"/before/"+relative;Directory.CreateDirectory(Path.GetDirectoryName(copy));
            File.WriteAllBytes(copy,pair.Value.before);File.WriteAllBytes(copy+".meta",pair.Value.meta);
            rows.Add(new JObject{["path"]=pair.Key,["backup"]=copy,["beforeSha256Base64"]=FileHash(pair.Key),
                ["metaSha256Base64"]=FileHash(pair.Key+".meta"),["changedBytes"]=1});
        }
        var proof=new JObject{["passed"]=false,["geometryRegenerated"]=false,["cacheFingerprintsModified"]=false,
            ["meshesChanged"]=pending.Count,["affectedPrefabs"]=affected.Count,["files"]=rows};
        File.WriteAllText(backup+"/receipt.json",proof.ToString());
        var written=new List<string>();
        try
        {
            // Complete backups and every precondition exist before the first write.
            foreach(var pair in pending)
            {
                if (!File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value.before) || !File.ReadAllBytes(pair.Key+".meta").SequenceEqual(pair.Value.meta))
                    throw new IOException("Mesh changed after repair preflight: "+pair.Key);
            }
            foreach(var pair in pending) { written.Add(pair.Key); File.WriteAllBytes(pair.Key,pair.Value.after); }
            foreach(string path in written) AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);
            foreach(var pair in pending)
            {
                if (!File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value.after) || !File.ReadAllBytes(pair.Key+".meta").SequenceEqual(pair.Value.meta))
                    throw new InvalidDataException("Import changed bytes beyond the proven flag: "+pair.Key);
                var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(pair.Key);
                if (mesh == null || !mesh.isReadable || mesh.vertexCount<=0 || mesh.subMeshCount<=0)
                    throw new InvalidDataException("Actual imported mesh not CPU-readable: "+pair.Key);
            }
            foreach(string path in affected)
                if (!CachedPrototypeValid(AssetDatabase.LoadAssetAtPath<GameObject>(path)))
                    throw new InvalidDataException("Repaired prefab still invalid: "+path);
            foreach(var pair in protectedFiles)
                if (FileHash(pair.Key)!=pair.Value) throw new InvalidDataException("Repair changed protected source/cache: "+pair.Key);
            foreach(var pair in inputs)
                if (AssetDatabase.GetAssetDependencyHash(pair.Key).ToString()!=pair.Value) throw new InvalidDataException("Input dependency hash changed: "+pair.Key);
            if (SimplifierInputFingerprint()!=simplifier) throw new InvalidDataException("Simplifier input changed");
            proof["passed"]=true; proof["allAffectedCachedPrototypeValid"]=true;proof["inputHashesUnchanged"]=true;
            proof["actualValidCachedPrefabs"]=allPrefabs.Count(path=>CachedPrototypeValid(AssetDatabase.LoadAssetAtPath<GameObject>(path)));
            foreach(JObject row in rows)row["afterSha256Base64"]=FileHash((string)row["path"]);
        }
        catch(Exception error)
        {
            proof["exception"]=error.ToString();
            foreach(string path in written) { File.WriteAllBytes(path,pending[path].before);File.WriteAllBytes(path+".meta",pending[path].meta); }
            foreach(string path in written) AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);
            proof["rolledBackBytes"]=written.All(path=>File.ReadAllBytes(path).SequenceEqual(pending[path].before) && File.ReadAllBytes(path+".meta").SequenceEqual(pending[path].meta));
            throw;
        }
        finally { File.WriteAllText(backup+"/receipt.json",proof.ToString()); }
        Debug.Log("LEGACY_READABILITY_NORMALIZED: "+pending.Count+" meshes; "+backup+"/receipt.json");
    }
}
}
#endif
