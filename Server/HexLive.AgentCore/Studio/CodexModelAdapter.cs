using HexLive.AgentHost;

namespace HexLive.AgentCore.Studio;

public sealed class CodexModelAdapter(string executable, string integrationId) : IModelAdapter
{
    public async Task<ModelAnswer> CompleteAsync(ModelSelection selection, ModelRequest request, CancellationToken token)
    {
        if (selection.Provider != ModelProviderKind.Codex || selection.IntegrationId != integrationId ||
            string.IsNullOrWhiteSpace(selection.ModelId) ||
            selection.Reasoning is not (null or "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra"))
            throw new InvalidDataException("InvalidCodexSelection");
        var answer = await CodexDecisionRunner.DecideAsync(executable,
            request.Instructions + "\n" + request.Context + "\n" + request.Input,
            token, selection.ModelId, selection.Reasoning ?? CodexDecisionRunner.ReasoningEffort);
        return new(answer);
    }
}
