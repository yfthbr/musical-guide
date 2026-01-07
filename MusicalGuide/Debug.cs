using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Math;

namespace MusicalGuide;

unsafe static class Debug
{
    public static void PrintDebug()
    {
        const int HEAD_SKELETON_INDEX = 1;
        const int BONE_INDEX = 33; // j_f_uhana

        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)((GameObject*)S.ObjectTable.LocalPlayer!.Address)->DrawObject;
        if (charaBase == null)
            return;

        var skeleton = charaBase->Skeleton;
        if (skeleton->PartialSkeletonCount < 2)
            return;
        var partialSkeleton = &skeleton->PartialSkeletons[HEAD_SKELETON_INDEX];

        for (int POSE_INDEX = 0; POSE_INDEX <= 3; POSE_INDEX++)
        {
            var havokPose = partialSkeleton->GetHavokPose(POSE_INDEX);
            if (havokPose == null)
                return;

            var bone = havokPose->Skeleton->Bones[BONE_INDEX];
            var boneTransform = havokPose->AccessBoneModelSpace(BONE_INDEX, FFXIVClientStructs.Havok.Animation.Rig.hkaPose.PropagateOrNot.Propagate);
            var boneModelPos = new Vector3(boneTransform->Translation.X, boneTransform->Translation.Y, boneTransform->Translation.Z);
            var boneQuaternion = CamController.QuaternionFromHkQuaternion(boneTransform->Rotation);
            var boneEuler = boneQuaternion.ToEuler();

            S.Log.Info($"Pose {POSE_INDEX}: Bone Model Position: {boneEuler}, Position: {boneModelPos}");
        }
    }
}
