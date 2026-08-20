using System;
using BeatSaberMarkupLanguage.Settings;
using ChatEmoteParticles.UI;
using ChatEmoteParticles.Utilities;
using Zenject;

namespace ChatEmoteParticles.Installers
{
    /// <summary>
    /// Menu コンテナへの登録。BSML の Mod Settings タブへ設定画面を追加する（SPEC §18）。
    /// </summary>
    internal class CepMenuInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.BindInterfacesTo<SettingsMenuRegistrar>().AsSingle().NonLazy();
        }
    }

    /// <summary>
    /// <c>BSMLSettings.AddSettingsMenu</c> は同名メニューを重複登録しないため、
    /// メニューシーンが再ロードされても安全に呼べる。
    /// </summary>
    internal sealed class SettingsMenuRegistrar : IInitializable
    {
        private const string MenuName = "Chat Emote Particles";
        private const string Resource = "ChatEmoteParticles.UI.settings.bsml";

        private readonly BSMLSettings _settings;
        private readonly SettingsViewController _host;

        public SettingsMenuRegistrar(
            [InjectOptional] BSMLSettings settings,
            [InjectOptional] SettingsViewController host)
        {
            _settings = settings;
            _host = host;
        }

        public void Initialize()
        {
            if (_host == null)
            {
                Log.Error("[Settings] host could not be resolved; settings menu will not be available");
                return;
            }

            try
            {
                var settings = _settings ?? BSMLSettings.Instance;
                if (settings == null)
                {
                    Log.Error("[Settings] BSMLSettings is not available; settings menu will not be available");
                    return;
                }

                settings.AddSettingsMenu(MenuName, Resource, _host);
                Log.Info("[Settings] menu registered");
            }
            catch (Exception ex)
            {
                Log.Error("[Settings] failed to register settings menu", ex);
            }
        }
    }
}
