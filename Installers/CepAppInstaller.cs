using ChatEmoteParticles.Chat;
using ChatEmoteParticles.Effects;
using ChatEmoteParticles.UI;
using ChatEmoteParticles.Utilities;
using Zenject;

namespace ChatEmoteParticles.Installers
{
    /// <summary>
    /// App コンテナへの登録（SPEC §5）。
    /// ESC の <c>ESCAppInstaller</c> と同じ Location.App に入るため、
    /// <c>IChatCoreManager</c> / <c>ChatImageProvider</c> をそのまま解決できる。
    /// </summary>
    internal class CepAppInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.Bind<EmoteImageResolver>().AsSingle();
            Container.BindInterfacesAndSelfTo<EmoteEventBridge>().AsSingle().NonLazy();
            Container.BindInterfacesAndSelfTo<EmotePrecacheService>().AsSingle().NonLazy();
            Container.BindInterfacesAndSelfTo<EmoteEmitterManager>().AsSingle().NonLazy();
            Container.Bind<SettingsViewController>().AsSingle();

            // 2026-08-25: Plugin.Init と App コンテナ構築の間には他 MOD の初期化が入るため、
            // ここまで到達したかを Verbose 非依存で残す（障害切り分け用）。
            Log.Notice("[App] bindings installed");
        }
    }
}
