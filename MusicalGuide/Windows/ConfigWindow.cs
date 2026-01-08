using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace MusicalGuide.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly CamController cam;

    // We give this window a constant ID using ###
    // This allows for labels being dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(MusicalGuide plugin) : base(
        $"Musical Guide ({plugin.Version.ToString()}) Configuration###MusicalGuideConfig")
    {
        Size = new Vector2(720, 560);
        SizeCondition = ImGuiCond.FirstUseEver;

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

            ImGui.Spacing();
        }

        if (ImGui.CollapsingHeader("First Person Settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Spacing();

            var realFirstPerson = configuration.RealFirstPerson;
            if (ImGui.Checkbox("Enable Real First Person", ref realFirstPerson))
            {
                configuration.RealFirstPerson = realFirstPerson;
                configuration.Save();
            }

            using (ImRaii.PushIndent())
            {
                if (ImGui.CollapsingHeader("First Person Adjustments", ImGuiTreeNodeFlags.Framed))
                {
                    var fov = configuration.FirstPersonFieldOfView;
                    if (ImGui.SliderInt("First Person Field of View", ref fov, CamController.MinFoV, CamController.MaxFoV, "%d", ImGuiSliderFlags.AlwaysClamp))
                    {
                        configuration.FirstPersonFieldOfView = fov;
                        configuration.Save();
                    }

                    var headOffsetForward = configuration.FirstPersonHeadOffsetForward;
                    if (ImGui.SliderFloat("Camera Offset Forward", ref headOffsetForward, -0.05f, 0.15f, "%.3f", ImGuiSliderFlags.AlwaysClamp))
                    {
                        configuration.FirstPersonHeadOffsetForward = headOffsetForward;
                        configuration.Save();
                    }

                    var headOffsetUpward = configuration.FirstPersonHeadOffsetUpward;
                    if (ImGui.SliderFloat("Camera Offset Upward", ref headOffsetUpward, -0.15f, 0.25f, "%.3f", ImGuiSliderFlags.AlwaysClamp))
                    {
                        configuration.FirstPersonHeadOffsetUpward = headOffsetUpward;
                        configuration.Save();
                    }

                    var headOffsetSideward = configuration.FirstPersonHeadOffsetSideward;
                    if (ImGui.SliderFloat("Camera Offset Sideward", ref headOffsetSideward, -0.15f, 0.15f, "%.3f", ImGuiSliderFlags.AlwaysClamp))
                    {
                        configuration.FirstPersonHeadOffsetSideward = headOffsetSideward;
                        configuration.Save();
                    }

                    var headRotationPitch = configuration.FirstPersonHeadRotationPitch;
                    if (ImGui.SliderInt("Head Rotation Pitch", ref headRotationPitch, -10, 40, "%d", ImGuiSliderFlags.AlwaysClamp))
                    {
                        configuration.FirstPersonHeadRotationPitch = headRotationPitch;
                        configuration.Save();
                    }
                }
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
        if (ImGui.CollapsingHeader("Third Person Camera Distances", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Spacing();

            var automatic = configuration.UseAutomaticDistance;
            if (ImGui.Checkbox("Remember previously used distances", ref automatic))
            {
                configuration.UseAutomaticDistance = automatic;
                configuration.Save();
            }

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
                if (ImGui.SliderFloat(name, ref dist, CamController.MinCameraDistance, CamController.MaxCameraDistance, "%.1f"))
                {
                    configuration.SetManualDistance(val, dist);
                    cam.SetTargetDistance(dist);
                }
            }

            ImGui.Spacing();
        }
    }
}
