using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace ChatEmoteParticles.Utilities
{
    /// <summary>
    /// ディスプレイのリフレッシュレート取得。結果はキャッシュし、シーン遷移時のみ再取得する。
    /// SPEC §14「フレームタイム予算」に対応。
    /// </summary>
    internal static class RefreshRateUtil
    {
        private const float FallbackHz = 90f;

        private static readonly List<XRDisplaySubsystem> s_buffer = new List<XRDisplaySubsystem>(2);
        private static float s_cachedHz = -1f;

        /// <summary>現在のリフレッシュレート [Hz]。取得できない場合は 90。</summary>
        internal static float Hz
        {
            get
            {
                if (s_cachedHz <= 0f)
                {
                    s_cachedHz = Resolve();
                    Log.Debug($"[RefreshRate] resolved to {s_cachedHz} Hz");
                }
                return s_cachedHz;
            }
        }

        /// <summary>キャッシュを破棄する。シーン遷移時に呼ぶ。</summary>
        internal static void Invalidate() => s_cachedHz = -1f;

        private static float Resolve()
        {
            // 1) XRDisplaySubsystem（VR ランタイムの実効レート。最優先）
            try
            {
                s_buffer.Clear();
                SubsystemManager.GetSubsystems(s_buffer);
                for (var i = 0; i < s_buffer.Count; i++)
                {
                    var s = s_buffer[i];
                    if (s != null && s.running && s.TryGetDisplayRefreshRate(out var hz) && hz > 0f)
                    {
                        return hz;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log.Debug($"[RefreshRate] XRDisplaySubsystem query failed: {ex.GetType().Name}");
            }

            // 2) デスクトップ側
            //    ※ Resolution.refreshRate はこの環境で既に obsolete だが、値自体は有効。
            //      refreshRateRatio への移行は別途検証してから行う。
            try
            {
#pragma warning disable CS0618
                var hz2 = Screen.currentResolution.refreshRate;
#pragma warning restore CS0618
                if (hz2 > 0) return hz2;
            }
            catch (System.Exception ex)
            {
                Log.Debug($"[RefreshRate] Screen query failed: {ex.GetType().Name}");
            }

            // 3) フォールバック
            return FallbackHz;
        }
    }
}
