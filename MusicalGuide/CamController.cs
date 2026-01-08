using System;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;

namespace MusicalGuide;

public class CamController : IDisposable
{
    #region Constants
    // Maximum allowed camera zoom in the normal game
    public const float MaxCameraDistance = 20f;

    // Minimum allowed camera zoom in the normal game
    public const float MinCameraDistance = 1.5f;

    public const int MinFoV = 60;
    public const int MaxFoV = 95;

    // Maximum distance change per delay tick
    private const float MaxDiff = 0.5f;

    // Delay between distance updates in milliseconds. Target 60fps adjustments.
    // Lower fps will result in slower camera adjustments, which is intentional to avoid choppiness.
    private const int DelayMs = 16;

    private const int DirVMaxDeg = 100;
    private const int DirHMaxDeg = 90;
    private const float DefaultDirVMin = -85 * (MathF.PI / 180f);
    private const float DefaultDirVMax = 45 * (MathF.PI / 180f);
    private const float DefaultFoV = 0.78f;
    private const float DirVEpsilon = 0.0050f; // Small value to avoid an issue with the game flipping camera when looking straight up/down, 1,570796327f ~= 90 degrees
    private const float EulerEpsilon = 0.001f; // Adjust to avoid camera jittering when idle
    private const int NoseBoneIndex = 33; // j_f_uhana
    private const int FaceBoneIndex = 2; // j_f_face    
    public const float DegreesToRadians = MathF.PI / 180.0f;
    public const float RadiansToDegrees = 180.0f / MathF.PI;

    private const int HeadSkeletonIndex = 1;
    #endregion

    #region Dynamic Camera Accessors
    public static unsafe float CurrentDistance
    {
        get { return Cam->Distance; }
        set { Cam->Distance = value; }
    }

    public static unsafe float CameraTilt
    {
        get { return ((ExpandedCamera*)Cam)->Tilt; }
        set { ((ExpandedCamera*)Cam)->Tilt = value; }
    }

    public static unsafe byte IsCameraFlipped
    {
        get { return ((ExpandedCamera*)Cam)->IsFlipped; }
        set { ((ExpandedCamera*)Cam)->IsFlipped = value; }
    }

    private static unsafe Camera* Cam => CameraManager.Instance()->GetActiveCamera();
    private static unsafe FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager* SceneCameraManager => FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
    public static unsafe bool InFirstPerson => ((ExpandedCamera*)Cam)->Mode == 0 && ((ExpandedCamera*)Cam)->ControlType == 0;
    #endregion

    #region Volatile State
    private volatile bool isDisposed = false;
    private volatile float targetDistance = MaxCameraDistance;
    private volatile bool shouldAdjustDistance = true;
    private volatile bool mouseKeyHeld = false;
    #endregion

    #region FirstPersonState
    private volatile bool previousTickWasFirstPerson = false;
    private float previousHeadPitch = 0f;
    private float previousFacing = 0f;
    private float previousDirV = 0f;
    private Vector3 nextCameraPosition = new();
    #endregion

    #region Class Lifetime
    private readonly Configuration configuration;

    // Hook delegates
    // Verify at: 48 8B C4 44 88 48 ?? 55 56
    private unsafe delegate void GetCameraPositionDelegate(Camera* camera, GameObject* target, Vector3* position, byte swapPerson);
    // Verify at: 40 53 41 57 48 83 EC ?? 80 A1
    private unsafe delegate void CameraUpdateDelegate(Camera* camera);
    private readonly Hook<GetCameraPositionDelegate>? getCameraPositionHook;
    private readonly Hook<CameraBase.Delegates.ShouldDrawGameObject>? shouldDrawGameObjectHook;

