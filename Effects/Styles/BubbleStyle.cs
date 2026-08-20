using ChatEmoteParticles.Configuration;
using UnityEngine;

namespace ChatEmoteParticles.Effects.Styles
{
    /// <summary>
    /// ゾーン下端の面からゆっくり浮上する。負の重力でわずかに加速し、
    /// Noise モジュールで横揺れさせる（SPEC §8）。
    /// </summary>
    internal sealed class BubbleStyle : IEmoteStyle
    {
        private const float SpeedMin = 0.5f;
        private const float SpeedMax = 1.2f;

        public EmoteStyle Kind => EmoteStyle.Bubble;
        public float GravityAccel => -0.3f;
        public bool UseNoise => true;
        public float NoiseStrength => 0.35f;
        public float NoiseFrequency => 0.25f;
        public bool FadeOut => false;

        public Vector3 BeginGroup(EmoteZoneResolver zones, System.Random rng, float z) => Vector3.zero;

        public void Fill(ref ParticleSystem.EmitParams p, EmoteZoneResolver zones, System.Random rng,
                         Vector3 anchor, float z, float speedScale)
        {
            var rect = zones.Pick(rng);
            p.position = new Vector3(rect.PickX(rng), rect.YMin, z);
            p.velocity = new Vector3(0f, StyleMath.Range(rng, SpeedMin, SpeedMax) * speedScale, 0f);
        }
    }
}
