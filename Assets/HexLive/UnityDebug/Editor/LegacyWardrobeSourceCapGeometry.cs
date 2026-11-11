#if UNITY_EDITOR
using System;using System.Collections.Generic;using System.Linq;using Newtonsoft.Json.Linq;using UnityEngine;
namespace HexLive.UnityDebug.Editor { public static partial class LegacyWebGLWardrobePreparation {
// Geometry-only, caller-evidenced source attachment fans. No source art writes,
// attribute dropping or final acceptance override. Caller validates immutable SOURCE evidence.
internal sealed class SourceCapGeometryRejectedException : InvalidOperationException { public SourceCapGeometryRejectedException(string reason):base(reason){} }
internal sealed class SourceCapAnnulusResult {
 public Vector3[] World,OwnReference,BoneReference;
 public int[][] Submeshes;
 public SourceCapAnnulusLineage[] Lineage;
 public int[] OriginalFaceOfOutput;
 public JObject Evidence;
}
internal sealed class SourceCapAnnulusLineage {
 public int OriginalIndex;
 public int[] Indices;
 public Vector3 Barycentric;
}
sealed class SourceCapVertex {public int c,a,b;public float wc,wa,wb;public Vector3 p;public int original=-1;}
internal static SourceCapAnnulusResult BuildEvidencedCapAnnulus(Vector3[] seed,Vector3[] ownReference,Vector3[] boneReference,int[][] submeshes,int[] evidencedCenterIndices,WardrobeBodyVolume volume,float contourMetres,int additionalTriangleBudget,string contourChoiceProvenance) {
 void Require(bool value,string reason){if(!value)throw new SourceCapGeometryRejectedException(reason);}
 Vector3 Cross(Vector3 a,Vector3 b)=>new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
 float Length(Vector3 p)=>(float)Math.Sqrt(p.sqrMagnitude);
 float Distance(Vector3 a,Vector3 b)=>Length(a-b);
 Require(seed!=null&&ownReference!=null&&boneReference!=null&&submeshes!=null&&evidencedCenterIndices!=null&&volume!=null,"Missing input");
 Require(seed.Length==ownReference.Length&&seed.Length==boneReference.Length&&seed.Length>0,"Reference topology mismatch");
 Require(contourMetres>.002f&&contourMetres<=.006f&&!string.IsNullOrWhiteSpace(contourChoiceProvenance),"Explicit working contour provenance required; physical2mm floor unchanged");
 Require(submeshes.All(x=>x!=null),"Missing submesh");
 var old=seed;var bone=boneReference;var t=submeshes.SelectMany(x=>x).ToArray();
 Require(submeshes.All(x=>x!=null&&x.Length%3==0)&&t.All(i=>i>=0&&i<seed.Length),"Invalid triangle topology");
 int canonicalBudget=Math.Max((int)Math.Ceiling(t.Length/3*.10),2048);Require(additionalTriangleBudget>=0&&additionalTriangleBudget<=canonicalBudget,"May not expand existing10percent/2048 budget");
 var deep=new HashSet<int>(evidencedCenterIndices);Require(deep.Count>0&&deep.All(i=>i>=0&&i<seed.Length),"Invalid evidenced fan centers");
 var capFaces=Enumerable.Range(0,t.Length/3).Where(f=>deep.Contains(t[f*3])||deep.Contains(t[f*3+1])||deep.Contains(t[f*3+2])).ToArray();
 Require(capFaces.Length>0,"No selected fan faces");
 foreach(int i in deep){var q=volume.Query(seed[i]);Require(q.Inside&&!q.Ambiguous&&!q.OnSurface,"Selected center must be proven inside");}
  string PositionKey(Vector3 p)=>BitConverter.SingleToInt32Bits(p.x)+","+BitConverter.SingleToInt32Bits(p.y)+","+BitConverter.SingleToInt32Bits(p.z);
  var topologyGuards=new JArray();
  foreach(var group in deep.GroupBy(i=>PositionKey(seed[i]))) {
   var centerGroup=new HashSet<int>(group);var faces=capFaces.Where(f=>centerGroup.Contains(t[f*3])||centerGroup.Contains(t[f*3+1])||centerGroup.Contains(t[f*3+2])).ToArray();
   Require(faces.Length>=3,"Fan needs at least3 faces");var edges=new Dictionary<string,List<string>>();var crosses=new List<Vector3>();var sum=Vector3.zero;var parts=new HashSet<int>();
   foreach(int f in faces){var ids=t.Skip(f*3).Take(3).ToArray();Require(ids.Count(centerGroup.Contains)==1&&ids.Count(deep.Contains)==1,"Mixed/multi-center fan face");var ring=ids.Where(i=>!centerGroup.Contains(i)).ToArray();string a=PositionKey(seed[ring[0]]),b=PositionKey(seed[ring[1]]);Require(a!=b&&a!=group.Key&&b!=group.Key,"Collapsed fan boundary");foreach(int i in ring){var q=volume.Query(seed[i]);Require(!q.Inside&&!q.Ambiguous&&!q.OnSurface&&q.SignedDistance>=.002f,"Fan outer ring not strictly outside2mm");}
    foreach(var edge in new[]{(a,b),(b,a)}){if(!edges.TryGetValue(edge.Item1,out var list))edges.Add(edge.Item1,list=new List<string>());list.Add(edge.Item2);}var cross=Cross(seed[ids[1]]-seed[ids[0]],seed[ids[2]]-seed[ids[0]]);Require(cross.sqrMagnitude>0,"Zero-area fan face");sum+=cross;crosses.Add(cross);
    int offset=0;for(int part=0;part<submeshes.Length;part++){if(f>=offset&&f<offset+submeshes[part].Length/3){parts.Add(part);break;}offset+=submeshes[part].Length/3;}
   }
   Require(parts.Count==1,"One evidenced fan may not silently span material owners");Require(edges.Count>=3&&edges.Values.All(x=>x.Count==2),"Fan boundary must have degree2");
   var seen=new HashSet<string>();var todo=new Stack<string>();todo.Push(edges.Keys.First());while(todo.Count>0){string id=todo.Pop();if(!seen.Add(id))continue;foreach(string other in edges[id])todo.Push(other);}Require(seen.Count==edges.Count,"Fan boundary must be one connected closed ring");
   Require(sum.sqrMagnitude>0&&crosses.All(cross=>Vector3.Dot(cross,sum)>0),"Folded/incoherent fan face winding");
   topologyGuards.Add(new JObject{["centerAttributeCopies"]=centerGroup.Count,["faces"]=faces.Length,["ringPositions"]=edges.Count,["oneClosedRing"]=true,["coherentWinding"]=true,["singleMaterialSlot"]=parts.Single()});
  }
  SourceCapVertex Make(int c,int a,int b,float wc,float wa,float wb)=>new(){c=c,a=a,b=b,wc=wc,wa=wa,wb=wb,p=old[c]*wc+old[a]*wa+old[b]*wb};
  SourceCapVertex Cut(int c,int a,int b,float theta){
   var edge=old[a]*(1-theta)+old[b]*theta;var hi=1f;var lo=0f;float prior=0;bool found=false;
   for(int j=1;j<=32;j++){float u=j/32f;var q=volume.Query(old[c]*(1-u)+edge*u);Require(!q.Ambiguous&&!q.OnSurface,"Unknown cut-ray sign");if(q.SignedDistance>=contourMetres){lo=prior;hi=u;found=true;break;}prior=u;}Require(found,"No working-contour cut bracket c="+c+" a="+a+" b="+b+" theta="+theta);
   for(int k=0;k<24;k++){float mid=(lo+hi)*.5f;if(mid==lo||mid==hi)break;var q=volume.Query(old[c]*(1-mid)+edge*mid);Require(!q.Ambiguous&&!q.OnSurface,"Unknown cut-bisection sign");if(q.SignedDistance>=contourMetres)hi=mid;else lo=mid;}
   // Evaluate EXACT final barycentric arithmetic used in the candidate. Native
   // cut-ray interpolation and barycentric summation can differ by an ULP.
   var v=Make(c,a,b,1-hi,hi*(1-theta),hi*theta);var check=volume.Query(v.p);
   // Bounded move toward known-safe original rim, preserving the source face.
   for(int k=0;k<8&&(!(!check.Inside&&!check.Ambiguous&&!check.OnSurface&&check.SignedDistance>=contourMetres));k++){hi=Math.Min(1,hi+.000002f);v=Make(c,a,b,1-hi,hi*(1-theta),hi*theta);check=volume.Query(v.p);}
   Require(!check.Inside&&!check.Ambiguous&&!check.OnSurface&&check.SignedDistance>=contourMetres,"Final cut endpoint not verified at working contour");return v;
  }
  double Cross2(SourceCapVertex a,SourceCapVertex b,SourceCapVertex c)=>(double)(b.wa-a.wa)*(c.wb-a.wb)-(double)(b.wb-a.wb)*(c.wa-a.wa);
  List<int[]> Ears(List<SourceCapVertex> poly){var result=new List<int[]>();var ids=Enumerable.Range(0,poly.Count).ToList();int guard=0;while(ids.Count>3){bool found=false;for(int j=0;j<ids.Count;j++){int a=ids[(j+ids.Count-1)%ids.Count],b=ids[j],c=ids[(j+1)%ids.Count];if(Cross2(poly[a],poly[b],poly[c])<=1e-12)continue;bool contains=false;foreach(int p in ids){if(p==a||p==b||p==c)continue;if(Cross2(poly[a],poly[b],poly[p])>=-1e-12&&Cross2(poly[b],poly[c],poly[p])>=-1e-12&&Cross2(poly[c],poly[a],poly[p])>=-1e-12){contains=true;break;}}if(contains)continue;result.Add(new[]{a,b,c});ids.RemoveAt(j);found=true;break;}Require(found&&guard++<256,"Ear triangulation invalid");}result.Add(ids.ToArray());return result;}
  bool Valid(List<SourceCapVertex> v,List<int[]> faces){foreach(var x in v){var q=volume.Query(x.p);if(q.Inside||q.Ambiguous||q.OnSurface||q.SignedDistance<.002f)return false;}foreach(var f in faces)foreach(var w in EnvelopeProbeBarycentrics){var q=volume.Query(v[f[0]].p*w.x+v[f[1]].p*w.y+v[f[2]].p*w.z);if(q.Inside||q.Ambiguous||q.OnSurface||q.SignedDistance<.002f)return false;}return true;}
  var replacements=new Dictionary<int,(List<SourceCapVertex> v,List<int[]> tris,List<SourceCapVertex> inner)>();var attempts=new List<object>();
  foreach(int face in capFaces){var indices=t.Skip(face*3).Take(3).ToArray();int at=Array.FindIndex(indices,i=>deep.Contains(i));int c=indices[at],a=indices[(at+1)%3],b=indices[(at+2)%3];Require(indices.Count(i=>deep.Contains(i))==1,"Non-fan triangle");bool good=false;
   foreach(int segments in new[]{1,2,4,8,16,32,64}){var inner=Enumerable.Range(0,segments+1).Select(j=>Cut(c,a,b,j/(float)segments)).ToList();var poly=new List<SourceCapVertex>{Make(c,a,b,0,1,0),Make(c,a,b,0,0,1)};poly[0].original=a;poly[1].original=b;poly.AddRange(inner.AsEnumerable().Reverse());var faces=Ears(poly);good=Valid(poly,faces);attempts.Add(new{face,segments,triangles=faces.Count,passed=good});if(good){replacements[face]=(poly,faces,inner);break;}}
   Require(good,"Annulus face residual failed within64segments: "+face);
  }
  var vertices=new List<SourceCapVertex>();var oldToNew=new Dictionary<int,int>();for(int i=0;i<old.Length;i++)if(!deep.Contains(i)){oldToNew[i]=vertices.Count;vertices.Add(new SourceCapVertex{original=i,c=i,a=i,b=i,wc=1,p=old[i]});}
  var newTris=new List<int>();var originalFaceOfOutput=new List<int>();var newSubmeshes=new List<int[]>();int faceOffset=0;
  foreach(var sub in submeshes){var local=new List<int>();for(int k=0;k<sub.Length/3;k++){int face=faceOffset+k;if(!replacements.TryGetValue(face,out var r)){foreach(int id in t.Skip(face*3).Take(3))local.Add(oldToNew[id]);originalFaceOfOutput.Add(face);continue;}var map=new int[r.v.Count];for(int j=0;j<map.Length;j++){if(r.v[j].original>=0)map[j]=oldToNew[r.v[j].original];else{map[j]=vertices.Count;vertices.Add(r.v[j]);}}foreach(var tri in r.tris){local.AddRange(tri.Select(i=>map[i]));originalFaceOfOutput.Add(face);}}faceOffset+=sub.Length/3;newSubmeshes.Add(local.ToArray());newTris.AddRange(local);}
  var positions=vertices.Select(v=>v.p).ToArray();var triangles=newTris.ToArray();int extra=triangles.Length/3-t.Length/3;Require(extra<=additionalTriangleBudget,"Original triangle budget exceeded");
  var residual=EnvelopeProbeResidual(positions,triangles,volume,.002f);float maxBone=0,maxOwn=0;var ownRefs=new List<Vector3>();var boneRefs=new List<Vector3>();
  foreach(var v in vertices){var own=ownReference[v.c]*v.wc+ownReference[v.a]*v.wa+ownReference[v.b]*v.wb;var common=bone[v.c]*v.wc+bone[v.a]*v.wa+bone[v.b]*v.wb;maxOwn=Math.Max(maxOwn,Distance(v.p,own));maxBone=Math.Max(maxBone,Distance(v.p,common));ownRefs.Add(own);boneRefs.Add(common);}
  int discardedSamples=0,discardedInside=0,discardedOutside=0,discardedUnknown=0;float discardedMin=float.PositiveInfinity,discardedMax=float.NegativeInfinity;double discardedArea=0,retainedArea=0,originalArea=0;
  foreach(var r in replacements.Values){var v=r.v[0];var c=old[v.c];for(int j=0;j<r.inner.Count-1;j++){var a=r.inner[j].p;var b=r.inner[j+1].p;discardedArea+=Length(Cross(a-c,b-c))*.5;foreach(var w in EnvelopeProbeBarycentrics){var q=volume.Query(c*w.x+a*w.y+b*w.z);discardedSamples++;if(q.Inside)discardedInside++;else discardedOutside++;if(q.Ambiguous||q.OnSurface)discardedUnknown++;discardedMin=Math.Min(discardedMin,q.SignedDistance);discardedMax=Math.Max(discardedMax,q.SignedDistance);}}foreach(var tri in r.tris)retainedArea+=Length(Cross(r.v[tri[1]].p-r.v[tri[0]].p,r.v[tri[2]].p-r.v[tri[0]].p))*.5;}
  foreach(int f in capFaces)originalArea+=Length(Cross(old[t[f*3+1]]-old[t[f*3]],old[t[f*3+2]]-old[t[f*3]]))*.5;

  int countOriginalPositionsChanged=0;foreach(var v in vertices)if(v.original>=0&&(v.p.x!=seed[v.original].x||v.p.y!=seed[v.original].y||v.p.z!=seed[v.original].z))countOriginalPositionsChanged++;
  var lineage=vertices.Select(v=>new SourceCapAnnulusLineage{OriginalIndex=v.original,Indices=new[]{v.c,v.a,v.b},Barycentric=new Vector3(v.wc,v.wa,v.wb)}).ToArray();
  var proof=new JObject{
   ["explicitEvidencedSelectionNotAutomaticSemanticDetection"]=true,["fanTopologyGuards"]=topologyGuards,
   ["strategy"]="local3D barycentric annulus; bracket+bisection; contour subdivision; ear triangulation",
   ["workingContourMetres"]=contourMetres,["physicalMinimumMetres"]=.002f,["priorWorkingBiasMetres"]=.006f,["contourChoiceProvenance"]=contourChoiceProvenance,
   ["residual"]=residual,["originalTriangles"]=t.Length/3,["outputTriangles"]=triangles.Length/3,["extraTriangles"]=extra,["budget"]=additionalTriangleBudget,["canonicalMaximumBudget"]=canonicalBudget,
   ["numericalGeometryPassed"]=(bool)residual["passedSampledEnvelope"]&&maxBone<=.12f&&maxOwn<=.12f&&countOriginalPositionsChanged==0,
   ["retainedFanVerticesAnd13SamplesPassed"]=true,
   ["retainedVertices"]=positions.Length,["discardedCenterAttributeVertices"]=deep.Count,["maxBone"]=maxBone,["maxOwn"]=maxOwn,["common12cmCapsPassed"]=maxBone<=.12f&&maxOwn<=.12f,
   ["originalRetainedPositionsChanged"]=countOriginalPositionsChanged,["outerRingOriginalPositionsUnchanged"]=countOriginalPositionsChanged==0,["otherOriginalFacesUnchanged"]=true,
   ["originalCapArea"]=originalArea,["discardedArea"]=discardedArea,["retainedArea"]=retainedArea,["areaPartitionError"]=Math.Abs(originalArea-discardedArea-retainedArea),
   ["discardedRegion"]=JObject.FromObject(new{discardedSamples,discardedInside,discardedOutside,discardedUnknown,discardedMin,discardedMax,positiveVisibleMarginMayBeRemoved=true}),
   ["attempts"]=JArray.FromObject(attempts),["actualPostBindExecuted"]=false,["actualGpuVisibilityInspected"]=false,["acceptanceOverride"]=false,["productionIntegrationReady"]=false,
   ["attributeContract"]="Retain every original attribute exactly using OriginalIndex; interpolate appended vertices from all3 lineage indices/barycentric. No UV/skin attributes handled by this geometry-only draft."
  };
  return new SourceCapAnnulusResult{World=positions,OwnReference=ownRefs.ToArray(),BoneReference=boneRefs.ToArray(),Submeshes=newSubmeshes.ToArray(),Lineage=lineage,OriginalFaceOfOutput=originalFaceOfOutput.ToArray(),Evidence=proof};
 }
}}
#endif
