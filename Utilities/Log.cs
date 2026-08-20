using IPALogger = IPA.Logging.Logger;

namespace ChatEmoteParticles.Utilities
{
    /// <summary>
    /// ログ出力。既定では Error 以上のみを出力し、Verbose 設定で全レベルを有効化する。
    /// ホットパスから呼ぶ場合は必ず <see cref="IsVerbose"/> でガードすること
    /// （文字列補間のアロケーションを避けるため）。
    /// </summary>
    internal static class Log
    {
        private static IPALogger _logger;

        /// <summary>Verbose ログが有効かどうか。PluginConfig から更新される。</summary>
        internal static bool IsVerbose { get; set; }

        internal static void Init(IPALogger logger) => _logger = logger;

        internal static void Debug(string message)
        {
            if (IsVerbose) _logger?.Debug(message);
        }

        internal static void Info(string message)
        {
            if (IsVerbose) _logger?.Info(message);
        }

        internal static void Notice(string message)
        {
            if (IsVerbose) _logger?.Notice(message);
        }

        internal static void Warn(string message)
        {
            if (IsVerbose) _logger?.Warn(message);
        }

        /// <summary>Error は Verbose 設定に関わらず常に出力する。</summary>
        internal static void Error(string message) => _logger?.Error(message);

        /// <summary>
        /// 機能が回復不能になったことを知らせる。Verbose 設定に関わらず常に出力する。
        /// ユーザーの対応が必要な場面に限って使うこと。
        /// </summary>
        internal static void Critical(string message) => _logger?.Critical(message);

        /// <summary>
        /// 例外ログ。例外メッセージにトークン等の機密が含まれ得る箇所では使用せず、
        /// 呼び出し側で内容を限定すること。
        /// </summary>
        internal static void Error(string message, System.Exception ex)
            => _logger?.Error($"{message}: {ex.GetType().Name}: {ex.Message}");
    }
}
