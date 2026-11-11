#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    internal static JObject CaptureNativeCollapsedZeroEvidence(GameObject native,ActorName actor)
    {
        var body=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot+"/Prefabs/Actors/"+actor+".prefab"));
        try
        {
            body.hideFlags=HideFlags.HideAndDontSave;body.GetComponent<Animator>().enabled=false;
            var bones=body.GetComponent<BodyBones>();bones.Construct(actor,649);
            bones.Equip("collapsed-proof-native",native.GetComponent<Wear>(),null);
            typeof(BodyBones).GetMethod("LateUpdate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(bones,null);
            var skin=bones.GetWorn("collapsed-proof-native").GetComponentInChildren<SkinnedMeshRenderer>(true);
            var proof=CaptureBakedCollapsedZeroEvidence(skin);
            string path=AssetDatabase.GetAssetPath(native),mesh=AssetDatabase.GetAssetPath(skin.sharedMesh);
            proof["equipmentFactory"]="BodyBones.Equip";proof["actor"]=actor.ToString();
            proof["nativeSource"]=path;proof["nativeSourceDependency"]=AssetDatabase.GetAssetDependencyHash(path).ToString();
            proof["nativeMesh"]=mesh;proof["nativeMeshSha"]=FinalFileSHA(mesh);return proof;
        }
        finally{Object.DestroyImmediate(body);}
    }
    internal static JObject CaptureBakedCollapsedZeroEvidence(SkinnedMeshRenderer skin)
    {
        var mesh=skin.sharedMesh;var raw=mesh.vertices;var zero=CollapsedZeroInfluences(mesh,out _,out _);
        var baked=new Mesh();
        try
        {
            skin.BakeMesh(baked,true);var actual=baked.vertices;
            if(actual.Length!=raw.Length)throw new InvalidOperationException("Native zero-island BakeMesh changed vertex count");
            int faces=0,mixed=0,rawVisible=0,bakedVisible=0;
            foreach(int sm in Enumerable.Range(0,mesh.subMeshCount))
            {
                if(mesh.GetTopology(sm)!=MeshTopology.Triangles)throw new InvalidOperationException("Native zero-island proof requires triangle topology");
                var triangles=mesh.GetTriangles(sm);
                for(int at=0;at<triangles.Length;at+=3)
                {
                    int a=triangles[at],b=triangles[at+1],c=triangles[at+2],n=(zero[a]?1:0)+(zero[b]?1:0)+(zero[c]?1:0);
                    if(n!=0&&n!=3)mixed++;
                    if(n!=3)continue;faces++;
                    if(CollapsedZeroNonzeroArea(raw[a],raw[b],raw[c]))rawVisible++;
                    if(CollapsedZeroNonzeroArea(actual[a],actual[b],actual[c]))bakedVisible++;
                }
            }
            int count=zero.Count(x=>x);bool collapsed=Enumerable.Range(0,zero.Length).Where(i=>zero[i]).All(i=>CollapsedZeroExact(actual[i]));
            return new JObject{["passed"]=count>0&&collapsed&&mixed==0&&bakedVisible==0,["actualBakeMesh"]=true,
                ["unweightedVertices"]=count,["allUnweightedFaces"]=faces,["mixedFaces"]=mixed,
                ["rawNondegenerateFaces"]=rawVisible,["actualBakedNondegenerateFaces"]=bakedVisible,
                ["allUnweightedBakedLocalVerticesExactlyZero"]=collapsed,
                ["acceptanceScope"]="Actual Native zero-influence islands render no triangles; no new weights are invented."};
        }
        finally{Object.DestroyImmediate(baked);}
    }
    // Returns null when no cleanup is applicable. Source bytes and all inputs
    // remain unchanged. Kept vertex-buffer bytes are copied without re-encoding.
    internal static Mesh CleanCollapsedUnweightedDerived(Mesh source,Vector3[] world,Vector3[] normals,
        Vector3[][] references,JObject nativeProof,out Vector3[] cleanedWorld,out Vector3[] cleanedNormals,
        out Vector3[][] cleanedReferences,out JObject proof)
    {
        cleanedWorld=world;cleanedNormals=normals;cleanedReferences=references;
        proof=new JObject{["applicable"]=false,["sourceUnchanged"]=true};
        if(source==null||world==null||normals==null||world.Length!=source.vertexCount||normals.Length!=world.Length)
            throw new ArgumentException("Collapsed-island mesh/world/normal count mismatch");
        if(references!=null&&references.Any(r=>r==null||r.Length!=world.Length))throw new ArgumentException("Collapsed-island reference count mismatch");
        var zero=CollapsedZeroInfluences(source,out var counts,out var weights);
        int removed=zero.Count(x=>x);if(removed==0)return null;
        if(source.blendShapeCount!=0)throw new InvalidOperationException("Cannot remove collapsed islands with authored blend shapes");
        if((bool?)nativeProof?["passed"]!=true||(bool?)nativeProof?["actualBakeMesh"]!=true||
            (bool?)nativeProof?["allUnweightedBakedLocalVerticesExactlyZero"]!=true||
            (int?)nativeProof?["unweightedVertices"]!=removed||(int?)nativeProof?["actualBakedNondegenerateFaces"]!=0||(int?)nativeProof?["mixedFaces"]!=0)
            throw new InvalidOperationException("Collapsed derived islands require matching actual Native BakeMesh evidence");
        var local=source.vertices;
        for(int i=0;i<zero.Length;i++)if(zero[i]&&(!CollapsedZeroExact(local[i])||!CollapsedZeroExact(world[i])||
            (references!=null&&references.Any(r=>!CollapsedZeroExact(r[i])))))
            throw new InvalidOperationException("Unweighted island retains a nonzero source/world/reference position");
        var kept=Enumerable.Range(0,zero.Length).Where(i=>!zero[i]).ToArray();
        if(kept.Length==0)throw new InvalidOperationException("Cleanup cannot remove the complete garment");
        bool Finite(Vector3 p)=>float.IsFinite(p.x)&&float.IsFinite(p.y)&&float.IsFinite(p.z);
        foreach(int i in kept)if(!Finite(local[i])||!Finite(world[i])||!Finite(normals[i])||
            (references!=null&&references.Any(r=>!Finite(r[i]))))throw new InvalidOperationException("Nonfinite retained topology or companion-reference input");
        var remap=Enumerable.Repeat(-1,zero.Length).ToArray();for(int i=0;i<kept.Length;i++)remap[kept[i]]=i;
        var submeshes=new int[source.subMeshCount][];int removedFaces=0;
        for(int sm=0;sm<submeshes.Length;sm++)
        {
            if(source.GetTopology(sm)!=MeshTopology.Triangles)throw new InvalidOperationException("Cleanup requires triangle submeshes");
            var triangles=source.GetTriangles(sm);var output=new List<int>();
            for(int at=0;at<triangles.Length;at+=3)
            {
                int a=triangles[at],b=triangles[at+1],c=triangles[at+2],n=(zero[a]?1:0)+(zero[b]?1:0)+(zero[c]?1:0);
                if(n==0){output.Add(remap[a]);output.Add(remap[b]);output.Add(remap[c]);continue;}
                if(n!=3||CollapsedZeroNonzeroArea(local[a],local[b],local[c])||CollapsedZeroNonzeroArea(world[a],world[b],world[c]))
                    throw new InvalidOperationException("Cannot remove a mixed or nondegenerate unweighted triangle");
                removedFaces++;
            }
            submeshes[sm]=output.ToArray();
        }
        if((int?)nativeProof["allUnweightedFaces"]!=removedFaces)throw new InvalidOperationException("Native/derived unweighted-island face coverage differs");
        var result=new Mesh{name=source.name+"_collapsed_islands_removed",indexFormat=source.indexFormat};
        Mesh.MeshDataArray writable=default;bool allocated=false,consumed=false;
        try
        {
            writable=Mesh.AllocateWritableMeshData(1);allocated=true;
            using(var read=Mesh.AcquireReadOnlyMeshData(source))
            {
                var from=read[0];var to=writable[0];to.SetVertexBufferParams(kept.Length,source.GetVertexAttributes());
                for(int stream=0;stream<source.vertexBufferCount;stream++)
                {
                    int stride=source.GetVertexBufferStride(stream);var input=from.GetVertexData<byte>(stream);var output=to.GetVertexData<byte>(stream);
                    for(int v=0;v<kept.Length;v++)NativeArray<byte>.Copy(input,kept[v]*stride,output,v*stride,stride);
                }
                int indexCount=submeshes.Sum(s=>s.Length);to.SetIndexBufferParams(indexCount,source.indexFormat);
                int cursor=0;
                if(source.indexFormat==IndexFormat.UInt32)
                {var output=to.GetIndexData<uint>();foreach(var sub in submeshes)foreach(int index in sub)output[cursor++]=(uint)index;}
                else
                {var output=to.GetIndexData<ushort>();foreach(var sub in submeshes)foreach(int index in sub)output[cursor++]=checked((ushort)index);}
                to.subMeshCount=submeshes.Length;cursor=0;
                for(int sm=0;sm<submeshes.Length;sm++)
                {
                    var indices=submeshes[sm];var bounds=new Bounds();
                    if(indices.Length>0)
                    {bounds=new Bounds(local[kept[indices[0]]],Vector3.zero);foreach(int index in indices)bounds.Encapsulate(local[kept[index]]);}
                    to.SetSubMesh(sm,new SubMeshDescriptor(cursor,indices.Length,MeshTopology.Triangles){baseVertex=0,firstVertex=0,vertexCount=kept.Length,bounds=bounds},MeshUpdateFlags.DontRecalculateBounds);
                    cursor+=indices.Length;
                }
            }
            Mesh.ApplyAndDisposeWritableMeshData(writable,result);consumed=true;
            result.bindposes=source.bindposes;
            var offsets=new int[counts.Length];int n=0;for(int i=0;i<counts.Length;i++){offsets[i]=n;n+=counts[i];}
            // Keep legacy four-weight float32 storage as legacy storage.
            // SetBoneWeights may requantize those floats to normalized 16-bit.
            // Higher-influence inputs still use their variable representation;
            // both paths must pass the same exact full-influence check below.
            var legacy=kept.All(i=>counts[i]<=4)?source.boneWeights:Array.Empty<BoneWeight>();
            bool legacyFour=legacy.Length==source.vertexCount;
            if(legacyFour)
                result.boneWeights=kept.Select(i=>legacy[i]).ToArray();
            else
            {
                using(var outCounts=new NativeArray<byte>(kept.Select(i=>counts[i]).ToArray(),Allocator.Temp))
                using(var outWeights=new NativeArray<BoneWeight1>(kept.SelectMany(i=>Enumerable.Range(offsets[i],counts[i]).Select(j=>weights[j])).ToArray(),Allocator.Temp))
                    result.SetBoneWeights(outCounts,outWeights);
            }
            result.RecalculateBounds();
            VerifyCollapsedKeptAttributes(source,result,kept,counts,weights,offsets);
            cleanedWorld=kept.Select(i=>world[i]).ToArray();cleanedNormals=kept.Select(i=>normals[i]).ToArray();
            cleanedReferences=references?.Select(r=>kept.Select(i=>r[i]).ToArray()).ToArray();
            proof=new JObject{["applicable"]=true,["passed"]=true,["sourceUnchanged"]=true,["removedVertices"]=removed,["removedTriangles"]=removedFaces,
                ["removedOriginalVertexIndices"]=new JArray(Enumerable.Range(0,zero.Length).Where(i=>zero[i])),["retainedOriginalVertexIndices"]=new JArray(kept),
                ["nativeActualEvidence"]=nativeProof.DeepClone(),["keptVertexBufferBytesCopied"]=true,["fullSkinInfluencesPreserved"]=true,
                ["skinWriteMode"]=legacyFour?"legacy-four-influence-preserved":"variable-influence-preserved",["keptNonSkinAttributeBytesVerifiedAfterSkinWrite"]=true,["fullSkinInfluencesVerifiedAfterWrite"]=true,["submeshBoundsRecomputed"]=true,
                ["submeshSlotsAndRetainedTriangleOrderPreserved"]=true,["allCompanionReferencesRemapped"]=true,
                ["acceptanceScope"]="Only exact-zero-area, exact-zero-position, exclusively unweighted derived islands; no visible source triangles removed."};
            return result;
        }
        catch{Object.DestroyImmediate(result);throw;}
        finally{if(allocated&&!consumed)writable.Dispose();}
    }
    static bool[] CollapsedZeroInfluences(Mesh mesh,out byte[] counts,out BoneWeight1[] weights)
    {
        counts=mesh.GetBonesPerVertex().ToArray();weights=mesh.GetAllBoneWeights().ToArray();
        if(counts.Length!=mesh.vertexCount)throw new InvalidOperationException("Missing explicit source skin influences");
        var zero=new bool[counts.Length];int cursor=0;
        for(int i=0;i<counts.Length;i++)
        {
            double sum=0;for(int k=0;k<counts[i];k++)
            {float weight=weights[cursor++].weight;if(!float.IsFinite(weight)||weight<0)throw new InvalidOperationException("Malformed source skin influence");sum+=weight;}
            zero[i]=sum==0;
        }
        if(cursor!=weights.Length)throw new InvalidOperationException("Source skin influence count differs");return zero;
    }
    static void VerifyCollapsedKeptAttributes(Mesh source,Mesh result,int[] kept,byte[] counts,BoneWeight1[] weights,int[] offsets)
    {
        // Unity's legacy and variable-influence APIs store skin channels in
        // different vertex-buffer layouts. The full influence arrays below
        // remain authoritative; every NON-skin channel must still match.
        bool NonSkin(VertexAttribute attribute)=>attribute!=VertexAttribute.BlendWeight&&attribute!=VertexAttribute.BlendIndices;
        var originalAttributes=source.GetVertexAttributes().Select(d=>d.attribute).Where(NonSkin).OrderBy(a=>a).ToArray();
        var actualAttributes=result.GetVertexAttributes().Select(d=>d.attribute).Where(NonSkin).OrderBy(a=>a).ToArray();
        if(!originalAttributes.SequenceEqual(actualAttributes))throw new InvalidOperationException("Cleanup changed non-skin attribute coverage: source="+string.Join(",",originalAttributes)+" result="+string.Join(",",actualAttributes));
        int Bytes(VertexAttributeFormat format)
        {
            switch(format)
            {
                case VertexAttributeFormat.Float32:case VertexAttributeFormat.UInt32:case VertexAttributeFormat.SInt32:return 4;
                case VertexAttributeFormat.Float16:case VertexAttributeFormat.UNorm16:case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16:case VertexAttributeFormat.SInt16:return 2;
                case VertexAttributeFormat.UNorm8:case VertexAttributeFormat.SNorm8:case VertexAttributeFormat.UInt8:case VertexAttributeFormat.SInt8:return 1;
                default:throw new InvalidOperationException("Unsupported vertex attribute byte format "+format);
            }
        }
        using(var a=Mesh.AcquireReadOnlyMeshData(source))using(var b=Mesh.AcquireReadOnlyMeshData(result))
        foreach(var descriptor in source.GetVertexAttributes())
        {
            var attribute=descriptor.attribute;
            if(attribute==VertexAttribute.BlendWeight||attribute==VertexAttribute.BlendIndices)continue; // Full influence API below is authoritative.
            if(!result.HasVertexAttribute(attribute)||result.GetVertexAttributeFormat(attribute)!=descriptor.format||result.GetVertexAttributeDimension(attribute)!=descriptor.dimension)
                throw new InvalidOperationException("Cleanup changed attribute format/dimension "+attribute);
            int sourceStream=source.GetVertexAttributeStream(attribute),resultStream=result.GetVertexAttributeStream(attribute);
            int sourceStride=source.GetVertexBufferStride(sourceStream),resultStride=result.GetVertexBufferStride(resultStream);
            int sourceOffset=source.GetVertexAttributeOffset(attribute),resultOffset=result.GetVertexAttributeOffset(attribute),bytes=Bytes(descriptor.format)*descriptor.dimension;
            var av=a[0].GetVertexData<byte>(sourceStream);var bv=b[0].GetVertexData<byte>(resultStream);
            for(int i=0;i<kept.Length;i++)for(int k=0;k<bytes;k++)
                if(av[kept[i]*sourceStride+sourceOffset+k]!=bv[i*resultStride+resultOffset+k])throw new InvalidOperationException("Cleanup changed kept attribute bytes "+attribute);
        }
        var actualCounts=result.GetBonesPerVertex().ToArray();var actualWeights=result.GetAllBoneWeights().ToArray();
        if(!actualCounts.SequenceEqual(kept.Select(i=>counts[i])))throw new InvalidOperationException("Cleanup changed full skin influence counts");
        var expected=kept.SelectMany(i=>Enumerable.Range(offsets[i],counts[i]).Select(j=>weights[j])).ToArray();
        if(actualWeights.Length!=expected.Length)throw new InvalidOperationException("Cleanup changed full skin influence length");
        for(int i=0;i<expected.Length;i++)if(actualWeights[i].boneIndex!=expected[i].boneIndex||BitConverter.SingleToInt32Bits(actualWeights[i].weight)!=BitConverter.SingleToInt32Bits(expected[i].weight))
            throw new InvalidOperationException("Cleanup changed full skin influence bytes at "+i+": bone "+expected[i].boneIndex+" -> "+actualWeights[i].boneIndex+", weight bits "+BitConverter.SingleToInt32Bits(expected[i].weight)+" -> "+BitConverter.SingleToInt32Bits(actualWeights[i].weight));
    }
    static bool CollapsedZeroExact(Vector3 p)=>p.x==0&&p.y==0&&p.z==0;
    static bool CollapsedZeroNonzeroArea(Vector3 a,Vector3 b,Vector3 c)
    {
        double x=(double)b.x-a.x,y=(double)b.y-a.y,z=(double)b.z-a.z,u=(double)c.x-a.x,v=(double)c.y-a.y,w=(double)c.z-a.z;
        return y*w-z*v!=0||z*u-x*w!=0||x*v-y*u!=0;
    }
}
}
#endif
