using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChatCore.Interfaces;
using ChatCore.Models.Twitch;
using ChatEmoteParticles.Configuration;
using ChatEmoteParticles.Utilities;
using EnhancedStreamChat.Chat;
using EnhancedStreamChat.Interfaces;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Zenject;

namespace ChatEmoteParticles.Chat
{
    /// <summary>
    /// 接続中チャンネルのチャンネルエモートを Twitch Helix API 経由で先読みし、
    /// ESC のキャッシュへ投入する（SPEC §15）。
    ///
    /// <para><b>Client-Id の決定</b>：Helix は「トークンの発行元 client_id」と
    /// <c>Client-Id</c> ヘッダが一致していないと 401 を返す。auth.ini のトークンは
    /// ChatCore の認証フロー以外（外部のトークンジェネレータ等）で取得されていることがあるため、
    /// <c>https://id.twitch.tv/oauth2/validate</c> で実際の client_id を取得する。</para>
    ///
    /// <para><b>取得できなかった場合</b>：client_id を知る手段が他に無く、以降 Helix へ
    /// 一切アクセスできない。<see cref="PluginConfig.PrecacheChannelEmotes"/> を無効化し、
    /// 設定 UI 上でもグレーアウトさせて、無駄な再試行を行わない。</para>
    ///
    /// <para><b>チャンネルエモートを持たないチャンネル</b>：アフィリエイト／パートナーでない
    /// チャンネルは Helix が 200 + <c>"data": []</c> を返すため、0件として正常終了する
    /// （エラー扱いにしない）。これは想定内の動作。</para>
    ///
    /// <para><b>トークンの取り扱い</b>：起動時のプリキャッシュ処理以外でトークン関連の情報を
    /// 保持しない。フィールドに持たず、<c>char[]</c> で読み取り、使用後ただちに
    /// <see cref="Array.Clear(Array,int,int)"/> する。ログには一切出力しない
    /// （HTTP エラーは status code のみ）。auth.ini は読み取り専用で開き、書き込みは行わない。</para>
    /// </summary>
    public sealed class EmotePrecacheService : IInitializable, IDisposable
    {
        private const string ValidateUrl = "https://id.twitch.tv/oauth2/validate";
        private const string HelixEmotesUrl = "https://api.twitch.tv/helix/chat/emotes?broadcaster_id=";
        private const string AuthIniDirectory = ".chatcore";
        private const string AuthIniFileName = "auth.ini";
        private const string OAuthKeyDotted = "Twitch.OAuthToken";
        private const string OAuthKeyUnderscored = "Twitch_OAuthToken";
        private const string OAuthPrefix = "oauth:";

        /// <summary>
        /// Twitch API へアクセスする手段が失われたかどうか。
        /// 設定 UI はこれを見て Pre-Cache トグルをグレーアウトする。
        /// </summary>
        public static bool Unavailable { get; private set; }

        private readonly IChatCoreManager _chatCore;
        private readonly ChatImageProvider _provider;

        /// <summary>ネットワークスレッドから書き込まれる broadcaster_id。</summary>
        private readonly ConcurrentDictionary<string, byte> _discoveredRoomIds =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        /// <summary>取得済み broadcaster_id（メインスレッドのみ）。</summary>
        private readonly HashSet<string> _fetchedRoomIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>取得できたエモート。フェッチ完了後にメインスレッドが消費する。</summary>
        private readonly ConcurrentQueue<PrecacheEntry> _ready = new ConcurrentQueue<PrecacheEntry>();

        private readonly List<string> _fetchTargets = new List<string>(4);

        private bool _subscribed;
        private int _fetching;
        private float _nextFetchAllowedAt = float.PositiveInfinity;
        private Coroutine _feedCoroutine;
        private CancellationTokenSource _cts;
        private int _precachedTotal;
        private bool _inSong;

        private struct PrecacheEntry
        {
            internal string Id;
            internal string Uri;
            internal ChatImageProvider.ESCAnimationType Type;
        }

        public EmotePrecacheService(
            [InjectOptional] IChatCoreManager chatCore,
            [InjectOptional] ChatImageProvider provider)
        {
            _chatCore = chatCore;
            _provider = provider;
        }

        public void Initialize()
        {
            if (_chatCore == null || _provider == null) return;

            _chatCore.OnRoomStateUpdated += OnRoomStateUpdated;
            _chatCore.OnJoinChannel += OnRoomStateUpdated;
            _chatCore.OnLeaveChannel += OnLeaveChannel;
            _subscribed = true;
            _cts = new CancellationTokenSource();
        }

