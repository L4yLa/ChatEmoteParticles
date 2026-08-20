using System;
using System.Collections.Generic;
using CameraUtils.Core;
using ChatEmoteParticles.Chat;
using ChatEmoteParticles.Configuration;
using ChatEmoteParticles.Effects.Styles;
using ChatEmoteParticles.Utilities;
using EnhancedStreamChat.Graphics;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zenject;

namespace ChatEmoteParticles.Effects
{
    /// <summary>
    /// ルート管理・シーン切替・フレーム監視・Emitter プール（SPEC §11 / §13 / §17）。
    /// 本MODで <c>Update()</c> を持つのはこのクラス（実体は <see cref="CepRunner"/>）だけ。
    /// </summary>
    public sealed class EmoteEmitterManager : IInitializable, IDisposable
    {
        private const string GameCoreSceneName = "GameCore";
        private const int MaxMessagesPerFrame = 4;

        /// <summary>
        /// 画像取得を待っている Emit 要求（SPEC §12）。
        /// 画像が未キャッシュのメッセージをその場で捨てると、取得完了時に「何個表示するか」の
        /// 情報が失われて二度と表示されない。タイムアウトまで保持して、揃い次第 Emit する。
        /// </summary>
        private sealed class DeferredEmit
        {
            internal string Id;
            internal int Count;
            internal float DeadlineAt;
        }

        private readonly EmoteEventBridge _bridge;
        private readonly EmoteImageResolver _resolver;
        private readonly EmotePrecacheService _precache;

        private readonly List<EmoteEmitter> _emitters = new List<EmoteEmitter>(64);
        private readonly Dictionary<string, EmoteEmitter> _bound =
            new Dictionary<string, EmoteEmitter>(64, StringComparer.Ordinal);

        private readonly List<DeferredEmit> _deferred = new List<DeferredEmit>(32);
        private readonly Stack<DeferredEmit> _deferredPool = new Stack<DeferredEmit>(32);

        private readonly EmoteZoneResolver _zones = new EmoteZoneResolver();
        private readonly IEmoteStyle[] _styles =
        {
            new ShowerStyle(),
            new FireworksStyle(),
            new FountainStyle(),
            new BubbleStyle(),
        };

        private readonly System.Random _rng = new System.Random();

        private GameObject _root;
        private float[] _frameSamples = new float[30];
        private float[] _frameScratch = new float[30];
        private int _frameSampleIndex;
        private int _frameSampleFilled;

        private bool _inSong;
        private bool _initialized;
        private bool _warmedUp;
        private bool _pendingRevalidate;
        private bool _pendingConfigApply;
        private bool _pendingStyleReset;
        private bool _wasActive;
        private int _layer = (int)VisibilityLayer.AlwaysVisibleAndReflected;

        public EmoteEmitterManager(
            EmoteEventBridge bridge,
            EmoteImageResolver resolver,
            EmotePrecacheService precache)
        {
            _bridge = bridge;
            _resolver = resolver;
            _precache = precache;
        }

        // ───────────────────────── ライフサイクル ─────────────────────────

        public void Initialize()
        {
            var config = PluginConfig.Instance;
            if (config == null)
            {
                Log.Error("[Manager] PluginConfig is not available; aborting initialization");
                return;
            }

            _root = new GameObject("ChatEmoteParticles.Root");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            ResizeFrameSamples(config.FrameTimeSampleCount);
            ApplyConfig(config);

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            config.OnSettingsChanged += OnSettingsChanged;

            var runner = CepRunner.GetOrCreate();
            runner.UpdateCallback += Tick;

            _initialized = true;
            Log.Info("[Manager] initialized");
        }

        public void Dispose()
        {
            if (!_initialized) return;
            _initialized = false;

            var config = PluginConfig.Instance;
            if (config != null) config.OnSettingsChanged -= OnSettingsChanged;

            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;

            if (CepRunner.Instance != null) CepRunner.Instance.UpdateCallback -= Tick;

            ClearDeferred();

            for (var i = 0; i < _emitters.Count; i++) _emitters[i]?.Destroy();
            _emitters.Clear();
            _bound.Clear();

            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            RefreshRateUtil.Invalidate();
            if (scene.name != GameCoreSceneName) return;

            _inSong = true;
            _precache?.SetInSong(true);
        }

