using System;
using System.Runtime.CompilerServices;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;
namespace HexLive.UnityPresentation.Input
{
/// <summary>§121/#646: tolerant live skeleton volumes, shared by manual actions and RTS selection.
/// Mesh holes, clothing layer visibility and the 0.1s skinned-mesh bake cache cannot erase a body hit.
/// </summary>
public static class NpcBodyPicker
{
    public const float Margin = .03f;
    private sealed class Body
    {
        public SkinnedMeshRenderer Skin;
        public Transform Head,Pelvis,LeftFoot,RightFoot,LeftKnee,RightKnee;
        public Transform LeftShoulder,RightShoulder,LeftElbow,RightElbow,LeftHand,RightHand,LeftPalm,RightPalm;
        public bool Ready => Skin!=null&&Head!=null&&Pelvis!=null&&LeftFoot!=null&&RightFoot!=null;
        public void Resolve(NpcActorView view)
        {
            Head=Pelvis=LeftFoot=RightFoot=LeftKnee=RightKnee=null;
            LeftShoulder=RightShoulder=LeftElbow=RightElbow=LeftHand=RightHand=LeftPalm=RightPalm=null;
            Skin=view.PrimaryBodySkin;
            if(Skin==null) return;
            foreach(var bone in Skin.bones)
            {
                if(bone==null) continue;
                switch(bone.name.ToLowerInvariant())
                {
                    case "head":Head=bone;break;
                    case "pelvis":Pelvis=bone;break;
                    case "lfoot":LeftFoot=bone;break;
                    case "rfoot":RightFoot=bone;break;
                    case "lshin":LeftKnee=bone;break;
                    case "rshin":RightKnee=bone;break;
                    case "lshldrbend":LeftShoulder=bone;break;
                    case "rshldrbend":RightShoulder=bone;break;
                    case "lforearmbend":LeftElbow=bone;break;
                    case "rforearmbend":RightElbow=bone;break;
                    case "lhand":LeftHand=bone;break;
                    case "rhand":RightHand=bone;break;
                    case "lmid2":LeftPalm=bone;break;
                    case "rmid2":RightPalm=bone;break;
                }
            }
        }
    }
    private static readonly ConditionalWeakTable<NpcActorView,Body> Bodies=new();
    private static Body Create(NpcActorView view) {var body=new Body();body.Resolve(view);return body;}
    private static NpcPickMath.Point P(Vector3 p)=>new(p.x,p.y,p.z);
    public static bool TryIntersect(NpcActorView view,Ray ray,float maximum,out float distance)
    {
        distance=maximum;
        if(view==null||!view.gameObject.activeInHierarchy) return false;
        var body=Bodies.GetValue(view,Create);
        if(!body.Ready) body.Resolve(view);
        if(!body.Ready) return view.TryRaycastVisibleGeometry(ray,maximum,out distance);
        var pelvis=body.Pelvis.position;var head=body.Head.position;
        var left=body.LeftFoot.position;var right=body.RightFoot.position;
        float LegLength(Vector3 foot,Transform knee)=>knee!=null?
            Vector3.Distance(pelvis,knee.position)+Vector3.Distance(knee.position,foot):Vector3.Distance(pelvis,foot);
        var height=Vector3.Distance(pelvis,head)+(LegLength(left,body.LeftKnee)+LegLength(right,body.RightKnee))*.5f;
        if(height<.05f) return view.TryRaycastVisibleGeometry(ray,maximum,out distance);
        var hit=false;var nearest=maximum;
        void Span(Vector3 a,Vector3 b,float radius)
        {
            if(NpcPickMath.Capsule(P(ray.origin),P(ray.direction),P(a),P(b),radius,nearest,out var candidate))
            {nearest=candidate;hit=true;}
        }
        Span(pelvis,head,height*.16f+Margin);
        void Leg(Vector3 foot,Transform knee)
        {
            var radius=height*.09f+Margin;
            if(knee!=null) {Span(pelvis,knee.position,radius);Span(knee.position,foot,radius);}
            else Span(pelvis,foot,radius);
        }
        Leg(left,body.LeftKnee);Leg(right,body.RightKnee);
        // Outstretched arms lie far outside the torso/leg volumes. Read the
        // cached live bones on every ray, including the palm beyond the wrist;
        // a normal miss must not turn into an expensive skinned-mesh bake.
        void Arm(Transform shoulder,Transform elbow,Transform hand,Transform palm)
        {
            var radius=height*.045f+Margin;
            if(shoulder!=null&&elbow!=null) Span(shoulder.position,elbow.position,radius);
            else if(shoulder!=null&&hand!=null) Span(shoulder.position,hand.position,radius);
            if(elbow!=null&&hand!=null) Span(elbow.position,hand.position,radius);
            if(hand!=null) Span(hand.position,palm!=null?palm.position:hand.position,radius);
        }
        Arm(body.LeftShoulder,body.LeftElbow,body.LeftHand,body.LeftPalm);
        Arm(body.RightShoulder,body.RightElbow,body.RightHand,body.RightPalm);
        distance=nearest;return hit;
    }
}
}
