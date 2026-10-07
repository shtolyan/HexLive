using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HexLive.Updates;
using HexLive.Updater;
using NUnit.Framework;

namespace HexLive.Updater.Tests;
public sealed class UpdateTests
{
    private string _root = "";
    [SetUp] public void SetUp() { _root = Path.Combine(Path.GetTempPath(), "hexlive-update-test-" + Guid.NewGuid()); Directory.CreateDirectory(_root); }
    [TearDown] public void TearDown() { Directory.Delete(_root, true); }
    private static ReleaseManifest Release() => new() {
        schemaVersion = 1, product = "client", platform = "windows", architecture = "x64", channel = "stable",
        version = "0.1.110", minimumVersion = "0.1.100", driveFileId = "drive-id", sha256 = new string('a',64),
        size = 1, unpackedSize = 2, publishedUtc = "2026-09-13T00:00:00Z" };
    [Test] public void NumericVersionsOrderCorrectly() => Assert.That(ReleaseManifest.ParseVersion("0.1.110"), Is.GreaterThan(ReleaseManifest.ParseVersion("0.1.99")));
    [TestCase("../1")][TestCase("1.2")][TestCase("1.2.3-beta")]
    public void InvalidVersionRejected(string value) => Assert.Throws<InvalidDataException>(() => ReleaseManifest.ParseVersion(value));
    [Test] public void WrongPlatformRejected() => Assert.Throws<InvalidDataException>(() => Release().Validate("macos", "arm64"));
    [Test] public void MinimumBeyondReleaseRejected() { var r=Release(); r.minimumVersion="9.0.0"; Assert.Throws<InvalidDataException>(()=>r.Validate("windows","x64")); }
    [Test] public void SignatureRejectsTamperingAndWrongKey()
    {
        using var key = RSA.Create(3072); using var other = RSA.Create(3072);
        var pub = key.ExportParameters(false);
        var config = new UpdateConfig { modulus = Convert.ToBase64String(pub.Modulus!), exponent = Convert.ToBase64String(pub.Exponent!) };
        var bytes = Encoding.UTF8.GetBytes("release payload");
        var e = new ReleaseEnvelope { payload = Convert.ToBase64String(bytes), signature = Convert.ToBase64String(key.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1)) };
        Assert.That(ReleaseTrust.Verify(e,config), Is.EqualTo(bytes));
        e.payload=Convert.ToBase64String(Encoding.UTF8.GetBytes("tampered"));
        Assert.Throws<CryptographicException>(()=>ReleaseTrust.Verify(e,config));
        e.payload=Convert.ToBase64String(bytes); e.signature=Convert.ToBase64String(other.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));
        Assert.Throws<CryptographicException>(()=>ReleaseTrust.Verify(e,config));
    }
    [Test] public void SameSizeCorruptionRejected()
    {
        var file=Path.Combine(_root,"archive"); File.WriteAllText(file,"x");
        Assert.Throws<InvalidDataException>(()=>ReleaseTrust.VerifyArchive(file,Release()));
    }
    [TestCase("../outside")][TestCase("/absolute")][TestCase("HexLive/../../outside")][TestCase("HexLive\\evil")][TestCase("C:/outside")]
    public void ArchiveTraversalRejected(string entry)
    {
        var zip=Path.Combine(_root,"archive.zip");
        using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)) { using var writer=new StreamWriter(archive.CreateEntry(entry).Open()); writer.Write("x"); }
        var destination=Path.Combine(_root,"out");Directory.CreateDirectory(destination);
        Assert.Throws<InvalidDataException>(()=>Program.ExtractSafe(zip,destination,1,false));
        Assert.That(Directory.GetFiles(destination),Is.Empty);
    }
    [Test] public void ExtractionLimitCheckedBeforeAnyWrite()
    {
        var zip=Path.Combine(_root,"archive.zip");
        using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)) { using var writer=new StreamWriter(archive.CreateEntry("HexLive/HexLive.exe").Open()); writer.Write("too large"); }
        var destination=Path.Combine(_root,"out");Directory.CreateDirectory(destination);
        Assert.Throws<InvalidDataException>(()=>Program.ExtractSafe(zip,destination,1,false));
        Assert.That(Directory.GetFiles(destination),Is.Empty);
    }
    [Test] public void ValidArchiveExtracted()
    {
        var zip=Path.Combine(_root,"archive.zip");
        using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)) { using var writer=new StreamWriter(archive.CreateEntry("HexLive/HexLive.exe").Open()); writer.Write("player"); }
        var destination=Path.Combine(_root,"out");Directory.CreateDirectory(destination);
        Program.ExtractSafe(zip,destination,100,false);
        Assert.That(File.ReadAllText(Path.Combine(destination,"HexLive/HexLive.exe")),Is.EqualTo("player"));
    }
    [Test] public void InterruptedSwitchRestoresPreviousAndKeepsCandidate()
    {
        var install=Path.Combine(_root,"HexLive");var backup=Path.Combine(_root,"previous");var candidate=Path.Combine(_root,"candidate");
        Directory.CreateDirectory(backup);File.WriteAllText(Path.Combine(backup,"HexLive.exe"),"previous");
        Directory.CreateDirectory(candidate);File.WriteAllText(Path.Combine(candidate,"HexLive.exe"),"new");
        Directory.CreateSymbolicLink(install,candidate);
        var journal=new InstallJournal {installPath=install,backup=backup,candidate=candidate,phase="switched"};
        Program.Restore(journal,Path.Combine(_root,"journal.json"));
        Assert.That(File.ReadAllText(Path.Combine(install,"HexLive.exe")),Is.EqualTo("previous"));
        Assert.That(File.ReadAllText(Path.Combine(candidate,"HexLive.exe")),Is.EqualTo("new"));
        Assert.That(journal.phase,Is.EqualTo("rolledBack"));
        Program.Restore(journal,Path.Combine(_root,"journal.json"));
    }
    [Test] public void RollbackNeverDeletesUnexpectedRealInstallation()
    {
        var install=Path.Combine(_root,"HexLive");var backup=Path.Combine(_root,"previous");
        Directory.CreateDirectory(install);Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(install,"keep"),"user");
        Assert.Throws<IOException>(()=>Program.Restore(new InstallJournal {installPath=install,backup=backup},Path.Combine(_root,"journal.json")));
        Assert.That(File.Exists(Path.Combine(install,"keep")),Is.True);
    }
}
