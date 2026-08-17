using System.IO;
using System.Linq;
using HexLive.Server.Mcp;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp
{

/// <summary>
/// §144.9: спека, доступная агенту по сети.
/// <para>
/// Первый тест здесь — про безопасность, и это не формальность: имя раздела
/// приходит из сети и попадает в путь к файлу. Всё остальное — про честность
/// усечения: раздел, оборванный на полуслове и выданный как целый, хуже
/// отсутствующего раздела.
/// </para>
/// </summary>
public sealed class SpecLibraryTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "hexlive-spec-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "spec.md"), "# Оглавление\n- §144\n");
        File.WriteAllText(Path.Combine(_root, "144.md"), new string('x', 30_000));
        File.WriteAllText(Path.Combine(_root, "29E.md"), "## §29E Вода\n");
        File.WriteAllText(Path.Combine(_root, "9.md"), "## §9\n");
        File.WriteAllText(Path.Combine(_root, "10.md"), "## §10\n");
        File.WriteAllText(Path.Combine(_root, "preamble.md"), "## Преамбула\n");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestCase("../../../etc/passwd")]
    [TestCase("..\\..\\secrets")]
    [TestCase("/etc/passwd")]
    [TestCase("144/../../appsettings")]
    [TestCase("144.md")]
    public void SectionNameThatIsNotASectionNameIsRefused(string probe)
    {
        var library = new SpecLibrary(_root);

        var ok = library.TryReadSection(probe, 0, out var text, out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False, "проверяется ФОРМА имени, а не наличие точек: " +
                                      "чёрные списки обходят, белые — нет");
            Assert.That(text, Is.Empty);
            Assert.That(error, Is.Not.Empty, "отказ обязан назвать причину");
        });
    }

    [TestCase("144")]
    [TestCase("§144")]
    [TestCase(" 144 ")]
    [TestCase("29E")]
    [TestCase("preamble")]
    public void RealSectionsResolve(string section)
    {
        var library = new SpecLibrary(_root);

        Assert.That(library.TryReadSection(section, 0, out var text, out _, out var error),
            Is.True, error);
        Assert.That(text, Is.Not.Empty);
    }

    [Test]
    public void LongSectionIsCutWithAnHonestContinuation()
    {
        var library = new SpecLibrary(_root);

        Assert.That(library.TryReadSection("144", 0, out var first, out var total, out _), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(total, Is.EqualTo(30_000));
            Assert.That(first.Length, Is.EqualTo(SpecLibrary.MaxChunkChars));
        });

        // Продолжение обязано начинаться ровно там, где оборвалось предыдущее:
        // иначе кусок текста исчезает молча.
        Assert.That(library.TryReadSection("144", first.Length, out var rest, out _, out _), Is.True);
        Assert.That(first.Length + rest.Length, Is.EqualTo(total));
    }

    [Test]
    public void SectionsAreOrderedByNumberNotAlphabet()
    {
        var library = new SpecLibrary(_root);

        var sections = library.Sections().ToArray();

        // По алфавиту «10» встаёт перед «9», и оглавление начинает врать
        // о порядке разделов.
        Assert.That(sections.ToList().IndexOf("9"), Is.LessThan(sections.ToList().IndexOf("10")));
        Assert.That(sections, Does.Contain("29E"));
        Assert.That(sections, Does.Not.Contain("spec"), "оглавление — не раздел");
    }

    [Test]
    public void MissingSpecIsAnAnswerNotACrash()
    {
        var library = new SpecLibrary(root: null);

        Assert.Multiple(() =>
        {
            Assert.That(library.Available, Is.False);
            Assert.That(library.ReadIndex(), Is.Null);
            Assert.That(library.Sections(), Is.Empty);
            Assert.That(library.TryReadSection("144", 0, out _, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("--spec-dir"),
                "отказ обязан подсказать, чем это чинится");
        });
    }

    [Test]
    public void SpecShipsBesideTheBinary()
    {
        // Ради этого и правился csproj: агент за сетью не имеет чекаута, и без
        // скопированного каталога знал бы о мире только описания инструментов.
        var library = SpecLibrary.Discover(null);

        Assert.That(library.Available, Is.True,
            "Spec/ обязан оказаться рядом со сборкой — иначе csproj перестал его копировать");
        Assert.Multiple(() =>
        {
            Assert.That(library.Sections().Count, Is.GreaterThan(100));
            Assert.That(library.ReadIndex(), Is.Not.Null.And.Not.Empty);
            Assert.That(library.TryReadSection("144", 0, out var text, out _, out _), Is.True);
            Assert.That(text, Does.Contain("MCP"));
        });
    }
}

}
