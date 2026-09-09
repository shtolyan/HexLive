using System.Text;
using System.Text.Json;

namespace HexLive.AgentHost;

/// <summary>Editable text, not provider configuration or executable permissions.</summary>
public static class AgentPromptFiles
{
    private static readonly object Gate = new();
    private static string? _json;
    private static Dictionary<string, string>? _texts;
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "AgentPrompts");
    public static string Read(string name)
    {
        if (Path.GetFileName(name) != name) throw new InvalidDataException("InvalidPromptFileName");
        var path = Path.Combine(DirectoryPath, name);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 262144 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("MissingOrInvalidPromptFile: " + name);
        // StreamReader's BOM autodetection silently accepts malformed UTF-16 even
        // when given a strict UTF-8 encoding. Decode the actual bytes explicitly.
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is < 1 or > 262144) throw new InvalidDataException("InvalidPromptSize: " + name);
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
    }

    public static string Text(string key)
    {
        lock (Gate)
        {
            var json = Read("text.json");
            if (_json != json)
            {
                _texts = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? throw new InvalidDataException("InvalidPromptText");
                _json = json;
            }
            return _texts!.TryGetValue(key, out var value) && value != null ? value :
                throw new InvalidDataException("MissingPromptText: " + key);
        }
    }
}