        public void Dispose()
        {
            if (_subscribed && _chatCore != null)
            {
                _chatCore.OnRoomStateUpdated -= OnRoomStateUpdated;
                _chatCore.OnJoinChannel -= OnRoomStateUpdated;
                _chatCore.OnLeaveChannel -= OnLeaveChannel;
                _subscribed = false;
            }

            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts?.Dispose();
            _cts = null;

            StopFeeding();
        }

        /// <summary>シーン状態の通知。曲中はプリキャッシュを実行しない（SPEC §15）。</summary>
        internal void SetInSong(bool inSong)
        {
            _inSong = inSong;
            if (inSong) StopFeeding();
        }

        /// <summary>
        /// Twitch API へアクセスできなくなったため機能を停止する。
        /// 回復にはユーザーの対応（ESC の再認証）が必要なため Critical で1行だけ通知する。
        /// </summary>
        private static void DisablePrecache(string reason)
        {
            Unavailable = true;

            var config = PluginConfig.Instance;
            if (config != null && config.PrecacheChannelEmotes)
            {
                config.PrecacheChannelEmotes = false;
            }

            Log.Critical($"[Precache] disabled: {reason}. " +
                         "Re-authenticate in Enhanced Stream Chat, then turn 'Pre-Cache Channel Emotes' back on.");
        }

        // ───────────────────────── ChatCore イベント（ネットワークスレッド） ─────────────────────────

        private void OnRoomStateUpdated(IChatService service, IChatChannel channel)
        {
            try
            {
                // broadcaster_id は TwitchChannel.Id（＝チャンネル名）ではなく
                // ROOMSTATE 由来の TwitchChannel.Roomstate.RoomId であることに注意。
                var roomId = (channel as TwitchChannel)?.Roomstate?.RoomId;
                if (string.IsNullOrEmpty(roomId)) return;

                _discoveredRoomIds.TryAdd(roomId, 0);
            }
            catch (Exception ex)
            {
                Log.Error("[Precache] failed to handle room state", ex);
            }
        }

        private void OnLeaveChannel(IChatService service, IChatChannel channel)
        {
            try
            {
                var roomId = (channel as TwitchChannel)?.Roomstate?.RoomId;
                if (string.IsNullOrEmpty(roomId)) return;
                _discoveredRoomIds.TryRemove(roomId, out _);
            }
            catch (Exception ex)
            {
                Log.Error("[Precache] failed to handle channel leave", ex);
            }
        }

        // ───────────────────────── 毎フレーム処理（メインスレッド） ─────────────────────────

        internal void Tick()
        {
            if (_chatCore == null || _provider == null) return;
            if (Unavailable) return;

            var config = PluginConfig.Instance;
            if (config == null || !config.Enabled || !config.PrecacheChannelEmotes) return;
            if (_inSong) return;

            // 未取得のチャンネルを洗い出す
            _fetchTargets.Clear();
            foreach (var kvp in _discoveredRoomIds)
            {
                if (_fetchedRoomIds.Contains(kvp.Key)) continue;
                _fetchTargets.Add(kvp.Key);
            }

            var now = Time.unscaledTime;

            if (_fetchTargets.Count == 0)
            {
                _nextFetchAllowedAt = float.PositiveInfinity;
            }
            else if (float.IsPositiveInfinity(_nextFetchAllowedAt))
            {
                // 接続直後の負荷を避けるため PrecacheDelaySec だけ待つ
                _nextFetchAllowedAt = now + Mathf.Max(0f, config.PrecacheDelaySec);
                Log.Debug($"[Precache] {_fetchTargets.Count} channel(s) discovered; fetching in {config.PrecacheDelaySec:F1}s");
            }
            else if (now >= _nextFetchAllowedAt && Interlocked.CompareExchange(ref _fetching, 1, 0) == 0)
            {
                var targets = _fetchTargets.ToArray();
                for (var i = 0; i < targets.Length; i++) _fetchedRoomIds.Add(targets[i]);
                _nextFetchAllowedAt = float.PositiveInfinity;

                var maxCount = Mathf.Max(0, config.MaxPrecacheCount - _precachedTotal);
                _ = FetchAsync(targets, maxCount, _cts?.Token ?? CancellationToken.None);
            }

            // 取得済みエントリの投入
            if (_feedCoroutine == null && !_ready.IsEmpty)
            {
                _feedCoroutine = CepRunner.Run(FeedRoutine());
            }
        }

        private void StopFeeding()
        {
            if (_feedCoroutine == null) return;
            CepRunner.Stop(_feedCoroutine);
            _feedCoroutine = null;
        }

