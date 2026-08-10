using System.Runtime.InteropServices;

namespace SPTarkov.Server.Core.Native;

internal static unsafe partial class NativeMethods
{
    private const string LibraryName = "spt_native";

    [LibraryImport(LibraryName, EntryPoint = "spt_native_abi_version")]
    internal static partial uint AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "spt_verify_database")]
    internal static partial int VerifyDatabase(byte* dirUtf8, nuint dirLen, byte** outPtr, nuint* outLen);

    [LibraryImport(LibraryName, EntryPoint = "spt_buf_free")]
    internal static partial void BufFree(byte* ptr, nuint len);
}
