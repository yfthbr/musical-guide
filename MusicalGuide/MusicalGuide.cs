using System;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MusicalGuide.Windows;

namespace MusicalGuide;

// ReSharper disable once ClassNeverInstantiated.Global - instantiated by Dalamud
public sealed partial class MusicalGuide : IDalamudPlugin
{
    private const string CommandName = "/mguide";

    public readonly Version Version = Assembly.GetExecutingAssembly().GetName().Version!;

    public readonly WindowSystem WindowSystem = new("MusicalGuide");

    public MusicalGuide(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<S>();

        Configuration = S.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Cam = new CamController(Configuration);
        ConfigWindow = new ConfigWindow(this);

        WindowSystem.AddWindow(ConfigWindow);

        S.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open settings for Musical Guide"
        });

        S.PluginInterface.UiBuilder.Draw += DrawUi;
        S.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        S.Framework.Update += FrameworkOnUpdateEvent;

        Cam.Start();
#if DEBUG
        S.Log.Info("Musical Guide loaded in DEBUG mode.");
        ConfigWindow.IsOpen = true;
#endif
    }

    private State LatestState { get; set; }

    public Configuration Configuration { get; init; }
    private ConfigWindow ConfigWindow { get; init; }
    public CamController Cam { get; init; }

    public void Dispose()
    {
        Cam.Dispose();

        S.Framework.Update -= FrameworkOnUpdateEvent;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();

        S.CommandManager.RemoveHandler(CommandName);
    }

    private void FrameworkOnUpdateEvent(IFramework framework)
    {
        EnsureIsOnFramework();
        if (!S.ClientState.IsLoggedIn || S.ObjectTable.LocalPlayer == null) return;

        var conditions = S.Condition.AsReadOnlySet();
        var newState = State.OutOfCombat;
        if (conditions.Contains(ConditionFlag.Mounted) || conditions.Contains(ConditionFlag.RidingPillion))
        {
            newState = State.Mounted;
        }
        else if (conditions.Contains(ConditionFlag.InCombat) || conditions.Contains(ConditionFlag.BoundByDuty))
        {
            newState = State.InCombat;
        }
        else if (conditions.Contains(ConditionFlag.Crafting) || conditions.Contains(ConditionFlag.ExecutingCraftingAction))
        {
            newState = State.Crafting;
        }

        if (newState != LatestState)
        {
            var currentDistance = CamController.CurrentDistance;
            S.Log.Debug($"State changed to: {newState}. Current distance: {currentDistance}.");
            Configuration.SetAutomatedDistance(LatestState, currentDistance);
            if (Configuration.Distances.TryGetValue(newState, out var newDistance))
            {
                S.Log.Debug($"Setting new distance to: {newDistance}.");
                Cam.SetTargetDistance(newDistance);
            }

            LatestState = newState;
        }
    }

    private static void EnsureIsOnFramework()
    {
        if (!S.Framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("This method must be called from the framework update thread.");
    }

    private void OnCommand(string command, string args)
    {
        if (args == "debug")
        {
            S.Framework.RunOnFrameworkThread(() => Debug.PrintDebug(Configuration));
            return;
        }
        ToggleConfigUi();
    }

    private void DrawUi()
    {
        WindowSystem.Draw();
    }

    public void ToggleConfigUi()
    {
        ConfigWindow.Toggle();
    }
}

public enum State
{
    OutOfCombat = 1,
    InCombat = 2,
    Mounted = 4,
    Crafting = 8,
}
