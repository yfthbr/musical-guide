using System;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Common.Math;

namespace MusicalGuide;

public class CamController : IDisposable
{
    #region Constants
    // Maximum allowed camera zoom in the normal game
    public const float MaxCameraDistance = 20f;

    // Minimum allowed camera zoom in the normal game
    public const float MinCameraDistance = 1.5f;

    public const int MinFoV = 78; // Lower than 78 breaks scrolling for camera transitions
    public const int MaxFoV = 112;

    // Maximum distance change per delay tick
    private const float MaxDiff = 0.5f;

    // Delay between distance updates in milliseconds. Target 60fps adjustments.
    // Lower fps will result in slower camera adjustments, which is intentional to avoid choppiness.
    private const int DelayMs = 16;

    private const int DirVMaxDeg = 100;
    private const int DirHMaxDeg = 120;
    private const float DefaultDirVMin = -85 * (MathF.PI / 180f);
    private const float DefaultDirVMax = 45 * (MathF.PI / 180f);
    private const float DefaultFoV = 0.78f;
    private const float DirVEpsilon = 0.0020f; // Small value to avoid an issue with the game flipping camera when looking straight up/down, 1,570796327f ~= 90 degrees
    private const float EulerEpsilon = 0.001f; // Adjust to avoid camera jittering when idle
    private const int NoseBoneIndex = 33; // j_f_uhana
    private const int FaceBoneIndex = 2; // j_f_face
    private const int HeadBoneIndex = 0; // j_kao
    public const float DegreesToRadians = MathF.PI / 180.0f;
    public const float RadiansToDegrees = 180.0f / MathF.PI;

    private const float StraightUp = 90 * DegreesToRadians;
    private const float StraightDown = -90 * DegreesToRadians;
    private const float DirVSmoothingThreshold = 90 * DegreesToRadians;

    private const int HeadSkeletonIndex = 1;
    #endregion

    #region Dynamic Accessors
    public static unsafe float CurrentDistance
    {
        get { return Cam->Distance; }
        set { Cam->Distance = value; }
    }

    public static unsafe float CameraRoll
    {
        get { return ((ExpandedCamera*)Cam)->Tilt; }
        set { ((ExpandedCamera*)Cam)->Tilt = value; }
    }

    internal static unsafe Camera* Cam => CameraManager.Instance()->Camera;
    public static unsafe bool InFirstPerson => Cam->ZoomMode == CameraZoomMode.FirstPerson;
    private static bool IsMounted => S.Condition.Any(ConditionFlag.Mounted, ConditionFlag.RidingPillion);
    #endregion

    #region Volatile State
    private volatile float targetDistance = MaxCameraDistance;
    private volatile bool shouldAdjustDistance = true;
    #endregion

    #region FirstPersonState
    private volatile bool previousTickWasFirstPerson = false;
    private volatile bool exitingFirstPerson = false;
    private float previousHeadPitch = 0f;
    private float previousFacing = 0f;
    private float previousDirH = 0f;
    private float previousDirV = 0f;
    private float previousRealDirH = 0f;
    private float previousRealDirV = 0f;
    private float realDirH = 0f;
    private float realDirV = 0f;
    private Vector3 nextCameraPosition = new();
    #endregion

    #region FrameworkRuntimeState
    private DateTime lastUpdateTime = DateTime.MinValue;
    #endregion

    #region Class Lifetime
    private readonly Configuration configuration;

    // Hook delegates
    // Verify at: 48 8B C4 44 88 48 ?? 55 56
    private unsafe delegate void GetCameraPositionDelegate(Camera* camera, GameObject* target, Vector3* position, byte swapPerson);
    private readonly Hook<GetCameraPositionDelegate>? getCameraPositionHook;
    private readonly Hook<CameraBase.Delegates.ShouldDrawGameObject>? shouldDrawGameObjectHook;

