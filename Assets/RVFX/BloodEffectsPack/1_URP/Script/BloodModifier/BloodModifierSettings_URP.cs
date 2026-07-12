using System;
using UnityEngine;

namespace BloodEffectsPack
{
    [Serializable]
    public class BloodModifierSettings_URP
    {
        public BloodModifier_URP.EffectType effectType = BloodModifier_URP.EffectType.Splash;
        public Color color = Color.white;
        public float colorIntensity = 1.0f;
        public float albedoPower = 1.0f;
        public float ambientColorIntensity = 1.0f;
        [Range(-180, 180)] public float hueShift = 0.0f;
        public float smoothness = 0.0f;
        public bool useSpecularity = true;
        public float gravityScale = 0.0f;

        public void CopyFrom(BloodModifierSettings_URP other)
        {
            if (other == null)
                return;

            effectType = other.effectType;
            color = other.color;
            colorIntensity = other.colorIntensity;
            albedoPower = other.albedoPower;
            ambientColorIntensity = other.ambientColorIntensity;
            hueShift = other.hueShift;
            smoothness = other.smoothness;
            useSpecularity = other.useSpecularity;
            gravityScale = other.gravityScale;
        }

        public void CopyFromPreset(BloodPreset preset, BloodModifier_URP.EffectType effectTypeValue)
        {
            if (preset == null)
                return;

            effectType = effectTypeValue;
            color = preset.color;
            colorIntensity = preset.colorIntensity;
            albedoPower = preset.albedoPower;
            ambientColorIntensity = preset.ambientColorIntensity;
            hueShift = preset.hueshift;
            smoothness = preset.smoothness;
            useSpecularity = preset.useSpecularity;
            gravityScale = preset.gravityScale;
        }
    }
}