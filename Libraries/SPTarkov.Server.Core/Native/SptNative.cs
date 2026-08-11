using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPTarkov.Server.Core.Native;

public sealed class VerifyFailure
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class VerifyResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("failures")]
    public List<VerifyFailure> Failures { get; set; } = [];

    [JsonPropertyName("checked")]
    public int Checked { get; set; }
}

public static class SptNative
{
    private const uint ExpectedAbiVersion = 2;

    // No CancellationToken: the native hash pass is a single bounded blocking call that cannot be
    // interrupted once in flight, so accepting a token would promise cancellation it can't deliver.
    public static Task<VerifyResult> VerifyDatabaseAsync(string sptDataDir)
    {
        return Task.Run(() => VerifyDatabase(sptDataDir));
    }

    /// <summary>
    /// Forces the native library to load and checks its ABI version, so a missing or stale
    /// spt_native fails at startup with a clear message instead of mid-request.
    /// </summary>
    public static void EnsureLoadable()
    {
        var actual = NativeMethods.AbiVersion();
        if (actual != ExpectedAbiVersion)
        {
            throw new InvalidOperationException(
                $"spt_native ABI version mismatch: expected {ExpectedAbiVersion}, found {actual}. Rebuild the native library (dotnet build runs cargo automatically)."
            );
        }
    }

    private static unsafe VerifyResult VerifyDatabase(string sptDataDir)
    {
        EnsureLoadable();

        var dirUtf8 = Encoding.UTF8.GetBytes(sptDataDir);
        byte* outPtr = null;
        nuint outLen = 0;
        int status;

        fixed (byte* dirPtr = dirUtf8)
        {
            status = NativeMethods.VerifyDatabase(dirPtr, (nuint)dirUtf8.Length, &outPtr, &outLen);
        }

        // Throwing before the try/finally is safe here only because verify writes a buffer on
        // success alone. Do NOT copy this shape into the generate exports (ABI 2): those also write
        // a message buffer on BAD_ARGS and ERROR, so their wrappers must branch on outPtr, never on
        // the status, and free whenever it is non-null (null-arg BAD_ARGS and PANIC write nothing).
        if (status != 0)
        {
            throw new InvalidOperationException(
                $"spt_native verification failed with internal status {status}; this indicates a native library bug, not corrupt game data."
            );
        }

        try
        {
            var json = new ReadOnlySpan<byte>(outPtr, checked((int)outLen));
            return JsonSerializer.Deserialize<VerifyResult>(json)
                ?? throw new InvalidOperationException("spt_native returned an empty verification result.");
        }
        finally
        {
            NativeMethods.BufFree(outPtr, outLen);
        }
    }
}