        private void OnSceneUnloaded(Scene scene)
        {
            RefreshRateUtil.Invalidate();
            if (scene.name != GameCoreSceneName) return;

            _inSong = false;
            _precache?.SetInSong(false);

            // SPEC §16：破棄済みキャッシュの掃除。メニューへ戻ったタイミングで実行する。
            _pendingRevalidate = true;
        }

        // ───────────────────────── 設定反映 ─────────────────────────

        /// <summary>
        /// 設定変更の通知。
        ///
        /// <para>これは BSML の UI イベントから<b>同期的に</b>呼ばれる。
        /// ここで最大 64 個の Emitter を走査すると UI のイベント連鎖を巻き込んで
        /// 重くなる（特に Fireworks は Emitter ごとに Gradient / AnimationCurve を生成する）。
        /// フラグだけ立てて次の Tick で処理する。</para>
        /// </summary>
        private void OnSettingsChanged()
        {
            _pendingConfigApply = true;
            _pendingStyleReset = true;
        }

        private void ApplyConfig(PluginConfig config)
        {
            _zones.Refresh(config);
            _layer = config.IncludeInReflection
                ? (int)VisibilityLayer.AlwaysVisibleAndReflected
                : (int)VisibilityLayer.AlwaysVisible;

            var style = CurrentStyle(config);
            for (var i = 0; i < _emitters.Count; i++)
            {
                var e = _emitters[i];
                if (e == null || !e.IsAlive) continue;

                e.SetLayer(_layer);
                e.SetMaxParticles(config.MaxConcurrentParticles);
                e.SetLifeTime(config.LifeTime);
                e.ApplyStyle(style);
            }
        }

        /// <summary>
        /// スタイル／ゾーン変更時に旧設定の粒が残らないよう全 Emitter を Clear して解放する（SPEC §18）。
        /// 保留中の Emit も旧設定に紐づくため破棄する。
        /// </summary>
        private void ResetAllEmitters()
        {
            for (var i = 0; i < _emitters.Count; i++)
            {
                var e = _emitters[i];
                if (e == null || !e.IsAlive) continue;
                e.Unbind();
            }
            _bound.Clear();
            ClearDeferred();
        }

        private IEmoteStyle CurrentStyle(PluginConfig config)
        {
            var index = (int)config.Style;
            if (index < 0 || index >= _styles.Length) index = 0;
            return _styles[index];
        }

        // ───────────────────────── 毎フレーム ─────────────────────────

        private void Tick()
        {
            if (!_initialized) return;

            var config = PluginConfig.Instance;
            if (config == null) return;

            RecordFrameTime(config);

            var recentMaxMs = RecentMaxFrameMs();
            var baselineMs = BaselineFrameMs();

            _precache?.Tick();
            _resolver?.Tick(_inSong, recentMaxMs, baselineMs);

            if (_pendingConfigApply)
            {
                _pendingConfigApply = false;
                ApplyConfig(config);
            }

            if (_pendingRevalidate)
            {
                _pendingRevalidate = false;
                Revalidate();
            }

            if (_pendingStyleReset)
            {
                _pendingStyleReset = false;
                ResetAllEmitters();
            }

            if (MaterialProvider.Failed) return;

            var active = Plugin.IsEnabled
                         && config.Enabled
                         && (_inSong ? config.EnabledInSong : config.EnabledInMenu);
            if (!active)
            {
                _bridge?.DrainAll();

                // 無効化された直後だけ生存粒を掃除する（毎フレーム走査しない）
                if (_wasActive) ResetAllEmitters();
                _wasActive = false;

                RecycleFinishedEmitters();
                return;
            }
            _wasActive = true;

            if (!_inSong)
            {
                PreallocateOne(config);
                WarmupOnce(config);
            }

            ProcessEmissions(config);
            RecycleFinishedEmitters();
        }