    public CamController(Configuration configuration)
    {
        this.configuration = configuration;

        configuration.OnConfigurationChanged += OnConfigurationChanged;

        unsafe
        {
            var camVTable = Marshal.ReadIntPtr((nint)Cam);
            var GetCameraPositionAddress = Marshal.ReadIntPtr(camVTable, IntPtr.Size * 15); // vf15 is GetCameraPosition

            // TODO: find and hook whatever function checks Cam->DirH for movement purposes and flip DirH for its duration if DirV is inverted
            // TODO: account for player movement occurring after camera position calculation (hide player based on velocity? predict?)

            getCameraPositionHook = S.Interop.HookFromAddress<GetCameraPositionDelegate>(GetCameraPositionAddress, GetCameraPositionDetour);
            shouldDrawGameObjectHook = S.Interop.HookFromAddress<CameraBase.Delegates.ShouldDrawGameObject>(CameraBase.MemberFunctionPointers.ShouldDrawGameObject, ShouldDrawGameObjectDetour);

            getCameraPositionHook.Enable();
            shouldDrawGameObjectHook.Enable();

            S.Log.Debug($"Current camera limits: Min={Cam->DirVMin}, Max={Cam->DirVMax}, FoV={Cam->FoV}");
        }

        S.Framework.Update += FrameworkOnUpdateEvent;
    }

    private void FrameworkOnUpdateEvent(IFramework framework)
    {
        if ((DateTime.Now - lastUpdateTime).TotalMilliseconds >= DelayMs)
        {
            lastUpdateTime = DateTime.Now;
            ProcessThrottledFrameworkUpdate();
        }
    }

    private void OnConfigurationChanged()
    {
        shouldAdjustDistance = true;
    }

    public void Dispose()
    {
        getCameraPositionHook?.Disable();
        shouldDrawGameObjectHook?.Disable();
        getCameraPositionHook?.Dispose();
        shouldDrawGameObjectHook?.Dispose();

        S.Framework.Update -= FrameworkOnUpdateEvent;

        unsafe
        {
            // Reset camera vertical limits
            Cam->DirVMin = DefaultDirVMin;
            Cam->DirVMax = DefaultDirVMax;
        }
        configuration.OnConfigurationChanged -= OnConfigurationChanged;
    }
    #endregion

    #region Public Methods

    public void SetTargetDistance(float distance)
    {
        targetDistance = ClampCameraDistance(distance);
        shouldAdjustDistance = true;
    }
    #endregion

    #region Internal Processing

    private void ProcessThrottledFrameworkUpdate()
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
        if (S.ClientState.IsGPosing) return;

