using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace MusicalGuide.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly CamController cam;
    private readonly Vector4 warningColor = new Vector4(1.0f, 1.0f, 0.0f, 1.0f);
    private float SliderWidth => 300f * ImGuiHelpers.GlobalScale;

    // We give this window a constant ID using ###
    // This allows for labels being dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(MusicalGuide plugin) : base(
        $"Musical Guide ({plugin.Version.ToString()}) Configuration###MusicalGuideConfig")
    {
        Size = new Vector2(720, 560) * ImGuiHelpers.GlobalScale;
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 560) * ImGuiHelpers.GlobalScale,
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

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

        if (configuration.Enabled)
        {
            DrawFirstPersonSettings();
            DrawThirdPersonSettings();
        }
        else
        {
            using (ImRaii.Disabled())
            {
                DrawFirstPersonSettings();
                DrawThirdPersonSettings();
            }
        }
    }

    private void DrawFirstPersonSettings()
    {
        if (ImGui.CollapsingHeader("First Person Settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Spacing();
            if (S.GameConfig.UiConfig.TryGetUInt("FPSCameraInterpolationType", out var value)
                && value == (uint)FPSCameraInterpolationType.Always)
            {
                using var color = ImRaii.PushColor(ImGuiCol.Text, warningColor);

                ImGui.Text("Warning! You are using the \"Always\" setting for \"1st Person Camera Auto-adjustment\".");
                ImGui.Text("This setting can cause additional nausea and makes you unable to control your view direction in first person.");
                ImGui.Text("Recommended setting is either \"Never\" or \"Only when moving\" while using real first person.");
            }
            ImGui.Spacing();

            var realFirstPerson = configuration.RealFirstPerson;
            if (ImGui.Checkbox("Enable real first person", ref realFirstPerson))
            {
                configuration.RealFirstPerson = realFirstPerson;
                configuration.Save();
            }

            var reducedMotion = configuration.ReducedMotion;
            if (ImGui.Checkbox("Reduce motion", ref reducedMotion))
            {
                configuration.ReducedMotion = reducedMotion;
                configuration.Save();
            }
            ImGui.SameLine();
            ImGuiComponents.HelpMarker("Disables rotating the camera automatically with head movement and allows the camera to rotate 360 degrees, which is recommended for regular gameplay to reduce motion sickness.");

            using (ImRaii.PushIndent())
            {
                var fullMotionInEmotes = configuration.FullMotionInEmotes;
                if (reducedMotion)
                {
                    if (ImGui.Checkbox("Allow full motion in emotes", ref fullMotionInEmotes))
                    {
                        configuration.FullMotionInEmotes = fullMotionInEmotes;
                        configuration.Save();
                    }
                }
                else
                {
                    using (ImRaii.Disabled())
                    {
                        var dummy = true;
                        ImGui.Checkbox("Allow full motion in emotes", ref dummy);
                    }
                }
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("Allows full head tracking in emotes and emote-like stationary activities (crafting, gathering, fishing). If disabled, the camera will not rotate with head movement even in emotes.");
            }

            var removeRoll = configuration.RemoveRollInFirstPerson;
            var removeRollLabel = "Remove camera roll in first person";
            if (ImGui.Checkbox(removeRollLabel, ref removeRoll))
            {
                configuration.RemoveRollInFirstPerson = removeRoll;
                configuration.Save();
            }
            ImGui.SameLine();
            ImGuiComponents.HelpMarker("Keeps your camera rotation on level with the horizon, which can help reduce motion sickness. Does not apply in reduced motion mode. May cause issues when the head is pointed directly up or down (for example, some sleeping positions.)");

            var reducedMotionInCombat = configuration.ReducedMotionInCombat;
            if (ImGui.Checkbox("Reduce motion in combat and instanced content", ref reducedMotionInCombat))
            {
                configuration.ReducedMotionInCombat = reducedMotionInCombat;
                configuration.Save();
            }

            var allowInGpose = configuration.AllowInGpose;
            if (ImGui.Checkbox("Allow first person tracking in Gpose", ref allowInGpose))
            {
                configuration.AllowInGpose = allowInGpose;
                configuration.Save();
            }

            ImGui.Spacing();

            using (ImRaii.PushIndent())
            {
                if (ImGui.CollapsingHeader("First Person Adjustments", ImGuiTreeNodeFlags.Framed))
                {
                    var fov = configuration.FirstPersonFieldOfView;
                    ImGui.SetNextItemWidth(SliderWidth);
                    if (ImGui.SliderInt("Field of view in first person", ref fov, CamController.MinFoV, CamController.MaxFoV, "%d", ImGuiSliderFlags.AlwaysClamp))
                    {
                        configuration.FirstPersonFieldOfView = fov;
                        configuration.Save();
                    }

                    var headOffsetForward = configuration.FirstPersonHeadOffsetForward;
                    ImGui.SetNextItemWidth(SliderWidth);
                    if (ImGui.SliderFloat("Camera offset forward", ref headOffsetForward, -0.05f, 0.15f, "%.3f", ImGuiSliderFlags.AlwaysClamp))
                    {
                        configuration.FirstPersonHeadOffsetForward = headOffsetForward;
                        configuration.Save();
                    }

                    var headOffsetUpward = configuration.FirstPersonHeadOffsetUpward;
                    ImGui.SetNextItemWidth(SliderWidth);
                    if (ImGui.SliderFloat("Camera offset up", ref headOffsetUpward, -0.15f, 0.25f, "%.3f", ImGuiSliderFlags.AlwaysClamp))
                    {
                        configuration.FirstPersonHeadOffsetUpward = headOffsetUpward;
                        configuration.Save();
                    }

                    var headOffsetSideward = configuration.FirstPersonHeadOffsetSideward;
                    ImGui.SetNextItemWidth(SliderWidth);
                    if (ImGui.SliderFloat("Camera offset sideways", ref headOffsetSideward, -0.15f, 0.15f, "%.3f", ImGuiSliderFlags.AlwaysClamp))
                    {
                        configuration.FirstPersonHeadOffsetSideward = headOffsetSideward;
                        configuration.Save();
                    }

                    var headRotationPitch = configuration.FirstPersonHeadRotationPitch;
                    ImGui.SetNextItemWidth(SliderWidth);
                    if (ImGui.SliderInt("Head rotation pitch adjustment", ref headRotationPitch, -10, 40, "%d", ImGuiSliderFlags.AlwaysClamp))
                    {
                        configuration.FirstPersonHeadRotationPitch = headRotationPitch;
                        configuration.Save();
                    }
                    ImGui.SameLine();
                    ImGuiComponents.HelpMarker("Some races may need to adjust this if their face bone is already rotated from the standard.");
                }
            }

            ImGui.Spacing();
        }
    }

    private void DrawThirdPersonSettings()
    {
        if (ImGui.CollapsingHeader("Third Person Camera Distances", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Spacing();

            var thirdPersonControl = configuration.ThirdPersonControl;
            if (ImGui.Checkbox("Enable third person camera control", ref thirdPersonControl))
            {
                configuration.ThirdPersonControl = thirdPersonControl;
                configuration.Save();
            }

            var useFurtherCameraForLargerMounts = configuration.UseFurtherCameraForLargerMounts;
            if (ImGui.Checkbox("Use further camera for larger mounts", ref useFurtherCameraForLargerMounts))
            {
                configuration.UseFurtherCameraForLargerMounts = useFurtherCameraForLargerMounts;
                configuration.Save();
            }

            ImGui.Separator();

            var automatic = configuration.UseAutomaticDistance;
            if (ImGui.Checkbox("Remember previously used distances", ref automatic))
            {
                configuration.UseAutomaticDistance = automatic;
                configuration.Save();
            }

            if (!configuration.UseAutomaticDistance)
            {
                foreach (State val in Enum.GetValues(typeof(State)))
                {
                    var name = Enum.GetName(typeof(State), val)!;
                    configuration.Distances.TryGetValue(val, out var dist);
                    if (dist == 0) dist = 10f;
                    ImGui.SetNextItemWidth(SliderWidth);
                    if (ImGui.SliderFloat(name, ref dist, CamController.MinCameraDistance, CamController.MaxCameraDistance, "%.1f"))
                    {
                        configuration.SetManualDistance(val, dist);
                        cam.SetTargetDistance(dist);
                    }
                }
            }

            ImGui.Spacing();
        }
    }
}