        private void RecordFrameTime(PluginConfig config)
        {
            var wanted = Mathf.Clamp(config.FrameTimeSampleCount, 1, 240);
            if (_frameSamples.Length != wanted) ResizeFrameSamples(wanted);

            _frameSamples[_frameSampleIndex] = Time.unscaledDeltaTime * 1000f;
            _frameSampleIndex = (_frameSampleIndex + 1) % _frameSamples.Length;
            if (_frameSampleFilled < _frameSamples.Length) _frameSampleFilled++;
        }

        private void ResizeFrameSamples(int count)
        {
            var size = Mathf.Clamp(count, 1, 240);
            _frameSamples = new float[size];
            _frameScratch = new float[size];
            _frameSampleIndex = 0;
            _frameSampleFilled = 0;
        }

        private float RecentMaxFrameMs()
        {
            var max = 0f;
            for (var i = 0; i < _frameSampleFilled; i++)
            {
                if (_frameSamples[i] > max) max = _frameSamples[i];
            }
            return max;
        }

        /// <summary>
        /// 直近フレーム時間の中央値。その環境・その瞬間の「通常状態」を表す。
        /// 挿入ソートだが要素数は既定30なので毎フレーム実行しても無視できる。
        /// </summary>
        private float BaselineFrameMs()
        {
            var n = _frameSampleFilled;
            if (n == 0) return 0f;

            Array.Copy(_frameSamples, _frameScratch, n);
            for (var i = 1; i < n; i++)
            {
                var value = _frameScratch[i];
                var j = i - 1;
                while (j >= 0 && _frameScratch[j] > value)
                {
                    _frameScratch[j + 1] = _frameScratch[j];
                    j--;
                }
                _frameScratch[j + 1] = value;
            }
            return _frameScratch[n / 2];
        }

        // ───────────────────────── 発生処理 ─────────────────────────

        private void ProcessEmissions(PluginConfig config)
        {
            if (_bridge == null || _resolver == null) return;

            var capacity = config.MaxConcurrentParticles - CountAliveParticles();
            var style = CurrentStyle(config);
            var z = _zones.GetZ(_inSong);
            var sizeScale = _inSong ? 1f : config.MenuEmoteScale;
            var alpha = Mathf.Clamp01(config.Alpha);
            var createdThisFrame = 0;

            // 1) 画像待ちだった保留分を先に処理する（古いものから）
            ProcessDeferred(config, style, z, sizeScale, alpha, ref capacity, ref createdThisFrame);

            // 2) 新着メッセージ
            for (var processed = 0; processed < MaxMessagesPerFrame; processed++)
            {
                if (!_bridge.TryDequeue(out var batch)) break;

                try
                {
                    for (var i = 0; i < batch.Count; i++)
                    {
                        var id = batch.Ids[i];

                        var count = Mathf.Clamp(
                            Mathf.CeilToInt(batch.Counts[i] * config.CountCoefficient),
                            1, Mathf.Max(1, config.MaxEmotesPerMessage));

                        if (!_resolver.TryGet(id, out var info))
                        {
                            // 画像が揃うまで保留する。ここで捨てると取得完了後に表示できない。
                            _resolver.Request(id, batch.Uris[i], batch.AnimTypes[i]);
                            Defer(config, id, count);
                            continue;
                        }

                        if (capacity <= 0) continue;
                        if (count > capacity) count = capacity;

                        var emitter = AcquireEmitter(config, id, info, style, ref createdThisFrame);
                        if (emitter == null) continue;

                        EmitGroup(emitter, style, count, z, sizeScale, alpha, config);
                        capacity -= count;
                    }
                }
                finally
                {
                    _bridge.Recycle(batch);
                }
            }
        }

