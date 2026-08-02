using System;
using System.IO;

namespace HexLive.Simulation.Soak
{

/// <summary>
/// Корень чекаута, из которого собран ЭТОТ бинарь — подъёмом вверх до каталога
/// с <c>SimData/simdata.json</c>.
/// <para>
/// Не константа и не переменная окружения намеренно: проба, нацеленная на главный
/// чекаут во время работы в worktree, меряет чужой код и читается как «моя правка
/// ничего не изменила». Отдельно важно для golden_trace.sh — он гоняет ДВА
/// чекаута сразу, и каждый обязан читать свой simdata.
/// </para>
/// </summary>
public static class RepoPaths
{
    private static readonly Lazy<string> LazyRoot = new Lazy<string>(Discover);

    public static string Root => LazyRoot.Value;

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
            "Не найден корень репозитория подъёмом от " + AppContext.BaseDirectory +
            ". Укажи --simdata явно.");
    }
}

}
