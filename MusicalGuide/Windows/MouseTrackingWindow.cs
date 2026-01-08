using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace MusicalGuide.Windows;

public class MouseTrackingWindow : Window, IDisposable
{

    // We give this window a constant ID using ###
    // This allows for labels being dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public MouseTrackingWindow(MusicalGuide plugin) : base(
        $"###MusicalGuideMouseTracking")
    {
    }

    public void Dispose() { }

    public override void PreDraw()
    {
    }

    public override void Draw()
    {
    }
}