        // First person is handled by camera position detour instead.
        if (!InFirstPerson)
        {
            SetThirdPersonDistance();
        }
    }
    #endregion

    #region First Person Handling
    private unsafe bool ShouldDrawGameObjectDetour(CameraBase* thisPtr, GameObject* gameObject, Vector3* sceneCameraPos, Vector3* lookAtVector)
    {
        if (!configuration.Enabled)
            goto Original;

        // Force draw all player and companion in first person with RealFirstPerson enabled
        var firstPersonModificationActive = configuration.RealFirstPerson && InFirstPerson;
        if (!firstPersonModificationActive)
            goto Original;

        if ((nint)gameObject == nint.Zero || (nint)sceneCameraPos == nint.Zero)
            goto Original;

        if (IsMounted)
            goto Original;

        var closeObject = Vector3.Distance(gameObject->Position, *sceneCameraPos) < 4f;
        if (!closeObject)
            goto Original;

        return true;

    Original:
        return shouldDrawGameObjectHook!.Original(thisPtr, gameObject, sceneCameraPos, lookAtVector);
    }

    private unsafe void GetCameraPositionDetour(Camera* camera, GameObject* target, Vector3* position, byte swapPerson)
    {
        getCameraPositionHook!.Original(camera, target, position, swapPerson);
        if (TryOverrideCameraPosition())
        {
            *position = nextCameraPosition;
        }
    }

    private unsafe bool PlayerDrawObjectExists()
    {
        var chara = Control.GetLocalPlayer();
        if ((nint)chara == 0 || (nint)chara->DrawObject == 0) return false;
        return chara->DrawObject->IsVisible;
    }

    private unsafe bool TryOverrideCameraPosition()
    {
        if (!PlayerDrawObjectExists())
        {
            if (IsMounted && previousTickWasFirstPerson)
            {
                S.Log.Debug("Mounted while in first person, restoring vanilla first person camera.");
                previousTickWasFirstPerson = false;
                RestoreDirVRestrictions();
                Cam->FoV = DefaultFoV;
                CameraRoll = 0;
            }
            return false;
        }

        if (S.ClientState.IsGPosing)
            return false;

        // Lock facing during transitions
        if (configuration.RealFirstPerson && ((ExpandedCamera*)Cam)->Transition > 0f)
        {
            // 3rd person and first person have 180 degree difference in DirH, this helps us face the right way when exiting first person
            if (previousTickWasFirstPerson)
            {
                previousTickWasFirstPerson = false;
                exitingFirstPerson = true;
            }
            if (exitingFirstPerson)
            {
                // Transition ticks from 0.5 to 0, progress goes from 1 to 0
                var wasLookingLeft = RotationalDifference(previousFacing, previousDirH) < 0;
                var progress = ((ExpandedCamera*)Cam)->Transition / 0.5f;
                if (!InFirstPerson)
                    Cam->DirH = previousDirH + ((wasLookingLeft ? -1 : 1) * (MathF.PI * (1 - progress)));
            }
            return false;
        }

        var dirHDiff = RotationalDifference(Cam->DirH, previousRealDirH);
        var dirVDiff = RotationalDifference(Cam->DirV, previousRealDirV);

        // Keep a rolling cache of DirH for when we exit first person
        previousDirH = Cam->DirH;

        if (!configuration.RealFirstPerson || !InFirstPerson || !configuration.Enabled)
        {
            if (exitingFirstPerson || previousTickWasFirstPerson)
            {
                S.Log.Debug("Exited real first person mode, resetting camera vertical limits.");
                unsafe
                {
                    RestoreDirVRestrictions();
                    Cam->FoV = DefaultFoV;
                    CameraRoll = 0;
                }
                exitingFirstPerson = false;
            }
            previousTickWasFirstPerson = false;
            return false;
        }

        var reducedMotion = configuration.ReducedMotion || (configuration.ReducedMotionInCombat && S.Condition.Any(ConditionFlag.InCombat, ConditionFlag.BoundByDuty));

        exitingFirstPerson = false;

        var chara = Control.GetLocalPlayer();
        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)chara->DrawObject;
        if ((nint)charaBase == 0)
            return false;

        var skeleton = charaBase->Skeleton;
        if (skeleton->PartialSkeletonCount < 2)
            return false;
        var partialSkeleton = &skeleton->PartialSkeletons[HeadSkeletonIndex];
        var havokPose = partialSkeleton->GetHavokPose(0);
        if ((nint)havokPose == 0)
            return false;

        // Only derestrict DirV if we are in normal movement state. Flying and diving should still restrict DirV as it is used for movement direction.
        if (chara->MovementState != MovementStateOptions.Normal)
            RestoreDirVRestrictions();
        else
            DerestrictDirV();

        var basePosition = charaBase->DrawObject.Object.Position;
        var baseRotation = charaBase->DrawObject.Object.Rotation;
        var baseScale = charaBase->DrawObject.Object.Scale * ((ExpandedCharacterBase*)charaBase)->ScaleFactor;
        var boneTransform = havokPose->AccessBoneModelSpace(HeadBoneIndex, FFXIVClientStructs.Havok.Animation.Rig.hkaPose.PropagateOrNot.DontPropagate);

        // TODO: calculate position and rotation from mount attachment point if mounted, see CharacterBase->Attach; for skeleton

        // Player's position matrix
        var playerModelMatrix = ToMatrix(new Transform()
        {
            Position = basePosition,
            Rotation = baseRotation,
            Scale = baseScale
        });

        var boneModelPos = Vector3.Transform(new Vector3(boneTransform->Translation.X, boneTransform->Translation.Y, boneTransform->Translation.Z), playerModelMatrix);

        // Calculate rotation of the bone in world space so we can determine where the player's head is facing
        var boneWorldRotation = charaBase->DrawObject.Object.Rotation * new Quaternion(boneTransform->Rotation.X, boneTransform->Rotation.Y, boneTransform->Rotation.Z, boneTransform->Rotation.W);

        // Apply Coordinate Correction
        var fixAxes = Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI / 2f) * Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, MathF.PI);
        var correctedBoneRot = boneWorldRotation * fixAxes;

        var (boneYaw, bonePitch, boneRoll, flippedByBone) = GetYawPitchRoll(correctedBoneRot);

        // // Apply configured offsets to virtual bone position
        var offset = Vector3.Transform(configuration.FirstPersonOffset, correctedBoneRot);
        boneModelPos += offset;

        // Begin adjusting camera rotation

        var dirV = realDirV + dirVDiff;
        var dirH = realDirH + dirHDiff;

        // Determine DirV and DirH limits
        CalculateDirectionRange(bonePitch, DirVMaxDeg, configuration.FirstPersonHeadRotationPitch * DegreesToRadians, out var dirvMin, out var dirvMax);

        if (previousTickWasFirstPerson)
        {
            // Apply rotation delta to camera

            // Yaw affects camera DirH
            var diff = RotationalDifference(boneYaw, previousFacing);
            if (Math.Abs(diff) > EulerEpsilon)
            {
                if (!reducedMotion && !InputManager.IsRightMouseDown())
                    dirH += diff;
                previousFacing = boneYaw;
            }

            // Pitch affects camera DirV
            diff = RotationalDifference(bonePitch, previousHeadPitch);
            if (Math.Abs(diff) > EulerEpsilon)
            {
                if (!reducedMotion)
                    dirV += diff;
                previousHeadPitch = bonePitch;
            }
        }
        else
        {
            DerestrictDirV();

            // Make sure we face the same way our character does when entering first person
            previousDirH = dirH = boneYaw;
            realDirH = boneYaw;
            previousFacing = boneYaw;
            dirV = bonePitch;
            realDirV = bonePitch;
            previousHeadPitch = bonePitch;
            previousTickWasFirstPerson = true;
            S.Log.Debug($"Entered first person, initializing camera vertical limits and orientation.");
        }

        // Keep DirV in reasonable range to avoid camera flipping issues
        dirV = dirV % (2 * MathF.PI);
        if (dirV > MathF.PI)
            dirV -= 2 * MathF.PI;
        else if (dirV < -MathF.PI)
            dirV += 2 * MathF.PI;

        // Clamp DirV before singularity check
        if (!reducedMotion)
            dirV = ClampRotational(dirV, dirvMin, dirvMax);

        // Jump over the singularity at straight up/down
        var distanceToSingularity = Math.Min(Math.Abs(dirV - StraightUp), Math.Abs(dirV - StraightDown));
        if (distanceToSingularity <= DirVEpsilon)
        {
            var before = dirV;
            if (previousDirV < dirV)
                dirV += DirVEpsilon * 2;
            else if (previousDirV > dirV)
                dirV -= DirVEpsilon * 2;
        }

        // Calculate isFlippedByGame after potential DirV adjustments
        var isFlippedByGame = Math.Abs(dirV) > StraightUp;

        // Handle DirH clamping
        CalculateDirectionRange(boneYaw, DirHMaxDeg, 0f, out var dirhMin, out var dirhMax);
        dirH = RotateDir(dirH);
        if (!reducedMotion)
            dirH = ClampRotational(dirH, dirhMin, dirhMax);

        // Save updated real DirH/DirV for next tick
        realDirH = dirH;
        realDirV = dirV;

        // Apply tilt to camera, accounting for how far we are from looking straight ahead and how much the head is pitched
        if (configuration.RemoveRollInFirstPerson || reducedMotion)
        {
            CameraRoll = isFlippedByGame ? (float)Math.PI : 0f;
        }
        else
        {
            var distFromStraightH = RotationalDifference(dirH, boneYaw);
            var distFromStraightV = RotationalDifference(dirV, bonePitch);

            var camAdjustQuaternion = Quaternion.CreateFromYawPitchRoll(distFromStraightH, -distFromStraightV, 0);
            var camAdjustedRotation = correctedBoneRot * camAdjustQuaternion;
            var (camYaw, camPitch, camRoll, camFlipped) = GetYawPitchRoll(camAdjustedRotation, true);

            // S.Log.Verbose($"Camera Original: Yaw={dirH * RadiansToDegrees:F2}, Pitch={dirV * RadiansToDegrees:F2}, BoneYaw={trueYaw * RadiansToDegrees:F2}, BonePitch={truePitch * RadiansToDegrees:F2}, BoneRoll={trueRoll * RadiansToDegrees:F2}");
            // S.Log.Verbose($"Camera Adjusted: Yaw={camYaw * RadiansToDegrees:F2}, Pitch={camPitch * RadiansToDegrees:F2}, Roll={camRoll * RadiansToDegrees:F2}");

            if (camFlipped)
            {
                camRoll += (float)Math.PI;
            }

            dirH = camYaw;
            dirV = camPitch;

            CameraRoll = camRoll;
        }

        // Apply FOV and rotational changes
        Cam->FoV = configuration.FirstPersonFieldOfView / 100f;
        Cam->DirV = RotateDir(dirV);
        Cam->DirH = dirH;

        // In first person with RealFirstPerson enabled, override position
        nextCameraPosition = boneModelPos;

        previousDirV = Cam->DirV;
        previousRealDirH = Cam->DirH;
        previousRealDirV = Cam->DirV;

        return true;
    }

    private static unsafe void DerestrictDirV()
    {
        Cam->DirVMin = -2 * MathF.PI;
        Cam->DirVMax = 2 * MathF.PI;
    }

    private static unsafe void RestoreDirVRestrictions()
    {
        Cam->DirVMin = DefaultDirVMin;
        Cam->DirVMax = DefaultDirVMax;
    }

    private static (float yaw, float pitch, float roll, bool inverted) GetYawPitchRoll(Quaternion rotation, bool correctInvertion = false)
    {
        var fwd = Vector3.Transform(System.Numerics.Vector3.UnitZ, rotation);
        var flatDist = MathF.Sqrt(fwd.X * fwd.X + fwd.Z * fwd.Z);
        var yaw = MathF.Atan2(fwd.X, fwd.Z);
        var pitch = MathF.Atan2(fwd.Y, flatDist);

        // Detect inverted state
        var qStandard = Quaternion.CreateFromYawPitchRoll(yaw, -pitch, 0);
        var calculatedUp = Vector3.Transform(System.Numerics.Vector3.UnitY, qStandard);
        var up = Vector3.Transform(System.Numerics.Vector3.UnitY, rotation);
        // Compare calculated Up with actual Bone Up. If they point in opposite directions, we are inverted.
        var dotUp = Vector3.Dot(up, calculatedUp);

        var inverted = dotUp < 0;
        if (inverted && correctInvertion)
        {
            // Unwrap Pitch: If we were at 89, we go to 91. 
            // Math: PI - Pitch (for positive) or -PI - Pitch (for negative)
            pitch = (pitch >= 0) ? (MathF.PI - pitch) : (-MathF.PI - pitch);

            // Yaw is also flipped 180 degrees when inverted
            yaw += MathF.PI;
        }

        // Normalize Yaw to -PI to PI
        while (yaw > MathF.PI) yaw -= 2 * MathF.PI;
        while (yaw <= -MathF.PI) yaw += 2 * MathF.PI;

        // Calculate roll
        var qLook = Quaternion.CreateFromYawPitchRoll(yaw, -pitch, 0);
        var qRoll = Quaternion.Invert(qLook) * rotation;
        var roll = MathF.Atan2(2.0f * (qRoll.W * qRoll.Z + qRoll.X * qRoll.Y), 1.0f - 2.0f * (qRoll.Y * qRoll.Y + qRoll.Z * qRoll.Z));

        return (yaw, pitch, roll, inverted);
    }

    private static void CalculateDirectionRange(float rootRotation, int degrees, float offset, out float dirMin, out float dirMax)
    {
        var targetDir = rootRotation - offset;
        dirMin = (targetDir - (degrees * (MathF.PI / 180f))) % (2 * MathF.PI);
        dirMax = (targetDir + (degrees * (MathF.PI / 180f))) % (2 * MathF.PI);
        if (dirMax < dirMin)
        {
            if (dirMin <= MathF.PI)
                dirMax += 2 * MathF.PI;
            else
                dirMin -= 2 * MathF.PI;
        }
        // Floating point imprecision in targetDirV can cause min/max to be out of range, fix that here
        if (dirMin <= -MathF.PI)
        {
            dirMin += 2 * MathF.PI;
            dirMax += 2 * MathF.PI;
        }
        // If max is too far out, wrap it back
        if (dirMax >= MathF.PI)
        {
            dirMax -= 2 * MathF.PI;
        }
    }

    private static float RotationalDifference(float one, float two)
    {
        var diff = one - two;
        if (diff > MathF.PI)
        {
            diff -= 2 * MathF.PI;
        }
        else if (diff < -MathF.PI)
        {
            diff += 2 * MathF.PI;
        }
        return diff;
    }

    // Assumes radians input
    private float ClampRotational(float rad, float min, float max)
    {
        // Clamp input to be within target range by choosing closest edge, accounting for wrap-around
        if ((min < max && (rad < min || rad > max)) ||
            (min > max && (rad > max && rad < min)))
        {
            var distToMin = Math.Abs(rad - min);
            var distToMax = Math.Abs(rad - max);

            if (distToMax > MathF.PI)
                distToMax = Math.Abs(distToMax - MathF.PI * 2);
            if (distToMin > MathF.PI)
                distToMin = Math.Abs(distToMin - MathF.PI * 2);

            if (distToMin < distToMax)
                rad = min;
            else
                rad = max;
        }
        return rad;
    }

    private float RotateDir(float dir)
    {
        const float maxRotation = MathF.PI;
        if (dir > maxRotation)
        {
            return dir - 2 * maxRotation;
        }
        else if (dir < -maxRotation)
        {
            return dir + 2 * maxRotation;
        }
        return dir;
    }

    public static Matrix4x4 ToMatrix(Transform transform)
    {
        System.Numerics.Matrix4x4 mat = Matrix4x4.Identity;

        mat *= System.Numerics.Matrix4x4.CreateScale(transform.Scale);

        Quaternion normalizedRotation = Quaternion.Normalize(transform.Rotation);
        mat *= System.Numerics.Matrix4x4.CreateFromQuaternion(normalizedRotation);

        mat.M41 = transform.Position.X;
        mat.M42 = transform.Position.Y;
        mat.M43 = transform.Position.Z;

        return mat;
    }
    #endregion

    #region Third Person Handling
    private void SetThirdPersonDistance()
    {
        if (!configuration.ThirdPersonControl || !shouldAdjustDistance) return;

        var distance = MountHitboxAdjustedDistance(targetDistance);

        if (CameraIsAtDistance(distance))
        {
            shouldAdjustDistance = false;
            return;
        }

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
            return MathF.Max(MinCameraDistance, MathF.Min(MaxCameraDistance, ((hitboxSize - 1f) * 2) + baseDistance));
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
        return Math.Clamp(distance, MinCameraDistance, MaxCameraDistance);
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
            return S.ObjectTable.First(s => s.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.MountType).HitboxRadius;
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

// expanding https://github.com/aers/FFXIVClientStructs/blob/d46387fe486d353588c62c64ebc3c60c22efa814/FFXIVClientStructs/FFXIV/Client/Game/Camera.cs
[StructLayout(LayoutKind.Explicit, Size = 0x2C0)]
internal struct ExpandedCamera
{
    [FieldOffset(0x170)] public float Tilt; // Roll axis of the camera in radians
    [FieldOffset(0x1A0)] public float Transition; // Normally counts down from 0.5 to 0 during transitions between 1st and 3rd person
}

// https://github.com/Etheirys/Brio/blob/main/Brio/Game/Actor/Interop/BrioCharacterBase.cs#L7
[StructLayout(LayoutKind.Explicit, Size = 0x9D0)]
public struct ExpandedCharacterBase
{
    [FieldOffset(0x2A0)] public float ScaleFactor1;
    [FieldOffset(0x2A4)] public float ScaleFactor2;

    public readonly float ScaleFactor => ScaleFactor1 * ScaleFactor2;
}
