using System;
using System.Collections.Generic;
using ChatEmoteParticles.Configuration;
using ChatEmoteParticles.Utilities;
using EnhancedStreamChat.Chat;
using EnhancedStreamChat.Graphics;
using UnityEngine;
using Zenject;

namespace ChatEmoteParticles.Chat
{
    /// <summary>
    /// ESC の <see cref="ChatImageProvider"/> との連携。
    /// 未キャッシュのエモートは「処理待ちキュー」に積み、フレームが乱れていないときだけ
    /// 1件ずつ取得を開始する（SPEC §12 / §14）。
    /// </summary>
    public sealed class EmoteImageResolver
    {
        private sealed class PendingRequest
        {
            internal string Id;
            internal string Uri;
            internal ChatImageProvider.ESCAnimationType Type;
            internal float EnqueuedAt;
        }

        private readonly ChatImageProvider _provider;

        private readonly List<PendingRequest> _pending = new List<PendingRequest>(32);
        private readonly HashSet<string> _pendingIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Stack<PendingRequest> _requestPool = new Stack<PendingRequest>(32);

        private int _inFlight;
        private float _lastProcessStart = float.NegativeInfinity;

        public EmoteImageResolver([InjectOptional] ChatImageProvider provider)
        {
            _provider = provider;
            if (_provider == null)
            {
                Log.Error("[Resolver] ChatImageProvider could not be resolved. Emote images will never load.");
            }
        }

        internal bool IsAvailable => _provider != null;

        internal int PendingCount => _pending.Count;

        // ───────────────────────── 取得 ─────────────────────────

        /// <summary>
        /// キャッシュ済み画像を取得する。
        /// Sprite が破棄済み（ESC の ClearCache 実行後など）のエントリは取り除いたうえで false を返す。
        /// </summary>
        internal bool TryGet(string id, out EnhancedImageInfo info)
        {
            info = null;
            if (_provider == null || string.IsNullOrEmpty(id)) return false;

            if (!_provider.CachedImageInfo.TryGetValue(id, out info)) return false;

            // Unity の == オーバーロードで「破棄済み」を検出する
            if (info == null || info.Sprite == null)
            {
                _provider.CachedImageInfo.TryRemove(id, out _);
                info = null;
                return false;
            }
            return true;
        }

        /// <summary>未キャッシュのエモートを処理待ちキューへ積む。</summary>
        internal void Request(string id, string uri, ChatImageProvider.ESCAnimationType type)
        {
            if (_provider == null || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(uri)) return;
            if (_pendingIds.Contains(id)) return;
            if (_provider.CachedImageInfo.ContainsKey(id)) return;

            var config = PluginConfig.Instance;
            var max = config != null ? config.MaxPendingAnimations : 32;
            if (max < 1) max = 1;

            // 溢れた分は古いものから破棄（SPEC §14）
            while (_pending.Count >= max)
            {
                var oldest = _pending[0];
                _pending.RemoveAt(0);
                _pendingIds.Remove(oldest.Id);
                Release(oldest);
            }

            var req = _requestPool.Count > 0 ? _requestPool.Pop() : new PendingRequest();
            req.Id = id;
            req.Uri = uri;
            req.Type = type;
            req.EnqueuedAt = Time.unscaledTime;

            _pending.Add(req);
            _pendingIds.Add(id);

            Log.Debug($"[Resolver] queued: {id} (pending={_pending.Count})");
        }

        // ───────────────────────── 毎フレーム処理 ─────────────────────────

        /// <summary>
        /// 処理待ちキューを進める。
        /// </summary>
        /// <param name="inSong">曲中かどうか。メニューではフレームタイムゲートを課さない。</param>
        /// <param name="recentMaxMs">直近Nフレームの最大フレーム時間 [ms]。</param>
        /// <param name="baselineMs">直近Nフレームの中央値 [ms]。その環境の「通常状態」。</param>
        internal void Tick(bool inSong, float recentMaxMs, float baselineMs)
        {
            if (_provider == null) return;

            var config = PluginConfig.Instance;
            if (config == null) return;

            var now = Time.unscaledTime;
            var timeout = config.ImageWaitTimeoutSec;
            if (timeout <= 0f) timeout = 10f;

            // ウォッチドッグ：コールバックが返らないまま滞留した場合の復帰
            if (_inFlight > 0 && now - _lastProcessStart > timeout)
            {
                Log.Warn("[Resolver] in-flight request timed out; resetting counter");
                _inFlight = 0;
            }

            // タイムアウトしたキューを破棄
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (now - _pending[i].EnqueuedAt <= timeout) continue;

                var expired = _pending[i];
                _pending.RemoveAt(i);
                _pendingIds.Remove(expired.Id);
                Log.Debug($"[Resolver] request expired: {expired.Id}");
                Release(expired);
            }

