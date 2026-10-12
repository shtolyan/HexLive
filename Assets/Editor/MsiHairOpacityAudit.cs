#if UNITY_EDITOR
using HexLive.UnityPresentation.Wearing.Editor;
public static class MsiHairOpacityAudit
{
    public static void Run()
    {
        PeopleAssetPreparation.PrepareHairPrefabs();
        MsiPreparedHairAudit.Run();
    }
}
#endif
