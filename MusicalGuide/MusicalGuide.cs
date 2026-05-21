using System;
using System.Linq;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiNotification;
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
        S.PluginInterface.ActivePluginsChanged += OnPluginsChanged;

        S.Framework.Update += FrameworkOnUpdateEvent;

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
        S.PluginInterface.ActivePluginsChanged -= OnPluginsChanged;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();

        S.CommandManager.RemoveHandler(CommandName);
    }

    private void OnPluginsChanged(IActivePluginsChangedEventArgs e)
    {
        if (e.AffectedInternalNames.Contains("Cammy") && e.Kind == PluginListInvalidationKind.Loaded)
        {
            S.Framework.RunOnTick(() =>
            {
                S.Log.Info("Rehooking due to conflicting plugin load.");
                S.Notifications.AddNotification(new Notification()
                {
                    Title = "Compatibility Notice",
                    Content =
                        "Cammy was loaded after Musical Guide. Loading Cammy after Musical Guide breaks the real first person mode. An automatic fix has been attempted, but you may need to reload Musical Guide yourself, or disable Cammy.",
                    InitialDuration = TimeSpan.MaxValue
                });
                Cam.ReHook();
            }, TimeSpan.FromTicks(1));
        }
    }

    private void FrameworkOnUpdateEvent(IFramework framework)
    {
        EnsureIsOnFramework();
        if (!S.ClientState.IsLoggedIn || S.ObjectTable.LocalPlayer == null) return;

        var newState = State.OutOfCombat;
        if (S.Condition.Any(ConditionFlag.Mounted, ConditionFlag.RidingPillion))
        {
            newState = State.Mounted;
        }
        else if (S.Condition.Any(ConditionFlag.InCombat, ConditionFlag.BoundByDuty))
        {
            newState = State.InCombat;
        }
        else if (S.Condition.Any(ConditionFlag.Crafting, ConditionFlag.ExecutingCraftingAction))
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
        args = args.Trim().ToLowerInvariant();

        if (args == "debug")
        {
            S.Framework.RunOnFrameworkThread(() => Debug.PrintDebug(Configuration));
            return;
        }

        if (args == "help")
        {
            S.ChatGui.Print("Musical Guide commands:");
            S.ChatGui.Print("  /mguide - Open settings for Musical Guide");
            S.ChatGui.Print("  /mguide toggle - Toggle Musical Guide on/off");
            S.ChatGui.Print("  /mguide 1pp - Toggle real first person handling");
            S.ChatGui.Print("  /mguide gpose - Toggle first person handling in Gpose");
            S.ChatGui.Print("  /mguide help - Show this help message");
            return;
        }

        if (args == "toggle")
        {
            Configuration.ToggleEnabled();
            S.ChatGui.Print($"Musical Guide {(Configuration.Enabled ? "enabled" : "disabled")}.");
            return;
        }

        if (args == "1pp")
        {
            if (!Configuration.Enabled)
            {
                S.ChatGui.Print("Musical Guide is disabled, enable it with /mguide toggle to use real first person handling.");
                return;
            }
            Configuration.ToggleRealFirstPerson();
            S.ChatGui.Print($"Musical Guide real first person handling {(Configuration.RealFirstPerson ? "enabled" : "disabled")}.");
            return;
        }

        if (args == "gpose")
        {
            if (!Configuration.Enabled)
            {
                S.ChatGui.Print("Musical Guide is disabled, enable it with /mguide toggle to use first person handling in Gpose.");
                return;
            }
            if (!Configuration.RealFirstPerson)
            {
                S.ChatGui.Print("Musical Guide: first person handling is disabled, enable it with /mguide 1pp to use it in Gpose.");
                return;
            }
            Configuration.ToggleGpose();
            S.ChatGui.Print($"Musical Guide: first person handling in Gpose {(Configuration.AllowInGpose ? "enabled" : "disabled")}.");
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
