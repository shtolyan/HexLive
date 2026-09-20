using System.ComponentModel;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;

namespace HexLive.Launcher.Tests;

[Platform("Win")]
public sealed class WindowsUpdaterProcessTests
{
    [Test]
    public void NativeLaunchWorksWithSpacesAndUnicodeInPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HexLive тест " + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "Updater probe.exe");
        try
        {
            // Harmless Windows executable: rejects updater arguments and exits immediately.
            File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), executable);
            Assert.DoesNotThrow(() => WindowsUpdaterProcess.Start(executable, 19));
            Assert.That(SpinWait.SpinUntil(() => {
                try { File.Delete(executable); return true; }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }, TimeSpan.FromSeconds(10)), Is.True);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void InvalidExecutableReportsNativeError()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "not an executable");
            var error = Assert.Throws<Win32Exception>(() => WindowsUpdaterProcess.Start(file, 19));
            Assert.That(error!.NativeErrorCode, Is.EqualTo(193));
        }
        finally { File.Delete(file); }
    }

    [Test]
    public void MissingHelperDoesNotStartAnything()
    {
        Assert.Throws<FileNotFoundException>(() => WindowsUpdaterProcess.Start(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), 19));
    }
}
