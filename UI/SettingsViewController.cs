using System;
using System.Collections.Generic;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Parser;
using ChatEmoteParticles.Configuration;
using ChatEmoteParticles.Effects;
using ChatEmoteParticles.Utilities;
using Zenject;

namespace ChatEmoteParticles.UI
{
    /// <summary>
    /// BSML の Mod Settings タブに表示する設定画面のホスト（SPEC §18）。
    ///
    /// <para>ラベルは <c>~</c> バインドでパース時に解決するため、言語切替は
    /// 設定画面を開き直したときに反映される。</para>
    /// <para>ドロップダウンは内部値を enum のまま保持し、表示名だけを formatter で差し替えるため、
    /// 設定ファイルの互換性が保たれる。</para>
    /// </summary>
    public class SettingsViewController
    {
        private const string AdvancedTag = "advanced";

        private readonly EmoteEmitterManager _manager;

        [UIParams] private readonly BSMLParserParams _parserParams = null;

        /// <summary>メニュー閉じイベントを登録済みか。二重登録を防ぐ。</summary>
        private bool _closeEventsHooked;

        public SettingsViewController([InjectOptional] EmoteEmitterManager manager)
        {
            _manager = manager;
        }

        private static PluginConfig C => PluginConfig.Instance;

        /// <summary>
        /// 設定変更時に呼ばれる。
        ///
        /// <para>BSML の setting コンポーネントは <c>OnChange?.Invoke(値)</c> と必ず引数 1 つで
        /// 呼び出す。<c>BSMLAction.Invoke</c> は引数なしへのフォールバックを持たないため、
        /// 引数を受けないと <c>TargetParameterCountException</c> になる。
        /// 型はコントロールごとに異なる（bool / object / float / int）ため <c>object</c> で受ける。</para>
        ///
        /// <para><b>例外を外へ出さないこと</b>：このコールバックは HMUI の選択イベントの
        /// 途中で同期的に呼ばれる。例えば <c>DropDownListSetting.OnSelectIndex</c> は
        /// <c>OnChange</c> の後に <c>ApplyValue()</c> を呼ぶため、ここで例外を投げると
        /// モーダルの後始末が実行されず、ポインタ入力が壊れる。</para>
        /// </summary>
        [UIAction("on-changed")]
        public void OnChanged(object value)
        {
            try
            {
                // BSIPA の Generated Config は setter で Changed() を呼ぶが、
                // 呼ばれない構成でも派生値が古くならないよう明示的に再計算する（冪等）。
                C?.Apply();
                _manager?.RequestRevalidate();
            }
            catch (Exception ex)
            {
                Log.Error("[Settings] failed to apply setting change", ex);
            }
        }

        /// <summary>
        /// 「高度な設定を表示」トグルの変更時。
        ///
        /// <para><c>ToggleSetting.OnValueChanged</c> は <c>ApplyValue()</c> より<b>先に</b>
        /// <c>OnChange</c> を呼ぶため、この時点ではまだ設定値が更新されていない。
        /// 設定を読むと表示が1手前の状態（＝反転）になるので、
        /// ハンドラに渡された値を使うこと。</para>
        /// </summary>
        [UIAction("on-advanced-changed")]
        public void OnAdvancedChanged(object value)
        {
            try
            {
                var show = value is bool flag ? flag : (C != null && C.ShowAdvanced);
                SetAdvancedVisible(show);
            }
            catch (Exception ex)
            {
                Log.Error("[Settings] failed to toggle advanced settings", ex);
            }
        }

        [UIAction("#post-parse")]
        public void PostParse()
        {
            try
            {
                SetAdvancedVisible(C != null && C.ShowAdvanced);
                _manager?.RequestRevalidate();
                HookCloseEvents();
            }
            catch (Exception ex)
            {
                Log.Error("[Settings] post-parse failed", ex);
            }
        }

        /// <summary>
        /// OK / Cancel のいずれで抜けても MOD 側に変更を反映させる（2026-08-25）。
        ///
        /// <para>BSML の <c>ModSettingsFlowCoordinator</c> は OK で <c>apply</c>、
        /// Cancel で <c>cancel</c> イベントを全メニューに発行する。
        /// <c>GenericSettingHandler</c> は既定で <c>apply</c>→<c>ApplyValue()</c>、
        /// <c>cancel</c>→<c>ReceiveValue()</c> を紐づけるため、
        /// <c>apply-on-change='true'</c> を使っていない項目は Cancel で巻き戻される。</para>
        ///
        /// <para>本 MOD は全項目に <c>apply-on-change='true'</c> を付けているので
        /// 現状巻き戻しは発生しないが、将来 <c>apply-on-change</c> を付け忘れた項目が
        /// 紛れ込んでも派生値と描画状態が設定と一致するよう、
        /// 両イベントで再適用を行う。<c>#post-parse</c> で登録するため、
        /// 各 setting の <c>ReceiveValue()</c> よりあとに呼ばれる
        /// （<c>BSMLParserParams</c> のイベントは登録順のマルチキャスト）。</para>
        /// </summary>
        private void HookCloseEvents()
        {
            if (_closeEventsHooked || _parserParams == null) return;
            _closeEventsHooked = true;

            _parserParams.AddEvent("apply,cancel", ApplyToMod);
        }

