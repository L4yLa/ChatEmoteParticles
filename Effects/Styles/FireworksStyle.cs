using ChatEmoteParticles.Configuration;
using UnityEngine;

namespace ChatEmoteParticles.Effects.Styles
{
    /// <summary>
    /// ゾーン内のランダム1点から全方向へ放出する。発生点はバースト（1メッセージの同一エモート）で共有する。
    /// Size / Color over Lifetime でフェードアウトさせる（SPEC §8）。
    /// </summary>
    internal sealed class FireworksStyle : IEmoteStyle
    {
        private const float SpeedMin = 2.0f;
        private const float SpeedMax = 4.0f;

        public EmoteStyle Kind => EmoteStyle.Fireworks;
        public float GravityAccel => 3.0f;
        public bool UseNoise => false;
        public float NoiseStrength => 0f;
        public float NoiseFrequency => 0f;
        public bool FadeOut => true;

        public Vector3 BeginGroup(EmoteZoneResolver zones, System.Random rng, float z)
        {
            var rect = zones.Pick(rng);
            return new Vector3(rect.PickX(rng), rect.PickY(rng), z);
        }

        public void Fill(ref ParticleSystem.EmitParams p, EmoteZoneResolver zones, System.Random rng,
                         Vector3 anchor, float z, float speedScale)
        {
            p.position = anchor;
            p.velocity = StyleMath.OnUnitSphere(rng) * (StyleMath.Range(rng, SpeedMin, SpeedMax) * speedScale);
        }
    }
}
