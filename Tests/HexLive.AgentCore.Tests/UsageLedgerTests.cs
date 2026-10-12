using System.Text;
using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

// §171.4: one append-only record per provider attempt; unknown cost is never zero.
public sealed class UsageLedgerTests
{
    private string _dir = "";
    private static readonly UsageContext Owner1 = new("owner-1", "char-1", "world-a", 7, "session-1");
    private static readonly ModelSelection Grok = new(ModelProviderKind.Grok, "grok", "grok-test");
    private static readonly VoiceSelection Voice = new("eleven", "voice-x", "eleven_multilingual_v2");

    private static readonly UsageTariffs Tariffs = new()
    {
        Version = "test-1", Currency = "USD",
        Rates =
        [
            new UsageRate { Kind = UsageKind.Llm, Provider = "Grok", Model = "grok-test", InputPerMillion = 2m, OutputPerMillion = 10m },
            new UsageRate { Kind = UsageKind.Tts, Provider = "ElevenLabs", Model = "*", PerThousandCharacters = 0.3m },
        ],
    };

    [SetUp] public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "hexlive-usage-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Test]
    public async Task SuccessfulModelCallIsRecordedWithTokensTurnAndEstimatedCost()
    {
        var ledger = new JsonlUsageLedger(_dir);
        using var model = new MeteredModelAdapter(new FakeModel(_ => Task.FromResult(new ModelAnswer("{}", 1000, 200))),
            ledger, Owner1, Tariffs);
        using (UsageScope.Turn("turn-42"))
            await model.CompleteAsync(Grok, new("i", "c", "x"), default);

        var e = JsonlUsageLedger.ReadAll(_dir).Single();
        Assert.Multiple(() =>
        {
            Assert.That(e.Status, Is.EqualTo("ok"));
            Assert.That((e.Kind, e.Provider, e.Model), Is.EqualTo((UsageKind.Llm, "Grok", "grok-test")));
            Assert.That((e.OwnerUserId, e.CharacterId, e.SessionId, e.TurnId), Is.EqualTo(("owner-1", "char-1", "session-1", "turn-42")));
            Assert.That((e.InputTokens, e.OutputTokens), Is.EqualTo(((long?)1000, (long?)200)));
            // 1000 × 2/1M + 200 × 10/1M = 0.004
            Assert.That(e.Cost, Is.EqualTo(0.004m));
            Assert.That((e.CostSource, e.Currency, e.TariffVersion), Is.EqualTo((UsageCostSource.Estimate, "USD", "test-1")));
        });
        Assert.That(UsageScope.TurnId, Is.Empty, "the scope must not leak past its using block");
    }

    [Test]
    public void FailedAndCancelledAttemptsAreStillRecordedAndRethrown()
    {
        var ledger = new JsonlUsageLedger(_dir);
        using var failing = new MeteredModelAdapter(new FakeModel(_ => throw new InvalidDataException("player said something private")),
            ledger, Owner1, Tariffs);
        Assert.ThrowsAsync<InvalidDataException>(() => failing.CompleteAsync(Grok, new("i", "c", "x"), default));

        using var cts = new CancellationTokenSource();
        using var slow = new MeteredModelAdapter(new FakeModel(async t => { cts.Cancel(); t.ThrowIfCancellationRequested(); await Task.Yield(); return new("{}"); }),
            ledger, Owner1, Tariffs);
        Assert.CatchAsync<OperationCanceledException>(() => slow.CompleteAsync(Grok, new("i", "c", "x"), cts.Token));

        var events = JsonlUsageLedger.ReadAll(_dir);
        Assert.That(events.Select(e => e.Status), Is.EqualTo(new[] { "error", "cancelled" }));
        Assert.That(events[0].Error, Is.EqualTo(nameof(InvalidDataException)), "only the type: the message may quote private speech");
        Assert.That(events.All(e => e.Cost == null && e.CostSource == UsageCostSource.Unknown), Is.True,
            "no units known ⇒ cost unknown, never zero");
    }

    [Test]
    public async Task MissingTokenCountsOrMissingRateLeaveCostUnknown()
    {
        var ledger = new JsonlUsageLedger(_dir);
        using var codexLike = new MeteredModelAdapter(new FakeModel(_ => Task.FromResult(new ModelAnswer("{}"))), ledger, Owner1, Tariffs);
        await codexLike.CompleteAsync(Grok, new("i", "c", "x"), default);
        using var unpriced = new MeteredModelAdapter(new FakeModel(_ => Task.FromResult(new ModelAnswer("{}", 5, 5))), ledger, Owner1, Tariffs);
        await unpriced.CompleteAsync(new(ModelProviderKind.Claude, "claude", "unlisted"), new("i", "c", "x"), default);

        var events = JsonlUsageLedger.ReadAll(_dir);
        Assert.That(events.Select(e => e.Cost), Is.All.Null);
        Assert.That(events.Select(e => e.CostSource), Is.All.EqualTo(UsageCostSource.Unknown));
    }

