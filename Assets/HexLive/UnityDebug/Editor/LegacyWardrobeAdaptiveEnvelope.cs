#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;
namespace HexLive.UnityDebug.Editor
{
public static partial class LegacyWebGLWardrobePreparation
{
    // Only a rejected private seed, not malformed skin/reference input or IO.
    // The caller may try another independently generated seed with full gates.
    sealed class EnvelopeCandidateDegenerateFaceException : InvalidOperationException
    {
        public readonly JObject Evidence;
        public EnvelopeCandidateDegenerateFaceException(JObject evidence)
            :base("Cannot refine a zero-area failed face: "+evidence.ToString(Newtonsoft.Json.Formatting.None)){Evidence=evidence;}
    }
    readonly struct EnvelopeEdge : IEquatable<EnvelopeEdge>
    {
        public readonly int A,B;
        public EnvelopeEdge(int a,int b){A=Math.Min(a,b);B=Math.Max(a,b);}
        public bool Equals(EnvelopeEdge other)=>A==other.A&&B==other.B;
        public override bool Equals(object obj)=>obj is EnvelopeEdge other&&Equals(other);
        public override int GetHashCode()=>unchecked(A*397^B);
    }

    sealed class EnvelopeTopology
    {
        public readonly List<Vector3> Local,World,WorldNormals,LocalNormals;
        public readonly List<Vector3>[] References;
        public readonly List<Vector4> Tangents;
        public readonly List<Color> Colors;
        public readonly List<Vector4>[] UV=new List<Vector4>[8];
        public readonly int[] UVDimensions=new int[8];
        public readonly List<SortedDictionary<int,float>> Weights;
        public readonly Matrix4x4[] Bindposes;
        public readonly BoneWeight[] OriginalLegacyWeights;
        public readonly IndexFormat InputIndexFormat;
        public readonly bool Colors32;
        public readonly int OriginalVertices;
        public readonly string Name;
        public List<int>[] Submeshes;
        public int InputBlendShapes;
        public EnvelopeTopology(Mesh source,Vector3[] seed,Vector3[] normals,Vector3[][] companionReferences=null)
        {
            if(source==null||seed==null||normals==null||seed.Length!=source.vertexCount||normals.Length!=seed.Length||seed.Length==0)
                throw new ArgumentException("Adaptive topology needs matching source/world/normal vertices");
            Name=source.name;OriginalVertices=source.vertexCount;InputBlendShapes=source.blendShapeCount;
            if(InputBlendShapes!=0)throw new InvalidOperationException("Adaptive prepared garments must have no authored blend shapes");
            Local=source.vertices.ToList();World=seed.ToList();WorldNormals=normals.ToList();
            References=(companionReferences??Array.Empty<Vector3[]>()).Select(reference=>
            {
                if(reference==null||reference.Length!=source.vertexCount)throw new ArgumentException("Companion reference vertex count differs from source");
                return reference.ToList();
            }).ToArray();
            if(source.HasVertexAttribute(VertexAttribute.Normal))LocalNormals=source.normals.ToList();
            if(source.HasVertexAttribute(VertexAttribute.Tangent))Tangents=source.tangents.ToList();
            if(source.HasVertexAttribute(VertexAttribute.Color)){Colors=source.colors.ToList();Colors32=source.GetVertexAttributeFormat(VertexAttribute.Color)==VertexAttributeFormat.UNorm8;}
            for(int ch=0;ch<8;ch++)if(source.HasVertexAttribute((VertexAttribute)((int)VertexAttribute.TexCoord0+ch)))
            {
                UVDimensions[ch]=source.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0+ch));
                UV[ch]=new List<Vector4>();source.GetUVs(ch,UV[ch]);
                if(UV[ch].Count!=source.vertexCount||UVDimensions[ch]<1||UVDimensions[ch]>4)throw new InvalidOperationException("Malformed UV channel "+ch);
            }
            Bindposes=source.bindposes;OriginalLegacyWeights=source.boneWeights;InputIndexFormat=source.indexFormat;Submeshes=new List<int>[source.subMeshCount];
            for(int i=0;i<Submeshes.Length;i++)
            {if(source.GetTopology(i)!=MeshTopology.Triangles)throw new InvalidOperationException("Adaptive garment requires triangle submeshes");Submeshes[i]=source.GetTriangles(i).ToList();}
            if(source.HasVertexAttribute(VertexAttribute.BlendWeight))
            {
                var counts=source.GetBonesPerVertex();var all=source.GetAllBoneWeights();int cursor=0;Weights=new List<SortedDictionary<int,float>>();
                if(counts.Length!=source.vertexCount)throw new InvalidOperationException("Missing skin influences");
                for(int v=0;v<counts.Length;v++)
                {
                    var dictionary=new SortedDictionary<int,float>();
                    for(int k=0;k<counts[v];k++){var w=all[cursor++];if(w.weight>0)dictionary[w.boneIndex]=dictionary.TryGetValue(w.boneIndex,out float old)?old+w.weight:w.weight;}
                    if(dictionary.Count==0)throw new InvalidOperationException("Unweighted skinned vertex");Weights.Add(dictionary);
                }
            }
            foreach(var p in Local.Concat(World).Concat(WorldNormals).Concat(References.SelectMany(r=>r)))
                if(!float.IsFinite(p.x)||!float.IsFinite(p.y)||!float.IsFinite(p.z))throw new InvalidOperationException("Nonfinite topology or companion-reference input");
        }
        public int TriangleCount=>Submeshes.Sum(s=>s.Count/3);
        public int[] Triangles=>Submeshes.SelectMany(s=>s).ToArray();

