#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
namespace HexLive.Tests
{
public sealed class FinalFitAtomicReceiptTests
{
    string directory,path;
    static Type Preparation=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HexLive.UnityDebug.Editor.LegacyWebGLWardrobePreparation",false)).First(t=>t!=null);
    static void Write(string path,byte[] bytes)=>Preparation.GetMethod("AtomicFinalBytes",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{path,bytes});
    [SetUp] public void SetUp(){directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"hexlive-final-receipt-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);path=System.IO.Path.Combine(directory,"receipt.json");}
    [TearDown] public void TearDown(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
    [Test] public void AtomicReceiptCreatesAndReplacesExactBytesWithoutTemporaryFiles()
    {
        Write(path,new byte[]{1,2});Write(path,new byte[]{3,4,5});
        Assert.That(File.ReadAllBytes(path),Is.EqualTo(new byte[]{3,4,5}));Assert.That(Directory.GetFiles(directory).Length,Is.EqualTo(1));
    }
    [Test,Platform("Win")] public void AtomicReceiptSurvivesTransientReaderDenyingDeleteShare()
    {
        File.WriteAllBytes(path,new byte[]{1,2});
        using(var blocker=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var release=new Thread(()=>{Thread.Sleep(120);blocker.Dispose();});release.Start();
            try{Write(path,new byte[]{3,4,5});}finally{release.Join();}
        }
        Assert.That(File.ReadAllBytes(path),Is.EqualTo(new byte[]{3,4,5}));Assert.That(Directory.GetFiles(directory).Length,Is.EqualTo(1));
    }
    [Test,Platform("Win")] public void PersistentLockFailsBoundedlyAndPreservesPreviousReceipt()
    {
        File.WriteAllBytes(path,new byte[]{1,2});var clock=Stopwatch.StartNew();
        using(var blocker=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var error=Assert.Throws<TargetInvocationException>(()=>Write(path,new byte[]{3,4,5}));
            Assert.That(error.InnerException,Is.TypeOf<IOException>());
            Assert.That(clock.Elapsed.TotalSeconds,Is.LessThan(5));
        }
        Assert.That(File.ReadAllBytes(path),Is.EqualTo(new byte[]{1,2}));Assert.That(Directory.GetFiles(directory).Length,Is.EqualTo(1));
    }
}
}
#endif