            if (_pending.Count == 0) return;

            var maxPerFrame = config.MaxImageLoadsPerFrame;
            if (maxPerFrame < 1) maxPerFrame = 1;

            for (var started = 0; started < maxPerFrame; started++)
            {
                if (!CanProcess(config, now, inSong, recentMaxMs, baselineMs)) return;
                if (_pending.Count == 0) return;

                var req = _pending[0];
                _pending.RemoveAt(0);
                StartRequest(req, now);
                now = Time.unscaledTime;
            }
        }

        /// <summary>
        /// 着手可否の判定。
        ///
        /// <para>フレームタイムの判定は「リフレッシュレートから計算した理論値」ではなく
        /// <b>直近フレーム時間の中央値（= その環境での通常状態）に対する相対比</b>で行う。
        /// 理論値との絶対比較では、常時リフレッシュレート未満で動く環境（CPU律速など）で
        /// ゲートが永久に閉じたままになるため。</para>
        ///
        /// <para><c>baseline / worst &gt;= FrameTimeBudgetRatio</c>
        /// … 0.85 なら「最悪フレームが通常の約1.18倍以内」を意味する。</para>
        ///
        /// <para>メニュー滞在中はゲートを課さない。SPEC §17 でプリアロケート・ウォームアップを
        /// メニューで行う方針と一貫させ、曲中のみスパイクを避ける。</para>
        /// </summary>
        private bool CanProcess(PluginConfig config, float now, bool inSong, float recentMaxMs, float baselineMs)
        {
            // 同時処理は常に1件のみ
            if (_inFlight != 0) return false;
            if (now - _lastProcessStart <= config.MinAnimationProcessIntervalSec) return false;

            if (!inSong) return true;
            if (baselineMs <= 0f || recentMaxMs <= 0f) return true;

            var ratio = Mathf.Clamp(config.FrameTimeBudgetRatio, 0.05f, 1f);
            return baselineMs / recentMaxMs >= ratio;
        }

        private void StartRequest(PendingRequest req, float now)
        {
            var id = req.Id;
            var uri = req.Uri;
            var type = req.Type;
            Release(req);

            _inFlight++;
            _lastProcessStart = now;

            Log.Debug($"[Resolver] start: {id} ({type})");

            // ESC の OnSingleImageCached はダウンロード失敗時に Finally を null チェックなしで
            // 呼び出すため、必ず非 null のコールバックを渡すこと。
            Action<EnhancedImageInfo> onDone = info =>
            {
                if (_inFlight > 0) _inFlight--;
                _pendingIds.Remove(id);

                if (!Log.IsVerbose) return;

                if (info == null || info.Sprite == null)
                {
                    Log.Debug($"[Resolver] failed: {id}");
                }
                else
                {
                    var frames = info.AnimControllerData?.Sprites?.Length ?? 1;
                    Log.Debug($"[Resolver] cached: {id} {info.Width}x{info.Height} frames={frames}");
                }
            };

            try
            {
                CepRunner.Run(_provider.TryCacheSingleImage(id, uri, type, onDone));
            }
            catch (Exception ex)
            {
                if (_inFlight > 0) _inFlight--;
                _pendingIds.Remove(id);
                Log.Error($"[Resolver] failed to start image request for {id}", ex);
            }
        }

        private void Release(PendingRequest req)
        {
            req.Id = null;
            req.Uri = null;
            if (_requestPool.Count < 64) _requestPool.Push(req);
        }

        // ───────────────────────── 再検証 ─────────────────────────

        /// <summary>
        /// ESC の <c>ClearCache()</c> によって破棄されたエントリを取り除く（SPEC §16）。
        /// 破棄されていなければ何も起きないため、通信・テクスチャ再生成は発生しない。
        /// </summary>
        /// <returns>取り除いたエントリ数。</returns>
        internal int Revalidate()
        {
            if (_provider == null) return 0;

            var removed = 0;
            foreach (var kvp in _provider.CachedImageInfo)
            {
                var info = kvp.Value;
                if (info != null && info.Sprite != null) continue;

                if (_provider.CachedImageInfo.TryRemove(kvp.Key, out _)) removed++;
            }

            if (removed > 0) Log.Info($"[Resolver] revalidate removed {removed} destroyed cache entries");
            return removed;
        }
    }
}
