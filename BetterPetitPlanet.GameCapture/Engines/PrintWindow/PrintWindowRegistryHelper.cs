using System;
using Microsoft.Win32;

namespace BetterPetitPlanet.GameCapture.Engines.PrintWindow;

public static class PrintWindowRegistryHelper
{
    /// <summary>
    /// 关闭 DirectX 窗口优化设置（SwapEffectUpgradeEnable=0）
    /// 仅在老旧 GDI BitBlt 模式回退时作为兼容支持
    /// </summary>
    public static void SetDirectXUserGlobalSettings()
    {
        try
        {
            const string keyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
            const string valueName = "DirectXUserGlobalSettings";
            const string settingName = "SwapEffectUpgradeEnable";
            const string settingData = "SwapEffectUpgradeEnable=0";

            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            if (key == null) return;

            var existingValue = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (existingValue is not null and not string) return;

            var valueData = existingValue as string ?? string.Empty;
            var settings = valueData.Split(';');
            var found = false;
            for (var i = 0; i < settings.Length; i++)
            {
                var separatorIndex = settings[i].IndexOf('=');
                if (separatorIndex < 0 || !string.Equals(settings[i][..separatorIndex].Trim(), settingName, StringComparison.Ordinal))
                {
                    continue;
                }

                settings[i] = settingData;
                found = true;
            }

            valueData = found
                ? string.Join(";", settings)
                : valueData + (valueData.Length > 0 && !valueData.EndsWith(';') ? ";" : string.Empty) + settingData + ";";

            if (valueData != existingValue as string)
            {
                var valueKind = existingValue is string ? key.GetValueKind(valueName) : RegistryValueKind.String;
                key.SetValue(valueName, valueData, valueKind);
            }
        }
        catch
        {
            // 忽略注册表写入权限异常
        }
    }
}