    public CamController(Configuration configuration)
    {
        this.configuration = configuration;

        configuration.OnConfigurationChanged += OnConfigurationChanged;

        unsafe
        {
            var camVTable = Marshal.ReadIntPtr((nint)Cam);
            var CameraUpdateAddress = Marshal.ReadIntPtr(camVTable, IntPtr.Size * 3); // vf3 is CameraUpdate
            var GetCameraPositionAddress = Marshal.ReadIntPtr(camVTable, IntPtr.Size * 15); // vf15 is GetCameraPosition

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
        // mouseKeyHeld = MouseDevice
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
        isDisposed = true;
        configuration.OnConfigurationChanged -= OnConfigurationChanged;
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
        shouldAdjustDistance = true;
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
        if (S.ClientState.IsGPosing) return;

        // First person is handled by camera position detour instead.
        if (!InFirstPerson)
        {
            SetThirdPersonDistance();
        }
    }
    #endregion

    #region First Person Handling
    private void UpdateFirstPersonCamera()
    {
        if (!configuration.RealFirstPerson) return;
    }

    private unsafe bool ShouldDrawGameObjectDetour(CameraBase* thisPtr, GameObject* gameObject, Vector3* sceneCameraPos, Vector3* lookAtVector)
    {
        // Force draw all player and companion in first person with RealFirstPerson enabled
        var firstPersonModificationActive = configuration.RealFirstPerson && InFirstPerson;
        if (!firstPersonModificationActive)
            goto Original;

        var isLocalPlayerOrCompanion = (nint)gameObject == S.ObjectTable.LocalPlayer?.Address || (nint)gameObject == S.ObjectTable[1]?.Address;
        if (!isLocalPlayerOrCompanion)
            goto Original;

        var objectIsGoodKind = gameObject->ObjectKind == ObjectKind.Pc
            || gameObject->ObjectKind == ObjectKind.Companion
            || gameObject->ObjectKind == ObjectKind.BattleNpc
            || gameObject->ObjectKind == ObjectKind.Aetheryte
            || gameObject->ObjectKind == ObjectKind.Retainer
            || gameObject->ObjectKind == ObjectKind.Mount;
        if (!objectIsGoodKind)
            goto Original;

        var closeObject = Vector3.Distance(gameObject->Position, *sceneCameraPos) < 3f;
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
        var player = S.ObjectTable.LocalPlayer;
        if (player == null) return false;
        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
        if ((nint)chara == 0 || (nint)chara->DrawObject == 0) return false;
        return chara->DrawObject->IsVisible;
    }

    private unsafe bool TryOverrideCameraPosition()
    {
        if (!PlayerDrawObjectExists() || S.ClientState.IsGPosing)
            return false;
        if (!configuration.RealFirstPerson || !InFirstPerson)
        {
            if (previousTickWasFirstPerson)
            {
                S.Log.Debug("Exited first person, resetting camera vertical limits.");
                unsafe
                {
                    Cam->DirVMin = DefaultDirVMin;
                    Cam->DirVMax = DefaultDirVMax;
                    Cam->FoV = DefaultFoV;
                    CameraTilt = 0;
                }
            }
            previousTickWasFirstPerson = false;
            return false;
        }

        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)((GameObject*)S.ObjectTable.LocalPlayer!.Address)->DrawObject;
        if ((nint)charaBase == 0)
            return false;

        var skeleton = charaBase->Skeleton;
        if (skeleton->PartialSkeletonCount < 2)
            return false;
        var partialSkeleton = &skeleton->PartialSkeletons[HeadSkeletonIndex];
        var havokPose = partialSkeleton->GetHavokPose(0);
        if ((nint)havokPose == 0)
            return false;

        // Player's position matrix
        var playerModelMatrix = ToMatrix(new Transform()
        {
            Position = charaBase->DrawObject.Object.Position,
            Rotation = charaBase->DrawObject.Object.Rotation,
            Scale = charaBase->DrawObject.Object.Scale * ((ExpandedCharacterBase*)charaBase)->ScaleFactor
        });

        var playerRotationMatrix = ToMatrix(new Transform()
        {
            Position = Vector3.Zero,
            Rotation = charaBase->DrawObject.Object.Rotation,
            Scale = charaBase->DrawObject.Object.Scale * ((ExpandedCharacterBase*)charaBase)->ScaleFactor
        });

        // Grab the bone's position and euler rotation, since we need to control the camera's pitch and yaw based on head orientation
        var bone = havokPose->Skeleton->Bones[NoseBoneIndex];
        var boneTransform = havokPose->AccessBoneModelSpace(NoseBoneIndex, FFXIVClientStructs.Havok.Animation.Rig.hkaPose.PropagateOrNot.DontPropagate);
        var boneModelPos = Vector3.Transform(new Vector3(boneTransform->Translation.X, boneTransform->Translation.Y, boneTransform->Translation.Z), playerModelMatrix);
        var boneTransformation = new Transformation(boneTransform);
        var boneWorldRotation = boneTransformation.ToQuaternion() * charaBase->DrawObject.Object.Rotation;

        var boneEuler = Vector3.Transform(System.Numerics.Vector3.UnitZ, boneWorldRotation);
        var worldVectors = GetPitchYawFromDirection(boneEuler);
        S.Log.Verbose($"World Vectors: Pitch={worldVectors.X} ({worldVectors.X * RadiansToDegrees}), Yaw={worldVectors.Y} ({worldVectors.Y * RadiansToDegrees})");

        var trueYaw = worldVectors.Y + MathF.PI / 2f; // Adjust to match camera yaw reference
        var truePitch = worldVectors.X;
        var trueRoll = -boneEuler.Y;

        // Begin adjusting camera rotation

        var dirV = Cam->DirV;
        var dirH = Cam->DirH;

        dirV = dirV % (2 * MathF.PI); // keep DirV in reasonable range to avoid camera flipping issues

        // Determine DirV and DirH limits
        CalculateDirectionRange(truePitch, DirVMaxDeg, configuration.FirstPersonHeadRotationPitch * DegreesToRadians, out var dirvMin, out var dirvMax);

        if (previousTickWasFirstPerson)
        {
            // Apply rotation delta to camera

            // Yaw (Y axis) affects camera DirH
            var diff = RotationalDifference(trueYaw, previousFacing);
            if (Math.Abs(diff) > EulerEpsilon && !mouseKeyHeld) // TODO: implement mouseKeyHeld detection
            {
                dirH += diff;
            }

            // Pitch (Z axis) affects camera DirV
            diff = RotationalDifference(truePitch, previousHeadPitch);
            if (Math.Abs(diff) > EulerEpsilon)
            {
                dirV += diff;
            }
        }
        else
        {
            Cam->DirVMin = -2 * MathF.PI;
            Cam->DirVMax = 2 * MathF.PI;
            previousTickWasFirstPerson = true;
        }
        previousHeadPitch = truePitch;
        previousFacing = trueYaw;

        var straightUp = 90 * DegreesToRadians;
        var straightDown = -90 * DegreesToRadians;

        // Clamp DirV and DirH to be within target range
        dirV = ClampRotational(dirV, dirvMin, dirvMax);

        // Jump over the singularity at straight up/down
        if (Math.Abs(dirV - straightUp) <= DirVEpsilon || Math.Abs(dirV - straightDown) <= DirVEpsilon)
        {
            var before = dirV;
            if (previousDirV < dirV)
                dirV += DirVEpsilon * 2;
            else if (previousDirV > dirV)
                dirV -= DirVEpsilon * 2;
        }

        var isFlippedByGame = Math.Abs(dirV) > straightUp;
        var isFlippedByBone = Math.Abs(truePitch) > straightUp;

        // Facing range is flipped when looking upside down
        CalculateDirectionRange(trueYaw, DirHMaxDeg, 0f, out var dirhMin, out var dirhMax);
        dirH = ClampRotational(RotateDir(dirH), dirhMin, dirhMax);

        // Apply tilt to camera
        var distFromStraightH = Math.Abs(RotationalDifference(dirH, trueYaw));
        var distFromStraightV = Math.Abs(RotationalDifference(dirV, truePitch));
        var tiltFactor = (1f - (Math.Max(distFromStraightH, distFromStraightV) / (MathF.PI / 2f))) * 0.5f;
        CameraTilt = trueRoll * tiltFactor;

        if (isFlippedByGame)
        {
            CameraTilt = -(trueRoll * tiltFactor) + (float)Math.PI; // flip camera when looking past straight up or down
        }

        // Apply FOV and rotational changes
        Cam->FoV = configuration.FirstPersonFieldOfView / 100f;
        Cam->DirV = RotateDir(dirV);
        Cam->DirH = dirH;

        // S.Log.Verbose($"@@@@@@@@@ Facing: {trueYaw:F2} ({trueYaw * RadiansToDegrees:F2}) - Pitch: {truePitch:F2} ({truePitch * RadiansToDegrees:F2}) - Roll: {boneEuler.Y:F2} ({boneEuler.Y * RadiansToDegrees:F2})");
        S.Log.Verbose($"Tiltfactor: {tiltFactor:F2} - CameraTilt: {CameraTilt:F2} ({CameraTilt * RadiansToDegrees:F2}) - DistFromStraightH: {distFromStraightH:F2} - DistFromStraightV: {distFromStraightV:F2}");
        // S.Log.Verbose($"DirV: {Cam->DirV:F2} ({Cam->DirV * RadiansToDegrees:F2}) - DirH: {Cam->DirH:F2} ({Cam->DirH * RadiansToDegrees:F2}) - Tilt: {CameraTilt:F2} ({CameraTilt * RadiansToDegrees:F2})");
        // S.Log.Verbose($"DirV Limits: Min={Cam->DirVMin:F2} ({Cam->DirVMin * RadiansToDegrees:F2}) - Max={Cam->DirVMax:F2} ({Cam->DirVMax * RadiansToDegrees:F2})");
        // S.Log.Verbose($"DirH Limits: Min={dirhMin:F2} ({dirhMin * RadiansToDegrees:F2}) - Max={dirhMax:F2} ({dirhMax * RadiansToDegrees:F2})");
        // S.Log.Verbose($"IsFlipped: Game={isFlippedByGame} - Bone={isFlippedByBone} - CamIsFlipped={IsCameraFlipped}");

        // Apply configured offsets
        // var offset = Vector3.Transform(configuration.FirstPersonOffset, Matrix4x4.CreateFromYawPitchRoll(trueFacing, truePitch, boneEuler.X));
        // boneModelPos += offset;

        // In first person with RealFirstPerson enabled, override position
        nextCameraPosition = boneModelPos;

        previousDirV = Cam->DirV;

        return true;
    }

