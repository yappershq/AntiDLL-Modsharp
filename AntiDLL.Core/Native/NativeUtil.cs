using System;

namespace AntiDLL.Native;

/// <summary>
///     User-space pointer gate for raw dereferences inside the native detour. Cheap canonical-shape check
///     so a non-pointer / garbage value never segfaults the engine.
///       Linux x64   : user pointers are 0x00007Fxx_xxxxxxxx → bits [63:40] == 0x7F.
///       Windows x64 : user pointers are below 0x0000_8000_0000_0000 → bits [63:48] == 0 (and non-tiny).
///     (Copied from the SendProxy production template.)
/// </summary>
internal static class NativeUtil
{
    private static readonly bool IsWindows = OperatingSystem.IsWindows();

    public static bool IsUserPtr(nint p)
        => IsWindows
            ? p > 0x10000 && ((ulong) p >> 48) == 0
            : p > 0 && ((ulong) p >> 40) == 0x7F;
}
