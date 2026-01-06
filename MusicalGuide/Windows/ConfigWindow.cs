using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MusicalGuide.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly CamController cam;
    private const float MaxDist = 20f;
    private const float MinDist = 1.5f;

    // We give this window a constant ID using ###
    // This allows for labels being dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(MusicalGuide plugin) : base(
        $"Musical Guide ({plugin.Version.ToString()}) Configuration###MusicalGuideConfig")
    {
        Size = new Vector2(360, 360);
        SizeCondition = ImGuiCond.Always;

        configuration = plugin.Configuration;
        cam = plugin.Cam;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        Flags &= ~ImGuiWindowFlags.NoMove;
    }

    public override void Draw()
    {
        if (ImGui.CollapsingHeader("General Settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Spacing();

            var enabled = configuration.Enabled;
            if (ImGui.Checkbox("Enable plugin", ref enabled))
            {
                configuration.Enabled = enabled;
                configuration.Save();
            }

            var automatic = configuration.UseAutomaticDistance;
            if (ImGui.Checkbox("Remember previously used distances", ref automatic))
            {
                configuration.UseAutomaticDistance = automatic;
                configuration.Save();
            }

            ImGui.Spacing();
        }

        if (!configuration.UseAutomaticDistance)
        {
            DrawCameraDistanceSliders();
        }
    }

    private void DrawCameraDistanceSliders()
    {
        if (ImGui.CollapsingHeader("Camera Distances", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Spacing();

            var useFurtherCameraForLargerMounts = configuration.UseFurtherCameraForLargerMounts;
            if (ImGui.Checkbox("Use further camera for larger mounts", ref useFurtherCameraForLargerMounts))
            {
                configuration.UseFurtherCameraForLargerMounts = useFurtherCameraForLargerMounts;
                configuration.Save();
            }

            ImGui.Spacing();

            foreach (State val in Enum.GetValues(typeof(State)))
            {
                var name = Enum.GetName(typeof(State), val)!;
                configuration.Distances.TryGetValue(val, out var dist);
                if (dist == 0) dist = 10f;
                if (ImGui.SliderFloat(name, ref dist, MinDist, MaxDist, "%.1f"))
                {
                    configuration.SetManualDistance(val, dist);
                    cam.SetTargetDistance(dist);
                }
            }

            ImGui.Spacing();
        }
    }
}
