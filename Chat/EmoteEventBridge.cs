using System;
using System.Collections.Concurrent;
using System.Threading;
using ChatEmoteParticles.Configuration;
using ChatEmoteParticles.Utilities;
using ChatCore.Interfaces;
using EnhancedStreamChat.Chat;
using EnhancedStreamChat.Interfaces;
using Zenject;

namespace ChatEmoteParticles.Chat
{
    /// <summary>
    /// ESC の <see cref="IChatCoreManager"/> を購読し、受信メッセージのエモートを集計して
    /// メインスレッド側へ受け渡す。
    ///
    /// <para><b>重要</b>：ChatCore のイベントはネットワークスレッドから発火する。
    /// ここでは Unity API を一切呼ばず、<see cref="ConcurrentQueue{T}"/> 経由でのみ受け渡す
    /// （SPEC §5 / §11）。</para>
    /// </summary>
    public sealed class EmoteEventBridge : IInitializable, IDisposable
    {
        private readonly IChatCoreManager _chatCore;

        private readonly ConcurrentQueue<EmoteBatch> _queue = new ConcurrentQueue<EmoteBatch>();
        private readonly ConcurrentBag<EmoteBatch> _pool = new ConcurrentBag<EmoteBatch>();

        private int _queuedCount;
        private int _pooledCount;
        private bool _subscribed;

        public EmoteEventBridge([InjectOptional] IChatCoreManager chatCore)
        {
            _chatCore = chatCore;
        }

        public void Initialize()
        {
            if (_chatCore == null)
            {
                Log.Error("[Bridge] IChatCoreManager could not be resolved. " +
                          "Is Enhanced Stream Chat (ChatCore) installed and enabled? ChatEmoteParticles will stay idle.");
                return;
            }

            _chatCore.OnTextMessageReceived += OnTextMessageReceived;
            _subscribed = true;
            Log.Info("[Bridge] subscribed to IChatCoreManager.OnTextMessageReceived");
        }

        public void Dispose()
        {
            if (_subscribed && _chatCore != null)
            {
                _chatCore.OnTextMessageReceived -= OnTextMessageReceived;
                _subscribed = false;
            }

            while (_queue.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _queuedCount, 0);
        }

        /// <summary>メインスレッドから呼ぶ。取り出した <see cref="EmoteBatch"/> は必ず <see cref="Recycle"/> すること。</summary>
        internal bool TryDequeue(out EmoteBatch batch)
        {
            if (_queue.TryDequeue(out batch))
            {
                Interlocked.Decrement(ref _queuedCount);
                return true;
            }
            batch = null;
            return false;
        }

        internal void Recycle(EmoteBatch batch)
        {
            if (batch == null) return;
            batch.Clear();

            // プールの上限は MaxQueuedMessages と同程度で十分
            if (Volatile.Read(ref _pooledCount) >= 128) return;
            Interlocked.Increment(ref _pooledCount);
            _pool.Add(batch);
        }

        /// <summary>キューを空にする。無効時・シーン不一致時に呼ぶ。</summary>
        internal void DrainAll()
        {
            while (TryDequeue(out var b)) Recycle(b);
        }

        private EmoteBatch Rent()
        {
            if (_pool.TryTake(out var b))
            {
                Interlocked.Decrement(ref _pooledCount);
                b.Clear();
                return b;
            }
            return new EmoteBatch();
        }

        // ───────────────────────── ネットワークスレッド ─────────────────────────

        private void OnTextMessageReceived(IChatService service, IChatMessage message)
        {
            try
            {
                var config = PluginConfig.Instance;
                if (config == null || !config.Enabled) return;

                var emotes = message?.Emotes;
                if (emotes == null || emotes.Length == 0) return;

                var batch = Rent();

                for (var i = 0; i < emotes.Length; i++)
                {
                    var emote = emotes[i];
                    if (emote == null || string.IsNullOrEmpty(emote.Id)) continue;

                    // ESC 側の URI 解決ロジックを共用する（SPEC §4）。
                    // 純粋な文字列処理のみでスレッドセーフ。
                    var (uri, animType) = ChatMessageBuilder.GetEmoteUriAndAnimationType(emote);
                    if (string.IsNullOrEmpty(uri)) continue;

                    batch.Add(emote.Id, uri, animType);
                }

                if (batch.Count == 0)
                {
                    Recycle(batch);
                    return;
                }

                _queue.Enqueue(batch);
                Interlocked.Increment(ref _queuedCount);

                // 溢れた分は古いものから破棄する（SPEC §11）
                var max = config.MaxQueuedMessages;
                if (max < 1) max = 1;
                while (Volatile.Read(ref _queuedCount) > max && TryDequeue(out var dropped))
                {
                    Recycle(dropped);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[Bridge] failed to process chat message", ex);
            }
        }
    }
}
