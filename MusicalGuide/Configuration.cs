using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace MusicalGuide;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public event Action? OnConfigurationChanged;
    public bool Enabled { get; set; } = true;

    public bool UseAutomaticDistance { get; set; } = true;

    public bool UseFurtherCameraForLargerMounts { get; set; } = true;

    public bool RealFirstPerson { get; set; } = true;
    public bool ReducedMotion { get; set; } = true;
    public bool FullMotionInEmotes { get; set; } = true;
    public bool RemoveRollInFirstPerson { get; set; } = false;
    public bool ReducedMotionInCombat { get; set; } = true;
    public bool AllowInGpose { get; set; } = false;
    public float FirstPersonHeadOffsetForward { get; set; } = 0.03f;
    public float FirstPersonHeadOffsetUpward { get; set; } = 0.0f;
    public float FirstPersonHeadOffsetSideward { get; set; } = 0.0f;
    public int FirstPersonHeadRotationPitch { get; set; } = 0;
    public int FirstPersonFieldOfView { get; set; } = 78;
    public bool ThirdPersonControl { get; set; } = false;

    [Newtonsoft.Json.JsonIgnore] // 0.1f and 0.12f are empirical adjustments to better match eye position, based on female miqo'te
    public FFXIVClientStructs.FFXIV.Common.Math.Vector3 FirstPersonOffset => new(FirstPersonHeadOffsetSideward, FirstPersonHeadOffsetUpward + 0.1f, FirstPersonHeadOffsetForward + 0.12f);

    // Version for migrations
    public int Version { get; set; } = 1;

    public Dictionary<State, float> Distances = [];

    public void SetAutomatedDistance(State state, float distance)
    {
        if (!UseAutomaticDistance) return;
        SetManualDistance(state, distance);
    }

    public void SetManualDistance(State state, float distance)
    {
        Distances[state] = distance;
        Save();
    }

    public void ToggleEnabled()
    {
        Enabled = !Enabled;
        Save();
    }

    public void ToggleRealFirstPerson()
    {
        RealFirstPerson = !RealFirstPerson;
        Save();
    }

    public void ToggleReducedMotion()
    {
        ReducedMotion = !ReducedMotion;
        Save();
    }

    public void ToggleGpose()
    {
        AllowInGpose = !AllowInGpose;
        Save();
    }

    // the below exist just to make saving less cumbersome
    public void Save()
    {
        S.PluginInterface.SavePluginConfig(this);
        OnConfigurationChanged?.Invoke();
    }
}