        /// <summary>設定値から派生値を再計算し、MOD 側の状態を設定に追従させる。</summary>
        private void ApplyToMod()
        {
            try
            {
                C?.Apply();
                _manager?.RequestRevalidate();
            }
            catch (Exception ex)
            {
                Log.Error("[Settings] failed to apply settings on menu close", ex);
            }
        }

        /// <summary>
        /// <c>tags='advanced'</c> を付けた要素をまとめて表示／非表示する。
        /// VerticalLayoutGroup は非アクティブな子を無視するため、隠しても隙間は残らない。
        /// </summary>
        private void SetAdvancedVisible(bool show)
        {
            if (_parserParams == null) return;

            var objects = _parserParams.GetObjectsWithTag(AdvancedTag);
            if (objects == null) return;

            for (var i = 0; i < objects.Count; i++)
            {
                if (objects[i] != null) objects[i].SetActive(show);
            }
        }

        // ───────────────────────── ラベル ─────────────────────────

        [UIValue("l-section-general")] public string LSectionGeneral => Loc.Get("section.general");
        [UIValue("l-section-appearance")] public string LSectionAppearance => Loc.Get("section.appearance");
        [UIValue("l-section-zone")] public string LSectionZone => Loc.Get("section.zone");
        [UIValue("l-section-advanced")] public string LSectionAdvanced => Loc.Get("section.advanced");

        [UIValue("l-group-limits")] public string LGroupLimits => Loc.Get("group.limits");
        [UIValue("l-group-above-lane")] public string LGroupAboveLane => Loc.Get("group.above-lane");
        [UIValue("l-group-side")] public string LGroupSide => Loc.Get("group.side");
        [UIValue("l-group-menu")] public string LGroupMenu => Loc.Get("group.menu");

        [UIValue("l-enabled")] public string LEnabled => Loc.Get("enabled");
        [UIValue("l-enabled-in-menu")] public string LEnabledInMenu => Loc.Get("enabled-in-menu");
        [UIValue("l-enabled-in-song")] public string LEnabledInSong => Loc.Get("enabled-in-song");
        [UIValue("l-precache")] public string LPrecache => Loc.Get("precache");
        [UIValue("l-verbose")] public string LVerbose => Loc.Get("verbose");
        [UIValue("l-show-advanced")] public string LShowAdvanced => Loc.Get("show-advanced");

        [UIValue("l-style")] public string LStyle => Loc.Get("style");
        [UIValue("l-zone")] public string LZone => Loc.Get("zone");

        [UIValue("l-count-coefficient")] public string LCountCoefficient => Loc.Get("count-coefficient");
        [UIValue("l-max-emotes-per-message")] public string LMaxEmotesPerMessage => Loc.Get("max-emotes-per-message");
        [UIValue("l-max-concurrent-particles")] public string LMaxConcurrentParticles => Loc.Get("max-concurrent-particles");

        [UIValue("l-emote-size-min")] public string LEmoteSizeMin => Loc.Get("emote-size-min");
        [UIValue("l-emote-size-max")] public string LEmoteSizeMax => Loc.Get("emote-size-max");
        [UIValue("l-alpha")] public string LAlpha => Loc.Get("alpha");

        [UIValue("l-emote-z")] public string LEmoteZ => Loc.Get("emote-z");
        [UIValue("l-above-lane-half-x")] public string LAboveLaneHalfX => Loc.Get("above-lane-half-x");
        [UIValue("l-above-lane-min-y")] public string LAboveLaneMinY => Loc.Get("above-lane-min-y");
        [UIValue("l-above-lane-max-y")] public string LAboveLaneMaxY => Loc.Get("above-lane-max-y");
        [UIValue("l-side-inner-x")] public string LSideInnerX => Loc.Get("side-inner-x");
        [UIValue("l-side-outer-x")] public string LSideOuterX => Loc.Get("side-outer-x");
        [UIValue("l-side-min-y")] public string LSideMinY => Loc.Get("side-min-y");
        [UIValue("l-side-max-y")] public string LSideMaxY => Loc.Get("side-max-y");
        [UIValue("l-menu-emote-z")] public string LMenuEmoteZ => Loc.Get("menu-emote-z");
        [UIValue("l-menu-emote-scale")] public string LMenuEmoteScale => Loc.Get("menu-emote-scale");

        [UIValue("l-lifetime")] public string LLifeTime => Loc.Get("lifetime");
        [UIValue("l-speed-scale")] public string LSpeedScale => Loc.Get("speed-scale");

        // ───────────────────────── toggle ─────────────────────────

