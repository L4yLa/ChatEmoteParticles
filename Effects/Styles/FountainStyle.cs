using ChatEmoteParticles.Configuration;
using UnityEngine;

namespace ChatEmoteParticles.Effects.Styles
{
    /// <summary>
    /// ゾーン下端の一点から上向きに噴き上げ、重力で放物線を描く（SPEC §8）。
    ///
    /// <para><b>初速はゾーン高さから逆算する。</b>仕様書の固定値（4〜6 m/s）だと重力 9.8 m/s² に対して
    /// 到達高度が 0.8〜1.8m しかなく、エモート自体のサイズ（0.8〜2.0m）を下回って
    /// 「上がらずに落ちる」見た目になるため。ゾーン上端付近まで届く初速を計算する。</para>
    ///
    /// <para><b>噴出口はグループで共有する。</b>粒ごとに位置を抽選するとゾーン全幅にばらけて
    /// 噴水ではなく「下から生えるシャワー」になるため、Fireworks と同様に
    /// <see cref="BeginGroup"/> で決めた1点から噴き上げる。</para>
    /// </summary>
    internal sealed class FountainStyle : IEmoteStyle
    {
        private const float Gravity = 9.8f;

        /// <summary>初速のばらつき。1.0 でゾーン上端にちょうど届く。</summary>
        private const float SpeedJitterMin = 0.85f;
        private const float SpeedJitterMax = 1.05f;

        /// <summary>横方向の初速 [m/s]。角度ではなく速度で指定し、初速を上げても広がりすぎないようにする。</summary>
        private const float LateralSpeed = 0.8f;
        private const float DepthSpeed = 0.4f;

        /// <summary>噴出口のゆらぎ [m]。</summary>
        private const float OriginJitter = 0.15f;

        /// <summary>到達高度の下限・上限 [m]。</summary>
        private const float MinApex = 1.0f;
        private const float MaxApex = 20.0f;

        /// <summary>
        /// <see cref="BeginGroup"/> で選んだゾーンの高さ。
        /// Emit グループの処理はメインスレッドの Tick から BeginGroup → Fill と
        /// 連続して呼ばれるため、インスタンスに保持して問題ない。
        /// </summary>
        private float _apex = 4f;

        public EmoteStyle Kind => EmoteStyle.Fountain;
        public float GravityAccel => Gravity;
        public bool UseNoise => false;
        public float NoiseStrength => 0f;
        public float NoiseFrequency => 0f;
        public bool FadeOut => false;

        public Vector3 BeginGroup(EmoteZoneResolver zones, System.Random rng, float z)
        {
            var rect = zones.Pick(rng);
            _apex = Mathf.Clamp(rect.YMax - rect.YMin, MinApex, MaxApex);
            return new Vector3(rect.PickX(rng), rect.YMin, z);
        }

        public void Fill(ref ParticleSystem.EmitParams p, EmoteZoneResolver zones, System.Random rng,
                         Vector3 anchor, float z, float speedScale)
        {
            p.position = new Vector3(
                anchor.x + StyleMath.Range(rng, -OriginJitter, OriginJitter),
                anchor.y,
                anchor.z + StyleMath.Range(rng, -OriginJitter, OriginJitter));

            // v = sqrt(2 * g * h) … ゾーン上端付近に到達する初速
            var up = Mathf.Sqrt(2f * Gravity * _apex) * StyleMath.Range(rng, SpeedJitterMin, SpeedJitterMax);

            p.velocity = new Vector3(
                StyleMath.Range(rng, -LateralSpeed, LateralSpeed),
                up,
                StyleMath.Range(rng, -DepthSpeed, DepthSpeed)) * speedScale;
        }
    }
}
