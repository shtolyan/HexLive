namespace HexLive.AgentCore.Studio;

/// <summary>Safe native diagnostic: never includes a credential or account identifier.</summary>
public sealed class CredentialStoreException(int? nativeStatus = null)
    : InvalidOperationException(nativeStatus.HasValue
        ? $"OperatingSystemCredentialOperationFailed (OSStatus={nativeStatus.Value})"
        : "OperatingSystemCredentialOperationFailed")
{
    public int? NativeStatus { get; } = nativeStatus;
}
