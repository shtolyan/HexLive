#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
using System;
using System.Reflection;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// Mobile adapter for the official Lunar asset. Reflection keeps the game
    /// assembly compilable while the Asset Store package is being imported;
    /// the imported type is still the sole mobile UI/backend.
    /// </summary>
    internal sealed class LunarRuntimeConsoleProvider : IRuntimeConsole
    {
        private const string LunarTypeName = "LunarConsolePlugin.LunarConsole";

        private readonly MethodInfo _show;
        private readonly MethodInfo _hide;
        private bool _visible;

        public LunarRuntimeConsoleProvider()
        {
            Type type = null;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length && type == null; i++)
            {
                type = assemblies[i].GetType(LunarTypeName, false);
            }

            _show = type?.GetMethod("Show", BindingFlags.Public | BindingFlags.Static);
            _hide = type?.GetMethod("Hide", BindingFlags.Public | BindingFlags.Static);
        }

        public bool IsVisible => _visible;

        public void Show()
        {
            if (_show == null)
            {
                Debug.LogError("Lunar Mobile Console is unavailable. Import the official Asset Store package and enable it for Development Builds.");
                return;
            }

            _show.Invoke(null, null);
            _visible = true;
        }

        public void Hide()
        {
            _hide?.Invoke(null, null);
            _visible = false;
        }

        public void Toggle()
        {
            if (_visible)
            {
                Hide();
            }
            else
            {
                Show();
            }
        }
    }
}
#endif
