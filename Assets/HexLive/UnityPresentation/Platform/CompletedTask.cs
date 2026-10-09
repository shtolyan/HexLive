#nullable enable
using System;
using System.Threading.Tasks;

namespace HexLive.UnityPresentation.Platform
{

/// <summary>
/// §168.4: reading the result of a task that is ALREADY finished.
/// <para>
/// <c>task.Result</c> is banned for the web build (Tools/webgl/BannedSymbols.txt)
/// because on the browser's single thread it is a forever-hang whenever the task
/// is still running. Polling code that checked <c>IsCompleted</c> first is fine —
/// and says so by calling this instead, which refuses to block rather than
/// quietly becoming the hang the ban exists for.
/// </para>
/// </summary>
public static class CompletedTask
{
    public static T Result<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            throw new InvalidOperationException(
                "§168.4: CompletedTask.Result on a running task would block the only thread.");
        }

#pragma warning disable RS0030 // the one sanctioned read: completion was checked above
        return task.Result;
#pragma warning restore RS0030
    }
}

}