        /// <summary>画像が揃った保留 Emit を処理する。期限切れは破棄する。</summary>
        private void ProcessDeferred(PluginConfig config, IEmoteStyle style, float z,
                                     float sizeScale, float alpha,
                                     ref int capacity, ref int createdThisFrame)
        {
            if (_deferred.Count == 0) return;

            var now = Time.unscaledTime;
            var index = 0;

            while (index < _deferred.Count)
            {
                var deferred = _deferred[index];

                if (now > deferred.DeadlineAt)
                {
                    Log.Debug($"[Manager] deferred emit expired: {deferred.Id}");
                    _deferred.RemoveAt(index);
                    ReleaseDeferred(deferred);
                    continue;
                }

                // 画像がまだ届いていない／容量が無い場合は次フレームに持ち越す
                if (capacity <= 0) break;
                if (!_resolver.TryGet(deferred.Id, out var info))
                {
                    index++;
                    continue;
                }

                var emitter = AcquireEmitter(config, deferred.Id, info, style, ref createdThisFrame);
                if (emitter == null)
                {
                    index++;
                    continue;
                }

                var count = Mathf.Min(deferred.Count, capacity);
                EmitGroup(emitter, style, count, z, sizeScale, alpha, config);
                capacity -= count;

                _deferred.RemoveAt(index);
                ReleaseDeferred(deferred);
            }
        }

        /// <summary>Emit を保留する。同一エモートは表示数を合算する。</summary>
        private void Defer(PluginConfig config, string id, int count)
        {
            var maxCount = Mathf.Max(1, config.MaxEmotesPerMessage);

            for (var i = 0; i < _deferred.Count; i++)
            {
                if (!string.Equals(_deferred[i].Id, id, StringComparison.Ordinal)) continue;
                _deferred[i].Count = Mathf.Min(_deferred[i].Count + count, maxCount);
                return;
            }

            // 溢れた分は古いものから破棄（処理待ちキューと同じ上限を使う）
            var limit = Mathf.Max(1, config.MaxPendingAnimations);
            while (_deferred.Count >= limit)
            {
                var dropped = _deferred[0];
                _deferred.RemoveAt(0);
                ReleaseDeferred(dropped);
            }

            var entry = _deferredPool.Count > 0 ? _deferredPool.Pop() : new DeferredEmit();
            entry.Id = id;
            entry.Count = Mathf.Min(count, maxCount);
            entry.DeadlineAt = Time.unscaledTime + Mathf.Max(1f, config.ImageWaitTimeoutSec);
            _deferred.Add(entry);
        }

        private void ReleaseDeferred(DeferredEmit entry)
        {
            entry.Id = null;
            entry.Count = 0;
            if (_deferredPool.Count < 64) _deferredPool.Push(entry);
        }

        private void ClearDeferred()
        {
            for (var i = 0; i < _deferred.Count; i++) ReleaseDeferred(_deferred[i]);
            _deferred.Clear();
        }

        private void EmitGroup(EmoteEmitter emitter, IEmoteStyle style, int count,
                               float z, float sizeScale, float alpha, PluginConfig config)
        {
            var anchor = style.BeginGroup(_zones, _rng, z);
            var minSize = config.SizeMinNormalized * sizeScale;
            var maxSize = config.SizeMaxNormalized * sizeScale;
            var speedScale = config.SpeedScale;
            var aspect = emitter.Aspect;

            var p = new ParticleSystem.EmitParams
            {
                applyShapeToPosition = false,
                startColor = new Color(1f, 1f, 1f, alpha),
                startLifetime = config.LifeTime,
            };

            for (var i = 0; i < count; i++)
            {
                style.Fill(ref p, _zones, _rng, anchor, z, speedScale);

                // 乱数サイズは粒ごとに1回だけ引く（SPEC §9）
                var size = StyleMath.Range(_rng, minSize, maxSize);
                p.startSize3D = new Vector3(size * aspect, size, size);

                emitter.Ps.Emit(p, 1);
            }

            emitter.LastEmitFrame = Time.frameCount;
        }

        // ───────────────────────── Emitter プール ─────────────────────────

        private EmoteEmitter AcquireEmitter(
            PluginConfig config, string id, EnhancedImageInfo info,
            IEmoteStyle style, ref int createdThisFrame)
        {
            // 1) 同じエモートに割り当て済み
            if (_bound.TryGetValue(id, out var bound) && bound != null && bound.IsAlive)
            {
                return bound;
            }

            // 2) 生存粒が0の Emitter を再利用（表示中のエモートが消える事故を防ぐ）
            var free = FindFreeEmitter();
            if (free != null)
            {
                BindEmitter(free, id, info, style, config);
                return free;
            }

            // 3) 新規生成。曲中は1フレーム1個まで。
            if (_emitters.Count >= config.MaxEmitters) return null;
            if (_inSong && createdThisFrame >= 1) return null;

            var created = CreateEmitter(config, style);
            if (created == null) return null;

            createdThisFrame++;
            BindEmitter(created, id, info, style, config);
            return created;
        }

