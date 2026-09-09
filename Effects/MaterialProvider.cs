using System;
using System.IO;
using System.Reflection;
using ChatEmoteParticles.Utilities;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChatEmoteParticles.Effects
{
    /// <summary>
    /// パーティクル用マテリアルの調達（SPEC §10）。
    ///
    /// <para><b>専用シェーダを同梱する理由</b>：当初はゲーム内シェーダの流用を試みたが、
    /// 以下の問題があり環境依存の不具合が解消できなかった。</para>
    /// <list type="bullet">
    /// <item><c>UINoGlow</c>（＝<c>Custom/CustomUI</c>）は UI 専用で、同じ設定にしても白飛びする</item>
    /// <item><c>Custom/CustomParticles</c> は <c>[Toggle(KEYWORD)]</c> 形式で、キーワード名を
    ///       実測に頼るしかなく、ゲーム更新で壊れ得る</item>
    /// <item>他 MOD が同名シェーダを持ち込むと、どちらを掴むかがロード順次第になる</item>
    /// </list>
    ///
    /// <para>固有名 <c>ChatEmoteParticles/Emote</c> の自前シェーダを AssetBundle で同梱することで、
    /// 名前衝突・キーワード推測・ロード順のいずれからも解放される。
    /// Bloom（白飛び）対策もシェーダ内のブレンド指定で完結する。</para>
    ///
    /// <para>バンドルの読み込みに失敗した場合のみ、従来のゲーム内シェーダ探索へ退避する。</para>
    /// </summary>
    internal static class MaterialProvider
    {
        private const string BundleResource = "ChatEmoteParticles.Resources.chatemoteparticles.bundle";
        private const string BundledMaterialName = "ChatEmoteParticle";
        private const string BundledShaderName = "ChatEmoteParticles/Emote";

        private const int TransparentQueue = 3000;

        /// <summary>バンドルが使えない場合の代替。いずれも白飛びする可能性がある。</summary>
        private static readonly string[] FallbackShaders =
        {
            "Custom/CustomParticles",
            "UI/Default",
            "Sprites/Default",
            "Legacy Shaders/Particles/Alpha Blended",
        };

        private static AssetBundle s_bundle;
        private static Material s_template;
        private static bool s_resolved;

        /// <summary>調達に失敗した場合 true。呼び出し側は機能を無効化すること。</summary>
        internal static bool Failed { get; private set; }

        /// <summary>同梱シェーダではなく代替を使っている場合 true。</summary>
        internal static bool UsingFallback { get; private set; }

        /// <summary>エモート1種ぶんのマテリアルインスタンスを作る。失敗時は null。</summary>
        internal static Material CreateInstance()
        {
            var template = GetTemplate();
            if (template == null) return null;

            return new Material(template) { name = "ChatEmoteParticles/Emote (instance)" };
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

        /// <summary>プラグイン無効化時に AssetBundle を解放する。</summary>
        internal static void Unload()
        {
            if (s_template != null)
            {
                UnityEngine.Object.Destroy(s_template);
                s_template = null;
            }

            if (s_bundle != null)
            {
                s_bundle.Unload(true);
                s_bundle = null;
            }

            s_resolved = false;
            Failed = false;
            UsingFallback = false;
        }

        // ───────────────────────── テンプレートの生成 ─────────────────────────

        private static Material GetTemplate()
        {
            if (s_resolved) return s_template;
            s_resolved = true;

            s_template = LoadFromBundle();
            if (s_template != null)
            {
                Log.Info($"[Material] using bundled shader '{BundledShaderName}'");
                return s_template;
            }

            // ここへ来るのは埋め込みリソースの欠落か、Unity バージョン不一致で
            // バンドルがロードできなかった場合。描画品質が落ちるため常時通知する。
            UsingFallback = true;
            Log.Error("[Material] the bundled shader could not be loaded; " +
                      "falling back to an in-game shader. Emotes may look washed out.");

            var shader = ResolveFallbackShader();
            if (shader == null)
            {
                Failed = true;
                Log.Error("[Material] no usable shader found. ChatEmoteParticles will not render anything.");
                return null;
            }

            s_template = new Material(shader) { name = "ChatEmoteParticles/Template (fallback)" };
            ConfigureFallback(s_template);
            return s_template;
        }

        /// <summary>埋め込みリソースの AssetBundle からマテリアルを取り出す。</summary>
        private static Material LoadFromBundle()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(BundleResource))
                {
                    if (stream == null)
                    {
                        Log.Error($"[Material] embedded resource not found: {BundleResource}");
                        return null;
                    }

                    byte[] bytes;
                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        bytes = memory.ToArray();
                    }

                    s_bundle = AssetBundle.LoadFromMemory(bytes);
                }

                if (s_bundle == null)
                {
                    // 典型的にはビルドに使った Unity のバージョンがゲームと一致していない
                    Log.Error("[Material] AssetBundle.LoadFromMemory returned null " +
                              "(the bundle was likely built with a different Unity version)");
                    return null;
                }

                var material = s_bundle.LoadAsset<Material>(BundledMaterialName);
                if (material == null)
                {
                    Log.Error($"[Material] material '{BundledMaterialName}' was not found in the bundle");
                    return null;
                }

                if (material.shader == null || material.shader.name != BundledShaderName)
                {
                    Log.Error($"[Material] bundled material references an unexpected shader " +
                              $"'{material.shader?.name ?? "<null>"}'");
                    return null;
                }

                material.renderQueue = TransparentQueue;
                return material;
            }
            catch (Exception ex)
            {
                Log.Error($"[Material] failed to load the bundled shader: {ex.GetType().Name}");
                return null;
            }
        }

        private static Shader ResolveFallbackShader()
        {
            for (var i = 0; i < FallbackShaders.Length; i++)
            {
                var shader = FindShader(FallbackShaders[i]);
                if (shader == null) continue;

                Log.Error($"[Material] fallback shader in use: '{FallbackShaders[i]}'");
                return shader;
            }
            return null;
        }

        /// <summary>
        /// シェーダを名前で探す。
        /// <c>Shader.Find</c> は Resources / Always Included Shaders に登録されたものしか返さないため、
        /// ロード済みオブジェクトからの検索を優先する。
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

        // ───────────────────────── フォールバック時の設定 ─────────────────────────

        /// <summary>
        /// 代替シェーダ向けの最低限の設定。
        /// 同梱シェーダを使う場合はシェーダ側で完結しているため何もしない。
        /// </summary>
        private static void ConfigureFallback(Material mat)
        {
            mat.renderQueue = TransparentQueue;

            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", Color.white);

            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)CullMode.Off);

            if (mat.HasProperty("_ZTest"))
            {
                var current = mat.GetFloat("_ZTest");
                if (Mathf.Approximately(current, (float)CompareFunction.Always) || current <= 0f)
                {
                    mat.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
                }
            }

            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);

            // Custom/CustomParticles にフォールバックした場合のみ意味を持つ設定。
            // 詳細は本クラスの docstring を参照。
            if (mat.HasProperty("_BlendSrcFactor")) mat.SetFloat("_BlendSrcFactor", (float)BlendMode.One);
            if (mat.HasProperty("_BlendDstFactor")) mat.SetFloat("_BlendDstFactor", (float)BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_BlendSrcFactorA")) mat.SetFloat("_BlendSrcFactorA", (float)BlendMode.Zero);
            if (mat.HasProperty("_BlendDstFactorA")) mat.SetFloat("_BlendDstFactorA", (float)BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_BloomMultiplier")) mat.SetFloat("_BloomMultiplier", 0f);

            if (mat.HasProperty("_Stencil")) mat.SetFloat("_Stencil", 0f);
            if (mat.HasProperty("_StencilComp")) mat.SetFloat("_StencilComp", (float)CompareFunction.Always);

            mat.DisableKeyword("ETC1_EXTERNAL_ALPHA");
            mat.DisableKeyword("UNITY_UI_CLIP_RECT");
            mat.DisableKeyword("UNITY_UI_ALPHACLIP");
        }
    }
}
