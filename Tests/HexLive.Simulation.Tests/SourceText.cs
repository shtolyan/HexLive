using System.IO;

namespace HexLive.Simulation.Tests;

/// <summary>Text contracts compare content independently of the checkout's newline convention.</summary>
internal static class SourceText
{
    public static string Read(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");
}
