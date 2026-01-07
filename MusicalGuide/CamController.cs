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
    private const float DirVEpsilon = 0.003f; // Small value to avoid an issue with the game flipping camera when looking straight up/down
    private const float EulerEpsilon = 0.001f; // Adjust to avoid camera jittering when idle
    private const float EulerLargeChangeThreshold = 1f;
    private const int BoneIndex = 33; // j_f_uhana

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
    public static unsafe bool InFirstPerson => ((ExpandedCamera*)Cam)->Mode == 0;
    #endregion

    #region Volatile State
    private volatile bool isDisposed = false;
    private volatile float targetDistance = MaxCameraDistance;
    private volatile bool shouldAdjustDistance = true;
    #endregion

    #region FirstPersonState
    private volatile bool previousTickWasFirstPerson = false;
    private DateTime pauseFpUntil = DateTime.MinValue;
    private Vector3 previousHeadEuler = new();
    private float previousDirV = 0f;
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
            SetThirdPersonDistance();
        }
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

        if (DateTime.Now < pauseFpUntil)
        {
            return true;
        }

        // Rough plan:
        // 1. Get bone position
        // 2. Update camera target position+rotation to that bone position + some offset (cam should be slightly in front of the face)
        // 3. Adjust camera position towards target position instantly (in the future: smoothly but quickly)
        // 4. Adjust camera rotation with changes in bone rotation, not overriding player rotation inputs

        // Note: floating point precision, use Abs with a small epsilon

        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)((GameObject*)S.ObjectTable.LocalPlayer!.Address)->DrawObject;
        if (charaBase == null)
            return false;

        var skeleton = charaBase->Skeleton;
        if (skeleton->PartialSkeletonCount < 2)
            return false;
        var partialSkeleton = &skeleton->PartialSkeletons[HeadSkeletonIndex];
        var havokPose = GetHavokPose(partialSkeleton);
        if (havokPose == null)
            return false;

        // Grab the bone's position and euler rotation
        var bone = havokPose->Skeleton->Bones[BoneIndex];
        var boneTransform = havokPose->AccessBoneModelSpace(BoneIndex, FFXIVClientStructs.Havok.Animation.Rig.hkaPose.PropagateOrNot.DontPropagate);
        var boneModelPos = new Vector3(boneTransform->Translation.X, boneTransform->Translation.Y, boneTransform->Translation.Z);
        var boneQuaternion = QuaternionFromHkQuaternion(boneTransform->Rotation);
        var boneEuler = boneQuaternion.ToEuler();

        // Apply pitch offset
        boneEuler.Z -= configuration.FirstPersonHeadRotationPitch * (MathF.PI / 180f);

        var deltaEuler = boneEuler - previousHeadEuler;
        if (deltaEuler.Magnitude > EulerLargeChangeThreshold)
        {
            if (pauseFpUntil != DateTime.MinValue)
            {
                // Large change, likely a teleport or cutscene adjustment. Ignore deltas for a few ticks to avoid camera snapping.
                S.Log.Debug($"Large head rotation change detected: {deltaEuler.Magnitude}, ignoring for a few ticks.");
                pauseFpUntil = DateTime.Now.AddMilliseconds(100);
                return true;
            }
            else
            {
                S.Log.Debug($"Large head rotation change detected: {deltaEuler.Magnitude}, but already paused, resuming normal operation.");
                pauseFpUntil = DateTime.MinValue;
            }
        }

        // Begin adjusting camera rotation

        previousDirV = Cam->DirV;

        Cam->DirV = Cam->DirV % (2 * MathF.PI); // keep DirV in reasonable range to avoid camera flipping issues

        // Apply tilt to camera
        CameraTilt = boneEuler.X;

        // Determine DirV and DirH limits
        float dirvMin, dirvMax;
        CalculateDirectionRange(boneEuler.Z, DirVMaxDeg, out dirvMin, out dirvMax);
        float dirhMin, dirhMax;
        CalculateDirectionRange(boneEuler.Y + MathF.PI, DirHMaxDeg, out dirhMin, out dirhMax);

        float? dirHAntiClockwise = null;
        if (previousTickWasFirstPerson)
        {
            // Apply rotation delta to camera

            // Yaw (Y axis) affects camera DirH
            if (Math.Abs(deltaEuler.Y) > EulerEpsilon)
            {
                dirHAntiClockwise = Cam->DirH - deltaEuler.Y;
                Cam->DirH = Cam->DirH + deltaEuler.Y;
                previousHeadEuler.Y = boneEuler.Y;
            }

            // Pitch (Z axis) affects camera DirV
            if (Math.Abs(deltaEuler.Z) > EulerEpsilon)
            {
                Cam->DirV = Cam->DirV + deltaEuler.Z;
                previousHeadEuler.Z = boneEuler.Z;
            }
        }
        else
        {
            Cam->DirH = boneEuler.Y; // adjust for model facing direction
            Cam->DirVMin = -2 * MathF.PI;
            Cam->DirVMax = 2 * MathF.PI;
            S.Log.Debug($"First person initial DirH set to {Cam->DirH} from bone yaw {boneEuler.Y}");
            previousTickWasFirstPerson = true;
        }

        var straightUp = 90 * MathF.PI / 180f;
        var straightDown = -90 * MathF.PI / 180f;

        // Called before adjustment to ensure the singularity is handled correctly
        RotateDirV();

        // Jump over the singularity at straight up/down
        if (Math.Abs(Cam->DirV - straightUp) < DirVEpsilon || Math.Abs(Cam->DirV - straightDown) < DirVEpsilon)
        {
            if (previousDirV < Cam->DirV)
                Cam->DirV += DirVEpsilon * 2;
            else if (previousDirV > Cam->DirV)
                Cam->DirV -= DirVEpsilon * 2;
        }

        // Called again after potential jump to ensure DirV is in valid range
        RotateDirV();

        if (Math.Abs(Cam->DirV) > straightUp)
        {
            CameraTilt += (float)Math.PI; // flip camera when looking past straight up or down
            if (dirHAntiClockwise.HasValue)
            {
                // Invert DirH when upside down
                Cam->DirH = dirHAntiClockwise.Value;
            }
        }

        // S.Log.Debug($"Camera DirH: {Cam->DirH}, DirV: {Cam->DirV} - {targetDirV} / {dirvMin} to {dirvMax}, Tilt: {CameraTilt}, Bone Euler: {boneEuler}");

        // Clamp DirV and DirH to be within target range
        Cam->DirV = ClampRotational(Cam->DirV, dirvMin, dirvMax);
        Cam->DirH = ClampRotational(Cam->DirH, dirhMin, dirhMax);

        // Apply FOV
        Cam->FoV = configuration.FirstPersonFieldOfView / 100f;

        // Rotate boneModelPos by the character's world rotation
        boneModelPos = Vector3.Transform(boneModelPos + configuration.FirstPersonOffset, Matrix4x4.CreateFromQuaternion(charaBase->Rotation));

        var nextCameraPosition = (Vector3)S.ObjectTable.LocalPlayer!.Position + boneModelPos;

        // Account for draw offsets (e.g. SimpleHeels)
        var playerGameObject = (GameObject*)S.ObjectTable.LocalPlayer!.Address;
        nextCameraPosition += playerGameObject->DrawOffset;
        nextCameraPosition += new Vector3(0, -0.1f, 0); // small downward offset to place camera better in front of face

        // In first person with RealFirstPerson enabled, override position
        *position = nextCameraPosition;

        return true;
    }

    private static void CalculateDirectionRange(float rootRotation, int degrees, out float dirMin, out float dirMax)
    {
        var targetDir = rootRotation % (2 * MathF.PI);
        if (targetDir > MathF.PI)
            targetDir -= 2 * MathF.PI;
        else if (targetDir < -MathF.PI)
            targetDir += 2 * MathF.PI;
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

    private float ClampRotational(float input, float min, float max)
    {
#if DEBUG
        var _input = input;
#endif
        // Clamp input to be within target range
        if (input > max && input < min) // between min and max, choose closest
        {
            var distToMin = Math.Abs(input - min);
            var distToMax = Math.Abs(input - max);
            if (distToMin < distToMax)
                input = min;
            else
                input = max;
        }
        else if (max > min) // Max has not rotated back to negatives
        {
            if (input < min) // below min
                input = min;
            else if (input > max) // above max
                input = max;
        }
#if DEBUG
        if (input != _input)
            S.Log.Verbose($"Clamped rotational from {_input} to {input} within {min} to {max}");
#endif
        return input;
    }

    private unsafe void RotateDirV()
    {
        const float maxRotation = MathF.PI;
        if (Cam->DirV > maxRotation)
        {
            S.Log.Debug($"Rotating DirV down from {Cam->DirV}");
            Cam->DirV -= 2 * maxRotation;
        }
        else if (Cam->DirV < -maxRotation)
        {
            S.Log.Debug($"Rotating DirV up from {Cam->DirV}");
            Cam->DirV += 2 * maxRotation;
        }
    }

    private unsafe FFXIVClientStructs.Havok.Animation.Rig.hkaPose* GetHavokPose(FFXIVClientStructs.FFXIV.Client.Graphics.Render.PartialSkeleton* partialSkeleton)
    {
        return partialSkeleton->GetHavokPose(0);
    }

    // Convert quaternion -> radians.
    // X = tilting sideways
    // Y = turning left/right
    // Z = rolling forward/backward
    internal static Vector3 QuaternionToEuler(Quaternion q)
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

    internal static Quaternion2 QuaternionFromHkQuaternion(FFXIVClientStructs.Havok.Common.Base.Math.Quaternion.hkQuaternionf hkQuat)
    {
        return new Quaternion2(hkQuat.X, hkQuat.Y, hkQuat.Z, hkQuat.W);
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
    [FieldOffset(0x1F4)] public byte IsFlipped;
}

public class Quaternion2
{
    public float X;
    public float Y;
    public float Z;
    public float W;

    public Quaternion2(float x, float y, float z, float w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public Quaternion2(Quaternion q)
    {
        X = q.X;
        Y = q.Y;
        Z = q.Z;
        W = q.W;
    }

    public Quaternion2(Vector3 euler)
    {
        // Convert Euler angles (in radians) to Quaternion
        float cy = MathF.Cos(euler.Y * 0.5f);
        float sy = MathF.Sin(euler.Y * 0.5f);
        float cp = MathF.Cos(euler.X * 0.5f);
        float sp = MathF.Sin(euler.X * 0.5f);
        float cr = MathF.Cos(euler.Z * 0.5f);
        float sr = MathF.Sin(euler.Z * 0.5f);

        W = cr * cp * cy + sr * sp * sy;
        X = sr * cp * cy - cr * sp * sy;
        Y = cr * sp * cy + sr * cp * sy;
    }

    public Quaternion ToQuaternion()
    {
        return new Quaternion(X, Y, Z, W);
    }

    public Vector3 ToEuler()
    {
        return CamController.QuaternionToEuler(Quaternion.Normalize(this.ToQuaternion()));
    }
}
