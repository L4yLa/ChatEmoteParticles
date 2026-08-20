using ChatCore.Interfaces;
using ChatCore.Models;
using ChatCore.Utilities;

namespace ChatEmoteParticles.Chat
{
    /// <summary>
    /// プリキャッシュ時に ESC の <c>GetEmoteUriAndAnimationType()</c> へ渡すための最小実装。
    ///
    /// <para><see cref="ChatCore.Models.Twitch.TwitchEmote"/> は setter が internal のため
    /// 外部アセンブリから値を設定できない。JSON コンストラクタ経由で組み立てることも可能だが、
    /// ESC 側は <see cref="Uri"/> と <see cref="IsAnimated"/> しか参照しないため、
    /// 余計な依存を持たない本クラスで代替する（SPEC §15 の意図と等価）。</para>
    /// </summary>
    internal sealed class PrecacheEmote : IChatEmote
    {
        internal PrecacheEmote(string id, string uri)
        {
            Id = id;
            Name = id;
            Uri = uri;
        }

        public string Id { get; }
        public string Name { get; }
        public string Uri { get; }
        public int StartIndex => 0;
        public int EndIndex => 0;

        /// <summary>
        /// Twitch v1 URI は ESC 側で v2 animated URL + MAYBE_GIF に書き換えられるため、
        /// ここでは常に false でよい（チャット受信時の ChatCore の挙動と同一）。
        /// </summary>
        public bool IsAnimated => false;

        public EmoteType Type => EmoteType.SingleImage;
        public ImageRect UVs => default;

        public JSONObject ToJson() => new JSONObject();
    }
}
