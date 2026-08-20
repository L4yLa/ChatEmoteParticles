using ChatEmoteParticles.Effects.Styles;
using ChatEmoteParticles.Utilities;
using EnhancedStreamChat.Graphics;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChatEmoteParticles.Effects
{
    /// <summary>
    /// エモート1種ぶんの ParticleSystem（SPEC §9 / §13）。
    /// MonoBehaviour は持たず、<see cref="EmoteEmitterManager"/> が配列で一括管理する。
    /// </summary>
    internal sealed class EmoteEmitter
    {
        /// <summary>BSML の GIF デコーダに倣い、10ms 以下のフレーム間隔は 100ms とみなす。</summary>
        private const float MinFrameDelayMs = 10f;
        private const float BumpedFrameDelayMs = 100f;

        internal GameObject Root { get; private set; }
        internal ParticleSystem Ps { get; private set; }
        internal ParticleSystemRenderer Renderer { get; private set; }
        internal Material Mat { get; private set; }

        /// <summary>割り当て中のエモートID。未割り当ては null。</summary>
        internal string BoundId { get; private set; }

        /// <summary>横 / 縦のアスペクト比。</summary>
        internal float Aspect { get; private set; } = 1f;

        /// <summary>最後に Emit したフレーム番号。回収判定の誤爆防止に使う。</summary>
        internal int LastEmitFrame { get; set; } = -10;

        internal int ParticleCount => Ps != null ? Ps.particleCount : 0;

        internal bool IsAlive => Root != null && Ps != null;

        /// <summary>本クラスが生成した Sprite（アトラスから pivot を付け替えたもの）。破棄責任を持つ。</summary>
        private Sprite[] _ownedSprites;

        // ───────────────────────── 生成 ─────────────────────────

        internal static EmoteEmitter Create(Transform parent, int layer, int maxParticles)
        {
            var mat = MaterialProvider.CreateInstance();
            if (mat == null) return null;

            var go = new GameObject("CEP_Emitter");
            go.transform.SetParent(parent, false);
            go.layer = layer;

            var ps = go.AddComponent<ParticleSystem>();
            var renderer = go.GetComponent<ParticleSystemRenderer>();

            ConfigureSystem(ps, maxParticles);
            ConfigureRenderer(renderer, mat);

            var emitter = new EmoteEmitter
            {
                Root = go,
                Ps = ps,
                Renderer = renderer,
                Mat = mat,
            };

            ps.Play();
            return emitter;
        }

        private static void ConfigureSystem(ParticleSystem ps, int maxParticles)
        {
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.duration = 3600f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.useUnscaledTime = true;   // ポーズ中も流し続ける（SPEC §17）
            main.startSize3D = true;
            main.startSpeed = 0f;          // 初速は EmitParams で与える
            main.startLifetime = 6f;
            main.startRotation = 0f;
            main.gravityModifier = 0f;
            main.maxParticles = Mathf.Max(1, maxParticles);
            main.cullingMode = ParticleSystemCullingMode.Automatic;
            main.ringBufferMode = ParticleSystemRingBufferMode.Disabled;
            main.startColor = Color.white;

            // 発生は EmitParams のみで行うため emission / shape は無効化する
            var emission = ps.emission;
            emission.enabled = false;

            var shape = ps.shape;
            shape.enabled = false;

            // 不要モジュールを明示的に無効化する（SPEC §17）
            var collision = ps.collision; collision.enabled = false;
            var trails = ps.trails; trails.enabled = false;
            var lights = ps.lights; lights.enabled = false;
            var subEmitters = ps.subEmitters; subEmitters.enabled = false;
            var externalForces = ps.externalForces; externalForces.enabled = false;
            var inheritVelocity = ps.inheritVelocity; inheritVelocity.enabled = false;
            var velocityOverLifetime = ps.velocityOverLifetime; velocityOverLifetime.enabled = false;
            var limitVelocity = ps.limitVelocityOverLifetime; limitVelocity.enabled = false;
            var forceOverLifetime = ps.forceOverLifetime; forceOverLifetime.enabled = false;
            var rotationOverLifetime = ps.rotationOverLifetime; rotationOverLifetime.enabled = false;
            var rotationBySpeed = ps.rotationBySpeed; rotationBySpeed.enabled = false;
            var sizeBySpeed = ps.sizeBySpeed; sizeBySpeed.enabled = false;
            var colorBySpeed = ps.colorBySpeed; colorBySpeed.enabled = false;
            var customData = ps.customData; customData.enabled = false;
            var noise = ps.noise; noise.enabled = false;
            var colorOverLifetime = ps.colorOverLifetime; colorOverLifetime.enabled = false;
            var sizeOverLifetime = ps.sizeOverLifetime; sizeOverLifetime.enabled = false;
            var textureSheet = ps.textureSheetAnimation; textureSheet.enabled = false;
        }

        private static void ConfigureRenderer(ParticleSystemRenderer renderer, Material mat)
        {
            // Mesh モードは三人称カメラで潰れるため Billboard を使う（SPEC §9）
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.sortMode = ParticleSystemSortMode.None;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.allowRoll = false;
            renderer.sharedMaterial = mat;
        }

        // ───────────────────────── 設定 ─────────────────────────

        internal void SetLayer(int layer)
        {
            if (Root != null) Root.layer = layer;
        }

        internal void SetMaxParticles(int maxParticles)
        {
            if (Ps == null) return;
            var main = Ps.main;
            main.maxParticles = Mathf.Max(1, maxParticles);
        }

        internal void SetLifeTime(float lifeTime)
        {
            if (Ps == null) return;
            var main = Ps.main;
            main.startLifetime = Mathf.Max(0.05f, lifeTime);
        }

        // ───────────────────────── 割り当て ─────────────────────────

        /// <summary>エモートを割り当てる。テクスチャ差し替えのみで GameObject の生成は行わない。</summary>
        internal void Bind(string id, EnhancedImageInfo info, float lifeTime)
        {
            if (!IsAlive || info == null || info.Sprite == null) return;

            // 先にモジュール側の参照を切ってから Sprite を破棄する
            TeardownAnimation();

            BoundId = id;
            SetLifeTime(lifeTime);

            Aspect = info.Height > 0 ? (float)info.Width / info.Height : 1f;
            if (float.IsNaN(Aspect) || float.IsInfinity(Aspect) || Aspect <= 0f) Aspect = 1f;

            var animData = info.AnimControllerData;
            if (animData != null && animData.Sprites != null && animData.Sprites.Length > 1)
            {
                SetupAnimated(animData);
            }
            else
            {
                MaterialProvider.ApplyTexture(Mat, info.Sprite.texture);
            }
        }

        internal void Unbind()
        {
            BoundId = null;
            Aspect = 1f;
            if (Ps != null) Ps.Clear(true);
            TeardownAnimation();
        }

        internal void Clear()
        {
            if (Ps != null) Ps.Clear(true);
        }

        internal void Destroy()
        {
            TeardownAnimation();

            if (Mat != null) Object.Destroy(Mat);
            if (Root != null) Object.Destroy(Root);

            Mat = null;
            Root = null;
            Ps = null;
            Renderer = null;
            BoundId = null;
        }

        /// <summary>
        /// BSML の <c>AnimationControllerData</c> をパーティクルの textureSheetAnimation へ移す。
        ///
        /// <para>BSML が作る Sprite は pivot (0,0) のため、そのまま渡すと Unity の Sprites モードが
        /// pivot を反映してパーティクルが半サイズぶんずれる。同じアトラステクスチャと rect から
        /// pivot (0.5, 0.5) の Sprite を作り直すことで静止画と同じ位置に描画する
        /// （テクスチャの複製は発生しない）。</para>
        ///
        /// <para>アトラスは <c>makeNoLongerReadable: true</c> でパックされているため、
        /// ピクセル読み出しの要らない <see cref="SpriteMeshType.FullRect"/> を指定する必要がある。</para>
        ///
        /// <para>再生速度は <c>ParticleSystemAnimationTimeMode.FPS</c> で与える。フレーム間隔が
        /// 不均一な GIF は平均 fps で再生されるため、厳密には元の速度と一致しない（既知の制約）。</para>
        /// </summary>
        private void SetupAnimated(BeatSaberMarkupLanguage.Animations.AnimationControllerData animData)
        {
            var source = animData.Sprites;
            var atlas = source[0] != null ? source[0].texture : null;
            if (atlas == null)
            {
                Log.Warn("[Emitter] animation atlas is missing; falling back to static rendering");
                if (source[0] != null) MaterialProvider.ApplyTexture(Mat, source[0].texture);
                return;
            }

            var textureSheet = Ps.textureSheetAnimation;
            textureSheet.enabled = true;
            textureSheet.mode = ParticleSystemAnimationMode.Sprites;
            ClearSprites(textureSheet);

            _ownedSprites = new Sprite[source.Length];
            var added = 0;
            for (var i = 0; i < source.Length; i++)
            {
                var src = source[i];
                if (src == null) continue;

                var sprite = Sprite.Create(
                    atlas, src.rect, new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                _ownedSprites[i] = sprite;
                textureSheet.AddSprite(sprite);
                added++;
            }

            MaterialProvider.ApplyTexture(Mat, atlas);

            if (added <= 1)
            {
                textureSheet.enabled = false;
                return;
            }

            textureSheet.animation = ParticleSystemAnimationType.WholeSheet;
            textureSheet.startFrame = 0f;
            textureSheet.cycleCount = 1;
            textureSheet.frameOverTime = new ParticleSystem.MinMaxCurve(1f);
            textureSheet.timeMode = ParticleSystemAnimationTimeMode.FPS;
            textureSheet.fps = ResolveFps(animData.Delays);
        }

        /// <summary>textureSheetAnimation の参照を切り、自前生成した Sprite を破棄する。</summary>
        private void TeardownAnimation()
        {
            if (Ps != null)
            {
                var textureSheet = Ps.textureSheetAnimation;
                ClearSprites(textureSheet);
                textureSheet.enabled = false;
            }

            if (_ownedSprites == null) return;

            for (var i = 0; i < _ownedSprites.Length; i++)
            {
                if (_ownedSprites[i] != null) Object.Destroy(_ownedSprites[i]);
            }
            _ownedSprites = null;
        }

        private static float ResolveFps(float[] delaysMs)
        {
            if (delaysMs == null || delaysMs.Length == 0) return 10f;

            var total = 0f;
            for (var i = 0; i < delaysMs.Length; i++)
            {
                var d = delaysMs[i];
                if (d <= MinFrameDelayMs) d = BumpedFrameDelayMs;
                total += d;
            }

            var averageMs = total / delaysMs.Length;
            if (averageMs <= 0f) return 10f;

            return Mathf.Clamp(1000f / averageMs, 1f, 60f);
        }

        private static void ClearSprites(ParticleSystem.TextureSheetAnimationModule module)
        {
            try
            {
                for (var i = module.spriteCount - 1; i >= 0; i--)
                {
                    module.RemoveSprite(i);
                }
            }
            catch (System.Exception ex)
            {
                Log.Debug($"[Emitter] failed to clear sprite list: {ex.GetType().Name}");
            }
        }

        // ───────────────────────── スタイル ─────────────────────────

        internal void ApplyStyle(IEmoteStyle style)
        {
            if (!IsAlive || style == null) return;

            var main = Ps.main;
            main.gravityModifier = style.GravityAccel / StyleMath.GravityUnit;

            var noise = Ps.noise;
            noise.enabled = style.UseNoise;
            if (style.UseNoise)
            {
                noise.quality = ParticleSystemNoiseQuality.Low;
                noise.strength = new ParticleSystem.MinMaxCurve(style.NoiseStrength);
                noise.frequency = style.NoiseFrequency;
                noise.scrollSpeed = new ParticleSystem.MinMaxCurve(0.5f);
                noise.damping = false;
                noise.separateAxes = false;
            }

            var colorOverLifetime = Ps.colorOverLifetime;
            var sizeOverLifetime = Ps.sizeOverLifetime;
            colorOverLifetime.enabled = style.FadeOut;
            sizeOverLifetime.enabled = style.FadeOut;

            if (!style.FadeOut) return;

            // スタイル変更は全 Emitter（最大 64 個）に一斉適用されるため、
            // Gradient / AnimationCurve は静的に作って使い回す。
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(FadeGradient);
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, FadeCurve);
        }

        private static Gradient s_fadeGradient;
        private static AnimationCurve s_fadeCurve;

        private static Gradient FadeGradient
        {
            get
            {
                if (s_fadeGradient != null) return s_fadeGradient;

                s_fadeGradient = new Gradient();
                s_fadeGradient.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new[]
                    {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(1f, 0.6f),
                        new GradientAlphaKey(0f, 1f),
                    });
                return s_fadeGradient;
            }
        }

        private static AnimationCurve FadeCurve
            => s_fadeCurve ?? (s_fadeCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0.35f));

        // ───────────────────────── ウォームアップ ─────────────────────────

        /// <summary>
        /// シェーダの初回バリアントコンパイルをメニュー滞在中に済ませる（SPEC §17）。
        /// 極小サイズ・極短寿命の粒を1つだけ出す。
        /// </summary>
        internal void Warmup(float z)
        {
            if (!IsAlive || Mat == null) return;

            MaterialProvider.ApplyTexture(Mat, Texture2D.whiteTexture);

            var p = new ParticleSystem.EmitParams
            {
                position = new Vector3(0f, -1000f, z),
                velocity = Vector3.zero,
                startSize3D = new Vector3(0.001f, 0.001f, 0.001f),
                startColor = new Color(1f, 1f, 1f, 0.01f),
                startLifetime = 0.05f,
                applyShapeToPosition = false,
            };

            Ps.Emit(p, 1);
        }
    }
}
