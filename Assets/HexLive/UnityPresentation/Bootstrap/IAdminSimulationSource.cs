namespace HexLive.UnityPresentation.Bootstrap
{
public interface IAdminSimulationSource
{
    string AdminClientId { get; }
    string AdminServer { get; }
    void SendAdmin(string json);
    bool TryTakeAdminResult(out string json);
}
}
