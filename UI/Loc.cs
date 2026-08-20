using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BGLib.Polyglot;
using ChatEmoteParticles.Utilities;
using Newtonsoft.Json.Linq;

namespace ChatEmoteParticles.UI
{
    /// <summary>
    /// 日本語 / 英語のみの簡易ローカライズ（SPEC §19）。
    /// BSML / Polyglot のローカライズ API には依存せず、埋め込み JSON を自前で読む。
    ///
    /// <para>フォールバック順：ja.json → en.json → キー文字列そのまま。</para>
    /// <para>言語判定は <see cref="Get"/> のたびに行うが、辞書自体はキャッシュするため
    /// 再読み込みは発生しない。<see cref="Get"/> が呼ばれるのは BSML のパース時だけなので
    /// ホットパスには影響しない。</para>
    /// <para>制約：ゲーム本体の言語を切り替えても、設定画面を開き直すまで反映されない。</para>
    /// </summary>
    internal static class Loc
    {
        private const string EnglishResource = "ChatEmoteParticles.UI.Localization.en.json";
        private const string JapaneseResource = "ChatEmoteParticles.UI.Localization.ja.json";

        private static Dictionary<string, string> s_english;
        private static Dictionary<string, string> s_japanese;
        private static bool s_englishLoaded;
        private static bool s_japaneseLoaded;

        internal static string Get(string key)
        {
            var primary = IsJapanese() ? GetJapanese() : GetEnglish();

            if (primary != null && primary.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }

            var fallback = GetEnglish();
            if (fallback != null && fallback.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }

            // 翻訳漏れが一目で分かるようキーをそのまま返す
            return key;
        }

        private static Dictionary<string, string> GetEnglish()
        {
            if (s_englishLoaded) return s_english;
            s_englishLoaded = true;
            s_english = Load(EnglishResource);
            return s_english;
        }

        private static Dictionary<string, string> GetJapanese()
        {
            if (s_japaneseLoaded) return s_japanese;
            s_japaneseLoaded = true;
            s_japanese = Load(JapaneseResource);
            return s_japanese;
        }

        /// <summary>
        /// 1.40.8 では Polyglot は <c>BGLib.Polyglot</c> 名前空間に移動している。
        ///
        /// <para>言語の enum 型名はバージョンによって異なるため（1.40.9 は <c>LocalizationLanguage</c>、
        /// 1.40.8 では別名）、型名に依存しない名前比較で判定する。
        /// enum のメンバ名 <c>Japanese</c> は Polyglot のどの世代でも共通。</para>
        /// </summary>
        private static bool IsJapanese()
        {
            try
            {
                var instance = Localization.Instance;
                if (instance == null) return false;

                var language = instance.SelectedLanguage.ToString();
                return string.Equals(language, "Japanese", StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                Log.Debug($"[Loc] language detection failed: {ex.GetType().Name}");
                return false;   // 取得失敗時は英語
            }
        }

        private static Dictionary<string, string> Load(string resourceName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        Log.Error($"[Loc] embedded resource not found: {resourceName}");
                        return null;
                    }

                    using (var reader = new StreamReader(stream))
                    {
                        var json = JObject.Parse(reader.ReadToEnd());
                        var map = new Dictionary<string, string>(json.Count, StringComparer.Ordinal);
                        foreach (var pair in json)
                        {
                            map[pair.Key] = pair.Value?.ToString() ?? string.Empty;
                        }
                        return map;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Loc] failed to load {resourceName}", ex);
                return null;
            }
        }
    }
}
