using System;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Math;

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

    private const float DefaultDirVMin = -85 * (MathF.PI / 180f);
    private const float DefaultDirVMax = 45 * (MathF.PI / 180f);
    private const float DefaultFoV = 0.78f;
    private const float DirVEpsilon = 0.003f; // Small value to avoid an issue with the game flipping camera when looking straight up/down
    private const float EulerEpsilon = 0.001f; // Adjust to avoid camera jittering when idle
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
    public static unsafe bool InFirstPerson => ((ExpandedCamera*)Cam)->Mode == 0;
    #endregion

    #region Volatile State
    private volatile bool isDisposed = false;
    private volatile float targetDistance = MaxDist;
    private volatile bool shouldAdjustDistance = true;
    #endregion

    #region FirstPersonState
    private bool previousTickWasFirstPerson = false;
    private Vector3 previousHeadEuler = Vector3.Zero;
    private float previousDirV = 0f;
    private Vector3 pendingRotationEuler = Vector3.Zero; // TODO: implement smoothing
    #endregion

    #region Class Lifetime
    private readonly Configuration configuration;

    // Verify at: 48 8B C4 44 88 48 ?? 55 56
    private unsafe delegate void GetCameraPositionDelegate(Camera* camera, GameObject* target, Vector3* position, byte swapPerson);
    private Hook<GetCameraPositionDelegate>? getCameraPositionHook;

    public CamController(Configuration configuration)
    {
        this.configuration = configuration;

        configuration.OnConfigurationChanged += OnConfigurationChanged;

        unsafe
        {
            var camVTable = Marshal.ReadIntPtr((nint)Cam);
            var GetCameraPositionAddress = Marshal.ReadIntPtr(camVTable, IntPtr.Size * 15); // vf15 is GetCameraPosition
            getCameraPositionHook = S.Interop.HookFromAddress<GetCameraPositionDelegate>(GetCameraPositionAddress, GetCameraPositionDetour);
            getCameraPositionHook.Enable();

            S.Log.Debug($"Current camera limits: Min={Cam->DirVMin}, Max={Cam->DirVMax}, FoV={Cam->FoV}");
        }

        // TODO: hook ShouldDrawGameObject to show player model in first person when configuration is set
    }

    private void OnConfigurationChanged()
    {
        shouldAdjustDistance = true;
    }

    public void Dispose()
    {
        getCameraPositionHook?.Disable();
        getCameraPositionHook?.Dispose();

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
            SetThirdPersonDistance();
        }

        previousTickWasFirstPerson = InFirstPerson;
    }
    #endregion

    #region First Person Handling
    private void UpdateFirstPersonCamera()
    {
        if (!configuration.RealFirstPerson) return;
    }

    private unsafe void GetCameraPositionDetour(Camera* camera, GameObject* target, Vector3* position, byte swapPerson)
    {
        getCameraPositionHook!.Original(camera, target, position, swapPerson);
        TryOverrideCameraPosition(position);
    }

    private unsafe bool TryOverrideCameraPosition(Vector3* position)
    {
        if (!configuration.RealFirstPerson || !InFirstPerson)
            return false;

        // Unrestrict vertical camera movement in first person
        Cam->DirVMin = -179 * (MathF.PI / 180f);
        Cam->DirVMax = 179 * (MathF.PI / 180f);

        // These are slightly off 90 degrees as the game does camera flip slightly earlier than that.
        var straightUp = 90 * MathF.PI / 180f;
        var straightDown = -90 * MathF.PI / 180f;

        // Jump over the singularity at straight up/down
        if (Math.Abs(Cam->DirV - straightUp) < DirVEpsilon || Math.Abs(Cam->DirV - straightDown) < DirVEpsilon)
        {
            if (previousDirV < Cam->DirV)
                Cam->DirV += DirVEpsilon * 2;
            else if (previousDirV > Cam->DirV)
                Cam->DirV -= DirVEpsilon * 2;
        }

        if (Math.Abs(Cam->DirV) > straightUp)
            CameraTilt = (float)Math.PI; // flip camera when looking past straight up or down
        else
            CameraTilt = 0;

        previousDirV = Cam->DirV;

        S.Log.Debug($"First person camera DirV: {Cam->DirV}, {Cam->DirH}, Tilt: {CameraTilt}");

        // Rough plan:
        // 1. Get bone position
        // 2. Update camera target position+rotation to that bone position + some offset (cam should be slightly in front of the face)
        // 3. Adjust camera position towards target position instantly (in the future: smoothly but quickly)
        // 4. Adjust camera rotation with changes in bone rotation, not overriding player rotation inputs

        // Note: floating point precision, use Abs with a small epsilon

        const int HEAD_SKELETON_INDEX = 1;
        const int POSE_INDEX = 0; // 0 seems to work? bone exists in all 0-3
        const int BONE_INDEX = 33; // j_f_uhana

        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)((GameObject*)S.ObjectTable.LocalPlayer!.Address)->DrawObject;
        if (charaBase == null)
            return false;

        var skeleton = charaBase->Skeleton;
        if (skeleton->PartialSkeletonCount < 2)
            return false;
        var partialSkeleton = &skeleton->PartialSkeletons[HEAD_SKELETON_INDEX];
        var havokPose = partialSkeleton->GetHavokPose(POSE_INDEX);
        if (havokPose == null)
            return false;

        var bone = havokPose->Skeleton->Bones[BONE_INDEX];
        var boneTransform = havokPose->AccessBoneModelSpace(BONE_INDEX, FFXIVClientStructs.Havok.Animation.Rig.hkaPose.PropagateOrNot.Propagate);
        var boneModelPos = new Vector3(boneTransform->Translation.X, boneTransform->Translation.Y, boneTransform->Translation.Z);
        var boneQuaternion = QuaternionFromHkQuaternion(boneTransform->Rotation);
        var normalizedQuaternion = Quaternion.Normalize(boneQuaternion);
        var boneEuler = QuaternionToEuler(normalizedQuaternion);

        if (previousTickWasFirstPerson)
        {
            // Apply rotation delta to camera
            var deltaEuler = boneEuler - previousHeadEuler;

            // Yaw (Y axis) affects camera DirH
            if (Math.Abs(deltaEuler.Y) > EulerEpsilon)
            {
                Cam->DirH += deltaEuler.Y;
                previousHeadEuler.Y = boneEuler.Y;
            }

            // Pitch (Z axis) affects camera DirV
            if (Math.Abs(deltaEuler.Z) > EulerEpsilon)
            {
                Cam->DirV += Math.Clamp(deltaEuler.Z, Cam->DirVMin, Cam->DirVMax);
                previousHeadEuler.Z = boneEuler.Z;
            }
        }
        else
        {
            //Cam->DirH += 90 * (MathF.PI / 180f); // adjust for model facing direction
            previousTickWasFirstPerson = true;
        }

        // Apply tilt to camera
        CameraTilt += boneEuler.X; // + (MathF.PI / 2f);

        // Apply FOV
        Cam->FoV = configuration.FirstPersonFieldOfView / 100f;

        // Rotate boneModelPos by the character's world rotation
        boneModelPos = Vector3.Transform(boneModelPos, Matrix4x4.CreateFromQuaternion(charaBase->Rotation));

        var nextCameraPosition = (Vector3)S.ObjectTable.LocalPlayer!.Position + boneModelPos;

        // In first person with RealFirstPerson enabled, override position
        *position = nextCameraPosition;

        return true;
    }

    // Convert quaternion -> radians.
    // X = tilting sideways
    // Y = turning left/right
    // Z = rolling forward/backward
    private static Vector3 QuaternionToEuler(Quaternion q)
    {
        // We use a rotation sequence that ensures the singularity is on X (Pitch).
        // This allows Y (Yaw) to spin 360 degrees without locking.

        // 1. Calculate the sin of the Pitch (X-axis)
        // Note: The term (w*x - y*z) might need sign flipping depending on 
        // exact quaternion generation, but this is the standard basis for Singularity on X.
        float sinPitch = 2f * (q.W * q.X - q.Y * q.Z);

        float pitch, yaw, roll;

        // 2. Check for Gimbal Lock (North/South Pole equivalent)
        // If we are looking straight up/down, Pitch is +/- 90.
        if (MathF.Abs(sinPitch) >= 0.9999f)
        {
            // Clamp pitch to 90 degrees
            pitch = MathF.CopySign(MathF.PI / 2f, sinPitch);

            // In this state, Yaw and Roll are linked. 
            // We force Yaw (Y) to do the work and zero out Roll (Z).
            yaw = 2f * MathF.Atan2(q.Y, q.W);
            roll = 0f;
        }
        else
        {
            // Standard Pitch Calculation
            pitch = MathF.Asin(sinPitch);

            // Yaw (Rotation about Y)
            // Uses Atan2, allowing full 360 degree rotation
            float sinYaw = 2f * (q.W * q.Y + q.X * q.Z);
            float cosYaw = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            yaw = MathF.Atan2(sinYaw, cosYaw);

            // Roll (Rotation about Z)
            float sinRoll = 2f * (q.W * q.Z + q.X * q.Y);
            float cosRoll = 1f - 2f * (q.X * q.X + q.Z * q.Z);
            roll = MathF.Atan2(sinRoll, cosRoll);
        }

        // Returns standard FFXIV vector format:
        // X = Pitch (Radians)
        // Y = Yaw   (Radians)
        // Z = Roll  (Radians)
        return new Vector3(pitch, yaw, roll);
    }

    public static unsafe Vector2 OwnAimVector2()
    {
        try
        {
            var camera = SceneCameraManager->CurrentCamera;
            var threeDAim =
                new Vector3(camera->RenderCamera->Origin.X, camera->RenderCamera->Origin.Y,
                            camera->RenderCamera->Origin.Z) - (Vector3)S.ObjectTable.LocalPlayer!.Position;
            return Vector2.Normalize(new Vector2(threeDAim.X, threeDAim.Z));
        }
        catch (NullReferenceException)
        {
            // Camera does not exist during loading screens
            return Vector2.Zero;
        }
    }

    private static Quaternion QuaternionFromHkQuaternion(FFXIVClientStructs.Havok.Common.Base.Math.Quaternion.hkQuaternionf hkQuat)
    {
        return new Quaternion(hkQuat.X, hkQuat.Y, hkQuat.Z, hkQuat.W);
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
        return Math.Clamp(distance, MinDist, MaxDist);
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
internal unsafe struct ExpandedCamera
{
    [FieldOffset(0x170)] public float Tilt;
    [FieldOffset(0x180)] public int Mode;
    [FieldOffset(0x1F4)] public byte IsFlipped;
}
