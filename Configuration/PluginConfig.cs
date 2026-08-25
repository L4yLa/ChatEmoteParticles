using System;
using System.Runtime.CompilerServices;
using ChatEmoteParticles.Utilities;
using IPA.Config.Stores;
using IPA.Config.Stores.Attributes;
using IPA.Config.Stores.Converters;

[assembly: InternalsVisibleTo(GeneratedStore.AssemblyVisibilityTarget)]

namespace ChatEmoteParticles.Configuration
{
    /// <summary>
    /// BSIPA Generated Config。
    /// 実体は Beat Saber\UserData\ChatEmoteParticles.json に生成される。
    /// </summary>
    public class PluginConfig
    {
        public static PluginConfig Instance { get; set; }

        // ───────────── UI: toggle ─────────────
        public virtual bool Enabled { get; set; } = true;
        public virtual bool EnabledInMenu { get; set; } = true;
        public virtual bool EnabledInSong { get; set; } = true;
        public virtual bool PrecacheChannelEmotes { get; set; } = true;
        public virtual bool Verbose { get; set; } = false;

        /// <summary>設定画面で「高度な設定」を展開するか。表示状態のみで、動作には影響しない。</summary>
        public virtual bool ShowAdvanced { get; set; } = false;

        // ───────────── UI: dropdown ─────────────
        [UseConverter(typeof(EnumConverter<EmoteStyle>))]
        public virtual EmoteStyle Style { get; set; } = EmoteStyle.Shower;

        [UseConverter(typeof(EnumConverter<EmoteZone>))]
        public virtual EmoteZone Zone { get; set; } = EmoteZone.Both;

        // ───────────── UI: slider（数量） ─────────────
        /// <summary>
        /// 1メッセージ中の同一エモート数に掛ける倍率。
        /// BSML の integer-only スライダは int プロパティを要求するため int とする。
        /// </summary>
        public virtual int CountCoefficient { get; set; } = 1;           // 1 – 10
        public virtual int MaxEmotesPerMessage { get; set; } = 20;       // 1 – 100
        public virtual int MaxConcurrentParticles { get; set; } = 300;   // 50 – 2000

        // ───────────── UI: slider（サイズ・透明度） ─────────────
        public virtual float EmoteSizeMin { get; set; } = 0.80f;         // 0.05 – 5.0 [m]
        public virtual float EmoteSizeMax { get; set; } = 2.00f;         // 0.05 – 5.0 [m]
        public virtual float Alpha { get; set; } = 0.80f;                // 0.0 – 1.0

        // ───────────── UI: slider（表示範囲） ─────────────
        public virtual float EmoteZ { get; set; } = 16.0f;               // -10.0 – 50.0 [m]
        public virtual float AboveLaneMinY { get; set; } = 1.80f;
        public virtual float AboveLaneMaxY { get; set; } = 8.00f;
        public virtual float AboveLaneHalfX { get; set; } = 1.20f;
        public virtual float SideInnerX { get; set; } = 1.30f;
        public virtual float SideOuterX { get; set; } = 6.00f;
        public virtual float SideMinY { get; set; } = 0.00f;
        public virtual float SideMaxY { get; set; } = 8.00f;
        public virtual float MenuEmoteZ { get; set; } = 5.0f;
        public virtual float MenuEmoteScale { get; set; } = 0.4f;

        // ───────────── UI: slider（挙動） ─────────────
        public virtual float LifeTime { get; set; } = 6.0f;              // 0.5 – 30.0 [s]
        public virtual float SpeedScale { get; set; } = 1.0f;            // 0.1 – 5.0