    public static unsafe bool PlayerIsSeated()
    {
        var poseType = (EmoteController.PoseType)Marshal.ReadByte((nint)(&((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)S.ObjectTable.LocalPlayer!.Address)->EmoteController) + 0x20);
        return poseType == EmoteController.PoseType.Sit;
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

    /// <summary>
    /// Calculates World Pitch and Yaw from a world-space direction vector.
    /// </summary>
    public Vector2 GetPitchYawFromDirection(Vector3 lookDir)
    {
        // Normalize to ensure consistent trigonometry
        lookDir = Vector3.Normalize(lookDir);

        // --- Pitch (Elevation) ---
        // Angle between the LookDir and the Horizon.
        // Asin returns radians between -PI/2 and +PI/2 (-90 to +90 degrees)
        // This works perfectly even if the player is upside down.
        var pitchRad = MathF.Asin(lookDir.Y);

        // --- Yaw (Heading) ---
        // Rotation around the World Y-axis (Up).
        // Atan2 handles all 4 quadrants (-PI to +PI)
        var yawRad = MathF.Atan2(lookDir.X, lookDir.Z);

        // [Optional] Normalize Yaw to 0 -> 2PI range
        if (yawRad < 0) yawRad += 2 * MathF.PI;

        return new Vector2(pitchRad, yawRad); // Returns Radians
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
    [FieldOffset(0x170)] public float Tilt;
    [FieldOffset(0x180)] public int Mode;
    [FieldOffset(0x184)] public int ControlType;
    [FieldOffset(0x1F4)] public byte IsFlipped;
}

// https://github.com/Etheirys/Brio/blob/main/Brio/Game/Actor/Interop/BrioCharacterBase.cs#L7
[StructLayout(LayoutKind.Explicit, Size = 0x9D0)]
public struct ExpandedCharacterBase
{
    [FieldOffset(0x2A0)] public float ScaleFactor1;
    [FieldOffset(0x2A4)] public float ScaleFactor2;

    public readonly float ScaleFactor => ScaleFactor1 * ScaleFactor2;
}

public class Transformation
{
    public Vector3 Position = Vector3.Zero;
    public Vector3 Scale = Vector3.Zero;
    public Quaternion Rotation = Quaternion.Identity;

    public unsafe Transformation(hkQsTransformf* boneTransform)
    {
        Rotation = new Quaternion(boneTransform->Rotation.X, boneTransform->Rotation.Y, boneTransform->Rotation.Z, boneTransform->Rotation.W);
        Position = new Vector3(boneTransform->Translation.X, boneTransform->Translation.Y, boneTransform->Translation.Z);
        Scale = new Vector3(boneTransform->Scale.X, boneTransform->Scale.Y, boneTransform->Scale.Z);
    }

    public Vector3 ToEuler()
    {
        var yaw = MathF.Atan2(2.0f * (Rotation.Y * Rotation.W + Rotation.X * Rotation.Z), 1.0f - 2.0f * (Rotation.X * Rotation.X + Rotation.Y * Rotation.Y));
        var pitch = MathF.Asin(2.0f * (Rotation.X * Rotation.W - Rotation.Y * Rotation.Z));
        var roll = MathF.Atan2(2.0f * (Rotation.X * Rotation.Y + Rotation.Z * Rotation.W), 1.0f - 2.0f * (Rotation.X * Rotation.X + Rotation.Z * Rotation.Z));

        return new Vector3(yaw, pitch, roll);
    }

    public Quaternion ToQuaternion()
    {
        return Rotation;
    }

    // public Quaternion2 Multiply(Matrix4x4 matrix)
    // {
    //     var q = Quaternion.CreateFromRotationMatrix(matrix);
    // }

    public override string ToString()
    {
        return $"Quaternion2(X={Rotation.X:F3}, Y={Rotation.Y:F3}, Z={Rotation.Z:F3}, W={Rotation.W:F3})";
    }
}

public class CameraRotationSolver
{
}
