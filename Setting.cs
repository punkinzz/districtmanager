using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using System.Collections.Generic;

namespace DistrictManager
{
    [FileLocation(nameof(DistrictManager))]
    [SettingsUIGroupOrder(kToggleGroup)]
    [SettingsUIShowGroupName(kToggleGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kToggleGroup = "Toggle";

        public Setting(IMod mod) : base(mod)
        {
        }

        [SettingsUISection(kSection, kToggleGroup)]
        public bool Enabled { get; set; } = true;

        public override void SetDefaults()
        {
            Enabled = true;
        }
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;
        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }
        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "DistrictManager" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kToggleGroup), "General" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Enabled)), "Enable District Manager" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Enabled)), "Shows the District Manager toolbar button and panel. Turn off to hide it without uninstalling the mod." },
            };
        }

        public void Unload()
        {
        }
    }
}
