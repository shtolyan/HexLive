#if UNITY_EDITOR
using HexLive.UnityPresentation.Wearing.Editor;
public static class MsiHairIntegration
{
    public static void Run()
    {
        PeopleAssetPreparation.ImportHairAndPrepare();
        MsiHairFbxAudit.RunProduction();
    }
}
#endif
