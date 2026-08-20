using System;
using System.Collections.Generic;
using EnhancedStreamChat.Chat;

namespace ChatEmoteParticles.Chat
{
    /// <summary>
    /// 1メッセージ分のエモート集計結果。
    /// プール再利用前提のため、フィールドは事前確保した List を Clear() で使い回す
    /// （SPEC §17「集計用 Dictionary / List は事前確保し Clear() で再利用」）。
    /// </summary>
    internal sealed class EmoteBatch
    {
        private const int InitialCapacity = 8;

        internal readonly List<string> Ids = new List<string>(InitialCapacity);
        internal readonly List<string> Uris = new List<string>(InitialCapacity);
        internal readonly List<ChatImageProvider.ESCAnimationType> AnimTypes =
            new List<ChatImageProvider.ESCAnimationType>(InitialCapacity);
        internal readonly List<int> Counts = new List<int>(InitialCapacity);

        internal int Count => Ids.Count;

        internal void Clear()
        {
            Ids.Clear();
            Uris.Clear();
            AnimTypes.Clear();
            Counts.Clear();
        }

        /// <summary>
        /// エモートを追加する。同一 Id が既にあれば個数を加算する。
        /// 件数は最大でも1メッセージ中のユニークエモート数（通常数個）なので線形探索で十分。
        /// </summary>
        internal void Add(string id, string uri, ChatImageProvider.ESCAnimationType type)
        {
            for (var i = 0; i < Ids.Count; i++)
            {
                if (string.Equals(Ids[i], id, StringComparison.Ordinal))
                {
                    Counts[i]++;
                    return;
                }
            }

            Ids.Add(id);
            Uris.Add(uri);
            AnimTypes.Add(type);
            Counts.Add(1);
        }
    }
}
