using System;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics;
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
    private const int DirHMaxDeg = 120;
    private const float DefaultDirVMin = -85 * (MathF.PI / 180f);
    private const float DefaultDirVMax = 45 * (MathF.PI / 180f);
    private const float DefaultFoV = 0.78f;
    private const float DirVEpsilon = 0.0050f; // Small value to avoid an issue with the game flipping camera when looking straight up/down, 1,570796327f ~= 90 degrees
    private const float EulerEpsilon = 0.001f; // Adjust to avoid camera jittering when idle
    private const int NoseBoneIndex = 33; // j_f_uhana
    private const int FaceBoneIndex = 2; // j_f_face
    private const int HeadBoneIndex = 0; // j_kao
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

    public static unsafe float CameraRoll
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
    public static unsafe bool InFirstPerson => ((ExpandedCamera*)Cam)->Mode == 0 && ((ExpandedCamera*)Cam)->ControlType == 0;
    #endregion

    #region Volatile State
    private volatile float targetDistance = MaxCameraDistance;
    private volatile bool shouldAdjustDistance = true;
    #endregion

    #region FirstPersonState
    private volatile bool previousTickWasFirstPerson = false;
    private float previousHeadPitch = 0f;
    private float previousFacing = 0f;
    private float previousDirV = 0f;
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
                    CameraRoll = 0;
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
        var boneTransform = havokPose->AccessBoneModelSpace(HeadBoneIndex, FFXIVClientStructs.Havok.Animation.Rig.hkaPose.PropagateOrNot.DontPropagate);
        var boneModelPos = Vector3.Transform(new Vector3(boneTransform->Translation.X, boneTransform->Translation.Y, boneTransform->Translation.Z), playerModelMatrix);

        // Calculate rotation of the bone in world space so we can determine where the player's head is facing
        var boneTransformation = new Transformation(boneTransform);
        var boneWorldRotation = charaBase->DrawObject.Object.Rotation * boneTransformation.ToQuaternion();

        // Apply Coordinate Correction
        var fixAxes = Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI / 2f) * Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, MathF.PI);
        var correctedBoneRot = boneWorldRotation * fixAxes;

        // Determine pitch, yaw and roll from bone world rotation
        var boneFwd = Vector3.Transform(System.Numerics.Vector3.UnitZ, correctedBoneRot);
        var boneUp = Vector3.Transform(System.Numerics.Vector3.UnitY, correctedBoneRot);

        // Calculate standard yaw and pitch from bone forward vector
        var flatDist = MathF.Sqrt(boneFwd.X * boneFwd.X + boneFwd.Z * boneFwd.Z);
        var standardYaw = MathF.Atan2(boneFwd.X, boneFwd.Z);
        var standardPitch = MathF.Atan2(boneFwd.Y, flatDist);

        // Detect inverted state
        var qStandard = Quaternion.CreateFromYawPitchRoll(standardYaw, -standardPitch, 0); // Note: Pitch sign might vary by engine, check FFXIV usually requires negation here
        var calculatedUp = Vector3.Transform(System.Numerics.Vector3.UnitY, qStandard);

        // Compare calculated Up with actual Bone Up. If they point in opposite directions, we are inverted.
        var dotUp = Vector3.Dot(boneUp, calculatedUp);

        var trueYaw = standardYaw; // Adjust yaw based on character rotation
        var truePitch = standardPitch;
        if (dotUp < 0)
        {
            // Unwrap Pitch: If we were at 89, we go to 91. 
            // Math: PI - Pitch (for positive) or -PI - Pitch (for negative)
            truePitch = (truePitch >= 0) ? (MathF.PI - truePitch) : (-MathF.PI - truePitch);

            // Yaw is also flipped 180 degrees when inverted
            trueYaw += MathF.PI;
        }

        // Normalize Yaw to -PI to PI
        while (trueYaw > MathF.PI) trueYaw -= 2 * MathF.PI;
        while (trueYaw <= -MathF.PI) trueYaw += 2 * MathF.PI;

        // Remove the Yaw and Pitch rotation from the Bone Rotation to isolate Roll.
        // We create a rotation representing just the Look Direction (Yaw + Pitch)
        // Note: FFXIV 'DirV' (Pitch) is typically inverted in quaternion calculation (Negative = Up)
        var qLook = Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, trueYaw) * Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, -truePitch);

        // The difference between the Look rotation and actual Bone rotation is the Roll
        // qBone = qLook * qRoll  =>  qRoll = Inverse(qLook) * qBone
        var qRoll = Quaternion.Invert(qLook) * correctedBoneRot;

        // Extract Roll angle from qRoll (Z-axis rotation)
        // Using Atan2 on the matrix components of the quaternion
        // qRoll should be roughly (0, 0, sin(a/2), cos(a/2))
        // Set to 0 on reduced motion
        var trueRoll = configuration.ReducedMotion ? 0f : MathF.Atan2(2.0f * (qRoll.W * qRoll.Z + qRoll.X * qRoll.Y), 1.0f - 2.0f * (qRoll.Y * qRoll.Y + qRoll.Z * qRoll.Z));

        // // Apply configured offsets to virtual bone position
        var offset = Vector3.Transform(configuration.FirstPersonOffset, correctedBoneRot);
        boneModelPos += offset;

        // Begin adjusting camera rotation

        var dirV = Cam->DirV;
        var dirH = Cam->DirH;

        dirV = dirV % (2 * MathF.PI); // keep DirV in reasonable range to avoid camera flipping issues

        // Determine DirV and DirH limits
        CalculateDirectionRange(truePitch, DirVMaxDeg, configuration.FirstPersonHeadRotationPitch * DegreesToRadians, out var dirvMin, out var dirvMax);

        if (previousTickWasFirstPerson)
        {
            // Apply rotation delta to camera

            // Yaw affects camera DirH
            var diff = RotationalDifference(trueYaw, previousFacing);
            if (Math.Abs(diff) > EulerEpsilon)
            {
                if (!configuration.ReducedMotion && !InputManager.IsRightMouseDown())
                    dirH += diff;
                previousFacing = trueYaw;
            }

            // Pitch affects camera DirV
            diff = RotationalDifference(truePitch, previousHeadPitch);
            if (Math.Abs(diff) > EulerEpsilon)
            {
                if (!configuration.ReducedMotion)
                    dirV += diff;
                previousHeadPitch = truePitch;
            }
        }
        else
        {
            Cam->DirVMin = -2 * MathF.PI;
            Cam->DirVMax = 2 * MathF.PI;
            previousTickWasFirstPerson = true;
        }

        var straightUp = 90 * DegreesToRadians;
        var straightDown = -90 * DegreesToRadians;

        // Clamp DirV before singularity check
        if (!configuration.ReducedMotion)
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

        // Handle DirH clamping
        CalculateDirectionRange(trueYaw, DirHMaxDeg, 0f, out var dirhMin, out var dirhMax);
        dirH = RotateDir(dirH);
        if (!configuration.ReducedMotion)
            dirH = ClampRotational(dirH, dirhMin, dirhMax);

        // Apply tilt to camera
        var distFromStraightH = Math.Abs(RotationalDifference(dirH, trueYaw));
        var distFromStraightV = Math.Abs(RotationalDifference(dirV, truePitch));
        var tiltFactor = 1f - (Math.Max(distFromStraightH, distFromStraightV) / (MathF.PI / 2f));
        CameraRoll = trueRoll * tiltFactor;

        if (isFlippedByGame)
        {
            CameraRoll = (trueRoll * tiltFactor) + (float)Math.PI; // flip camera when looking past straight up or down
        }

        // Apply FOV and rotational changes
        Cam->FoV = configuration.FirstPersonFieldOfView / 100f;
        Cam->DirV = RotateDir(dirV);
        Cam->DirH = dirH;

        // In first person with RealFirstPerson enabled, override position
        nextCameraPosition = boneModelPos;

        previousDirV = Cam->DirV;

        return true;
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
        return $"Rotation(X={Rotation.X:F3}, Y={Rotation.Y:F3}, Z={Rotation.Z:F3}, W={Rotation.W:F3})";
    }
}