        [UIValue("enabled")]
        public bool Enabled { get => C.Enabled; set => C.Enabled = value; }

        [UIValue("enabled-in-menu")]
        public bool EnabledInMenu { get => C.EnabledInMenu; set => C.EnabledInMenu = value; }

        [UIValue("enabled-in-song")]
        public bool EnabledInSong { get => C.EnabledInSong; set => C.EnabledInSong = value; }

        [UIValue("precache")]
        public bool PrecacheChannelEmotes { get => C.PrecacheChannelEmotes; set => C.PrecacheChannelEmotes = value; }

        [UIValue("verbose")]
        public bool Verbose { get => C.Verbose; set => C.Verbose = value; }

        [UIValue("show-advanced")]
        public bool ShowAdvanced { get => C.ShowAdvanced; set => C.ShowAdvanced = value; }

        /// <summary>
        /// Pre-Cache トグルを操作できるか。
        /// Twitch トークンの検証に失敗している場合は Helix へ一切アクセスできないため、
        /// グレーアウトして再有効化を防ぐ。<c>~</c> バインドはパース時に解決されるため、
        /// 設定画面を開き直したタイミングで反映される。
        /// </summary>
        [UIValue("precache-interactable")]
        public bool PrecacheInteractable => !Chat.EmotePrecacheService.Unavailable;

        // ───────────────────────── dropdown ─────────────────────────

        [UIValue("style-options")]
        public List<object> StyleOptions { get; } = new List<object>
        {
            EmoteStyle.Shower, EmoteStyle.Fireworks, EmoteStyle.Fountain, EmoteStyle.Bubble,
        };

        [UIValue("zone-options")]
        public List<object> ZoneOptions { get; } = new List<object>
        {
            EmoteZone.AboveLane, EmoteZone.Sides, EmoteZone.Both,
        };

        [UIValue("style")]
        public EmoteStyle Style { get => C.Style; set => C.Style = value; }

        [UIValue("zone")]
        public EmoteZone Zone { get => C.Zone; set => C.Zone = value; }

        [UIAction("style-formatter")]
        public string StyleFormatter(object value) => Loc.Get("style." + value);

        [UIAction("zone-formatter")]
        public string ZoneFormatter(object value) => Loc.Get("zone." + value);

        // ───────────────────────── slider ─────────────────────────

        [UIValue("count-coefficient")]
        public int CountCoefficient { get => C.CountCoefficient; set => C.CountCoefficient = value; }

        [UIValue("max-emotes-per-message")]
        public int MaxEmotesPerMessage { get => C.MaxEmotesPerMessage; set => C.MaxEmotesPerMessage = value; }

        [UIValue("max-concurrent-particles")]
        public int MaxConcurrentParticles { get => C.MaxConcurrentParticles; set => C.MaxConcurrentParticles = value; }

        [UIValue("emote-size-min")]
        public float EmoteSizeMin { get => C.EmoteSizeMin; set => C.EmoteSizeMin = value; }

        [UIValue("emote-size-max")]
        public float EmoteSizeMax { get => C.EmoteSizeMax; set => C.EmoteSizeMax = value; }

        [UIValue("alpha")]
        public float Alpha { get => C.Alpha; set => C.Alpha = value; }

        [UIValue("emote-z")]
        public float EmoteZ { get => C.EmoteZ; set => C.EmoteZ = value; }

        [UIValue("above-lane-half-x")]
        public float AboveLaneHalfX { get => C.AboveLaneHalfX; set => C.AboveLaneHalfX = value; }

        [UIValue("above-lane-min-y")]
        public float AboveLaneMinY { get => C.AboveLaneMinY; set => C.AboveLaneMinY = value; }

        [UIValue("above-lane-max-y")]
        public float AboveLaneMaxY { get => C.AboveLaneMaxY; set => C.AboveLaneMaxY = value; }

        [UIValue("side-inner-x")]
        public float SideInnerX { get => C.SideInnerX; set => C.SideInnerX = value; }

        [UIValue("side-outer-x")]
        public float SideOuterX { get => C.SideOuterX; set => C.SideOuterX = value; }

        [UIValue("side-min-y")]
        public float SideMinY { get => C.SideMinY; set => C.SideMinY = value; }

        [UIValue("side-max-y")]
        public float SideMaxY { get => C.SideMaxY; set => C.SideMaxY = value; }

        [UIValue("menu-emote-z")]
        public float MenuEmoteZ { get => C.MenuEmoteZ; set => C.MenuEmoteZ = value; }

        [UIValue("menu-emote-scale")]
        public float MenuEmoteScale { get => C.MenuEmoteScale; set => C.MenuEmoteScale = value; }

        [UIValue("lifetime")]
        public float LifeTime { get => C.LifeTime; set => C.LifeTime = value; }

        [UIValue("speed-scale")]
        public float SpeedScale { get => C.SpeedScale; set => C.SpeedScale = value; }
    }
}
