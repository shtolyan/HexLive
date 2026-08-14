using System;
using System.Collections.Generic;
using HexLive.Server.Llm;
using NUnit.Framework;

namespace HexLive.Server.Tests.Llm
{

public sealed class LlmHostOptionsTests
{
    [Test]
    public void EnvironmentConfiguration_IsValidatedAndComplete()
    {
        var environment = new Dictionary<string, string?>
        {
            [LlmHostOptions.EndpointEnvironmentVariable] = "https://llm-gateway.example/v1/decision",
            [LlmHostOptions.ApiKeyEnvironmentVariable] = "fixture-secret",
            [LlmHostOptions.ModelEnvironmentVariable] = "fixture-model",
            [LlmHostOptions.NpcsEnvironmentVariable] = "7, 9",
            [LlmHostOptions.TimeoutEnvironmentVariable] = "20",
            [LlmHostOptions.BackoffEnvironmentVariable] = "3",
            [LlmHostOptions.MaxQueuedEnvironmentVariable] = "4",
            [LlmHostOptions.MaxConcurrentEnvironmentVariable] = "1",
        };
        var options = new LlmHostOptions();

        options.ApplyEnvironment(name => environment.GetValueOrDefault(name));
        options.Validate();

        Assert.Multiple(() =>
        {
            Assert.That(options.Enabled, Is.True);
            Assert.That(options.Endpoint, Is.EqualTo(
                new Uri("https://llm-gateway.example/v1/decision")));
            Assert.That(options.ApiKey, Is.EqualTo("fixture-secret"));
            Assert.That(options.Model, Is.EqualTo("fixture-model"));
            Assert.That(options.SelectedNpcIds, Has.Count.EqualTo(2));
            Assert.That(options.SelectedNpcIds[0].Value, Is.EqualTo(7));
            Assert.That(options.SelectedNpcIds[1].Value, Is.EqualTo(9));
            Assert.That(options.RequestTimeout, Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(options.Backoff, Is.EqualTo(TimeSpan.FromSeconds(3)));
            Assert.That(options.MaxQueuedRequests, Is.EqualTo(4));
            Assert.That(options.MaxConcurrentRequests, Is.EqualTo(1));
        });
    }

    [Test]
    public void PartialConfiguration_IsRejectedInsteadOfSilentlyDisablingProvider()
    {
        var missingEndpoint = new LlmHostOptions();
        missingEndpoint.SetSelectedNpcIds("7");

        var missingNpcs = new LlmHostOptions();
        missingNpcs.SetEndpoint("https://llm-gateway.example/decision");

        Assert.Multiple(() =>
        {
            Assert.That(() => missingEndpoint.Validate(),
                Throws.InvalidOperationException.With.Message.Contains("HEXLIVE_LLM_ENDPOINT"));
            Assert.That(() => missingNpcs.Validate(),
                Throws.InvalidOperationException.With.Message.Contains("HEXLIVE_LLM_NPCS"));
        });
    }

    [Test]
    public void UnsafeOrAmbiguousOperationalValues_AreRejected()
    {
        var options = new LlmHostOptions();

        Assert.Multiple(() =>
        {
            Assert.That(() => options.SetEndpoint("http://llm-gateway.example/decision"),
                Throws.ArgumentException.With.Message.Contains("HTTPS"));
            Assert.That(() => options.SetEndpoint("https://user:secret@llm-gateway.example/decision"),
                Throws.ArgumentException.With.Message.Contains("user-info"));
            Assert.That(() => options.SetSelectedNpcIds("7,7"),
                Throws.ArgumentException.With.Message.Contains("unique"));
            Assert.That(() => options.SetApiKey("not a bearer token"),
                Throws.ArgumentException.With.Message.Contains("bearer"));
            Assert.That(() => options.SetRequestTimeoutSeconds("0"),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => options.SetMaxConcurrentRequests("17"),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void LoopbackHttp_RemainsAvailableForAnExplicitLocalProvider()
    {
        var options = new LlmHostOptions();
        options.SetEndpoint("http://127.0.0.1:11434/hexlive");
        options.SetSelectedNpcIds("7");

        Assert.That(() => options.Validate(), Throws.Nothing);
        Assert.That(options.Enabled, Is.True);
    }
}

}
