using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HexLive.AgentCore.Studio;

namespace HexLive.AgentHost;

public sealed record VoiceArtifact(byte[] Wav);

public interface IAgentProviders : IDisposable
{
    Task<CompanionDecision> DecideAsync(string trigger, string stateJson, string memoryContext,
        string transcript, IReadOnlyList<string> recentConversation, CancellationToken cancellationToken);
    Task<VoiceArtifact> SynthesizeAsync(string text, CancellationToken cancellationToken);
}

/// <summary>Cloud provider boundary. Raw voice and transcripts never leave this object in logs.</summary>
public sealed class AgentProviders : IAgentProviders
{
    private static readonly HashSet<string> AllowedEmotions = new(StringComparer.OrdinalIgnoreCase)
        { "neutral", "warm", "curious", "happy", "sad", "tense", "angry", "afraid", "tired" };
    private static readonly HashSet<string> AllowedReactions = new(StringComparer.OrdinalIgnoreCase)
        { "None", "Warm", "Neutral", "Tense", "Hostile" };
    private static readonly HashSet<string> AllowedTools = new(StringComparer.Ordinal)
    {
        "move_to", "interact", "craft_item", "stop", "talk_to", "aid_person",
        "treat_limbs", "self_action", "carry_person", "put_down_person",
        "put_person_in_bed", "manage_inventory", "attack_mob", "merge_camps", "request_item",
        "query_known_objects", "transfer_inventory", "rest_until"
    };
    public static bool IsAllowedTool(string name) => AllowedTools.Contains(name);