    [Test]
    public async Task VoiceAttemptRecordsSentCharactersAndReturnedAudioLength()
    {
        var ledger = new JsonlUsageLedger(_dir);
        var wav = Wav(seconds: 1.5, sampleRate: 44100);
        var voice = new MeteredVoiceAdapter(new FakeVoice(wav), ledger, Owner1, "ElevenLabs", Tariffs);
        await voice.SynthesizeWavAsync(Voice, new string('а', 200), default);

        var e = JsonlUsageLedger.ReadAll(_dir).Single();
        Assert.Multiple(() =>
        {
            Assert.That((e.Kind, e.Provider, e.Model, e.VoiceId), Is.EqualTo((UsageKind.Tts, "ElevenLabs", "eleven_multilingual_v2", "voice-x")));
            Assert.That(e.Characters, Is.EqualTo(200));
            Assert.That(e.AudioSeconds, Is.EqualTo(1.5).Within(1e-9));
            Assert.That(e.Cost, Is.EqualTo(0.06m), "200 chars × 0.3 / 1000");
        });
    }

    [Test]
    public void ReconciliationReplacesTheEstimateInsteadOfAddingASecondCharge()
    {
        var ledger = new JsonlUsageLedger(_dir);
        var at = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
        var estimate = new UsageEvent { AtUtc = at, OwnerUserId = "owner-1", Kind = UsageKind.Llm, Status = "ok", Cost = 0.004m, Currency = "USD", CostSource = UsageCostSource.Estimate };
        ledger.Append(estimate);
        ledger.Append(estimate with { Revision = 1, Cost = 0.0037m, CostSource = UsageCostSource.Reconciled });
        ledger.Append(estimate with { Revision = 0, Cost = 9m }); // a late duplicate of the old revision
        File.AppendAllText(Path.Combine(_dir, JsonlUsageLedger.FileName(at)), "{\"eventId\":\"torn", Encoding.UTF8);

        var e = JsonlUsageLedger.ReadAll(_dir).Single();
        Assert.That((e.Cost, e.CostSource), Is.EqualTo(((decimal?)0.0037m, UsageCostSource.Reconciled)));
    }

    [Test]
    public void DailyReportKeepsOwnersApartAndCountsUnknownCostSeparately()
    {
        var day = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        UsageEvent E(string owner, decimal? cost, long input = 10) => new()
        {
            AtUtc = day, OwnerUserId = owner, CharacterId = "c", Kind = UsageKind.Llm, Provider = "Grok", Model = "m",
            Status = "ok", InputTokens = input, OutputTokens = 1, Cost = cost, Currency = cost == null ? "" : "USD",
            CostSource = cost == null ? UsageCostSource.Unknown : UsageCostSource.Estimate,
        };
        var rows = UsageReport.Daily([E("a", 0.5m), E("a", null), E("b", 0.25m, 99)]);

        Assert.That(rows, Has.Count.EqualTo(2));
        var a = rows.Single(r => r.OwnerUserId == "a"); var b = rows.Single(r => r.OwnerUserId == "b");
        Assert.Multiple(() =>
        {
            Assert.That((a.Requests, a.KnownCost, a.UnknownCostRequests, a.InputTokens), Is.EqualTo((2, 0.5m, 1, 20L)));
            Assert.That((b.Requests, b.KnownCost, b.UnknownCostRequests, b.InputTokens), Is.EqualTo((1, 0.25m, 0, 99L)));
        });
    }

    [Test]
    public async Task ABrokenLedgerNeverBreaksTheConversation()
    {
        using var model = new MeteredModelAdapter(new FakeModel(_ => Task.FromResult(new ModelAnswer("{}", 1, 1))),
            new ThrowingLedger(), Owner1, Tariffs);
        var answer = await model.CompleteAsync(Grok, new("i", "c", "x"), default);
        Assert.That(answer.DecisionJson, Is.EqualTo("{}"));
        Assert.That(model.FailedWrites, Is.EqualTo(1));
    }

    [Test]
    public void WavLengthIsReadFromTheHeaderAndNonWavIsUnknown()
    {
        Assert.That(MeteredVoiceAdapter.WavSeconds(Wav(2.0, 24000)), Is.EqualTo(2.0).Within(1e-9));
        Assert.That(MeteredVoiceAdapter.WavSeconds(new byte[100]), Is.Null);
    }

    private static byte[] Wav(double seconds, int sampleRate)
    {
        var data = (int)(seconds * sampleRate) * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sampleRate); w.Write(sampleRate * 2);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data); w.Write(new byte[data]);
        return ms.ToArray();
    }

    private sealed class FakeModel(Func<CancellationToken, Task<ModelAnswer>> answer) : IModelAdapter
    {
        public Task<ModelAnswer> CompleteAsync(ModelSelection selection, ModelRequest request, CancellationToken cancellationToken) => answer(cancellationToken);
    }

    private sealed class FakeVoice(byte[] wav) : IVoiceAdapter
    {
        public Task<byte[]> SynthesizeWavAsync(VoiceSelection selection, string text, CancellationToken cancellationToken) => Task.FromResult(wav);
    }

    private sealed class ThrowingLedger : IUsageLedger
    {
        public void Append(UsageEvent usage) => throw new IOException("disk full");
    }
}
