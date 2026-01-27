using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Math;

namespace MusicalGuide;

internal static unsafe class Debug
{
    public static void PrintDebug(Configuration configuration)
    {
#if DEBUG
        const int HEAD_SKELETON_INDEX = 1;
        const int BONE_INDEX = 33; // j_f_uhana

        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)((GameObject*)S.ObjectTable.LocalPlayer!.Address)->DrawObject;
        if (charaBase == null)
            return;

        var skeleton = charaBase->Skeleton;
        if (skeleton->PartialSkeletonCount < 2)
            return;
        var partialSkeleton = &skeleton->PartialSkeletons[HEAD_SKELETON_INDEX];
        {
            var havokPose = partialSkeleton->GetHavokPose(0);
            if (havokPose == null)
                return;

            for (int i = 0; i < havokPose->Skeleton->Bones.Length; i++)
            {
                var bone = havokPose->Skeleton->Bones[i];
                S.Log.Info($"Bone {i}: {bone.Name.String}");
            }
        }

        for (int POSE_INDEX = 0; POSE_INDEX <= 3; POSE_INDEX++)
        {
            var havokPose = partialSkeleton->GetHavokPose(POSE_INDEX);
            if (havokPose == null)
                return;

            var bone = havokPose->Skeleton->Bones[BONE_INDEX];
            var boneTransform = havokPose->AccessBoneModelSpace(BONE_INDEX, FFXIVClientStructs.Havok.Animation.Rig.hkaPose.PropagateOrNot.Propagate);
            var boneModelPos = new Vector3(boneTransform->Translation.X, boneTransform->Translation.Y, boneTransform->Translation.Z);

            var transformedPos = Vector3.Transform(boneModelPos + configuration.FirstPersonOffset, Matrix4x4.CreateFromQuaternion(charaBase->Rotation));

            S.Log.Info($"Pose {POSE_INDEX}: Bone {bone.Name.String}, Position: {boneModelPos} -> Transformed Position: {transformedPos} - Character Rotation: {charaBase->Rotation}");
        }
        S.Log.Info($"Character Position: {(Vector3)S.ObjectTable.LocalPlayer!.Position} - Draw Offset: {((GameObject*)S.ObjectTable.LocalPlayer!.Address)->DrawOffset} - Rotation: {((GameObject*)S.ObjectTable.LocalPlayer!.Address)->Rotation} - CameraTilt {CamController.CameraRoll}");
        S.Log.Info($"Status: IsSeated: {Marshal.ReadByte((nint)(&((Character*)S.ObjectTable.LocalPlayer!.Address)->EmoteController) + 0x20)}");
        S.Log.Info($"Camera {Marshal.ReadByte((nint)CamController.Cam + 0x2a)} - DirH: {CamController.Cam->DirH * CamController.RadiansToDegrees:F2} degrees");
#endif
    }
}
