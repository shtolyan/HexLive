using System;
using System.IO;

namespace HexLive.Simulation.Tests
{

/// <summary>
/// Где лежит чекаут, из которого собрали ЭТУ сборку.
/// <para>
/// Ищется подъёмом вверх от каталога сборки до первого, где есть
/// <c>SimData/simdata.json</c>. Не переменная окружения и не константа: CLAUDE.md
/// отдельно предупреждает, что проба, нацеленная на главный чекаут во время работы
/// в worktree (<c>.claude/worktrees/&lt;name&gt;/</c>), меряет ЧУЖОЙ код и читается как
/// «моя правка ничего не изменила». Подъём от BaseDirectory не может ошибиться:
/// bin лежит внутри того самого чекаута.
/// </para>
/// </summary>
public static class RepoPaths
{
    private static readonly Lazy<string> LazyRoot = new Lazy<string>(Discover);

    public static string Root => LazyRoot.Value;

    public static string SimData => Path.Combine(Root, "SimData", "simdata.json");

    /// <summary>Корень исходников симуляции — то, что сканируют линты и гейты.</summary>
    public static string SimulationSources =>
        Path.Combine(Root, "Assets", "HexLive", "Simulation");

    private static string Discover()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SimData", "simdata.json")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Не найден корень репозитория: подъём от " + AppContext.BaseDirectory +
            " не встретил каталога с SimData/simdata.json. Файл коммитится — " +
            "если его нет, чекаут неполный.");
    }
}

}