    private readonly AgentProviderOptions _options;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };

    private readonly IModelAdapter? _modelAdapter;
    private readonly ModelSelection? _modelSelection;
    private readonly IVoiceAdapter? _voiceAdapter;
    private readonly VoiceSelection? _voiceSelection;

    public AgentProviders(AgentProviderOptions options, IModelAdapter? modelAdapter = null,
        ModelSelection? modelSelection = null, IVoiceAdapter? voiceAdapter = null, VoiceSelection? voiceSelection = null)
    {
        _options = options;
        _modelAdapter = modelAdapter; _modelSelection = modelSelection;
        _voiceAdapter = voiceAdapter; _voiceSelection = voiceSelection;
        if ((modelAdapter == null) != (modelSelection == null)) throw new ArgumentException("ModelAdapterSelectionRequired");
        if ((voiceAdapter == null) != (voiceSelection == null)) throw new ArgumentException("VoiceAdapterSelectionRequired");
    }

    public async Task DoctorAsync(CancellationToken cancellationToken)
    {
        if (_options.FakeProviders) return;
        if (_options.LlmBackend == "codex")
        {
            await CodexDecisionRunner.CheckLoginAsync(_options.CodexExecutable, cancellationToken);
            Console.WriteLine($"Codex: ChatGPT subscription login; model {CodexDecisionRunner.Model}, reasoning {CodexDecisionRunner.ReasoningEffort} (no generation requested)");
        }
        using var xai = new HttpRequestMessage(HttpMethod.Get, "https://api.x.ai/v1/api-key");
        xai.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.XaiKey);
        using var voice = new HttpRequestMessage(HttpMethod.Get,
            "https://api.elevenlabs.io/v1/voices/" + Uri.EscapeDataString(_options.ElevenLabsVoiceId));
        voice.Headers.TryAddWithoutValidation("xi-api-key", _options.ElevenLabsKey);
        foreach (var (provider, request) in new[] { ("XAI", xai), ("ElevenLabs voice", voice) })
        {
            if (provider == "XAI" && _options.LlmBackend == "codex") continue;
            using var response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            Console.WriteLine($"{provider}: HTTP {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Provider account check failed (no generation was requested).");
        }
    }

    public async Task<CompanionDecision> DecideAsync(
        string trigger,
        string stateJson,
        string memoryContext,
        string transcript,
        IReadOnlyList<string> recentConversation,
        CancellationToken cancellationToken)
    {
        if (_options.FakeProviders)
        {
            return new CompanionDecision
            {
                Speech = trigger == "voice" ? AgentPromptFiles.Text("AgentProviders.01") : string.Empty,
                RelationshipAssessment = trigger == "voice" ? new(false, RelationshipDirection.Unchanged, RelationshipDirection.Unchanged, false, AgentPromptFiles.Text("AgentProviders.02")) : null,
                Emotion = "curious",
                Reaction = trigger == "voice" ? "Neutral" : "None",
                IntentSummary = AgentPromptFiles.Text("AgentProviders.03")
            };
        }

        var system = AgentPromptBuilder.Build(_options.DialogueStyleId, memoryContext, transcript);

        using var stateDocument = JsonDocument.Parse(stateJson);
        var user = JsonSerializer.Serialize(new
        {
            trigger,
            worldAndBody = stateDocument.RootElement,
            hasNewPlayerMessage = trigger == "voice" && !string.IsNullOrWhiteSpace(transcript),
            playerSpeech = trigger == "voice" ? transcript : "",
            recentConversation = new { alreadyProcessed = true, messages = recentConversation }
        });
        if (_modelAdapter != null)
        {
            var answer = await _modelAdapter.CompleteAsync(_modelSelection!,
                new ModelRequest(system + "\nExact response contract:\n" + JsonSerializer.Serialize(DecisionResponseFormat()),
                    memoryContext, user), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return ParseDecision(answer.DecisionJson, trigger);
        }
        if (_options.LlmBackend == "codex")
        {
            var codexJson = await CodexDecisionRunner.DecideAsync(_options.CodexExecutable,
                system + "\nDo not use tools. Return only the requested decision JSON.\n" +
                "Exact response contract:\n" + JsonSerializer.Serialize(DecisionResponseFormat()) + "\n" +
                "<memory>\n" + memoryContext + "\n</memory>\n" + user, cancellationToken);
            return ParseDecision(codexJson, trigger);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.ai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.XaiKey);
        request.Content = JsonContent.Create(new
        {
            model = _options.XaiModel,
            temperature = 0.7,
            response_format = DecisionResponseFormat(),
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "system", content = memoryContext },
                new { role = "user", content = user }
            }
        });

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var envelope = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var json = envelope.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "{}";
        return ParseDecision(json, trigger);
    }

    public static CompanionDecision ParseDecision(string json, string trigger)
    {
        json = StripFence(json);
        ValidateDecisionSchema(json);
        var decision = JsonSerializer.Deserialize<CompanionDecision>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = false })
            ?? throw new InvalidDataException("Model returned an empty companion decision.");
        Validate(decision, trigger);
        return decision;
    }

    private static object DecisionResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "hexlive_companion_turn",
            strict = true,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    memoryRequests = new { type = "array", maxItems = 2, items = new { type = "object", additionalProperties = false,
                        properties = new { operation = new { type = "string", @enum = new[] { "memory.search", "memory.read", "spec.read", "skills.list", "skills.read", "recipes.read", "build.read" } },
                            arguments = new { type = "object", additionalProperties = true } }, required = new[] { "operation", "arguments" } } },
                    memorySources = new { type = "array", maxItems = 16, items = new { type = "string" } },
                    speech = new { type = "string", maxLength = 600 },
                    emotion = new
                    {
                        type = "string",
                        @enum = AllowedEmotions.OrderBy(x => x, StringComparer.Ordinal).ToArray()
                    },
                    action = new
                    {
                        anyOf = new object[]
                        {
                            new { type = "null" },
                            new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    tool = new
                                    {
                                        type = "string",
                                        @enum = AllowedTools.OrderBy(x => x, StringComparer.Ordinal).ToArray()
                                    },
                                    arguments = new { type = "object", additionalProperties = true }
                                },
                                required = new[] { "tool", "arguments" }
                            }
                        }
                    },
                    relationshipAssessment = new
                    {
                        anyOf = new object[] {
                            new { type = "null" },
                            new { type = "object", additionalProperties = false,
                                properties = new {
                                    learnedSomethingSignificant = new { type = "boolean" },
                                    trust = new { type = "string", @enum = new[] { "Decrease", "Unchanged", "Increase" } },
                                    sympathy = new { type = "string", @enum = new[] { "Decrease", "Unchanged", "Increase" } },
                                    seriousHarm = new { type = "boolean" },
                                    reason = new { type = "string", minLength = 1, maxLength = 240 },
                                    voiceName = new { type = new[] { "string", "null" }, minLength = 1, maxLength = 48 },
                                    namingReason = new { type = new[] { "string", "null" }, minLength = 1, maxLength = 240 }
                                },
                                required = new[] { "learnedSomethingSignificant", "trust", "sympathy", "seriousHarm", "reason", "voiceName", "namingReason" }
                            }
                        }
                    },
                    reaction = new
                    {
                        type = "string",
                        @enum = AllowedReactions.OrderBy(x => x, StringComparer.Ordinal).ToArray()
                    },
                    intentSummary = new { type = "string", maxLength = 240 },
                    objectiveUpdate = new
                    {
                        anyOf = new object[] {
                            new { type = "null" },
                            new { type = "object", additionalProperties = false,
                                properties = new {
                                    operation = new { type = "string", @enum = new[] { "set", "pause", "resume", "complete", "clear" } },
                                    text = new { type = "string", maxLength = AgentObjectivePolicy.TextLimit },
                                    reason = new { type = "string", minLength = 1, maxLength = AgentObjectivePolicy.ReasonLimit }
                                }, required = new[] { "operation", "text", "reason" }
                            }
                        }
                    },
                    executionPlanUpdate = new
                    {
                        anyOf = new object[] {
                            new { type = "null" },
                            new { type = "object", additionalProperties = false,
                                properties = new {
                                    operation = new { type = "string", @enum = new[] { "replace", "pause", "resume", "cancel" } },
                                    reason = new { type = "string", minLength = 1, maxLength = 96 },
                                    steps = new { type = "array", maxItems = AgentExecutionPlanPolicy.MaxSteps,
                                        items = new { type = "object", additionalProperties = false,
                                            properties = new { id = new { type = "string", minLength = 1, maxLength = 96 },
                                                tool = new { type = "string", @enum = AllowedTools.Where(t => t != "query_known_objects").ToArray() },
                                                arguments = new { type = "object", additionalProperties = true },
                                                condition = new { type = new[] { "object", "null" }, additionalProperties = false,
                                                    properties = new {
                                                        path = new { type = "string", @enum = new[] { "inventorySummary.freeSlots", "bodyNeeds.energy.value", "bodyNeeds.stamina.value", "bodyNeeds.hunger", "bodyNeeds.thirst" } },
                                                        @operator = new { type = "string", @enum = new[] { "gte", "lte" } },
                                                        value = new { type = "number" }, onFalseStepId = new { type = "string", maxLength = 96 } },
                                                    required = new[] { "path", "operator", "value", "onFalseStepId" } } },
                                            required = new[] { "id", "tool", "arguments" } } }
                                }, required = new[] { "operation", "reason", "steps" } }
                        }
                    },
                    memoryUpserts = new
                    {
                        type = "array",
                        maxItems = 3,
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                key = new { type = "string", minLength = 1, maxLength = 64 },
                                value = new { type = "string", minLength = 1, maxLength = 400 },
                                importance = new { type = "number", minimum = 0, maximum = 1 }
                            },
                            required = new[] { "key", "value", "importance" }
                        }
                    },
                    journalText = new { type = "string", maxLength = 400 }
                },
                required = new[]
                {
                    "speech", "emotion", "action", "reaction", "relationshipAssessment", "intentSummary",
                    "memoryUpserts", "journalText", "memoryRequests", "memorySources", "objectiveUpdate", "executionPlanUpdate"
                }
            }
        }
    };

    private static void ValidateDecisionSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Companion decision must be one JSON object.");

        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "speech", "emotion", "action", "reaction", "relationshipAssessment", "intentSummary",
            "memoryUpserts"
        };
        var allowed = new HashSet<string>(required, StringComparer.Ordinal) { "journalText", "memoryRequests", "memorySources", "objectiveUpdate", "executionPlanUpdate" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidDataException("Companion decision contains an unknown or duplicate field.");
        }
        if (!required.IsSubsetOf(seen) ||
            root.GetProperty("speech").ValueKind != JsonValueKind.String ||
            root.GetProperty("emotion").ValueKind != JsonValueKind.String ||
            root.GetProperty("reaction").ValueKind != JsonValueKind.String ||
            root.GetProperty("intentSummary").ValueKind != JsonValueKind.String ||
            root.GetProperty("memoryUpserts").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Companion decision does not match the required schema.");
        if (root.TryGetProperty("journalText", out var journalText) &&
            journalText.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Companion journal entry must be a string.");

        if (root.TryGetProperty("objectiveUpdate", out var objective) && objective.ValueKind != JsonValueKind.Null)
            RequireExactObject(objective, new Dictionary<string, JsonValueKind>(StringComparer.Ordinal)
            {
                ["operation"] = JsonValueKind.String, ["text"] = JsonValueKind.String,
                ["reason"] = JsonValueKind.String
            });

        var assessment = root.GetProperty("relationshipAssessment");
        if (root.TryGetProperty("executionPlanUpdate", out var planUpdate) && planUpdate.ValueKind != JsonValueKind.Null)
        {
            RequireExactObject(planUpdate, new Dictionary<string, JsonValueKind>(StringComparer.Ordinal)
                { ["operation"] = JsonValueKind.String, ["reason"] = JsonValueKind.String, ["steps"] = JsonValueKind.Array });
            foreach (var step in planUpdate.GetProperty("steps").EnumerateArray())
            {
                var fields = new Dictionary<string, JsonValueKind>(StringComparer.Ordinal)
                    { ["id"] = JsonValueKind.String, ["tool"] = JsonValueKind.String, ["arguments"] = JsonValueKind.Object };
                if (step.TryGetProperty("condition", out var condition))
                {
                    fields["condition"] = condition.ValueKind == JsonValueKind.Null ? JsonValueKind.Null : JsonValueKind.Object;
                    if (condition.ValueKind != JsonValueKind.Null)
                        RequireExactObject(condition, new Dictionary<string, JsonValueKind>(StringComparer.Ordinal)
                        { ["path"] = JsonValueKind.String, ["operator"] = JsonValueKind.String,
                            ["value"] = JsonValueKind.Number, ["onFalseStepId"] = JsonValueKind.String });
                }
                RequireExactObject(step, fields);
            }
        }
        if (assessment.ValueKind != JsonValueKind.Null)
        {
            if (assessment.ValueKind != JsonValueKind.Object) throw new InvalidDataException("InvalidRelationshipAssessment");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var requiredAssessment = new[] { "learnedSomethingSignificant", "trust", "sympathy", "seriousHarm", "reason" };
            foreach (var property in assessment.EnumerateObject())
            {
                if (!names.Add(property.Name) || (!requiredAssessment.Contains(property.Name) &&
                    property.Name is not ("voiceName" or "namingReason")))
                    throw new InvalidDataException("InvalidRelationshipAssessment");
                var valid = property.Name switch {
                    "learnedSomethingSignificant" or "seriousHarm" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "voiceName" or "namingReason" => property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                    _ => property.Value.ValueKind == JsonValueKind.String
                };
                if (!valid) throw new InvalidDataException("InvalidRelationshipAssessment");
            }
            if (requiredAssessment.Any(n => !names.Contains(n))) throw new InvalidDataException("InvalidRelationshipAssessment");
        }

        var action = root.GetProperty("action");
        if (action.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
            throw new InvalidDataException("Companion action must be null or an object.");
        if (action.ValueKind == JsonValueKind.Object)
        {
            RequireExactObject(action, new Dictionary<string, JsonValueKind>(StringComparer.Ordinal)
            {
                ["tool"] = JsonValueKind.String,
                ["arguments"] = JsonValueKind.Object
            });
        }

        foreach (var memory in root.GetProperty("memoryUpserts").EnumerateArray())
        {
            if (memory.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Memory update must be an object.");
            RequireExactObject(memory, new Dictionary<string, JsonValueKind>(StringComparer.Ordinal)
            {
                ["key"] = JsonValueKind.String,
                ["value"] = JsonValueKind.String,
                ["importance"] = JsonValueKind.Number
            });
        }
    }

    private static void RequireExactObject(
        JsonElement value, IReadOnlyDictionary<string, JsonValueKind> fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Nested companion decision must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.TryGetValue(property.Name, out var expected) ||
                !seen.Add(property.Name) || property.Value.ValueKind != expected)
                throw new InvalidDataException("Nested companion decision object has an invalid field.");
        }
        if (seen.Count != fields.Count)
            throw new InvalidDataException("Nested companion decision object is missing a field.");
    }

    public async Task<VoiceArtifact> SynthesizeAsync(
        string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_voiceAdapter != null)
            return new VoiceArtifact(await _voiceAdapter.SynthesizeWavAsync(_voiceSelection!, text, cancellationToken));
        byte[] pcm;
        if (_options.FakeProviders)
        {
            pcm = FakePcm(text);
        }
        else
        {
            var url = $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(_options.ElevenLabsVoiceId)}" +
                      "?output_format=pcm_24000";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("xi-api-key", _options.ElevenLabsKey);
            request.Content = JsonContent.Create(new
            {
                text,
                model_id = _options.ElevenLabsModel,
                voice_settings = new { stability = 0.55, similarity_boost = 0.78 }
            });
            using var response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            pcm = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            // pcm_44100 is restricted to higher ElevenLabs plans. The authored Molly
            // key supports raw 24 kHz PCM, so resample locally and always expose the
            // format required by HexLive/FMOD without adding an MP3 decoder dependency.
            pcm = ResamplePcm16Mono(pcm, 24000, 44100);
        }

        return new VoiceArtifact(WrapPcm16Mono44100(pcm));
    }

    private static byte[] ResamplePcm16Mono(byte[] pcm, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate || pcm.Length < 4) return pcm;
        var sourceSamples = pcm.Length / 2;
        var targetSamples = Math.Max(1,
            (int)Math.Round(sourceSamples * (double)targetRate / sourceRate));
        var output = new byte[targetSamples * 2];
        for (var i = 0; i < targetSamples; i++)
        {
            var position = i * (double)sourceRate / targetRate;
            var lower = Math.Min(sourceSamples - 1, (int)position);
            var upper = Math.Min(sourceSamples - 1, lower + 1);
            var fraction = position - lower;
            var a = BitConverter.ToInt16(pcm, lower * 2);
            var b = BitConverter.ToInt16(pcm, upper * 2);
            var sample = (short)Math.Clamp(
                Math.Round(a + (b - a) * fraction), short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(output.AsSpan(i * 2, 2), sample);
        }
        return output;
    }

    private static void Validate(CompanionDecision decision, string trigger)
    {
        if (decision.MemoryRequests == null || decision.MemorySources == null || decision.MemoryRequests.Count > 2 || decision.MemorySources.Count > 16)
            throw new InvalidDataException("InvalidMemoryOperations");
        foreach (var request in decision.MemoryRequests)
            if (request == null || !(request.Operation is "memory.search" or "memory.read" || AgentReferenceReader.Allowed(request.Operation)) || request.Arguments.ValueKind != JsonValueKind.Object || request.Arguments.GetRawText().Length > 4000)
                throw new InvalidDataException("InvalidMemoryOperation");
        if (decision.MemoryRequests.Count > 0)
        {
            decision.ObjectiveUpdate = null; decision.ExecutionPlanUpdate = null; decision.Speech = ""; decision.Action = null; decision.RelationshipAssessment = null;
            decision.Reaction = "None"; decision.MemoryUpserts.Clear(); decision.JournalText = ""; decision.IntentSummary = "";
            return;
        }
        if (decision.ObjectiveUpdate != null) AgentObjectivePolicy.ValidateUpdate(decision.ObjectiveUpdate);
        if (decision.ExecutionPlanUpdate != null) AgentExecutionPlanPolicy.ValidateUpdate(decision.ExecutionPlanUpdate);
        if (trigger == "voice" && decision.RelationshipAssessment == null)
            throw new InvalidDataException("VoiceRelationshipAssessmentRequired");
        if (trigger != "voice") decision.RelationshipAssessment = null;
        if (decision.RelationshipAssessment is { } assessment)
            new VoiceRelationship(new(0, 0, 0, AgentPromptFiles.Text("AgentProviders.04"), null)).Apply(["validate"], assessment, DateTimeOffset.UnixEpoch);
        decision.Speech = (decision.Speech ?? string.Empty).Trim();
        decision.IntentSummary = (decision.IntentSummary ?? string.Empty).Trim();
        decision.JournalText = (decision.JournalText ?? string.Empty).Trim();
        if (decision.Speech.Length > 600 || decision.IntentSummary.Length > 240 ||
            decision.JournalText.Length > 400 || decision.MemoryUpserts.Count > 3)
            throw new InvalidDataException("Companion decision exceeds a bounded text/list field.");
        if (!AllowedEmotions.Contains(decision.Emotion) || !AllowedReactions.Contains(decision.Reaction))
            throw new InvalidDataException("Companion decision contains an unknown emotion/reaction.");
        if (!string.Equals(trigger, "voice", StringComparison.OrdinalIgnoreCase))
            decision.Reaction = "None";
        if (decision.Action != null &&
            (!AllowedTools.Contains(decision.Action.Tool) ||
             decision.Action.Arguments.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("Companion action is not on the allow-list.");
        foreach (var memory in decision.MemoryUpserts)
        {
            memory.Key = (memory.Key ?? string.Empty).Trim();
            memory.Value = (memory.Value ?? string.Empty).Trim();
            if (memory.Key.Length is < 1 or > 64 || memory.Value.Length is < 1 or > 400 ||
                memory.Importance is < 0f or > 1f)
                throw new InvalidDataException("Companion memory update is invalid.");
        }
    }

    private static string StripFence(string text)
    {
        text = text.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewline = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline
            ? text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim()
            : text;
    }

    private static byte[] FakePcm(string text)
    {
        var seconds = Math.Clamp(text.Length / 14.0, 0.5, 3.0);
        var samples = (int)(44100 * seconds);
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var envelope = Math.Min(1.0, Math.Min(i / 2205.0, (samples - i) / 2205.0));
            var sample = (short)(Math.Sin(i * Math.PI * 2 * 190 / 44100) * 2400 * envelope);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), sample);
        }
        return bytes;
    }

    private static byte[] WrapPcm16Mono44100(byte[] pcm)
    {
        using var stream = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        writer.Flush();
        return stream.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
