using System.Diagnostics.CodeAnalysis;
using System.Text;
using RocksDbSharp;

namespace GitCommands;

/// <summary>
/// Provides a process-wide persistent key-value store backed by RocksDB,
/// used to cache immutable git command outputs and refs across application restarts.
/// </summary>
/// <remarks>
/// <para>
/// The store is opened lazily on first use. If the database cannot be opened
/// (e.g., because another process holds the filesystem lock), all operations
/// silently become no-ops so the rest of the application continues to function.
/// </para>
/// </remarks>
public static class PersistentCacheStore
{
    private const string CommandKeyPrefix = "cmd:";

    private static RocksDb? _db;
    private static readonly Lock _initLock = new();
    private static bool _initFailed;

    /// <summary>
    /// Gets the path to the RocksDB database directory on disk.
    /// </summary>
    public static string CachePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitExtensions",
        "cache");

    private static RocksDb? GetDb()
    {
        if (_initFailed)
        {
            return null;
        }

        if (_db is not null)
        {
            return _db;
        }

        lock (_initLock)
        {
            if (_db is not null)
            {
                return _db;
            }

            try
            {
                Directory.CreateDirectory(CachePath);

                DbOptions options = new DbOptions()
                    .SetCreateIfMissing(true)
                    .SetMaxOpenFiles(100)
                    .OptimizeForPointLookup(8);

                _db = RocksDb.Open(options, CachePath);
            }
            catch (Exception)
            {
                // Another instance may hold the exclusive write lock, or the path is inaccessible.
                // The cache is an optimisation only — fail silently and let git run normally.
                _initFailed = true;
            }
        }

        return _db;
    }

    /// <summary>
    /// Attempts to retrieve the raw bytes stored for <paramref name="key"/>.
    /// </summary>
    /// <returns><see langword="true"/> if the key was found and <paramref name="value"/> is populated.</returns>
    public static bool TryGet(string key, [NotNullWhen(true)] out byte[]? value)
    {
        RocksDb? db = GetDb();

        if (db is null)
        {
            value = null;
            return false;
        }

        try
        {
            value = db.Get(Encoding.UTF8.GetBytes(key));
            return value is not null;
        }
        catch (Exception)
        {
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/>, overwriting any prior value.
    /// </summary>
    public static void Put(string key, byte[] value)
    {
        RocksDb? db = GetDb();

        if (db is null)
        {
            return;
        }

        try
        {
            db.Put(Encoding.UTF8.GetBytes(key), value);
        }
        catch (Exception)
        {
            // Best-effort — a failed write is acceptable; data will be re-populated on next miss.
        }
    }

    /// <summary>
    /// Removes the entry for <paramref name="key"/> if it exists.
    /// </summary>
    public static void Remove(string key)
    {
        RocksDb? db = GetDb();

        if (db is null)
        {
            return;
        }

        try
        {
            db.Remove(Encoding.UTF8.GetBytes(key));
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }

    // ─── Command-output helpers ──────────────────────────────────────────────────
    //
    // Values are stored as:  [int32 outputByteLen][output UTF-8 bytes][error UTF-8 bytes]

    /// <summary>
    /// Attempts to retrieve a previously cached git command's stdout and stderr.
    /// </summary>
    public static bool TryGetCommandOutput(
        string cmd,
        [NotNullWhen(true)] out string? output,
        [NotNullWhen(true)] out string? error)
    {
        output = null;
        error = null;

        if (!TryGet(CommandKeyPrefix + cmd, out byte[]? stored))
        {
            return false;
        }

        if (stored.Length < sizeof(int))
        {
            return false;
        }

        int outputByteLength = BitConverter.ToInt32(stored, 0);

        if (stored.Length < sizeof(int) + outputByteLength)
        {
            return false;
        }

        output = Encoding.UTF8.GetString(stored, sizeof(int), outputByteLength);
        error = Encoding.UTF8.GetString(stored, sizeof(int) + outputByteLength, stored.Length - sizeof(int) - outputByteLength);
        return true;
    }

    /// <summary>
    /// Stores the stdout and stderr of a git command in the persistent cache.
    /// </summary>
    public static void PutCommandOutput(string cmd, string output, string error)
    {
        byte[] outputBytes = Encoding.UTF8.GetBytes(output);
        byte[] errorBytes = Encoding.UTF8.GetBytes(error);
        byte[] stored = new byte[sizeof(int) + outputBytes.Length + errorBytes.Length];
        BitConverter.TryWriteBytes(stored.AsSpan(0, sizeof(int)), outputBytes.Length);
        outputBytes.CopyTo(stored.AsSpan(sizeof(int)));
        errorBytes.CopyTo(stored.AsSpan(sizeof(int) + outputBytes.Length));
        Put(CommandKeyPrefix + cmd, stored);
    }

    // ─── Maintenance ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the approximate total size of all data in the cache directory in bytes,
    /// or <c>-1</c> when the size cannot be determined.
    /// </summary>
    public static long GetApproximateSizeBytes()
    {
        try
        {
            return Directory.Exists(CachePath)
                ? new DirectoryInfo(CachePath)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length)
                : 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// Deletes all cached data from disk and resets the in-memory handle
    /// so that the next access recreates the database from scratch.
    /// </summary>
    public static void Clear()
    {
        lock (_initLock)
        {
            try
            {
                _db?.Dispose();
            }
            catch (Exception)
            {
                // Ignore disposal errors.
            }

            _db = null;
            _initFailed = false;

            try
            {
                if (Directory.Exists(CachePath))
                {
                    Directory.Delete(CachePath, recursive: true);
                }
            }
            catch (Exception)
            {
                // Best-effort.
            }
        }
    }
}
