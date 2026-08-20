using ChatEmoteParticles.Configuration;
using UnityEngine;

namespace ChatEmoteParticles.Effects.Styles
{
    /// <summary>
    /// 表示スタイル（SPEC §8）。
    /// 発生位置・初速は <see cref="ParticleSystem.EmitParams"/> で粒ごとに決まるため、
    /// ParticleSystem の Shape モジュールは使用しない。
    /// </summary>
    internal interface IEmoteStyle
    {
        EmoteStyle Kind { get; }

        /// <summary>下向きを正とする重力加速度 [m/s²]。Unity の gravityModifier へは 9.81 で割って渡す。</summary>
        float GravityAccel { get; }

        /// <summary>Noise モジュールを使うか。</summary>
        bool UseNoise { get; }

        float NoiseStrength { get; }

        float NoiseFrequency { get; }

        /// <summary>Size / Color over Lifetime によるフェードアウトを行うか。</summary>
        bool FadeOut { get; }

        /// <summary>
        /// 1回の Emit グループの開始時に呼ばれる。
        /// Fireworks のようにグループ内で発生点を共有するスタイルのためのフック。
        /// 使わないスタイルは <see cref="Vector3.zero"/> を返してよい。
        /// </summary>
        Vector3 BeginGroup(EmoteZoneResolver zones, System.Random rng, float z);

        /// <summary>粒1つぶんの発生位置と初速を設定する。</summary>
        void Fill(ref ParticleSystem.EmitParams p, EmoteZoneResolver zones, System.Random rng,
                  Vector3 anchor, float z, float speedScale);
    }

    internal static class StyleMath
    {
        internal const float GravityUnit = 9.81f;

        internal static float Range(System.Random rng, float min, float max)
            => min + (float)rng.NextDouble() * (max - min);

        /// <summary>System.Random だけで一様な単位球面ベクトルを作る。</summary>
        internal static Vector3 OnUnitSphere(System.Random rng)
        {
            var z = Range(rng, -1f, 1f);
            var theta = Range(rng, 0f, Mathf.PI * 2f);
            var r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return new Vector3(r * Mathf.Cos(theta), r * Mathf.Sin(theta), z);
        }
    }
}
