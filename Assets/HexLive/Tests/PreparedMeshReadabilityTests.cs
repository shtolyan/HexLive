#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
namespace HexLive.Tests
{
public sealed class PreparedMeshReadabilityTests
{
    static byte[] Normalize(byte[] bytes)
    {
        var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation")).First(t=>t!=null);
        return (byte[])type.GetMethod("ReadableMeshBytes",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{bytes});
    }
    [TestCase("0",1)] [TestCase("1",0)]
    public void FlagOnlyNormalizationPreservesEveryGeometryByte(string flag,int expectedDifferences)
    {
        var before=Encoding.UTF8.GetBytes("%YAML 1.1\r\n--- !u!43 &4300000\r\nMesh:\r\n  m_Name: précis\r\n  m_IsReadable: "+flag+"\r\n  m_VertexData: 010203abcdef\r\n");
        var original=(byte[])before.Clone();var after=Normalize(before);
        Assert.That(before,Is.EqualTo(original));Assert.That(after.Length,Is.EqualTo(before.Length));
        Assert.That(Enumerable.Range(0,before.Length).Count(i=>before[i]!=after[i]),Is.EqualTo(expectedDifferences));
        Assert.That(Encoding.UTF8.GetString(after),Does.Contain("m_IsReadable: 1\r\n"));
    }
    [TestCase("%YAML 1.1\n--- !u!43 &1\nMesh:\n  m_IsReadable: 0\n  m_IsReadable: 0\n")]
    [TestCase("%YAML 1.1\n--- !u!21 &1\nMaterial:\n  m_IsReadable: 0\n")]
    [TestCase("not yaml or a mesh")]
    public void RejectsAmbiguousOrNonMeshInput(string text)
    {
        var error=Assert.Throws<TargetInvocationException>(()=>Normalize(Encoding.UTF8.GetBytes(text)));
        Assert.That(error.InnerException,Is.TypeOf<InvalidDataException>());
    }
    [Test]
    public void ActualCopiedMeshImportsReadableWithOneByteChangeAndSameGuid()
    {
        const string source="Assets/HexLiveContent/People/Wardrobe/Meshes/2c31f3077c018e48e5764b41517092d8_Marta.asset";
        string directory="Assets/__PreparedReadability_"+Guid.NewGuid().ToString("N"),path=directory+"/mesh.asset";
        Assert.That(AssetDatabase.LoadAssetAtPath<Mesh>(source),Is.Not.Null,source);
        var sourceBefore=File.ReadAllBytes(source);
        AssetDatabase.CreateFolder("Assets",Path.GetFileName(directory));
        try
        {
            Assert.That(AssetDatabase.CopyAsset(source,path),Is.True);
            string text=File.ReadAllText(path).Replace("  m_IsReadable: 1","  m_IsReadable: 0");
            File.WriteAllText(path,text,new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);
            Assert.That(AssetDatabase.LoadAssetAtPath<Mesh>(path).isReadable,Is.False);
            var before=File.ReadAllBytes(path);var meta=File.ReadAllBytes(path+".meta");string guid=AssetDatabase.AssetPathToGUID(path);
            var after=Normalize(before);File.WriteAllBytes(path,after);
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);
            var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            Assert.That(mesh.isReadable,Is.True);Assert.That(mesh.vertices.Length,Is.EqualTo(mesh.vertexCount));
            Assert.That(AssetDatabase.AssetPathToGUID(path),Is.EqualTo(guid));Assert.That(File.ReadAllBytes(path+".meta"),Is.EqualTo(meta));
            Assert.That(File.ReadAllBytes(path),Is.EqualTo(after));
            Assert.That(Enumerable.Range(0,before.Length).Count(i=>before[i]!=after[i]),Is.EqualTo(1));
        }
        finally
        {
            AssetDatabase.DeleteAsset(directory);
            Assert.That(File.ReadAllBytes(source),Is.EqualTo(sourceBefore),"Real source asset must remain unchanged");
        }
    }
}
}
#endif
