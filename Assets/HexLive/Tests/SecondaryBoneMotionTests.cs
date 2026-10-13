using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEngine;

namespace HexLive.Tests
{
    public class SecondaryBoneMotionTests
    {
        GameObject root;
        [TearDown] public void Cleanup(){if(root!=null)Object.DestroyImmediate(root);}
        [TestCase(30)] [TestCase(60)] [TestCase(120)]
        public void MovingChain_PreservesLengthAndAngle(int fps)
        {
            root=new GameObject("spring test");
            var first=new GameObject("first").transform;first.SetParent(root.transform,false);
            var second=new GameObject("second").transform;second.SetParent(first,false);second.localPosition=Vector3.down*.2f;
            var spring=root.AddComponent<SecondaryBoneMotion>();spring.enabled=false;
            spring.Configure(root.transform,new[]{first,second},new[]{Vector3.down*.2f,Vector3.down*.2f},SecondaryBoneMotion.Settings.Hair);
            float peak=0;
            for(int frame=0;frame<fps*4;frame++)
            {
                spring.RestorePose();root.transform.position=new Vector3(Mathf.Sin(frame/(float)fps*6)*.12f,0,0);
                spring.Step(1f/fps);peak=Mathf.Max(peak,spring.LastMaxAngle);
                Assert.That(spring.LastMaxAngle,Is.LessThanOrEqualTo(14.01f));
                Assert.That(spring.LastMaxLengthError,Is.LessThan(.00001f));
                Assert.That(float.IsNaN(second.rotation.x),Is.False);
            }
            Assert.That(peak,Is.GreaterThan(.1f));
            spring.RestorePose();Assert.That(Quaternion.Angle(first.localRotation,Quaternion.identity),Is.LessThan(.01f));
        }
        [Test] public void TeleportAndHitch_ResetWithoutStretch()
        {
            root=new GameObject("reset test");var bone=new GameObject("bone").transform;bone.SetParent(root.transform,false);
            var spring=root.AddComponent<SecondaryBoneMotion>();spring.enabled=false;
            spring.Configure(root.transform,new[]{bone},new[]{Vector3.forward*.1f},SecondaryBoneMotion.Settings.Breast);
            int resets=spring.ResetCount;root.transform.position=Vector3.up*10;spring.Step(1f/30);
            Assert.That(spring.ResetCount,Is.EqualTo(resets+1));Assert.That(spring.LastMaxAngle,Is.Zero);
            spring.Step(.5f);Assert.That(spring.ResetCount,Is.EqualTo(resets+2));
        }
        [Test] public void VariableFrameRate_SettlesAndDisableRestoresPose()
        {
            root=new GameObject("variable rate test");var bone=new GameObject("bone").transform;bone.SetParent(root.transform,false);
            var spring=root.AddComponent<SecondaryBoneMotion>();spring.enabled=false;
            spring.Configure(root.transform,new[]{bone},new[]{Vector3.forward*.1f},SecondaryBoneMotion.Settings.Breast);
            float elapsed=0;
            for(int frame=0;frame<360;frame++)
            {
                float dt=frame%2==0?1f/30:1f/120;elapsed+=dt;
                spring.RestorePose();root.transform.position=elapsed<3?Vector3.up*(.03f*Mathf.Sin(elapsed*8)):Vector3.zero;
                spring.Step(dt);Assert.That(spring.LastMaxAngle,Is.LessThanOrEqualTo(5.01f));
            }
            Assert.That(spring.LastMaxAngle,Is.LessThan(.1f));
            spring.enabled=true;spring.enabled=false;
            Assert.That(Quaternion.Angle(bone.localRotation,Quaternion.identity),Is.LessThan(.01f));
        }
    }
}
