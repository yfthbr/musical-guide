using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace MusicalGuide;

internal static unsafe class Debug
{
#if DEBUG
    private const int StructSize = 0x2C0;
    private static readonly byte[] PrevBytes = new byte[StructSize];
    private static readonly byte[] NewBytes = new byte[StructSize];

    public static void PrintDebug(Configuration configuration)
    {
        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)((GameObject*)S.ObjectTable.LocalPlayer!.Address)->DrawObject;
        if (charaBase == null)
            return;

        var skeleton = charaBase->Skeleton;
        if (skeleton->PartialSkeletonCount < 2)
            return;
        for (var j = 0; j < skeleton->PartialSkeletonCount; j++)
        {
            var partialSkeleton = &skeleton->PartialSkeletons[j];
            {
                var havokPose = partialSkeleton->GetHavokPose(0);
                if (havokPose == null)
                    return;

                for (var i = 0; i < havokPose->Skeleton->Bones.Length; i++)
                {
                    var bone = havokPose->Skeleton->Bones[i];
                    S.Log.Info($"Skeleton {j} Bone {i}: {bone.Name.String}");
                }
            }
        }
        // if (S.GameConfig.UiConfig.TryGetUInt("FPSCameraInterpolationType", out var value))
        // {
        //     S.Log.Info($"FPSCameraInterpolationType: {value}");
        // }

        var changed = new List<int>();
        for (var offset = 0; offset < StructSize; offset++)
        {
            var b = Marshal.ReadByte((nint)CamController.Cam, offset);
            if (b != NewBytes[offset])
            {
                changed.Add(offset);
            }
            PrevBytes[offset] = NewBytes[offset];
            NewBytes[offset] = b;
        }

        if (changed.Count > 0)
        {
            // Log changes in hex
            S.Log.Info($"Struct changed at offsets:\n{string.Join("\n", changed.Select(o => $"+{o:X} (0x{NewBytes[o]:X2} -> 0x{PrevBytes[o]:X2})"))}");

            // Read in buckets of 4 bytes as floats
            // var changedFloats = changed.Where(o => o % 4 == 0).Select(o => (Offset: o, Value: System.BitConverter.ToSingle(NewBytes, o)));
            // S.Log.Info($"Cam changed at offsets:\n{string.Join("\n", changedFloats.Select(c => $"+{c.Offset:X} (float: {c.Value:F2})"))}");
        }
    }
#else
    public static void PrintDebug(Configuration configuration)
    {
    }
#endif
}
