using System;
using System.Collections.Generic;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    // §51.3: one dismissal and input policy for auxiliary runtime windows.
    public sealed class RuntimeWindow
    {
        private static readonly List<RuntimeWindow> OpenWindows = new();
        private static bool _gesture;
        private static int _releaseFrame = -1;
        private readonly VisualElement _window;
        private readonly Action _close;
        public VisualElement Backdrop { get; }
        public bool IsOpen { get; private set; }

        public RuntimeWindow(VisualElement root, VisualElement window, Action close,
            string name, VisualElement backdrop = null)
        {
            _window = window; _close = close;
            Backdrop = backdrop ?? new VisualElement { name = name + "-backdrop" };
            Backdrop.pickingMode = PickingMode.Position;
            Backdrop.style.position = Position.Absolute;
            Backdrop.style.left = 0; Backdrop.style.right = 0;
            Backdrop.style.top = 0; Backdrop.style.bottom = 0;
            Backdrop.style.display = DisplayStyle.None;
            if (backdrop == null)
            {
                var index = root.IndexOf(window);
                if (index < 0) root.Add(Backdrop); else root.Insert(index, Backdrop);
            }
            window.pickingMode = PickingMode.Position;
            Backdrop.RegisterCallback<PointerDownEvent>(e =>
            {
                SuppressGesture();
                if (e.button == 0 && ReferenceEquals(e.target, Backdrop)) RequestClose();
                e.StopPropagation();
            });
            Backdrop.RegisterCallback<PointerUpEvent>(e => e.StopPropagation());
            Backdrop.RegisterCallback<WheelEvent>(e => e.StopPropagation());
            window.RegisterCallback<PointerDownEvent>(e => { SuppressGesture(); e.StopPropagation(); });
            window.RegisterCallback<PointerUpEvent>(e => e.StopPropagation());
            window.RegisterCallback<WheelEvent>(e => e.StopPropagation());
            window.RegisterCallback<DetachFromPanelEvent>(e => { if (ReferenceEquals(e.target, window)) Untrack(); });
            window.RegisterCallback<AttachToPanelEvent>(e => { if (ReferenceEquals(e.target, window) && IsOpen) Track(); });
        }

        public void Show(bool open)
        {
            if (open && !IsOpen) SuppressGesture();
            IsOpen = open;
            _window.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            Backdrop.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            if (open) Track(); else Untrack();
        }

        private void Track() { if (!OpenWindows.Contains(this)) OpenWindows.Add(this); }
        private void Untrack() { if (OpenWindows.Remove(this)) SuppressGesture(); }
        private void RequestClose() { if (IsOpen) { SuppressGesture(); _close(); } }

        public static bool CloseTopmost()
        {
            if (OpenWindows.Count == 0) return false;
            OpenWindows[OpenWindows.Count - 1].RequestClose(); return true;
        }

        public static Button CloseButton(Action close, Color color, string name)
        {
            var button = new Button(() => { SuppressGesture(); close(); })
                { name = name, tooltip = Loc.Get("panel.close") };
            button.style.width = 32; button.style.height = 32;
            button.style.minWidth = 32; button.style.minHeight = 32; button.style.flexShrink = 0;
            button.style.alignItems = Align.Center; button.style.justifyContent = Justify.Center;
            button.style.paddingLeft = 0; button.style.paddingRight = 0;
            button.style.paddingTop = 0; button.style.paddingBottom = 0;
            var icon = new VectorIcon(VectorIcon.Kind.Close, color) { name = "close-icon" };
            icon.style.width = 20; icon.style.height = 20;
            button.Add(icon);
            return button;
        }

        public static void SuppressGesture()
        {
            _gesture = true; _releaseFrame = PointerHeld() ? -1 : Time.frameCount;
        }

        // Input System consumes the same physical press independently of UITK.
        // Keep the whole gesture, including the release frame, out of the world.
        public static bool BlocksWorldInput
        {
            get
            {
                if (_gesture)
                {
                    if (PointerHeld()) _releaseFrame = -1;
                    else if (_releaseFrame < 0) _releaseFrame = Time.frameCount;
                    else if (Time.frameCount > _releaseFrame) _gesture = false;
                }
                return OpenWindows.Count > 0 || _gesture;
            }
        }

        private static bool PointerHeld()
        {
            // A different mouse/touch device can become current mid-gesture.
            foreach (var device in InputSystem.devices)
            {
                if (device is Mouse mouse && (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed)) return true;
                if (device is Touchscreen touch)
                    foreach (var finger in touch.touches) if (finger.press.isPressed) return true;
            }
            return false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { OpenWindows.Clear(); _gesture = false; _releaseFrame = -1; }
    }
}