        // ───────────── ファイルのみ（UIに出さない） ─────────────
        public virtual bool IncludeInReflection { get; set; } = true;
        public virtual float ImageWaitTimeoutSec { get; set; } = 10.0f;
        public virtual int MaxQueuedMessages { get; set; } = 64;
        public virtual int EmitterPoolSize { get; set; } = 64;
        public virtual int MaxEmitters { get; set; } = 192;
        public virtual float PrecacheDelaySec { get; set; } = 5.0f;
        public virtual int PrecacheIntervalMs { get; set; } = 200;
        public virtual int MaxPrecacheCount { get; set; } = 300;
        public virtual float MinAnimationProcessIntervalSec { get; set; } = 0.5f;
        public virtual float FrameTimeBudgetRatio { get; set; } = 0.85f;
        public virtual int FrameTimeSampleCount { get; set; } = 30;
        public virtual int MaxPendingAnimations { get; set; } = 32;
        public virtual int MaxImageLoadsPerFrame { get; set; } = 1;

        // ───────────── 正規化済みの派生値 ─────────────

        /// <summary>正規化後の最小サイズ。Min &gt; Max の場合は入れ替えた値を返す。</summary>
        [Ignore] public float SizeMinNormalized { get; private set; } = 0.80f;

        /// <summary>正規化後の最大サイズ。</summary>
        [Ignore] public float SizeMaxNormalized { get; private set; } = 2.00f;

        /// <summary>Both 指定時に AboveLane ゾーンへ割り当てる比率（面積比）。</summary>
        [Ignore] public float AboveLaneWeight { get; private set; } = 0.5f;

        /// <summary>
        /// 設定が変更されたときに発火する。UI の Changed と OnReload の両方から呼ばれる。
        /// ※ イベントには [Ignore] を付けられない（CS0592）。BSIPA は virtual プロパティのみを
        ///    シリアライズ対象とするため、イベントは属性なしで問題ない。
        /// </summary>
        public event Action OnSettingsChanged;

        /// <summary>設定ファイルがロード／リロードされたときに BSIPA から呼ばれる。</summary>
        public virtual void OnReload() => Apply();

        /// <summary>設定値が変更されたときに BSIPA から呼ばれる。</summary>
        public virtual void Changed() => Apply();

        /// <summary>
        /// 正規化と派生値の再計算を行い、変更を通知する。
        ///
        /// <para><b>ここと <see cref="OnSettingsChanged"/> の購読者で Unity API を呼んではならない
        /// （2026-08-25）。</b>本メソッドはメインスレッド以外からも呼ばれる：
        /// <list type="bullet">
        /// <item><see cref="OnReload"/> → BSIPA の設定ロード専用スレッド
        ///       （<c>SingleThreadTaskScheduler</c>）</item>
        /// <item><see cref="Changed"/> → <c>EmotePrecacheService.DisablePrecache()</c> が
        ///       <c>PrecacheChannelEmotes</c> を書き換える際、HTTP タスクの
        ///       スレッドプールスレッド</item>
        /// </list>
        /// Unity API を 1 つでも混入させるとネイティブクラッシュになる。</para>
        /// </summary>
        internal void Apply()
        {
            Log.IsVerbose = Verbose;

            // サイズの正規化（Min > Max なら入れ替え）
            if (EmoteSizeMin <= EmoteSizeMax)
            {
                SizeMinNormalized = EmoteSizeMin;
                SizeMaxNormalized = EmoteSizeMax;
            }
            else
            {
                SizeMinNormalized = EmoteSizeMax;
                SizeMaxNormalized = EmoteSizeMin;
            }

            // ゾーンの面積比を算出（常に空間密度が一様になるようにする）
            var areaAbove = (AboveLaneHalfX * 2f) * Math.Max(0f, AboveLaneMaxY - AboveLaneMinY);
            var areaSides = 2f * Math.Max(0f, SideOuterX - SideInnerX) * Math.Max(0f, SideMaxY - SideMinY);
            var total = areaAbove + areaSides;
            AboveLaneWeight = total > 0f ? areaAbove / total : 0.5f;

            OnSettingsChanged?.Invoke();
        }
    }
}
