using System.Text;
using System.Text.RegularExpressions;

namespace HexLive.AgentHost;

public sealed record MashaPromptContext(
    string Text,
    int CharacterCount,
    int EstimatedTokenUpperBound,
    int RecalledFragments)
{
    public long ObjectiveRevision { get; init; }
    public long ExecutionPlanRevision { get; init; }
    public string ExecutionPlanId { get; init; } = "";
}

/// <summary>
/// Agent-readable memory workspace inspired by OpenClaw. Markdown is the portable
/// human-facing memory layer; state JSON is a bounded transactional sidecar and is
/// never injected into an LLM prompt.
/// </summary>
public sealed partial class MashaMemoryWorkspace
{
    public const int MaxPromptCharacters = 4800;
    public const int MemoryBudgetCharacters = 3200;
    public const int MaxRecallFragments = 4;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        AgentPromptFiles.Text("MashaMemoryWorkspace.01"), AgentPromptFiles.Text("MashaMemoryWorkspace.02"), AgentPromptFiles.Text("MashaMemoryWorkspace.03"), AgentPromptFiles.Text("MashaMemoryWorkspace.04"), AgentPromptFiles.Text("MashaMemoryWorkspace.05"), AgentPromptFiles.Text("MashaMemoryWorkspace.06"), AgentPromptFiles.Text("MashaMemoryWorkspace.07"), AgentPromptFiles.Text("MashaMemoryWorkspace.08"), AgentPromptFiles.Text("MashaMemoryWorkspace.09"), AgentPromptFiles.Text("MashaMemoryWorkspace.10"),
        AgentPromptFiles.Text("MashaMemoryWorkspace.11"), AgentPromptFiles.Text("MashaMemoryWorkspace.12"), AgentPromptFiles.Text("MashaMemoryWorkspace.13"), AgentPromptFiles.Text("MashaMemoryWorkspace.14"), AgentPromptFiles.Text("MashaMemoryWorkspace.15"), AgentPromptFiles.Text("MashaMemoryWorkspace.16"), AgentPromptFiles.Text("MashaMemoryWorkspace.17"), AgentPromptFiles.Text("MashaMemoryWorkspace.18"), AgentPromptFiles.Text("MashaMemoryWorkspace.19"), AgentPromptFiles.Text("MashaMemoryWorkspace.20"), AgentPromptFiles.Text("MashaMemoryWorkspace.21"),
        AgentPromptFiles.Text("MashaMemoryWorkspace.22"), AgentPromptFiles.Text("MashaMemoryWorkspace.23"), AgentPromptFiles.Text("MashaMemoryWorkspace.24"), AgentPromptFiles.Text("MashaMemoryWorkspace.25"), "with", "that", "this", "from", "have"
    };

    private readonly string _root;
    private readonly string _memoryRoot;
    private readonly string _worldRoot;
    private readonly string _dailyRoot;
    private readonly string _importRoot;

    public MashaMemoryWorkspace(string root)
    {
        _root = root;
        _memoryRoot = Path.Combine(root, "memory");
        _worldRoot = Path.Combine(_memoryRoot, "worlds");
        _dailyRoot = Path.Combine(_memoryRoot, "daily");
        _importRoot = Path.Combine(_memoryRoot, "imports");
        StateDirectory = Path.Combine(root, ".state");
        StatePath = Path.Combine(StateDirectory, "state.json");
        LegacyStatePath = Path.Combine(root, "memory-v1.json");
        EnsureDirectories();
    }

    public string StateDirectory { get; }
    public string StatePath { get; }
    public string LegacyStatePath { get; }

    public void WriteViews(MashaArchive archive, bool documentLockHeld = false)
    {
        EnsureDirectories();
        FileStream? writeLease;
        try { writeLease = documentLockHeld ? null : AcquireDocumentLock(); }
        catch (IOException) { return; } // Editor owns the document transaction; regenerate on the next turn.
        using var lease = writeLease;
        var soulPath = Path.Combine(_root, "SOUL.md");
        var userPath = Path.Combine(_root, "USER.md");
        if (!File.Exists(soulPath)) WriteAtomic(soulPath, Soul(archive));
        else MigrateDefaultSoul(soulPath);
        WriteGeneratedView(userPath, User(archive));
        var selfNotes = string.Join('\n', archive.CoreMemories.Where(x => x.Source == "model-self")
            .OrderByDescending(x => x.UpdatedAtUtc).Select(x => "- " + Clean(x.Value, 400)));
        if (selfNotes.Length > 0) WriteGeneratedView(soulPath, AgentPromptFiles.Text("MashaMemoryWorkspace.26") + selfNotes);
        WriteGeneratedView(Path.Combine(_root, "MEMORY.md"), LongTerm(archive));

        foreach (var world in archive.Worlds)
        {
            WriteGeneratedView(Path.Combine(_worldRoot, SafeName(world.Id) + ".md"), World(world, archive.Objective));
        }

        foreach (var group in archive.Worlds.SelectMany(world => world.Journal.Select(entry =>
                     new { World = world, Entry = entry }))
                 .GroupBy(x => x.Entry.CreatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd")))
        {
            var path = Path.Combine(_dailyRoot, group.Key + ".md");
            var text = new StringBuilder(AgentPromptFiles.Text("MashaMemoryWorkspace.27")).Append(Clean(archive.Identity.Name, 48)).Append(" — ").AppendLine(group.Key).AppendLine();
            foreach (var row in group.OrderBy(x => x.Entry.CreatedAtUtc))
            {
                text.Append("- [").Append(row.Entry.CreatedAtUtc.ToString("HH:mm"))
                    .Append("] (").Append(Clean(row.World.Label, 80)).Append(") ")
                    .AppendLine(Clean(row.Entry.Text, 400));
            }
            WriteGeneratedView(path, text.ToString());
        }
    }

    public MashaPromptContext BuildPrompt(
        MashaArchive archive,
        MashaWorldEpisode current,
        string recallQuery, MashaWorldHandle? world = null, bool includeLegacyUser = true)
    {
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string ActiveDocument(string name, int budget) => File.Exists(Path.Combine(_root, name))
            ? AtLineBoundary(string.Join('\n', File.ReadAllLines(Path.Combine(_root, name)).Where(line =>
                !MemoryDocumentEdits.IsSuppressedInContext(archive, world?.SpeakerKey ?? "", line))), budget) : "";
        var prompt = new StringBuilder();
        prompt.AppendLine(archive.Identity.Id == "masha" ? AgentPromptFiles.Text("MashaMemoryWorkspace.28") : AgentPromptFiles.Text("MashaMemoryWorkspace.29") + Clean(archive.Identity.Name, 48))
            .AppendLine(AgentPromptFiles.Text("MashaMemoryWorkspace.30"))
            .AppendLine();
        AppendSection(prompt, AgentPromptFiles.Text("MashaMemoryWorkspace.31"), ActiveDocument("SOUL.md", 720), 760);
        AppendSection(prompt, AgentPromptFiles.Text("MashaMemoryWorkspace.32"), string.Join('\n',
            archive.CoreMemories.Where(x => x.Source == "model-self").OrderByDescending(x => x.UpdatedAtUtc)
                .Take(2).Select(x => Clean(x.Value, 160))), 340);
        // Imported identity facts remain authoritative memories even when USER.md
        // already existed before the import. Never overwrite the user's document.
        AppendSection(prompt, AgentPromptFiles.Text("MashaMemoryWorkspace.33"), string.Join('\n',
            archive.CoreMemories.Where(x => x.Source is "molly-user" or "model-user")
                .OrderByDescending(x => x.Importance).Select(x => "- " + Clean(x.Value, 300))), 400);
        if (includeLegacyUser) AppendSection(prompt, AgentPromptFiles.Text("MashaMemoryWorkspace.34"), ActiveDocument("USER.md", 520), 560);
        AppendSection(prompt, AgentPromptFiles.Text("MashaMemoryWorkspace.35"), includeLegacyUser ? ActiveDocument("MEMORY.md", 850) : string.Join("\n", archive.CoreMemories.Where(m => m.Source == "model-core").Select(m => Clean(m.Value, 200))), 890);

        var currentText = new StringBuilder()
            .Append(AgentPromptFiles.Text("MashaMemoryWorkspace.36")).Append(Clean(current.Label, 100))
            .Append(AgentPromptFiles.Text("MashaMemoryWorkspace.37")).Append(current.Status)
            .Append(AgentPromptFiles.Text("MashaMemoryWorkspace.38")).Append(current.LanguageExposure).AppendLine(".");
        // Transient intentions are not current body facts. Re-injecting them here
        // reinforces stale states (e.g. unconsciousness) after the body recovers.

        foreach (var memory in RankMemories(current.Memories, recallQuery).Take(5))
        {
            included.Add(memory.Value);
            currentText.Append("- ").AppendLine(Clean(memory.Value, 260));
        }
        foreach (var entry in current.Journal.TakeLast(2))
        {
            included.Add(entry.Text);
            currentText.Append(AgentPromptFiles.Text("MashaMemoryWorkspace.39")).AppendLine(Clean(entry.Text, 260));
        }
        AppendSection(prompt, AgentPromptFiles.Text("MashaMemoryWorkspace.40"), currentText.ToString(), 920);

        var previous = archive.Worlds.Where(x => x.Id != current.Id)
            .OrderByDescending(x => x.LastSeenUtc).Take(6).ToArray();
        if (previous.Length > 0)
        {
            var list = string.Join('\n', previous.Select(x =>
                $"- {Clean(x.Label, 70)} — {x.Status}"));
            AppendSection(prompt, AgentPromptFiles.Text("MashaMemoryWorkspace.41"), list, 360);
        }

        var recalled = Recall(archive, current, recallQuery, included, includeLegacyUser, world?.SpeakerKey ?? "").ToArray();
        if (recalled.Length > 0)
        {
            var text = string.Join('\n', recalled.Select(x =>
                $"- [{Clean(x.Source, 55)}] {Clean(x.Text, 280)}"));
            AppendSection(prompt, AgentPromptFiles.Text("MashaMemoryWorkspace.42"), text, 760);
        }

        var bond = archive.PlayerBond;
        var relation = new HexLive.AgentCore.Studio.VoiceRelationship(new(bond.Familiarity, bond.Trust, bond.Affinity,
            world != null && archive.Speakers.TryGetValue(world.SpeakerKey, out var speaker) ? speaker.VoiceName : AgentPromptFiles.Text("MashaMemoryWorkspace.43"), null));
        var mandatory = relation.BuildPromptBlock() + "\n" +
            (bond.Trust <= .25f ? AgentPromptFiles.Text("MashaMemoryWorkspace.44") : bond.Trust >= .75f ? AgentPromptFiles.Text("MashaMemoryWorkspace.45") : AgentPromptFiles.Text("MashaMemoryWorkspace.46")) +
            (bond.Affinity <= -.25f ? AgentPromptFiles.Text("MashaMemoryWorkspace.47") : bond.Affinity >= .75f ? AgentPromptFiles.Text("MashaMemoryWorkspace.48") : AgentPromptFiles.Text("MashaMemoryWorkspace.49")) +
            "\n" + AgentGameTime.Describe(bond, world ?? new(current.Id, current.WorldKey, current.LastTick, -1)) + "\n" +
            AgentObjectivePolicy.Describe(archive.Objective, current.WorldKey, current.AvatarNpcId) + "\n";
        mandatory += DescribeExecutionPlan(archive, current.WorldKey, current.AvatarNpcId,
            Math.Max(0, Math.Min(2200, MaxPromptCharacters - mandatory.Length)));
        var bounded = mandatory + AtLineBoundary(prompt.ToString(), Math.Min(MemoryBudgetCharacters, MaxPromptCharacters - mandatory.Length));
        return new MashaPromptContext(
            bounded,
            bounded.Length,
            (bounded.Length + 1) / 2,
            recalled.Length) { ObjectiveRevision = archive.Objective?.Revision ?? 0,
                ExecutionPlanRevision = archive.ExecutionPlan?.Revision ?? 0, ExecutionPlanId = archive.ExecutionPlan?.Id ?? "" };
    }

    private static string DescribeExecutionPlan(MashaArchive archive, string worldKey, int npcId, int budget)
    {
        var plan = archive.ExecutionPlan;
        if (plan == null || plan.WorldKey != worldKey || plan.NpcId != npcId) return "";
        var text = new StringBuilder();
        text.AppendLine($"Исполняемый план: {Clean(plan.Status, 16)}, позиция {plan.Cursor}/{plan.Steps.Length}, версия {plan.Revision}. " +
            $"Причина: {Clean(plan.Reason, 96)}. Команда: {Clean(plan.Command?.Status ?? "none", 16)}.");
        if (plan.Command?.Status == "failed")
            text.AppendLine($"Отказ сервера: {Clean(plan.Command.Reason, 96)}. Измени подход; не повторяй тот же шаг без новых оснований.");
        if (plan.Cursor < plan.Steps.Length)
        {
            text.AppendLine("Следующие шаги очереди (ещё не завершены):");
            foreach (var step in plan.Steps.Skip(plan.Cursor).Take(3))
                text.AppendLine($"{Clean(step.Id, 48)}: {Clean(step.Tool, 32)} {Clean(step.Arguments.GetRawText(), 220)}");
        }
        text.AppendLine("Подтверждённые completed квитанции этой цели (от новых к старым; уже выполнено, не повторять):");
        foreach (var row in archive.ExecutionProgress.Where(p => p.WorldKey == worldKey && p.NpcId == npcId).TakeLast(8).Reverse())
            text.AppendLine($"#{row.Sequence} {Clean(row.Step.Id, 48)}: {Clean(row.Step.Tool, 32)} {Clean(row.Step.Arguments.GetRawText(), 220)}");
        text.AppendLine("Сопоставь квитанции со свежим миром: цель могла уже выполниться. Разговор не заменяет план; accepted не значит completed.");
        return AtLineBoundary(text.ToString(), budget);
    }

    public string PreserveImport(string sourceRoot, string fingerprint, string label)
    {
        var suffix = fingerprint.Length > 12 ? fingerprint[..12] : fingerprint;
        var destination = Path.Combine(_importRoot, "molly-" + SafeName(label) + "-" + suffix);
        if (Directory.Exists(destination)) return destination;
        Directory.CreateDirectory(destination);
        RestrictDirectory(destination);

        foreach (var source in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(source);
            if (name.Equals("inventory.txt", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("clothing_prefs.txt", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("state.txt", StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = Path.GetRelativePath(sourceRoot, source);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, false);
            RestrictFile(target);
        }
        return destination;
    }

    private IEnumerable<RecallCandidate> Recall(
        MashaArchive archive,
        MashaWorldEpisode current,
        string query,
        HashSet<string> included, bool includeLegacyUser, string speakerKey)
    {
        var terms = Terms(query);
        if (terms.Count == 0) return Array.Empty<RecallCandidate>();
        var candidates = new List<RecallCandidate>();

        foreach (var world in archive.Worlds)
        {
            foreach (var memory in world.Memories)
                Add(memory.Value, world.Label, memory.UpdatedAtUtc, memory.Importance,
                    world.Id == current.Id);
            foreach (var entry in world.Journal)
                Add(entry.Text, world.Label + AgentPromptFiles.Text("MashaMemoryWorkspace.50"), entry.CreatedAtUtc, 0.55f,
                    world.Id == current.Id);
        }

        foreach (var root in includeLegacyUser ? ImportRoots() : Array.Empty<string>())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
                         .Take(256))
            {
                var count = 0;
                foreach (var line in File.ReadLines(file))
                {
                    if (++count > 2000) break;
                    var text = line.Trim().TrimStart('-', '*').Trim();
                    if (text.Length < 12 || text.StartsWith('#') || text.StartsWith("<!--")) continue;
                    Add(text, AgentPromptFiles.Text("MashaMemoryWorkspace.51") + Path.GetFileName(file), File.GetLastWriteTimeUtc(file), 0.5f, false);
                }
            }
        }

        return candidates.OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.When)
            .Take(MaxRecallFragments);

        void Add(string text, string source, DateTimeOffset when, float importance, bool sameWorld)
        {
            if (MemoryDocumentEdits.IsSuppressedInContext(archive, speakerKey, text)) return;
            text = Clean(text, 400);
            if (text.Length == 0 || included.Contains(text)) return;
            var words = Terms(text);
            var overlap = terms.Count(words.Contains);
            if (overlap == 0) return;
            included.Add(text);
            candidates.Add(new RecallCandidate(
                text, source, when,
                overlap * 10f + importance * 2f + (sameWorld ? 1.5f : 0f)));
        }
    }

    private IEnumerable<string> ImportRoots()
    {
        yield return _importRoot;
        // Compatibility with the one-off iPhone extraction made before workspace v2.
        yield return Path.Combine(_root, "imports");
    }

    private static IEnumerable<PortableMemory> RankMemories(
        IEnumerable<PortableMemory> memories, string query)
    {
        var terms = Terms(query);
        return memories.OrderByDescending(memory =>
                Terms(memory.Value).Count(terms.Contains) * 10f + memory.Importance * 2f)
            .ThenByDescending(memory => memory.UpdatedAtUtc);
    }

    private static HashSet<string> Terms(string value)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in WordRegex().Matches(value.ToLowerInvariant()))
        {
            var term = match.Value;
            if (StopWords.Contains(term)) continue;
            if (term.Length > 6) term = term[..5];
            terms.Add(term);
        }
        return terms;
    }

    private static string Soul(MashaArchive archive) => string.Format(
        AgentPromptFiles.Read("soul.md"), Clean(archive.Identity.Name, 48), archive.Identity.Age,
        string.Join(", ", archive.Identity.Traits));

    private static void MigrateDefaultSoul(string path)
    {
        var oldTemplate = AgentPromptFiles.Text("MashaMemoryWorkspace.52") +
            AgentPromptFiles.Text("MashaMemoryWorkspace.53") +
            AgentPromptFiles.Text("MashaMemoryWorkspace.54");
        var text = File.ReadAllText(path);
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.Contains(oldTemplate, StringComparison.Ordinal)) return;
        var backup = path + ".pre-studio-" + Guid.NewGuid().ToString("N") + ".bak";
        File.Copy(path, backup, false);
        RestrictFile(backup);
        WriteAtomic(path, normalized.Replace(oldTemplate,
            AgentPromptFiles.Text("MashaMemoryWorkspace.55"), StringComparison.Ordinal));
    }

    private static string User(MashaArchive archive)
    {
        var text = new StringBuilder(AgentPromptFiles.Text("MashaMemoryWorkspace.56"));
        var speaker = archive.PrimarySpeakerKey != null && archive.Speakers.TryGetValue(archive.PrimarySpeakerKey, out var primary) ? primary : null;
        var bond = speaker?.Bond ?? archive.PlayerBond;
        foreach (var memory in (speaker?.Facts ?? archive.CoreMemories).Where(x => x.Source is "molly-user" or "model-user"))
            text.Append("- ").AppendLine(Clean(memory.Value, 300));
        text.Append(AgentPromptFiles.Text("MashaMemoryWorkspace.57")).Append(Math.Round(bond.Familiarity * 100))
            .Append(AgentPromptFiles.Text("MashaMemoryWorkspace.58")).Append(Math.Round(bond.Trust * 100))
            .Append(AgentPromptFiles.Text("MashaMemoryWorkspace.59")).Append(Math.Round(bond.Affinity * 100))
            .AppendLine("%.");
        return text.ToString();
    }

    private static string LongTerm(MashaArchive archive)
    {
        var text = new StringBuilder(AgentPromptFiles.Text("MashaMemoryWorkspace.60"));
        foreach (var memory in archive.CoreMemories.Where(x => x.Source is not ("molly-user" or "model-user" or "model-self"))
                     .OrderByDescending(x => x.Importance).ThenByDescending(x => x.UpdatedAtUtc))
            text.Append("- ").AppendLine(Clean(memory.Value, 400));
        return text.ToString();
    }

    private static string World(MashaWorldEpisode world, AgentObjective? objective)
    {
        var text = new StringBuilder("# ").AppendLine(Clean(world.Label, 100)).AppendLine()
            .Append("- ID: `").Append(world.Id).AppendLine("`")
            .Append(AgentPromptFiles.Text("MashaMemoryWorkspace.61")).AppendLine(world.Game)
            .Append(AgentPromptFiles.Text("MashaMemoryWorkspace.62")).AppendLine(world.Status)
            .Append("- Hexkufa: ").AppendLine(world.LanguageExposure.ToString())
            .AppendLine().AppendLine(AgentPromptFiles.Text("MashaMemoryWorkspace.63")).AppendLine();
        foreach (var memory in world.Memories.OrderByDescending(x => x.Importance))
            text.Append("- ").AppendLine(Clean(memory.Value, 400));
        text.AppendLine().AppendLine(AgentPromptFiles.Text("MashaMemoryWorkspace.64")).AppendLine()
            .AppendLine(Clean(world.LastIntentSummary, 400));
        if (objective != null && objective.WorldKey == world.WorldKey && objective.AvatarNpcId == world.AvatarNpcId)
            text.AppendLine().AppendLine(AgentObjectivePolicy.Describe(objective, world.WorldKey, world.AvatarNpcId));
        return text.ToString();
    }

    private static void AppendSection(StringBuilder target, string title, string value, int budget)
    {
        value = AtLineBoundary(value.Trim(), budget);
        if (value.Length == 0) return;
        target.Append("## ").AppendLine(title).AppendLine(value).AppendLine();
    }

    private static string Read(string path, int budget) =>
        File.Exists(path) ? AtLineBoundary(File.ReadAllText(path), budget) : string.Empty;

    private static string AtLineBoundary(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        var marker = AgentPromptFiles.Text("MashaMemoryWorkspace.65");
        var limit = maximum - marker.Length;
        if (limit <= 0) return value[..maximum];
        var cut = value.LastIndexOf('\n', limit - 1, limit);
        if (cut < limit / 2) cut = limit;
        return value[..cut].TrimEnd() + marker;
    }

    private static string Clean(string? value, int maximum)
    {
        var clean = string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= maximum ? clean : clean[..maximum].TrimEnd() + "…";
    }

    private static string SafeName(string value)
    {
        var chars = value.ToLowerInvariant().Select(x =>
            char.IsLetterOrDigit(x) || x is '-' or '_' ? x : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private void EnsureDirectories()
    {
        foreach (var directory in new[]
                 { _root, _memoryRoot, _worldRoot, _dailyRoot, _importRoot, StateDirectory })
        {
            Directory.CreateDirectory(directory);
            RestrictDirectory(directory);
        }
    }

    public FileStream AcquireDocumentLock()
    {
        var path = Path.Combine(_root, ".documents.lock");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("LinkedDocumentLockNotAllowed");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content.TrimEnd() + "\n");
            RestrictFile(temp);
            File.Move(temp, path, true);
            RestrictFile(path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private void WriteGeneratedView(string path, string content)
    {
        var stamps = Path.Combine(StateDirectory, "view-revisions");
        RejectLink(_root); RejectLink(StateDirectory); RejectLink(stamps);
        Directory.CreateDirectory(stamps); RestrictDirectory(stamps);
        var relative = Path.GetRelativePath(_root, path);
        var stamp = Path.Combine(stamps, Hash(Encoding.UTF8.GetBytes(relative)) + ".sha256");
        RejectLink(stamp);
        const string start = "<!-- agent-studio:notes:start -->";
        const string end = "<!-- agent-studio:notes:end -->";
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("LinkedMemoryDocumentNotAllowed");
        var original = File.Exists(path) ? File.ReadAllText(path) : "";
        var normalized = MemoryDocumentEdits.WithoutDuplicate(original);
        var authoredEdit = MemoryDocumentEdits.HasPendingVersion(_root, relative, original);
        var generated = content.Replace(start, "").Replace(end, "").Trim();
        var from = normalized.IndexOf(start, StringComparison.Ordinal);
        var to = normalized.IndexOf(end, StringComparison.Ordinal);
        var manual = normalized.TrimEnd();
        string result;
        if (from >= 0 && to >= from + start.Length)
        {
            var previous = normalized[(from + start.Length)..to].Trim();
            var expected = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : "";
            var preserved = authoredEdit || expected == "section:" + Hash(Encoding.UTF8.GetBytes(previous)) || previous == generated
                ? "" : AgentPromptFiles.Text("MashaMemoryWorkspace.66") + previous + "\n\n";
            result = normalized[..from] + preserved + start + "\n" + generated + "\n" + end + normalized[(to + end.Length)..];
        }
        else
        {
            // Legacy documents have no stamp: preserve them, then append an explicitly managed section.
            if (MemoryDocumentEdits.Normalize(manual) == MemoryDocumentEdits.Normalize(generated)) manual = "";
            result = manual + (manual.Length == 0 ? "" : "\n\n") + start + "\n" + generated + "\n" + end + "\n";
        }
        if (result.TrimEnd() != original.TrimEnd())
        {
            if (original.Length > 0)
            {
                var history = Path.Combine(_root, ".history", Hash(Encoding.UTF8.GetBytes(relative)));
                RejectLink(Path.Combine(_root, ".history")); RejectLink(history);
                Directory.CreateDirectory(history);
                var backup = Path.Combine(history, Guid.NewGuid().ToString("N") + ".md");
                WriteAtomic(backup, original);
            }
            WriteAtomic(path, result);
        }
        WriteAtomic(stamp, "section:" + Hash(Encoding.UTF8.GetBytes(generated)));
        static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        static void RejectLink(string candidate)
        {
            if ((File.Exists(candidate) || Directory.Exists(candidate)) &&
                (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("LinkedMemoryDocumentNotAllowed");
        }
    }

    private static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed record RecallCandidate(
        string Text, string Source, DateTimeOffset When, float Score);

    [GeneratedRegex(@"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