        // Exact source bind-position + canonical full influences. Attribute
        // vertices remain distinct; this identifies only common movement DOFs.
        public int[] MovementGroups()
        {
            var byPosition=new Dictionary<Vector3,List<int>>();var groups=new int[Local.Count];int next=0;
            bool SameWeights(int a,int b)
            {return Weights==null||Weights[a].Count==Weights[b].Count&&Weights[a].SequenceEqual(Weights[b]);}
            for(int i=0;i<Local.Count;i++)
            {
                if(!byPosition.TryGetValue(Local[i],out var candidates))byPosition.Add(Local[i],candidates=new List<int>());
                int representative=-1;foreach(int index in candidates)if(SameWeights(index,i)){representative=index;break;}
                if(representative<0){groups[i]=next++;candidates.Add(i);}else groups[i]=groups[representative];
            }
            return groups;
        }
        int Add(int[] indices,float[] blend)
        {
            int index=Local.Count;Vector3 local=default,world=default,normal=default,localNormal=default;Vector4 tangent=default;Color color=default;
            for(int j=0;j<indices.Length;j++)
            {int v=indices[j];float w=blend[j];local+=Local[v]*w;world+=World[v]*w;normal+=WorldNormals[v]*w;
             if(LocalNormals!=null)localNormal+=LocalNormals[v]*w;if(Tangents!=null)tangent+=Tangents[v]*w;if(Colors!=null)color+=Colors[v]*w;}
            Local.Add(local);World.Add(world);WorldNormals.Add(normal);
            foreach(var reference in References)
            {Vector3 point=default;for(int j=0;j<indices.Length;j++)point+=reference[indices[j]]*blend[j];reference.Add(point);}
            if(LocalNormals!=null)LocalNormals.Add(localNormal);if(Tangents!=null)Tangents.Add(tangent);if(Colors!=null)Colors.Add(color);
            for(int ch=0;ch<8;ch++)if(UV[ch]!=null){Vector4 uv=default;for(int j=0;j<indices.Length;j++)uv+=UV[ch][indices[j]]*blend[j];UV[ch].Add(uv);}
            if(Weights!=null)
            {
                var result=new SortedDictionary<int,float>();
                for(int j=0;j<indices.Length;j++)foreach(var pair in Weights[indices[j]])
                    result[pair.Key]=result.TryGetValue(pair.Key,out float old)?old+pair.Value*blend[j]:pair.Value*blend[j];
                Weights.Add(result);
            }
            return index;
        }
        public JObject Split(int[] markedFaces,int remainingTriangleBudget)
        {
            if(InputBlendShapes!=0)throw new InvalidOperationException("Adaptive topology cannot discard authored blend shapes");
            int[] groups=MovementGroups();var selected=new HashSet<EnvelopeEdge>();var marked=new HashSet<int>(markedFaces);
            if(marked.Any(i=>i<0||i>=TriangleCount))throw new ArgumentException("Invalid marked global face");
            int face=0;
            foreach(var sub in Submeshes)for(int at=0;at<sub.Count;at+=3,face++)if(marked.Contains(face))
            {selected.Add(new EnvelopeEdge(groups[sub[at]],groups[sub[at+1]]));selected.Add(new EnvelopeEdge(groups[sub[at+1]],groups[sub[at+2]]));selected.Add(new EnvelopeEdge(groups[sub[at+2]],groups[sub[at]]));}
            int additions=0,affected=0;var geometricSplitEdges=new HashSet<EnvelopeEdge>();
            int visitedFace=0;
            for(int sm=0;sm<Submeshes.Length;sm++)for(int at=0;at<Submeshes[sm].Count;at+=3,visitedFace++)
            {
                var sub=Submeshes[sm];
                int a=sub[at],b=sub[at+1],c=sub[at+2];var edges=new[]{new EnvelopeEdge(groups[a],groups[b]),new EnvelopeEdge(groups[b],groups[c]),new EnvelopeEdge(groups[c],groups[a])};
                int count=edges.Count(selected.Contains);if(count==0)continue;
                float squaredCross=Vector3.Cross(World[b]-World[a],World[c]-World[a]).sqrMagnitude;
                if(squaredCross<1e-20f)throw new EnvelopeCandidateDegenerateFaceException(new JObject{
                    ["mesh"]=Name,["submesh"]=sm,["globalFace"]=visitedFace,["directlyMarked"]=marked.Contains(visitedFace),
                    ["conformingNeighbourOnly"]=!marked.Contains(visitedFace),["indices"]=new JArray(a,b,c),
                    ["worldVertices"]=new JArray(Vec(World[a]),Vec(World[b]),Vec(World[c])),["squaredCross"]=squaredCross,["threshold"]=1e-20f});
                additions+=count+2;affected++;foreach(var edge in edges)if(selected.Contains(edge))geometricSplitEdges.Add(edge);
            }
            var audit=new JObject{["markedFaces"]=marked.Count,["affectedFacesIncludingConformingNeighbours"]=affected,["additionalTriangles"]=additions,
                ["remainingTriangleBudget"]=remainingTriangleBudget,["geometricSplitEdges"]=geometricSplitEdges.Count,["budgetPassed"]=additions<=remainingTriangleBudget};
            if(additions>remainingTriangleBudget)return audit;
            int beforeVertices=Local.Count;var midpointByIndexEdge=new Dictionary<EnvelopeEdge,int>();
            int Mid(int a,int b)
            {var key=new EnvelopeEdge(a,b);if(midpointByIndexEdge.TryGetValue(key,out int v))return v;v=Add(new[]{key.A,key.B},new[]{.5f,.5f});midpointByIndexEdge.Add(key,v);return v;}
            var replacement=new List<int>[Submeshes.Length];
            for(int sm=0;sm<Submeshes.Length;sm++)
            {
                replacement[sm]=new List<int>();var sub=Submeshes[sm];
                for(int at=0;at<sub.Count;at+=3)
                {
                    int a=sub[at],b=sub[at+1],c=sub[at+2];bool ab=selected.Contains(new EnvelopeEdge(groups[a],groups[b])),bc=selected.Contains(new EnvelopeEdge(groups[b],groups[c])),ca=selected.Contains(new EnvelopeEdge(groups[c],groups[a]));
                    if(!ab&&!bc&&!ca){replacement[sm].AddRange(new[]{a,b,c});continue;}
                    var ring=new List<int>{a};if(ab)ring.Add(Mid(a,b));ring.Add(b);if(bc)ring.Add(Mid(b,c));ring.Add(c);if(ca)ring.Add(Mid(c,a));
                    int center=Add(new[]{a,b,c},new[]{1f/3,1f/3,1f/3});
                    for(int k=0;k<ring.Count;k++)replacement[sm].AddRange(new[]{center,ring[k],ring[(k+1)%ring.Count]});
                }
            }
            Submeshes=replacement;audit["separateIndexEdgeMidpoints"]=midpointByIndexEdge.Count;audit["addedVertices"]=Local.Count-beforeVertices;
            audit["attributeIndicesWelded"]=false;audit["sourceSurfacePreservedBeforeFit"]=true;audit["windingPreserved"]=true;
            return audit;
        }
        public Vector3[] ExportWorldNormals()=>WorldNormals.Select(n=>n.normalized).ToArray();
        public Mesh Write(out JObject audit)
        {
            var mesh=new Mesh{name=Name+"_adaptive_private",indexFormat=Local.Count>65535?IndexFormat.UInt32:InputIndexFormat};
            try
            {
                mesh.SetVertices(Local);
                if(LocalNormals!=null)mesh.SetNormals(LocalNormals.Select((n,i)=>i<OriginalVertices?n:n.normalized).ToList());
                if(Tangents!=null)mesh.SetTangents(Tangents.Select((t,i)=>{if(i<OriginalVertices)return t;var n=new Vector3(t.x,t.y,t.z).normalized;return new Vector4(n.x,n.y,n.z,t.w<0?-1:1);}).ToList());
                if(Colors!=null){if(Colors32)mesh.SetColors(Colors.Select(c=>(Color32)c).ToList());else mesh.SetColors(Colors);}
                for(int ch=0;ch<8;ch++)if(UV[ch]!=null)
                {
                    if(UVDimensions[ch]==1){using var data=new NativeArray<float>(UV[ch].Select(p=>p.x).ToArray(),Allocator.Temp);mesh.SetUVs(ch,data);}
                    else if(UVDimensions[ch]==2)mesh.SetUVs(ch,UV[ch].Select(p=>new Vector2(p.x,p.y)).ToList());
                    else if(UVDimensions[ch]==3)mesh.SetUVs(ch,UV[ch].Select(p=>new Vector3(p.x,p.y,p.z)).ToList());
                    else mesh.SetUVs(ch,UV[ch]);
                }
                float maxDrop=0,totalDrop=0;int droppedVertices=0;
                if(Weights!=null)
                {
                    var output=new BoneWeight[Weights.Count];
                    for(int i=0;i<Weights.Count;i++)
                    {
                        var sorted=Weights[i].Where(p=>p.Value>0).OrderByDescending(p=>p.Value).ThenBy(p=>p.Key).ToArray();
                        float all=sorted.Sum(p=>p.Value),kept=sorted.Take(4).Sum(p=>p.Value);if(!(kept>0))throw new InvalidOperationException("Lost all skin influences");
                        float drop=Mathf.Max(0,1-kept/all);maxDrop=Mathf.Max(maxDrop,drop);totalDrop+=drop;if(drop>1e-7f)droppedVertices++;
                        var w=new BoneWeight();
                        if(sorted.Length>0){w.boneIndex0=sorted[0].Key;w.weight0=sorted[0].Value/kept;}
                        if(sorted.Length>1){w.boneIndex1=sorted[1].Key;w.weight1=sorted[1].Value/kept;}
                        if(sorted.Length>2){w.boneIndex2=sorted[2].Key;w.weight2=sorted[2].Value/kept;}
                        if(sorted.Length>3){w.boneIndex3=sorted[3].Key;w.weight3=sorted[3].Value/kept;}
                        output[i]=i<OriginalVertices&&OriginalLegacyWeights.Length==OriginalVertices&&Weights[i].Count<=4?OriginalLegacyWeights[i]:w;
                    }
                    mesh.boneWeights=output;
                }
                mesh.bindposes=Bindposes;mesh.subMeshCount=Submeshes.Length;
                for(int i=0;i<Submeshes.Length;i++)mesh.SetTriangles(Submeshes[i],i,false);mesh.RecalculateBounds();
                audit=new JObject{["uvDimensions"]=new JArray(UVDimensions),["submeshesPreserved"]=Submeshes.Length,
                    ["normalChannelPreserved"]=LocalNormals!=null,["tangentChannelPreserved"]=Tangents!=null,["colorChannelPreserved"]=Colors!=null,
                    ["bindposesPreserved"]=true,["normalizedTop4BoneWeights"]=Weights!=null,["maximumDroppedWeightFraction"]=maxDrop,
                    ["maximumAllowedDroppedWeightFraction"]=.05f,["boneWeightDropPassed"]=maxDrop<=.05f,
                    ["totalDroppedWeightFraction"]=totalDrop,["verticesWithDroppedWeights"]=droppedVertices,
                    ["inputIndexFormat"]=InputIndexFormat.ToString(),["outputIndexFormat"]=mesh.indexFormat.ToString()};
                return mesh;
            }
            catch{Object.DestroyImmediate(mesh);throw;}
        }
    }

    internal static Mesh SubdivideEnvelopeFaces(Mesh source,Vector3[] seedWorld,Vector3[] normals,int[] markedFaces,
        out Vector3[] newSeed,out Vector3[] newNormals,out JObject audit)
        =>SubdivideEnvelopeFacesWithReferences(source,seedWorld,normals,markedFaces,null,out newSeed,out newNormals,out audit,out _);

    internal static Mesh SubdivideEnvelopeFacesWithReferences(Mesh source,Vector3[] seedWorld,Vector3[] normals,int[] markedFaces,
        Vector3[][] companionReferences,out Vector3[] newSeed,out Vector3[] newNormals,out JObject audit,out Vector3[][] interpolatedReferences)
    {
        var data=new EnvelopeTopology(source,seedWorld,normals,companionReferences);int before=data.TriangleCount;
        var split=data.Split(markedFaces,int.MaxValue);var result=data.Write(out var attributes);
        newSeed=data.World.ToArray();newNormals=data.ExportWorldNormals();
        interpolatedReferences=data.References.Select(r=>r.ToArray()).ToArray();
        audit=new JObject{["inputTriangles"]=before,["outputTriangles"]=data.TriangleCount,["inputVertices"]=source.vertexCount,
            ["outputVertices"]=data.Local.Count,["split"]=split,["attributes"]=attributes,["sourceUnchanged"]=true,
            ["passed"]=(bool)attributes["boneWeightDropPassed"]};return result;
    }

    static int[] EnvelopeViolatingFaces(Vector3[] points,int[] triangles,WardrobeBodyVolume body,float minimum)
    {
        bool Fails(Vector3 p){var q=body.Query(p);return q.Ambiguous||q.OnSurface||q.Inside||q.SignedDistance<minimum;}
        var badVertices=points.Select(Fails).ToArray();var marked=new List<int>();
        for(int at=0;at<triangles.Length;at+=3)
        {
            int a=triangles[at],b=triangles[at+1],c=triangles[at+2];bool bad=badVertices[a]||badVertices[b]||badVertices[c];
            if(!bad)foreach(var w in EnvelopeProbeBarycentrics)
                if(Fails(points[a]*w.x+points[b]*w.y+points[c]*w.z)){bad=true;break;}
            if(bad)marked.Add(at/3);
        }
        return marked.ToArray();
    }

    internal static Mesh RefineEnvelopeMesh(Mesh source,Vector3[] seedWorld,Vector3[] normals,TriangleTree body,
        float desiredClearance,float minimumAcceptedClearance,int passes,float maxDisplacement,
        out Vector3[] fittedWorld,out Vector3[] newNormals,out JObject proof)
        =>RefineEnvelopeMeshWithReferences(source,seedWorld,normals,body,desiredClearance,minimumAcceptedClearance,passes,maxDisplacement,
            null,out fittedWorld,out newNormals,out proof,out _);

    internal static Mesh RefineEnvelopeMeshWithReferences(Mesh source,Vector3[] seedWorld,Vector3[] normals,TriangleTree body,
        float desiredClearance,float minimumAcceptedClearance,int passes,float maxDisplacement,Vector3[][] companionReferences,
        out Vector3[] fittedWorld,out Vector3[] newNormals,out JObject proof,out Vector3[][] interpolatedReferences)
    {
        var data=new EnvelopeTopology(source,seedWorld,normals,companionReferences);int originalTriangles=data.TriangleCount;
        int additionalBudget=Math.Max((int)Math.Ceiling(originalTriangles*.10),2048),refinements=0;
        var volume=WardrobeBodyVolume.For(body);var attempts=new JArray();var splits=new JArray();
        Mesh result=null;JObject attributes=null,surface=null;string reason="refinement-cap";
        fittedWorld=null;newNormals=null;
        try
        {
            for(int round=0;round<=3;round++)
            {
                result=data.Write(out attributes);var triangles=data.Triangles;
                if(!(bool)attributes["boneWeightDropPassed"])
                {reason="bone-weight-drop-cap";fittedWorld=data.World.ToArray();newNormals=data.ExportWorldNormals();break;}
                int[] groups=data.MovementGroups();
                // Every retry starts from the original, linearly interpolated
                // UNFITTED surface. Failed displaced output is never the next seed.
                fittedWorld=RefineEnvelope(data.World.ToArray(),triangles,volume,desiredClearance,minimumAcceptedClearance,
                    passes,maxDisplacement,out surface,groups);
                newNormals=data.ExportWorldNormals();
                surface["solverTopologyUnchangedWithinRound"]=true;
                surface["topologyUnchanged"]=data.Local.Count==source.vertexCount&&data.TriangleCount==originalTriangles;
                surface["inputVertices"]=source.vertexCount;surface["outputVertices"]=data.Local.Count;
                surface["inputTriangles"]=originalTriangles;surface["outputTriangles"]=data.TriangleCount;
                attempts.Add(new JObject{["round"]=round,["vertices"]=data.Local.Count,["triangles"]=data.TriangleCount,
                    ["movementGroups"]=groups.Distinct().Count(),["surfaceEnvelope"]=surface.DeepClone()});
                if((bool)surface["passed"]){reason="physical-floor-passed";break;}
                if(round==3)break;
                int[] marked=EnvelopeViolatingFaces(fittedWorld,triangles,volume,minimumAcceptedClearance);
                if(marked.Length==0){reason="failed-proof-without-a-refinable-face";break;}
                JObject split;
                try{split=data.Split(marked,additionalBudget-(data.TriangleCount-originalTriangles));}
                catch(EnvelopeCandidateDegenerateFaceException error)
                {error.Evidence["refinementRound"]=round;error.Evidence["lastSurfaceEnvelope"]=surface.DeepClone();throw;}
                splits.Add(split);
                if(!(bool)split["budgetPassed"]){reason="additional-triangle-budget";break;}
                refinements++;Object.DestroyImmediate(result);result=null;
            }
            bool passed=surface!=null&&(bool)surface["passed"]&&(bool)attributes["boneWeightDropPassed"]&&data.TriangleCount-originalTriangles<=additionalBudget;
            proof=new JObject{["passed"]=passed,["stoppedReason"]=reason,["inputTriangles"]=originalTriangles,["outputTriangles"]=data.TriangleCount,
                ["inputVertices"]=source.vertexCount,["outputVertices"]=data.Local.Count,["addedVertices"]=data.Local.Count-source.vertexCount,
                ["additionalTriangleBudget"]=additionalBudget,["additionalTriangleBudgetPolicy"]="MAX(ceil(inputTriangles*0.10),2048)",
                ["maximumRefinementRounds"]=3,["refinementRounds"]=refinements,["attempts"]=attempts,["splits"]=splits,
                ["splitEdges"]=splits.Where(s=>(bool)s["budgetPassed"]).Sum(s=>(int)s["geometricSplitEdges"]),
                ["attributes"]=attributes,["surfaceEnvelope"]=surface,["sourceUnchanged"]=true,["topologyUnchanged"]=refinements==0,
                ["retrySeedPolicy"]="original interpolated surface, never previous failed deformation",
                ["movementGroupPolicy"]="exact source bind-position + canonical influences; separate UV/normal/attribute indices",
                ["companionReferenceCount"]=data.References.Length,["companionReferenceInputVertices"]=source.vertexCount,
                ["companionReferenceOutputVertices"]=data.Local.Count,["companionReferencePolicy"]="copied immutable inputs; identical original-surface split weights across all refinement rounds",
                ["allSourceSubmeshesPreserved"]=true,["allUVChannelsPreserved"]=true};
            interpolatedReferences=data.References.Select(r=>r.ToArray()).ToArray();
            return result;
        }
        catch{if(result!=null)Object.DestroyImmediate(result);throw;}
    }

    // No models, remote content, runtime or persistent assets. Independent CPU
    // geometry controls can invoke this before the real saved-boot fixture.
    public static void AuditAdaptiveEnvelopeTopologyControls()
    {
        var source=new Mesh();Mesh divided=null;var report=new JObject{["passed"]=false,["assetsChanged"]=false};
        Directory.CreateDirectory(OutputRoot);string output=OutputRoot+"/adaptive-envelope-topology-controls.json";
        try
        {
            source.vertices=new[]{new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(0,1,0),new Vector3(1,1,0)};
            source.normals=Enumerable.Repeat(Vector3.forward,4).ToArray();source.uv=new[]{Vector2.zero,Vector2.right,Vector2.up,Vector2.one};
            source.subMeshCount=2;source.SetTriangles(new[]{0,1,2},0);source.SetTriangles(new[]{1,3,2},1);
            var originalVertices=source.vertices;var originalTriangles=source.triangles;
            divided=SubdivideEnvelopeFaces(source,originalVertices,source.normals,new[]{0},out var newSeed,out _,out var audit);report["subdivision"]=audit;
            if(!source.vertices.SequenceEqual(originalVertices)||!source.triangles.SequenceEqual(originalTriangles))throw new InvalidOperationException("Subdivision mutated its source");
            if(divided.subMeshCount!=2||divided.vertexCount!=9||divided.GetTriangles(0).Length!=18||divided.GetTriangles(1).Length!=12)
                throw new InvalidOperationException("Conforming shared-edge midpoint/centroid counts are wrong");
            float area=0;var vertices=divided.vertices;var triangles=divided.triangles;
            for(int i=0;i<triangles.Length;i+=3)
            {
                var cross=Vector3.Cross(vertices[triangles[i+1]]-vertices[triangles[i]],vertices[triangles[i+2]]-vertices[triangles[i]]);
                if(!(cross.z>0))throw new InvalidOperationException("Subdivision reversed winding or created a degenerate triangle");area+=cross.magnitude*.5f;
            }
            if(Mathf.Abs(area-1)>1e-6f||newSeed.Any(p=>p.z!=0))throw new InvalidOperationException("Subdivision changed the original surface");
            report["outputArea"]=area;report["passed"]=true;
        }
        catch(Exception e){report["error"]=e.ToString();throw;}
        finally{if(divided!=null)Object.DestroyImmediate(divided);Object.DestroyImmediate(source);File.WriteAllText(output,report.ToString());}
        Debug.Log("ADAPTIVE_ENVELOPE_TOPOLOGY_CONTROLS_PASS "+output);
    }

    public static void AuditAdaptiveEnvelopeReferenceControls()
    {
        var mesh=new Mesh();Mesh divided=null;var report=new JObject{["passed"]=false,["assetsChanged"]=false};
        Directory.CreateDirectory(OutputRoot);string output=OutputRoot+"/adaptive-envelope-reference-controls.json";
        try
        {
            mesh.vertices=new[]{Vector3.zero,Vector3.right,Vector3.up};mesh.triangles=new[]{0,1,2};mesh.normals=Enumerable.Repeat(Vector3.forward,3).ToArray();
            var common=mesh.vertices;var own=common.Select(p=>p+Vector3.forward*.1f).ToArray();var ownBefore=(Vector3[])own.Clone();var commonBefore=(Vector3[])common.Clone();
            divided=SubdivideEnvelopeFacesWithReferences(mesh,own,mesh.normals,new[]{0},new[]{own,common},out var seed,out _,out var audit,out var references);
            if(references.Length!=2||references.Any(r=>r.Length!=divided.vertexCount))throw new InvalidOperationException("Companion references do not cover every output vertex");
            if(!own.SequenceEqual(ownBefore)||!common.SequenceEqual(commonBefore))throw new InvalidOperationException("Companion inputs were mutated");
            var local=divided.vertices;
            for(int i=0;i<local.Length;i++)
                if(Vector3.Distance(references[0][i],local[i]+Vector3.forward*.1f)>1e-6f||Vector3.Distance(references[1][i],local[i])>1e-6f)
                    throw new InvalidOperationException("Midpoint/centroid reference used different interpolation weights");
            var adversarial=(Vector3[])seed.Clone();
            for(int i=0;i<adversarial.Length;i++)adversarial[i]+=Vector3.forward*(i<mesh.vertexCount?-.1f:.1f);
            var firstN=RetargetProbeError(common,adversarial.Take(mesh.vertexCount).ToArray());
            var all=RetargetProbeError(references[1],adversarial);
            if((float)firstN["maxMetres"]>.000001f||(float)all["maxMetres"]<.199f||(float)all["maxMetres"]<=.12f)
                throw new InvalidOperationException("The new midpoint/centroid counterexample did not expose the old first-N bound gap");
            report["subdivision"]=audit;report["oldFirstNIncorrectlyPasses"]=firstN;report["allOutputVerticesCorrectlyFail12cm"]=all;report["passed"]=true;
        }
        catch(Exception e){report["error"]=e.ToString();throw;}
        finally{if(divided!=null)Object.DestroyImmediate(divided);Object.DestroyImmediate(mesh);File.WriteAllText(output,report.ToString());}
        Debug.Log("ADAPTIVE_ENVELOPE_REFERENCE_CONTROLS_PASS "+output);
    }
}
}
#endif
