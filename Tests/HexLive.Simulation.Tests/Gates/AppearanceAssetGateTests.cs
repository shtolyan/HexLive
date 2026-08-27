using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §85: пул цветов глаз обязан разрешаться в реальные ассеты.
/// <para>
/// Симуляция катит СТРОКУ (<c>"blue_green"</c>), а вид грузит по ней папку
/// <c>HexLiveContent/RuntimeSource/Eyes/&lt;id&gt;/</c>. Между ними нет ни компилятора, ни
/// ссылки — ровно та щель, в которую §74.4 уже проваливался с причёсками: пул
/// правится в C#, ассеты живут на диске, и расхождение не падает, а тихо
/// оставляет девушку с глазами от префаба тела.
/// </para>
/// <para>
/// Проверяется и то, что делает подмену подменой: материал обязан СОХРАНЯТЬ имя
/// слота (по имени его ищут в <c>ReplaceBodyMaterials</c> и по имени же
/// исключают из загара §40.8-G) и ссылаться на СВОЮ карту, а не на донорскую —
/// перекрашенная текстура, которую никто не использует, выглядит как «палитра
/// не работает».
/// </para>
/// </summary>
public sealed class AppearanceAssetGateTests
{
    private static string EyesRoot =>
        Path.Combine(RepoPaths.Root, "Assets", "HexLiveContent", "RuntimeSource", "Eyes");

    private static string TexturesRoot =>
        Path.Combine(RepoPaths.Root, "Assets", "HexLive", "Art", "Eyes", "Textures");

    // Материалы без текстуры — одни и те же ассеты на всю колонию.
    private static readonly string[] Shared = { "Pupils", "Cornea", "EyeMoisture" };

    // Материалы, несущие карту глаза — по копии на цвет.
    private static readonly string[] Tinted = { "Irises", "Sclera" };

    [Test]
    public void EveryEyeColorResolvesToRealMaterials()
    {
        var problems = ColonistAppearance.EyeColors.SelectMany(Check).ToList();

        Assert.That(problems, Is.Empty,
            "Цвет глаз из ColonistAppearance.EyeColors не разрешается в ассеты. " +
            "Пересобрать: python3 Tools/make_eye_textures.py\n" +
            string.Join("\n", problems));
    }

    [Test]
    public void SharedEyeMaterialsExist()
    {
        var missing = Shared
            .Select(name => Path.Combine(EyesRoot, "Common", name + ".mat"))
            .Where(path => !File.Exists(path) || !File.Exists(path + ".meta"))
            .Select(path => Path.GetFileName(path))
            .ToList();

        Assert.That(missing, Is.Empty,
            $"В {Path.Combine("HexLiveContent", "RuntimeSource", "Eyes", "Common")} нет общих " +
            "материалов глаз: " + string.Join(", ", missing));
    }

    private static System.Collections.Generic.IEnumerable<string> Check(string color)
    {
        var texture = Path.Combine(TexturesRoot, $"eye_{color}.jpg");
        if (!File.Exists(texture) || !File.Exists(texture + ".meta"))
        {
            yield return $"{color}: нет карты eye_{color}.jpg (или её .meta)";
            yield break;
        }

        var wantGuid = Regex.Match(File.ReadAllText(texture + ".meta"), @"guid: (\w{32})")
            .Groups[1].Value;

        foreach (var slot in Tinted)
        {
            var path = Path.Combine(EyesRoot, color, slot + ".mat");
            if (!File.Exists(path))
            {
                yield return $"{color}: нет {slot}.mat";
                continue;
            }

            if (!File.Exists(path + ".meta"))
            {
                yield return $"{color}/{slot}.mat: нет .meta — Unity его не увидит";
            }

            var body = File.ReadAllText(path);
            if (!body.Contains("m_Name: " + slot))
            {
                yield return $"{color}/{slot}.mat: m_Name не '{slot}' — подмена идёт по ИМЕНИ";
            }

            if (!body.Contains($"guid: {wantGuid}, type: 3"))
            {
                yield return $"{color}/{slot}.mat: не ссылается на eye_{color}.jpg";
            }
        }
    }
}

}
