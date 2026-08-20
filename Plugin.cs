using ChatEmoteParticles.Configuration;
using ChatEmoteParticles.Installers;
using ChatEmoteParticles.Utilities;
using IPA;
using IPA.Config;
using IPA.Config.Stores;
using SiraUtil.Zenject;
using IPALogger = IPA.Logging.Logger;

namespace ChatEmoteParticles
{
    [Plugin(RuntimeOptions.DynamicInit)]
    public class Plugin
    {
        internal static Plugin Instance { get; private set; }

        /// <summary>
        /// BSIPA の OnEnable / OnDisable に追従するフラグ。
        /// Zenject のバインディングはプラグインを無効化しても外れないため、
        /// Manager 側はこのフラグを見て処理を止める。
        /// </summary>
        internal static bool IsEnabled { get; private set; }

        [Init]
        public Plugin(IPALogger logger, Config config, Zenjector zenjector)
        {
            Instance = this;
            Log.Init(logger);

            PluginConfig.Instance = config.Generated<PluginConfig>();
            PluginConfig.Instance.Apply();

            // ESC の ESCAppInstaller と同じ Location.App に入れることで、
            // IChatCoreManager / ChatImageProvider をそのまま解決できる（SPEC §5）。
            zenjector.Install<CepAppInstaller>(Location.App);
            zenjector.Install<CepMenuInstaller>(Location.Menu);

            Log.Info("ChatEmoteParticles initialized.");
        }

        [OnEnable]
        public void OnEnable()
        {
            IsEnabled = true;
            Log.Info("ChatEmoteParticles enabled.");
        }

        [OnDisable]
        public void OnDisable()
        {
            // CepRunner はここで破棄しない。破棄すると再有効化時に Manager の
            // Initialize が二度と走らず、Update が復帰しなくなるため。
            IsEnabled = false;
            Log.Info("ChatEmoteParticles disabled.");
        }
    }
}