        private EmoteEmitter FindFreeEmitter()
        {
            for (var i = 0; i < _emitters.Count; i++)
            {
                var e = _emitters[i];
                if (e == null || !e.IsAlive) continue;
                if (e.BoundId != null) continue;
                return e;
            }
            return null;
        }

        private void BindEmitter(EmoteEmitter emitter, string id, EnhancedImageInfo info,
                                 IEmoteStyle style, PluginConfig config)
        {
            emitter.SetLayer(_layer);
            emitter.SetMaxParticles(config.MaxConcurrentParticles);
            emitter.ApplyStyle(style);
            emitter.Bind(id, info, config.LifeTime);
            _bound[id] = emitter;
        }

        private EmoteEmitter CreateEmitter(PluginConfig config, IEmoteStyle style)
        {
            if (_root == null) return null;

            var emitter = EmoteEmitter.Create(_root.transform, _layer, config.MaxConcurrentParticles);
            if (emitter == null) return null;

            emitter.SetLifeTime(config.LifeTime);
            emitter.ApplyStyle(style);
            _emitters.Add(emitter);
            return emitter;
        }

        /// <summary>メニュー滞在中に EmitterPoolSize までプリアロケートする（1フレーム1個）。</summary>
        private void PreallocateOne(PluginConfig config)
        {
            var target = Mathf.Min(config.EmitterPoolSize, config.MaxEmitters);
            if (_emitters.Count >= target) return;

            CreateEmitter(config, CurrentStyle(config));
        }

        private void WarmupOnce(PluginConfig config)
        {
            if (_warmedUp || _emitters.Count == 0) return;

            var emitter = _emitters[0];
            if (emitter == null || !emitter.IsAlive) return;

            emitter.Warmup(_zones.GetZ(false));
            emitter.Clear();
            _warmedUp = true;
            Log.Debug("[Manager] shader warmup emitted");
        }

        private int CountAliveParticles()
        {
            var total = 0;
            for (var i = 0; i < _emitters.Count; i++)
            {
                var e = _emitters[i];
                if (e == null || !e.IsAlive) continue;
                total += e.ParticleCount;
            }
            return total;
        }

        /// <summary>生存粒が0になった Emitter を Free に戻す。</summary>
        private void RecycleFinishedEmitters()
        {
            var frame = Time.frameCount;

            for (var i = 0; i < _emitters.Count; i++)
            {
                var e = _emitters[i];
                if (e == null || !e.IsAlive) continue;
                if (e.BoundId == null) continue;
                if (e.ParticleCount > 0) continue;
                if (frame - e.LastEmitFrame < 2) continue;

                _bound.Remove(e.BoundId);
                e.Unbind();
            }
        }

        // ───────────────────────── 再初期化 ─────────────────────────

        /// <summary>
        /// ESC の <c>ClearCache()</c> で破棄されたテクスチャを参照している Emitter を解放し、
        /// キャッシュ側の死んだエントリも取り除く（SPEC §16）。
        /// </summary>
        internal void Revalidate()
        {
            if (!_initialized || _resolver == null) return;

            var removed = _resolver.Revalidate();
            if (removed == 0) return;

            // 破棄済みテクスチャを掴んだままの Emitter を解放する
            for (var i = 0; i < _emitters.Count; i++)
            {
                var e = _emitters[i];
                if (e == null || !e.IsAlive || e.BoundId == null) continue;
                if (_resolver.TryGet(e.BoundId, out _)) continue;

                _bound.Remove(e.BoundId);
                e.Unbind();
            }
        }

        /// <summary>設定画面から呼ぶ再初期化要求。次のフレームで処理する。</summary>
        internal void RequestRevalidate() => _pendingRevalidate = true;
    }
}
