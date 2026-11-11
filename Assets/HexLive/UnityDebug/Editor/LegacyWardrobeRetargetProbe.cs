#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;
namespace HexLive.UnityDebug.Editor
{
// DIAGNOSTIC ONLY. Compile beside the authoring partials. No AssetDatabase
// save/import, source setting, persistent mesh, cache, or fingerprint write.
public static partial class LegacyWebGLWardrobePreparation
{
    static JObject RetargetProbeContacts(Vector3[] positions,Vector3[] normals,TriangleTree body,bool preferred)
    {
        int below1=0,below3=0,below10=0,skipped=0,deepSkipped=0,worst=-1,near40=0;
        float maxDepth=0;var depths=new List<float>();Vector3 worstBody=default,worstNormal=default;
        for(int i=0;i<positions.Length;i++)
        {
            var c=body.Nearest(positions[i],preferred?normals[i]:default);
            float signed=Vector3.Dot(positions[i]-c.Point,c.Normal);
            if(c.Distance<.04f)near40++;
            if(c.Distance>=.04f&&signed<Clearance)skipped++;
            if(c.Distance>=.04f&&signed<-.001f)deepSkipped++;
            if(signed<-.001f)below1++;
            if(signed<-.003f)below3++;
            if(signed<-.01f)below10++;
            if(signed>=0)continue;
            depths.Add(-signed);
            if(-signed<=maxDepth)continue;
            maxDepth=-signed;worst=i;worstBody=c.Point;worstNormal=c.Normal;
        }
        depths.Sort();
        return new JObject{["vertexCount"]=positions.Length,["normalPreference"]=preferred,["signedBelowMinus1mm"]=below1,
            ["signedBelowMinus3mm"]=below3,["signedBelowMinus10mm"]=below10,["maxDepthMetres"]=maxDepth,
            ["p95NegativeDepthMetres"]=depths.Count==0?0:depths[(int)((depths.Count-1)*.95)],
            ["contactDistanceBelow40mm"]=near40,["signedBelowClearanceButSkippedBy40mmCutoff"]=skipped,
            ["penetrating1mmButSkippedBy40mmCutoff"]=deepSkipped,
            ["worstVertex"]=worst,["worstPosition"]=worst<0?null:Vec(positions[worst]),
            ["worstNearestBodyPoint"]=worst<0?null:Vec(worstBody),["worstNearestBodyNormal"]=worst<0?null:Vec(worstNormal)};
    }

    static JObject RetargetProbeError(Vector3[] expected,Vector3[] actual)
    {
        if(expected.Length!=actual.Length)throw new InvalidOperationException("Probe changed topology");
        var errors=new float[expected.Length];int worst=0;
        for(int i=0;i<errors.Length;i++){errors[i]=Vector3.Distance(expected[i],actual[i]);if(errors[i]>errors[worst])worst=i;}
        float maximum=errors[worst];Array.Sort(errors);
        return new JObject{["maxMetres"]=maximum,["p95Metres"]=errors[(int)((errors.Length-1)*.95)],
            ["worstVertex"]=worst,["expectedWorld"]=Vec(expected[worst]),["actualWorld"]=Vec(actual[worst]),
            ["expectedBounds"]=Box(VertexBounds(expected)),["actualBounds"]=Box(VertexBounds(actual))};
    }

    static Vector3[] RetargetProbePush(Fit baseline,bool cutoff,bool preferred,int passes,out JObject changes)
    {
        var points=(Vector3[])baseline.WorldVertices.Clone();int operations=0;var perPass=new JArray();
        for(int pass=0;pass<passes;pass++)
        {
            int moved=0;
            for(int i=0;i<points.Length;i++)
            {
                var c=baseline.BodySurface.Nearest(points[i],preferred?baseline.WorldNormals[i]:default);
                float signed=Vector3.Dot(points[i]-c.Point,c.Normal);
                if(signed>=Clearance||cutoff&&c.Distance>=.04f)continue;
                points[i]+=c.Normal*(Clearance-signed);moved++;operations++;
            }
            perPass.Add(moved);
        }
        changes=new JObject{["cutoff40mm"]=cutoff,["normalPreference"]=preferred,["passes"]=passes,["pushOperations"]=operations,["verticesPushedPerPass"]=perPass,
            ["displacementFromSaved"]=RetargetProbeError(baseline.WorldVertices,points)};
        return points;
    }

