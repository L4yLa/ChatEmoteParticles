using System;
using System.Collections;
using UnityEngine;

namespace ChatEmoteParticles.Utilities
{
    /// <summary>
    /// 本MOD唯一の MonoBehaviour。
    /// <list type="bullet">
    /// <item>Manager の Update 駆動（SPEC §17「Update を持つのは Manager 1個のみ」）</item>
    /// <item>ESC の IEnumerator（TryCacheSingleImage 等）を回すためのコルーチンホスト</item>
    /// </list>
    /// SPEC §9 では <c>SceneManager.CreateScene()</c> による隔離を想定していたが、
    /// 非 additive なシーンロードで破棄される可能性があるため <see cref="UnityEngine.Object.DontDestroyOnLoad"/>
    /// を採用している（隔離目的は同等に達成される）。
    /// </summary>
    internal sealed class CepRunner : MonoBehaviour
    {
        internal static CepRunner Instance { get; private set; }

        /// <summary>毎フレーム呼ばれる。例外は握りつぶしてログのみ出す。</summary>
        internal Action UpdateCallback;

        /// <summary>
        /// 連続失敗時にログを打ち切るまでの回数（2026-08-25）。
        /// <see cref="Log.Error"/> は Verbose 非依存のため、毎フレーム同じ例外が出ると
        /// ログ I/O だけでゲームが成立しなくなる。
        /// </summary>
        private const int MaxLoggedConsecutiveErrors = 5;

        private int _consecutiveErrors;

        internal static CepRunner GetOrCreate()
        {
            if (Instance != null) return Instance;

            var go = new GameObject("ChatEmoteParticles.Runner");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<CepRunner>();
            return Instance;
        }

        internal static void DestroyIfExists()
        {
            if (Instance == null) return;
            var go = Instance.gameObject;
            Instance = null;
            Destroy(go);
        }

        /// <summary>コルーチンを開始する。Runner が無い場合は null を返す。</summary>
        internal static Coroutine Run(IEnumerator routine)
        {
            if (routine == null) return null;
            var r = GetOrCreate();
            return r == null ? null : r.StartCoroutine(routine);
        }

        internal static void Stop(Coroutine c)
        {
            if (c == null || Instance == null) return;
            Instance.StopCoroutine(c);
        }

        private void Update()
        {
            try
            {
                UpdateCallback?.Invoke();
                _consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                if (_consecutiveErrors >= MaxLoggedConsecutiveErrors) return;

                _consecutiveErrors++;
                Log.Error("Runner update failed", ex);

                if (_consecutiveErrors == MaxLoggedConsecutiveErrors)
                {
                    Log.Error("Runner update keeps failing; " +
                              "further reports are suppressed until it recovers.");
                }
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
