using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInExposition.Capture;
using BepInExposition.Config;
using Il2CppInterop.Runtime.Injection;

[assembly: AssemblyVersion(BepInExposition.PluginInfo.Version)]
[assembly: AssemblyFileVersion(BepInExposition.PluginInfo.Version)]
[assembly: AssemblyInformationalVersion(BepInExposition.PluginInfo.Version)]

namespace BepInExposition;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public sealed class Main : BasePlugin
{
    internal static ManualLogSource? Logger { get; private set; }

    public override void Load()
    {
        Logger = Log;

        try
        {
            CaptureSettings.Load(Config);

            if (!CaptureSettings.Enabled.Value)
            {
                Log.LogInfo($"{PluginInfo.Name} disabled by config.");
                return;
            }

            ClassInjector.RegisterTypeInIl2Cpp<CaptureRunner>();
            AddComponent<CaptureRunner>();

            Log.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} loaded.");
        }
        catch (Exception ex)
        {
            Log.LogError($"{PluginInfo.Name} failed to load: {ex}");
            throw;
        }
    }
}