    static void RetargetProbeRender(Fit fit,Wear authored,ActorName actor,Material[] materials,string label,JObject row)
    {
        Transform originalParent=fit.Garment.transform.parent;
        try
        {
            // ConstructFit's garment root is independent; attach only this
            // private clone so the existing production-preview renderer sees it.
            fit.Garment.transform.SetParent(fit.Body.transform,true);
            if(materials!=null&&materials.Length>0)fit.Skin.sharedMaterials=materials;
            var bones=fit.Body.GetComponent<BodyBones>();
            if(!(authored.Layer==VisualWearLayer.Underwear&&authored.Slots.Contains(VisualWearSlot.Pelvis)))
            {
                var briefs=AssetDatabase.LoadAssetAtPath<GameObject>(PeopleRoot+"/Prefabs/Wear/"+actor+"_PrimalBriefs.prefab").GetComponent<Wear>();
                bones.Equip("retarget-probe-baseline",briefs,null);
                typeof(BodyBones).GetField("_heel",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bones,authored.Heel);
                typeof(BodyBones).GetMethod("LateUpdate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(bones,null);
            }
            RenderWardrobe(fit.Body,label+"_front",false);RenderWardrobe(fit.Body,label+"_back",true);
            row["front"]=OutputRoot+"/review/"+label+"_front.png";row["back"]=OutputRoot+"/review/"+label+"_back.png";
        }
        catch(Exception e){row["renderError"]=e.ToString();}
        finally{if(fit.Garment!=null)fit.Garment.transform.SetParent(originalParent,true);}
    }

    static JObject RetargetProbeRecord(string id,string mode,ActorName actor,Fit fit,Vector3[] expected,Vector3[] saved)
    {
        return new JObject{["id"]=id,["mode"]=mode,["actor"]=actor.ToString(),["worldBounds"]=Box(VertexBounds(fit.WorldVertices)),
            ["unfilteredNearestBody"]=RetargetProbeContacts(fit.WorldVertices,fit.WorldNormals,fit.BodySurface,false),
            ["normalPreferredNearestBody"]=RetargetProbeContacts(fit.WorldVertices,fit.WorldNormals,fit.BodySurface,true),
            ["targetToActualReconstructionError"]=RetargetProbeError(expected,fit.WorldVertices),
            ["savedToActualPositionDifference"]=RetargetProbeError(saved,fit.WorldVertices)};
    }

    public static void AuditSavedWardrobeContacts()
    {
        var ids=new[]{"clothing.sweater_naughty","clothing.sweater_flair","clothing.boots_anarchy","clothing.croptop_fit","clothing.gloves_strap_anarchy"};
        string catalogPath=PreparedRoot+"/catalog.json",catalogBefore=File.ReadAllText(catalogPath);
        var records=((JArray)JObject.Parse(catalogBefore)["records"]).Where(r=>(string)r["type"]=="wear").ToArray();
        var inputs=ids.ToDictionary(id=>id,id=>records.Single(r=>(string)r["id"]==id));
        var hashes=inputs.Values.Select(r=>(string)r["main"]).Distinct().ToDictionary(p=>p,AssetDatabase.GetAssetDependencyHash);
        var rows=new JArray();var report=new JObject{["schemaVersion"]=1,["models"]=new JArray(ids),["rows"]=rows,
            ["assetsChanged"]=false,["bundlesBuilt"]=false,["cacheChanged"]=false,["units"]="world metres",
            ["nearestSignedLimitation"]="Local nearest-triangle normal sign, not a watertight volume test; both unfiltered and normal-preferred queries are reported without distance exclusion.",
            ["purpose"]="Measure saved fits, replayed binding, and isolated nearest-surface push controls; no production algorithm/threshold selected."};
        string output=OutputRoot+"/saved-retarget-contact-probe.json";Directory.CreateDirectory(OutputRoot);
        try
        {
            foreach(string id in ids)
            {
                var record=inputs[id];string path=(string)record["main"];
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);var authored=prefab.GetComponent<Wear>();
                var definition=AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)record["definition"]);
                var femaleMesh=SourceMesh(authored,ActorName.Marta,out _);var maleMesh=SourceMesh(authored,ActorName.Kshishtof,out _);
                foreach(var actor in new[]{ActorName.Marta,ActorName.Kshishtof})
                {
                    using var baseline=ConstructFit(prefab,femaleMesh,maleMesh,actor);
                    var source=actor==ActorName.Marta?femaleMesh:maleMesh;
                    var row=RetargetProbeRecord(id,"saved",actor,baseline,baseline.WorldVertices,baseline.WorldVertices);rows.Add(row);
                    RetargetProbeRender(baseline,authored,actor,definition.variantMaterials,"contact_"+id+"_"+actor+"_saved",row);
                    foreach(string mode in new[]{"rebind_identity","push_cutoff40mm_1","push_no_cutoff_1","push_no_cutoff_4","push_unfiltered_no_cutoff_4"})
                    {
                        JObject changes=null;
                        var requested=mode=="rebind_identity"?(Vector3[])baseline.WorldVertices.Clone():
                            RetargetProbePush(baseline,mode=="push_cutoff40mm_1",mode!="push_unfiltered_no_cutoff_4",mode.EndsWith("_4",StringComparison.Ordinal)?4:1,out changes);
                        var candidate=BindMesh(source,baseline,requested,baseline.WorldNormals,"diagnostic_"+mode);
                        try
                        {
                            using var rebuilt=ConstructFit(prefab,actor==ActorName.Marta?candidate:femaleMesh,actor==ActorName.Kshishtof?candidate:maleMesh,actor);
                            var result=RetargetProbeRecord(id,mode,actor,rebuilt,requested,baseline.WorldVertices);result["push"]=changes;rows.Add(result);
                            if(mode!="rebind_identity"&&mode!="push_no_cutoff_1")
                                RetargetProbeRender(rebuilt,authored,actor,definition.variantMaterials,"contact_"+id+"_"+actor+"_"+mode,result);
                        }
                        finally{Object.DestroyImmediate(candidate);}
                        File.WriteAllText(output,report.ToString());
                    }
                }
                // Reuse the actual current Retarget, with its .04 cutoff, on
                // the saved donor mesh. This distinguishes retained old output
                // from what today's mapping/binding reconstructs from that donor.
                using var donor=ConstructFit(prefab,femaleMesh,femaleMesh,ActorName.Marta);
                using var targetSeed=ConstructFit(prefab,femaleMesh,femaleMesh,ActorName.Kshishtof);
                using var savedMale=ConstructFit(prefab,femaleMesh,maleMesh,ActorName.Kshishtof);
                foreach(bool fitToBody in new[]{false,true})
                {
                    var replay=Retarget(femaleMesh,donor,targetSeed,out int conformed,false,fitToBody);
                    try
                    {
                        using var fitted=ConstructFit(prefab,femaleMesh,replay,ActorName.Kshishtof);
                        var intended=replay.vertices.Select(targetSeed.Skin.transform.localToWorldMatrix.MultiplyPoint3x4).ToArray();
                        var replayRow=RetargetProbeRecord(id,fitToBody?"replay_current_retarget":"replay_bones_only",ActorName.Kshishtof,
                            fitted,intended,savedMale.WorldVertices);
                        replayRow["conformedVertices"]=conformed;replayRow["usesProductionRetarget"]=true;rows.Add(replayRow);
                    }
                    finally{Object.DestroyImmediate(replay);}
                }
                File.WriteAllText(output,report.ToString());
            }
            if(File.ReadAllText(catalogPath)!=catalogBefore||hashes.Any(pair=>AssetDatabase.GetAssetDependencyHash(pair.Key)!=pair.Value))
                throw new InvalidOperationException("Diagnostic changed persistent inputs");
            report["completed"]=true;report["persistentInputsUnchanged"]=true;
        }
        catch(Exception e){report["completed"]=false;report["error"]=e.ToString();throw;}
        finally{File.WriteAllText(output,report.ToString());}
        Debug.Log("SAVED_RETARGET_CONTACT_PROBE_COMPLETE "+rows.Count+" cases; "+output);
    }
}
}
#endif
