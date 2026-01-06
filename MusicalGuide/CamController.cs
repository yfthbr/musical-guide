using System;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace MusicalGuide;

public class CamController : IDisposable
{
    #region Constants
    // Maximum allowed camera zoom in the normal game
    private const float MaxDist = 20f;

    // Minimum allowed camera zoom in the normal game
    private const float MinDist = 1.5f;

    // Maximum distance change per delay tick
    private const float MaxDiff = 0.5f;

    // Delay between distance updates in milliseconds. Target 60fps adjustments.
    // Lower fps will result in slower camera adjustments, which is intentional to avoid choppiness.
    private const int DelayMs = 16;
    #endregion

    #region Dynamic Camera Accessors
    public static unsafe float CurrentDistance
    {
        get { return Cam->Distance; }
        set { Cam->Distance = value; }
    }

    // TODO: use clientstructs for this when the field is added upstream
    private static unsafe Camera* Cam => CameraManager.Instance()->GetActiveCamera();
    public static unsafe bool InFirstPerson => Marshal.ReadInt32((nint)Cam + 0x180) == 0;
    #endregion

    #region Volatile State
    private volatile bool isDisposed = false;
    private volatile float targetDistance = MaxDist;
    #endregion

    #region Class Lifetime
    private readonly Configuration configuration;

    public CamController(Configuration configuration)
    {
        this.configuration = configuration;

        // TODO: hook ShouldDrawGameObject to show player model in first person when configuration is set
    }

    public void Dispose()
    {
        isDisposed = true;
    }
    #endregion

    #region Public Methods
    public void Start()
    {
        S.Framework.RunOnFrameworkThread(() => ProcessTick());
    }

    public void SetTargetDistance(float distance)
    {
        targetDistance = ClampCameraDistance(distance);
    }
    #endregion

    #region Internal Processing
    private void ProcessTick()
    {
        if (isDisposed) return;

        ProcessTickInternal();

        S.Framework.RunOnTick(ProcessTick, TimeSpan.FromMilliseconds(DelayMs));
    }

    private void ProcessTickInternal()
    {
        if (!configuration.Enabled) return;
        if (!S.ClientState.IsLoggedIn) return;

        if (!S.Framework.IsInFrameworkUpdateThread)
        {
            S.Log.Error("CamController not in framework update thread.");
            return;
        }

        if (S.ObjectTable.LocalPlayer == null) return;
        if (!S.ObjectTable.LocalPlayer.IsValid()) return;

        if (InFirstPerson)
            UpdateFirstPersonCamera();
        else
            SetThirdPersonDistance();
    }
    #endregion

    #region First Person Handling
    private void UpdateFirstPersonCamera()
    {
        if (!configuration.RealFirstPerson) return;

        // Rough plan:
        // 1. Get bone position (id 1? 26?)
        // 2. Update camera target position+rotation to that bone position + some offset (cam should be slightly in front of the face)
        // 3. Tick camera position+rotation towards target position+rotation smoothly but quickly

        // Note: floating point precision, use Abs with a small epsilon
    }
    #endregion

    #region Third Person Handling
    private void SetThirdPersonDistance()
    {
        if (!configuration.ThirdPersonControl) return;

        var distance = MountHitboxAdjustedDistance(targetDistance);

        if (CameraIsAtDistance(distance)) return;

        // Important! Clamp distance to allowed range before any adjustments are made
        distance = ClampCameraDistance(distance);

        AdjustCameraDistanceTowards(distance);
    }

    private bool CameraIsAtDistance(float distance) => Math.Abs(CurrentDistance - distance) < 0.01f;

    private float MountHitboxAdjustedDistance(float baseDistance)
    {
        if (!configuration.UseFurtherCameraForLargerMounts) return baseDistance;

        try
        {
            var hitboxSize = MountHitboxSize();
            S.Log.Debug($"Mount hitbox size: {hitboxSize}");
            return MathF.Max(MinDist, MathF.Min(MaxDist, ((hitboxSize - 1f) * 2) + baseDistance));
        }
        catch (NotReadyException)
        {
            S.Log.Debug("Mount hitbox size not ready, using base distance.");
            return baseDistance;
        }
    }

    private void AdjustCameraDistanceTowards(float distance)
    {
        if (Math.Abs(CurrentDistance - distance) > MaxDiff)
        {
            var diff = MaxDiff;
            if (CurrentDistance > distance) diff *= -1;
            var newDist = CurrentDistance + diff;
            S.Log.Debug($"Setting distance to {newDist}, target was {distance} ({targetDistance} before hitbox adjustments)");
            CurrentDistance = newDist;
        }
        else
        {
            S.Log.Debug($"Setting distance to {distance}, target was {distance} ({targetDistance} before hitbox adjustments)");
            CurrentDistance = distance;
        }
    }

    private static float ClampCameraDistance(float distance)
    {
        if (distance < MinDist) distance = MinDist;
        if (distance > MaxDist) distance = MaxDist;
        return distance;
    }

    private static float MountHitboxSize()
    {
        var mountId = S.ObjectTable.LocalPlayer?.CurrentMount?.ValueNullable?.RowId;
        if (mountId is null)
        {
            S.Log.Debug("Mount was not found");
            return 1f;
        }

        try
        {
            return S.ObjectTable.First(s => s.ObjectKind == ObjectKind.MountType).HitboxRadius;
        }
        catch (InvalidOperationException)
        {
            S.Log.Debug("Mount object was not found");
            S.Log.Debug($"Looking for {mountId} in {string.Join(',', S.ObjectTable.Select(s => s))}");
            throw new NotReadyException();
        }
    }
    #endregion
}

internal class NotReadyException : Exception
{
}
