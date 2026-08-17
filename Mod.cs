using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Colossal.IO.AssetDatabase;
using DistrictManager.Systems;

namespace DistrictManager
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(DistrictManager)}.{nameof(Mod)}").SetShowsErrorsInUI(false);

        // can be null briefly right after boot - null-check on the caller side
        public static Setting Instance { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");

            Instance = new Setting(this);
            Instance.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Instance));


            AssetDatabase.global.LoadSettings(nameof(DistrictManager), Instance, new Setting(this));

            updateSystem.UpdateAt<DistrictOverviewUISystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            if (Instance != null)
            {
                Instance.UnregisterInOptionsUI();
                Instance = null;
            }
        }
    }
}
