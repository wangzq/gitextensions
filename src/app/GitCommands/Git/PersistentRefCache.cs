using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace GitCommands;

/// <summary>
/// Provides persistent caching for the raw text output of <c>git for-each-ref</c>,
/// keyed by a fingerprint of the repository's ref files.
/// </summary>
/// <remarks>
/// <para>
/// The fingerprint is the maximum <see cref="DateTime.Ticks"/> value of
/// <c>LastWriteTimeUtc</c> across the <c>packed-refs</c> file and every file
/// recursively inside the <c>refs/</c> directory.  Since git writes these files
/// monotonically, a changed fingerprint reliably signals stale cached data.
/// </para>
/// <para>
/// Each stored value consists of an 8-byte little-endian fingerprint followed
/// immediately by the UTF-8 encoded raw git output.
/// </para>
/// <para>
/// Worktrees of the same repository share one cache entry because all worktrees
/// share the same <c>commonDir</c> (and therefore the same ref namespace).
/// </para>
/// </remarks>
internal static class PersistentRefCache
{
    private const string KeyPrefix = "refs:";

    /// <summary>
    /// Attempts to retrieve previously cached raw <c>git for-each-ref</c> output.
    /// </summary>
    /// <param name="commonDir">
    /// The git common directory (see <see cref="GitModule.GitCommonDirectory"/>).
    /// </param>
    /// <param name="commandKey">
    /// A stable, colon-free string that uniquely identifies the specific refs
    /// command variant (filter flags, sort field, sort order).
    /// </param>
    /// <param name="rawOutput">The cached raw git output, when the cache is valid.</param>
    /// <returns>
    /// <see langword="true"/> when a fresh, fingerprint-matching cache entry was found;
    /// <see langword="false"/> when the entry is absent or stale.
    /// </returns>
    internal static bool TryGet(
        string commonDir,
        string commandKey,
        [NotNullWhen(true)] out string? rawOutput)
    {
        rawOutput = null;

        if (string.IsNullOrEmpty(commonDir))
        {
            return false;
        }

        string cacheKey = BuildKey(commonDir, commandKey);

        if (!PersistentCacheStore.TryGet(cacheKey, out byte[]? stored))
        {
            return false;
        }

        if (stored.Length < sizeof(long))
        {
            return false;
        }

        long storedFingerprint = BitConverter.ToInt64(stored, 0);
        long currentFingerprint = ComputeFingerprint(commonDir);

        if (storedFingerprint != currentFingerprint)
        {
            return false;
        }

        rawOutput = Encoding.UTF8.GetString(stored, sizeof(long), stored.Length - sizeof(long));
        return true;
    }

    /// <summary>
    /// Stores the raw <c>git for-each-ref</c> output together with the current
    /// fingerprint of the repository's ref files.
    /// </summary>
    internal static void Set(string commonDir, string commandKey, string rawOutput)
    {
        if (string.IsNullOrEmpty(commonDir))
        {
            return;
        }

        long fingerprint = ComputeFingerprint(commonDir);
        byte[] outputBytes = Encoding.UTF8.GetBytes(rawOutput);
        byte[] stored = new byte[sizeof(long) + outputBytes.Length];
        BitConverter.TryWriteBytes(stored.AsSpan(0, sizeof(long)), fingerprint);
        outputBytes.CopyTo(stored.AsSpan(sizeof(long)));

        PersistentCacheStore.Put(BuildKey(commonDir, commandKey), stored);
    }

    // ─── Internals ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes a fingerprint for the current state of ref files inside
    /// <paramref name="commonDir"/> by finding the maximum
    /// <c>LastWriteTimeUtc.Ticks</c> across <c>packed-refs</c> and every file
    /// recursively under <c>refs/</c>.
    /// </summary>
    /// <remarks>
    /// Only filesystem stat() calls are issued — no file content is read.
    /// For most repositories this is far cheaper than spawning a
    /// <c>git for-each-ref</c> process (which requires a new process, git startup
    /// time, and ref enumeration from scratch).
    /// </remarks>
    private static long ComputeFingerprint(string commonDir)
    {
        long maxTicks = 0;

        string packedRefs = Path.Combine(commonDir, "packed-refs");

        if (File.Exists(packedRefs))
        {
            maxTicks = File.GetLastWriteTimeUtc(packedRefs).Ticks;
        }

        string refsDir = Path.Combine(commonDir, "refs");

        if (Directory.Exists(refsDir))
        {
            foreach (string file in Directory.EnumerateFiles(refsDir, "*", SearchOption.AllDirectories))
            {
                long ticks = File.GetLastWriteTimeUtc(file).Ticks;

                if (ticks > maxTicks)
                {
                    maxTicks = ticks;
                }
            }
        }

        return maxTicks;
    }

    private static string BuildKey(string commonDir, string commandKey)
    {
        Span<byte> hashBytes = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(commonDir), hashBytes);
        string hex16 = Convert.ToHexString(hashBytes)[..16];
        return $"{KeyPrefix}{hex16}:{commandKey}";
    }
}
