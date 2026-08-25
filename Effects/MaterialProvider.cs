using ChatEmoteParticles.Utilities;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChatEmoteParticles.Effects
{
    /// <summary>
    /// パーティクル用マテリアルの調達（SPEC §10）。AssetBundle は使用しない。
    ///
    /// <para><b>シェーダ選定の経緯</b>：仕様書は <c>UINoGlow</c> マテリアルの流用を想定していたが、
    /// その実体は <c>Custom/CustomUI</c>（UI 専用シェーダ）で、ゲーム本体とまったく同じ設定値・
    /// キーワードにしてもパーティクルとしては白飛びする。実機比較の結果、Beat Saber 自身の
    /// パーティクル用シェーダである <b><c>Custom/CustomParticles</c></b> のみが正しく描画できた。</para>
    ///
    /// <para><b>取得方法の注意</b>：<c>Shader.Find</c> は Resources / Always Included Shaders に
    /// 登録されたものしか返さないため <c>Custom/CustomParticles</c> を取りこぼす。
    /// <see cref="FindShader"/> でロード済みオブジェクトから検索すること。</para>
    /// </summary>
    internal static class MaterialProvider
    {
        private const int TransparentQueue = 3000;
        private const float ColorMaskRgba = 15f;

        /// <summary>
        /// 優先順にシェーダを試す。実機比較の結果に基づく順序。
        /// <list type="number">
        /// <item><c>Custom/CustomParticles</c> … 正しい色で描画される（本命）</item>
        /// <item><c>UI/Default</c> … 描画されるがやや白飛びする</item>
        /// <item>それ以降 … 白飛びするが、何も出ないよりはよい</item>
        /// </list>
        /// </summary>
        private static readonly string[] PreferredShaders =
        {
            "Custom/CustomParticles",
            "UI/Default",
            "Sprites/Default",
            "Legacy Shaders/Particles/Alpha Blended",
        };

        /// <summary>
        /// <c>Custom/CustomParticles</c> で有効化するキーワード。
        /// ゲーム内 <c>SlashTrail</c> マテリアルの実測値から、本 MOD に不要なもの
        /// （歪み・ソフトパーティクル・<c>ETC1_EXTERNAL_ALPHA</c>）を除いたもの。
        /// </summary>
        private static readonly string[] CustomParticlesKeywords =
        {
            "ENABLE_TEXTURE_COLOR",
            "TEXTURE_COLOR",
            "ENABLE_VERTEX_COLOR",
            "VERTEX_COLOR",
            "_ALPHACHANNEL_ALPHA",
            "_VERTEXCHANNELS_RGBA",
            "_WHITEBOOSTTYPE_NONE",
            "_BILLBOARD_NONE",
            "_FOGTYPE_NONE",
            "_ROTATE_UV_NONE",
            "_CURVE_VERTICES_NONE",
            "_SPECTROGRAM_NONE",
            "DISTORTION_NONE",
            "_MASKBLEND_MULTIPLY",
            "_MASK2BLEND_MULTIPLY",
            "_DISSOLVEAXIS_LOCALX",
            "_CLIP_LOW_ALPHA",
        };

        /// <summary>UI コンテキスト固有で、ワールド空間のパーティクルでは害になるキーワード。</summary>
        private static readonly string[] UnwantedKeywords =
        {
            "ETC1_EXTERNAL_ALPHA",     // _AlphaTex 未設定でアルファが 0 になる
            "UNITY_UI_CLIP_RECT",      // _ClipRect (0,0,0,0) で全クリップされる
            "UNITY_UI_ALPHACLIP",
        };

        private static Material s_template;
        private static bool s_resolved;

        /// <summary>調達に失敗した場合 true。呼び出し側は機能を無効化すること。</summary>
        internal static bool Failed { get; private set; }

        /// <summary>エモート1種ぶんのマテリアルインスタンスを作る。失敗時は null。</summary>
        internal static Material CreateInstance()
        {
            var template = GetTemplate();
            if (template == null) return null;

            // 2026-08-25: hideFlags 未設定だと Resources.UnloadUnusedAssets() の候補になる。
            // 破棄されても IsAlive は Root/Ps しか見ないため、無言で描画されなくなる。
            // 明示的な Object.Destroy（EmoteEmitter.Destroy）はこのフラグでも有効。
            return new Material(template)
            {
                name = "ChatEmoteParticles/Emote",
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
        }

        /// <summary>
        /// テクスチャを割り当てる。<c>mainTexture</c> が効かないシェーダに備えて
        /// <c>_MainTex</c> にも直接設定する。
        /// </summary>
        internal static void ApplyTexture(Material mat, Texture texture)
        {
            if (mat == null) return;

            mat.mainTexture = texture;
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", texture);
        }

        /// <summary>
        /// シェーダを名前で探す。
        ///
        /// <para><c>Shader.Find</c> は Resources / Always Included Shaders に登録されたものしか返さず、
        /// ゲームがロード済みのシェーダ（<c>Custom/CustomParticles</c> など）を取りこぼす。
        /// ロード済みオブジェクトからの検索を優先する。</para>
        /// </summary>
        private static Shader FindShader(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var shaders = Resources.FindObjectsOfTypeAll<Shader>();
            for (var i = 0; i < shaders.Length; i++)
            {
                var shader = shaders[i];
                if (shader != null && shader.name == name) return shader;
            }

            return Shader.Find(name);
        }

        // ───────────────────────── テンプレートの生成 ─────────────────────────

        private static Material GetTemplate()
        {
            // Unity の == オーバーロードで「破棄済み」も null として検出される。
            if (s_resolved && s_template != null) return s_template;

            // シェーダ自体が見つからなかった場合は再試行しない
            // （Resources.FindObjectsOfTypeAll は高コストなため毎フレーム呼ばない）。
            if (Failed) return null;

            // 2026-08-25: 以前は s_resolved だけを見ていたため、テンプレートが破棄されると
            // 以降すべての Emitter 生成が無言で失敗し続ける状態に陥っていた。
            if (s_resolved)
            {
                Log.Warn("[Material] template material was destroyed; rebuilding");
            }
            s_resolved = true;

            var shader = ResolveShader();
            if (shader == null)
            {
                Failed = true;
                Log.Error("[Material] no usable shader found. ChatEmoteParticles will not render anything.");
                return null;
            }

            s_template = new Material(shader)
            {
                name = "ChatEmoteParticles/Template",
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            Configure(s_template);
            return s_template;
        }

        private static Shader ResolveShader()
        {
            for (var i = 0; i < PreferredShaders.Length; i++)
            {
                var shader = FindShader(PreferredShaders[i]);
                if (shader == null) continue;

                if (i == 0)
                {
                    Log.Info($"[Material] using shader '{PreferredShaders[i]}'");
                }
                else
                {
                    Log.Warn($"[Material] '{PreferredShaders[0]}' not found; " +
                             $"falling back to '{PreferredShaders[i]}' (emotes may look washed out)");
                }
                return shader;
            }

            return null;
        }

        // ───────────────────────── 設定 ─────────────────────────

        /// <summary>
        /// シェーダ既定値のままでは成立しない項目を明示的に設定する。
        /// プロパティが存在しないシェーダでは何もしない。
        /// </summary>
        private static void Configure(Material mat)
        {
            mat.renderQueue = TransparentQueue;

            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", Color.white);

            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)CullMode.Off);

            // ZTest が Always だとノーツやセイバーを貫通するため LessEqual に戻す
            if (mat.HasProperty("_ZTest"))
            {
                var current = mat.GetFloat("_ZTest");
                if (Mathf.Approximately(current, (float)CompareFunction.Always) || current <= 0f)
                {
                    mat.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
                }
            }

            // Unity 標準名のブレンド係数
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);

            // Beat Saber 独自名のブレンド係数。RGB は乗算済みアルファ、
            // アルファ側は destination alpha を 0 へ押し下げてブルームを抑える。
            if (mat.HasProperty("_BlendSrcFactor")) mat.SetFloat("_BlendSrcFactor", (float)BlendMode.One);
            if (mat.HasProperty("_BlendDstFactor")) mat.SetFloat("_BlendDstFactor", (float)BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_BlendSrcFactorA")) mat.SetFloat("_BlendSrcFactorA", (float)BlendMode.Zero);
            if (mat.HasProperty("_BlendDstFactorA")) mat.SetFloat("_BlendDstFactorA", (float)BlendMode.OneMinusSrcAlpha);

            // Mask 由来のステンシル設定が残っているとパーティクルが常にテストに落ちる
            if (mat.HasProperty("_Stencil")) mat.SetFloat("_Stencil", 0f);
            if (mat.HasProperty("_StencilComp")) mat.SetFloat("_StencilComp", (float)CompareFunction.Always);
            if (mat.HasProperty("_StencilOp")) mat.SetFloat("_StencilOp", 0f);
            if (mat.HasProperty("_StencilReadMask")) mat.SetFloat("_StencilReadMask", 255f);
            if (mat.HasProperty("_StencilWriteMask")) mat.SetFloat("_StencilWriteMask", 255f);
            if (mat.HasProperty("_ColorMask")) mat.SetFloat("_ColorMask", ColorMaskRgba);

            for (var i = 0; i < UnwantedKeywords.Length; i++)
            {
                mat.DisableKeyword(UnwantedKeywords[i]);
            }

            if (mat.HasProperty("_EnableExternalAlpha")) mat.SetFloat("_EnableExternalAlpha", 0f);
            if (mat.HasProperty("_UseUIAlphaClip")) mat.SetFloat("_UseUIAlphaClip", 0f);
            if (mat.HasProperty("_ClipRect"))
            {
                mat.SetVector("_ClipRect", new Vector4(-100000f, -100000f, 100000f, 100000f));
            }

            if (mat.HasProperty("_EnableTextureColor")) ConfigureCustomParticles(mat);
        }

        /// <summary>
        /// <c>Custom/CustomParticles</c> 向けの設定。
        ///
        /// <para>このシェーダは <c>[Toggle(KEYWORD)]</c> / <c>[KeywordEnum(...)]</c> によって
        /// float プロパティとシェーダキーワードが連動する。エディタ上は自動で同期されるが、
        /// ランタイムの <c>SetFloat</c> ではキーワードが変わらないため、
        /// <c>EnableKeyword</c> を別途呼ばないとシェーダ側の <c>#if</c> が成立しない。</para>
        ///
        /// <para>キーワード名はゲーム内 <c>SlashTrail</c> マテリアルの実測値に基づく。
        /// <c>_*_NONE</c> 系は <c>[KeywordEnum]</c> 由来で、1つも立てないと意図しない
        /// バリアントで動作するため明示的に有効化する。</para>
        /// </summary>
        private static void ConfigureCustomParticles(Material mat)
        {
            // 値とキーワードの両方を揃える
            mat.SetFloat("_EnableTextureColor", 1f);
            if (mat.HasProperty("_EnableVertexColor")) mat.SetFloat("_EnableVertexColor", 1f);
            if (mat.HasProperty("_AlphaChannel")) mat.SetFloat("_AlphaChannel", 0f);      // アルファチャンネル
            if (mat.HasProperty("_VertexChannels")) mat.SetFloat("_VertexChannels", 0f);  // RGBA
            if (mat.HasProperty("_WhiteBoostType")) mat.SetFloat("_WhiteBoostType", 0f);  // 白ブーストなし
            if (mat.HasProperty("_Billboard")) mat.SetFloat("_Billboard", 0f);            // PS 側で Billboard 済み
            if (mat.HasProperty("_FogType")) mat.SetFloat("_FogType", 0f);
            if (mat.HasProperty("_Intensity")) mat.SetFloat("_Intensity", 1f);

            for (var i = 0; i < CustomParticlesKeywords.Length; i++)
            {
                mat.EnableKeyword(CustomParticlesKeywords[i]);
            }
        }
    }
}
