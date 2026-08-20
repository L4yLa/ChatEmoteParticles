using ChatEmoteParticles.Configuration;
using UnityEngine;

namespace ChatEmoteParticles.Effects.Styles
{
    /// <summary>ゾーン上端の面から下向きに均一に降下する（SPEC §8）。</summary>
    internal sealed class ShowerStyle : IEmoteStyle
    {
        private const float SpeedMin = 1.5f;
        private const float SpeedMax = 3.0f;

        public EmoteStyle Kind => EmoteStyle.Shower;
        public float GravityAccel => 0f;
        public bool UseNoise => false;
        public float NoiseStrength => 0f;
        public float NoiseFrequency => 0f;
        public bool FadeOut => false;

        public Vector3 BeginGroup(EmoteZoneResolver zones, System.Random rng, float z) => Vector3.zero;

        public void Fill(ref ParticleSystem.EmitParams p, EmoteZoneResolver zones, System.Random rng,
                         Vector3 anchor, float z, float speedScale)
        {
            var rect = zones.Pick(rng);
            p.position = new Vector3(rect.PickX(rng), rect.YMax, z);
            p.velocity = new Vector3(0f, -StyleMath.Range(rng, SpeedMin, SpeedMax) * speedScale, 0f);
        }
    }
}
