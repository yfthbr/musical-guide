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
    public float FirstPersonHeadOffsetForward { get; set; } = 0.03f;
    public float FirstPersonHeadOffsetUpward { get; set; } = 0.0f;
    public float FirstPersonHeadOffsetSideward { get; set; } = 0.0f;
    public int FirstPersonHeadRotationPitch { get; set; } = 25;
    public int FirstPersonFieldOfView { get; set; } = 78;
    public bool ThirdPersonControl { get; set; } = true;

    [Newtonsoft.Json.JsonIgnore]
    public FFXIVClientStructs.FFXIV.Common.Math.Vector3 FirstPersonOffset => new(-FirstPersonHeadOffsetForward, FirstPersonHeadOffsetUpward, -FirstPersonHeadOffsetSideward);

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

    // the below exist just to make saving less cumbersome
    public void Save()
    {
        S.PluginInterface.SavePluginConfig(this);
        OnConfigurationChanged?.Invoke();
    }
}
