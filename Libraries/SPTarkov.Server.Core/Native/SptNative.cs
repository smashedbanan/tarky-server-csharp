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
    private const uint ExpectedAbiVersion = 1;

    public static Task<VerifyResult> VerifyDatabaseAsync(string sptDataDir, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => VerifyDatabase(sptDataDir), cancellationToken);
    }

    private static unsafe VerifyResult VerifyDatabase(string sptDataDir)
    {
        EnsureAbiVersion();

        var dirUtf8 = Encoding.UTF8.GetBytes(sptDataDir);
        byte* outPtr = null;
        nuint outLen = 0;
        int status;

        fixed (byte* dirPtr = dirUtf8)
        {
            status = NativeMethods.VerifyDatabase(dirPtr, (nuint)dirUtf8.Length, &outPtr, &outLen);
        }

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

    private static void EnsureAbiVersion()
    {
        var actual = NativeMethods.AbiVersion();
        if (actual != ExpectedAbiVersion)
        {
            throw new InvalidOperationException(
                $"spt_native ABI version mismatch: expected {ExpectedAbiVersion}, found {actual}. Rebuild the native library (dotnet build runs cargo automatically)."
            );
        }
    }
}