        /// <summary>PrecacheIntervalMs 間隔で1件ずつ ESC のキャッシュへ投入する。</summary>
        private IEnumerator FeedRoutine()
        {
            var config = PluginConfig.Instance;
            var intervalSec = Mathf.Max(0.01f, (config?.PrecacheIntervalMs ?? 200) / 1000f);
            var fed = 0;

            while (_ready.TryDequeue(out var entry))
            {
                if (_inSong) break;

                config = PluginConfig.Instance;
                if (config == null || !config.Enabled || !config.PrecacheChannelEmotes) break;

                if (!_provider.CachedImageInfo.ContainsKey(entry.Id))
                {
                    var completed = false;
                    // Finally は必ず非 null を渡すこと（ESC 側が null チェックしていないため）
                    yield return _provider.TryCacheSingleImage(
                        entry.Id, entry.Uri, entry.Type, _ => completed = true);

                    if (completed)
                    {
                        _precachedTotal++;
                        fed++;
                    }
                }

                yield return new WaitForSecondsRealtime(intervalSec);
            }

            Log.Info($"[Precache] feed finished (+{fed}, total cached {_precachedTotal})");
            _feedCoroutine = null;
        }

        // ───────────────────────── Helix API ─────────────────────────

        private async Task FetchAsync(string[] roomIds, int maxCount, CancellationToken ct)
        {
            char[] token = null;
            HttpClient client = null;

            try
            {
                if (maxCount <= 0)
                {
                    Log.Debug("[Precache] MaxPrecacheCount reached; skipping fetch");
                    return;
                }

                token = ReadTokenFromAuthIni();
                if (token == null || token.Length == 0)
                {
                    DisablePrecache("no Twitch credentials were found in ChatCore auth.ini");
                    return;
                }

                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                }
                catch { /* 環境によっては設定不可。既定値のまま続行する */ }

                client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

                // Helix は「トークンの発行元 client_id」と一致する Client-Id ヘッダを要求する。
                // 検証に失敗すると client_id を知る手段が無く、以降 Helix へ一切アクセスできない。
                var clientId = await ResolveClientIdAsync(client, token, ct).ConfigureAwait(false);
                if (clientId == null)
                {
                    DisablePrecache("the Twitch token in ChatCore auth.ini could not be validated");
                    return;
                }

                var added = 0;
                for (var i = 0; i < roomIds.Length && added < maxCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    added += await FetchChannelAsync(client, clientId, token, roomIds[i], maxCount - added, ct)
                        .ConfigureAwait(false);
                }

                Log.Info($"[Precache] queued {added} channel emote(s) from {roomIds.Length} channel(s)");
            }
            catch (OperationCanceledException)
            {
                // 終了時のキャンセルは正常系
            }
            catch (Exception ex)
            {
                // 例外メッセージにトークンが含まれ得る経路は無いが、念のため型名のみに留める
                Log.Error($"[Precache] fetch failed: {ex.GetType().Name}");
            }
            finally
            {
                if (token != null) Array.Clear(token, 0, token.Length);
                client?.Dispose();
                Interlocked.Exchange(ref _fetching, 0);
            }
        }

