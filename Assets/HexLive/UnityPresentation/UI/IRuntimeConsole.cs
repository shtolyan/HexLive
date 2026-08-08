#if UNITY_EDITOR || DEVELOPMENT_BUILD
namespace HexLive.UnityPresentation.UI
{
    /// <summary>Common development-build console surface.</summary>
    internal interface IRuntimeConsole
    {
        bool IsVisible { get; }

        void Show();

        void Hide();

        void Toggle();
    }
}
#endif
