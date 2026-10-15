using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>§128.1b / §123.5: one quantity for the slider, number and command.</summary>
    public sealed class InventoryQuantityChoice
    {
        public SliderInt Slider { get; }
        public IntegerField Input { get; }
        public int Value { get; private set; } = 1;
        public int Maximum { get; private set; } = 1;
        private bool _synchronizing;

        public InventoryQuantityChoice(string prefix)
        {
            Slider = new SliderInt { name = prefix + "-slider" };
            Input = new IntegerField { name = prefix + "-input", isDelayed = false };
            Slider.RegisterValueChangedCallback(evt =>
            {
                // Setting bounds during PointerUp can enqueue a change from the
                // old range. It arrives after Configure set the new full stack.
                // Never let that stale "one" overwrite either displayed value.
                if (!_synchronizing && ReferenceEquals(evt.target, Slider) && evt.newValue == Slider.value)
                    Select(evt.newValue);
            });
            Input.RegisterValueChangedCallback(evt =>
            {
                if (!_synchronizing && ReferenceEquals(evt.target, Input) && evt.newValue == Input.value)
                    Select(evt.newValue);
            });
        }

        public void Configure(int maximum)
        {
            _synchronizing = true;
            try
            {
                Maximum = Mathf.Max(1, maximum);
                Slider.lowValue = 1;
                Slider.highValue = Maximum;
                Write(Maximum);
            }
            finally { _synchronizing = false; }
        }

        private void Select(int value)
        {
            _synchronizing = true;
            try { Write(value); }
            finally { _synchronizing = false; }
        }

        private void Write(int value)
        {
            Value = Mathf.Clamp(value, 1, Maximum);
            Slider.SetValueWithoutNotify(Value);
            Input.SetValueWithoutNotify(Value);
        }
    }
}