        /// <summary>
        /// トークン検証エンドポイントで発行元 client_id を得る。失敗時は null。
        /// client_id は秘密情報ではないためログに出してよい（切り分けに有用）。
        /// </summary>
        private static async Task<string> ResolveClientIdAsync(HttpClient client, char[] token, CancellationToken ct)
        {
            try
            {
                string body;
                using (var request = new HttpRequestMessage(HttpMethod.Get, ValidateUrl))
                {
                    // SPEC §21-7：HttpRequestMessage.Headers が string を要求するため、
                    // トークンの string 化はここと Helix 呼び出しの2箇所のみ（消去不可）。
                    request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + new string(token));

                    using (var response = await client.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            Log.Error($"[Precache] token validation returned {(int)response.StatusCode} " +
                                      "(the token in ChatCore auth.ini is expired or invalid)");
                            return null;
                        }
                        body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }

                var root = JObject.Parse(body);
                var clientId = root["client_id"]?.ToString();
                var login = root["login"]?.ToString();

                if (string.IsNullOrEmpty(clientId))
                {
                    Log.Error("[Precache] token validation response did not contain a client_id");
                    return null;
                }

                Log.Info($"[Precache] token validated (client_id={clientId}, login={login})");
                return clientId;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"[Precache] token validation failed: {ex.GetType().Name}");
                return null;
            }
        }

        private async Task<int> FetchChannelAsync(
            HttpClient client, string clientId, char[] token, string roomId, int remaining, CancellationToken ct)
        {
            string body;
            using (var request = new HttpRequestMessage(HttpMethod.Get, HelixEmotesUrl + roomId))
            {
                request.Headers.TryAddWithoutValidation("Client-Id", clientId);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + new string(token));

                using (var response = await client.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Log.Error($"[Precache] Helix returned {(int)response.StatusCode} for broadcaster {roomId}");
                        return 0;
                    }
                    body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }

            var root = JObject.Parse(body);
            if (!(root["data"] is JArray data))
            {
                Log.Debug($"[Precache] broadcaster {roomId}: unexpected response shape");
                return 0;
            }

            if (data.Count == 0)
            {
                // アフィリエイト／パートナーでないチャンネルはここに来る。想定内。
                Log.Debug($"[Precache] broadcaster {roomId}: no channel emotes (not an affiliate/partner)");
                return 0;
            }

            var added = 0;
            foreach (var item in data)
            {
                if (added >= remaining) break;

                var emoteId = item["id"]?.ToString();
                if (string.IsNullOrEmpty(emoteId)) continue;

                // ESC のキャッシュキーと一致させるため ChatCore の命名規則に合わせる（SPEC §15）
                var id = "TwitchEmote_" + emoteId;
                var uri = "https://static-cdn.jtvnw.net/emoticons/v1/" + emoteId + "/3.0";

                var (resolvedUri, animType) =
                    ChatMessageBuilder.GetEmoteUriAndAnimationType(new PrecacheEmote(id, uri));
                if (string.IsNullOrEmpty(resolvedUri)) continue;

                _ready.Enqueue(new PrecacheEntry { Id = id, Uri = resolvedUri, Type = animType });
                added++;
            }

            Log.Debug($"[Precache] broadcaster {roomId}: {added} emote(s) queued (available={data.Count})");
            return added;
        }

        // ───────────────────────── auth.ini ─────────────────────────

        /// <summary>
        /// <c>%LOCALAPPDATA%\.chatcore\auth.ini</c>（ChatCore が生成・管理するファイル）から
        /// OAuth トークンを読み取る。<b>読み取り専用で開き、書き込みは一切行わない。</b>
        /// 戻り値は呼び出し側が <see cref="Array.Clear(Array,int,int)"/> する責任を持つ。
        /// 失敗時は null（ログには理由を出さない）。
        /// </summary>
        private static char[] ReadTokenFromAuthIni()
        {
            char[] buffer = null;
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AuthIniDirectory);
                var path = Path.Combine(dir, AuthIniFileName);
                if (!File.Exists(path)) return null;

                int length;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    buffer = new char[16 * 1024];
                    length = reader.Read(buffer, 0, buffer.Length);
                }

                if (length <= 0) return null;

                var start = FindValueStart(buffer, length, OAuthKeyDotted);
                if (start < 0) start = FindValueStart(buffer, length, OAuthKeyUnderscored);
                if (start < 0) return null;

                return ExtractQuotedValue(buffer, length, start);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (buffer != null) Array.Clear(buffer, 0, buffer.Length);
            }
        }

        /// <summary>
        /// <paramref name="key"/> に続く '=' の直後の位置を返す。見つからなければ -1。
        /// 文字列を生成せずに走査する。
        /// </summary>
        private static int FindValueStart(char[] buffer, int length, string key)
        {
            var keyLength = key.Length;
            for (var i = 0; i + keyLength <= length; i++)
            {
                var matched = true;
                for (var k = 0; k < keyLength; k++)
                {
                    if (buffer[i + k] == key[k]) continue;
                    matched = false;
                    break;
                }
                if (!matched) continue;

                var j = i + keyLength;
                while (j < length && (buffer[j] == ' ' || buffer[j] == '\t')) j++;
                if (j < length && buffer[j] == '=') return j + 1;
            }
            return -1;
        }

        /// <summary>
        /// <c>="oauth:xxxx" //comment</c> 形式から値だけを取り出す。
        /// クォートが無い場合は行末（または空白／コメント開始）までを値とみなす。
        /// </summary>
        private static char[] ExtractQuotedValue(char[] buffer, int length, int start)
        {
            var i = start;
            while (i < length && (buffer[i] == ' ' || buffer[i] == '\t')) i++;
            if (i >= length) return null;

            int from, to;
            if (buffer[i] == '"')
            {
                from = i + 1;
                to = from;
                while (to < length && buffer[to] != '"') to++;
            }
            else
            {
                from = i;
                to = from;
                while (to < length && buffer[to] != '\r' && buffer[to] != '\n' &&
                       buffer[to] != ' ' && buffer[to] != '\t') to++;
            }

            if (to <= from) return null;

            // "oauth:" プレフィックスを除去する
            var prefixLength = OAuthPrefix.Length;
            if (to - from > prefixLength)
            {
                var hasPrefix = true;
                for (var k = 0; k < prefixLength; k++)
                {
                    if (char.ToLowerInvariant(buffer[from + k]) == OAuthPrefix[k]) continue;
                    hasPrefix = false;
                    break;
                }
                if (hasPrefix) from += prefixLength;
            }

            var result = new char[to - from];
            Array.Copy(buffer, from, result, 0, result.Length);
            return result;
        }
    }
}
