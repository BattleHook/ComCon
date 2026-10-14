using System.Text;
using ComCon.Core.Models;

namespace ComCon.Core.Configuration;

/// <summary>
/// Persists <see cref="AppSettings"/> to a single, explicit on-disk path using the existing
/// strict <see cref="ConfigurationJson"/> serialization/validation. The caller supplies the
/// exact path; there is no default location, path discovery or silent fallback.
/// </summary>
/// <remarks>
/// <see cref="Load"/> never invents default settings: a missing file is a hard
/// <see cref="FileNotFoundException"/>. <see cref="Save"/> validates and serializes before
/// touching disk, bounds both reads and writes to <see cref="MaxFileSizeBytes"/>, and writes
/// atomically via a same-directory temporary file followed by <see cref="File.Move(string, string, bool)"/>,
/// so a failed write preserves the original file. To avoid silently discarding a concurrent
/// edit, this store refuses to overwrite a target whose on-disk bytes do not match the last
/// snapshot this instance itself loaded or saved - including an existing file that this
/// instance has never loaded, and a previously loaded file that has since been deleted.
/// Snapshot checks are not an operating-system compare-and-swap; arbitrary external
/// writers racing the final replacement are not coordinated by this store.
/// </remarks>
public sealed class ConfigurationFileStore
{
    /// <summary>Bounded size limit applied to both reads and writes, in bytes (1 MiB).</summary>
    public const int MaxFileSizeBytes = 1024 * 1024;

    private readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private byte[]? _lastKnownBytes;

    public ConfigurationFileStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = Path.GetFullPath(path);
    }

    public string FilePath { get; }

    /// <summary>Loads and validates settings from <see cref="FilePath"/>.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist. No default is returned.</exception>
    /// <exception cref="ConfigurationSizeLimitExceededException">The file exceeds <see cref="MaxFileSizeBytes"/>.</exception>
    /// <exception cref="ConfigurationSerializationException">The file is malformed, unknown or duplicate JSON.</exception>
    /// <exception cref="ConfigurationValidationException">The deserialized settings fail validation.</exception>
    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            throw new FileNotFoundException($"Configuration file not found: {FilePath}", FilePath);
        }

        var bytes = ReadBoundedBytes(FilePath);
        var json = _strictUtf8.GetString(bytes);
        var settings = ConfigurationJson.Deserialize(json);

        _lastKnownBytes = bytes;
        return settings;
    }

    /// <summary>
    /// Validates and serializes <paramref name="settings"/>, then atomically writes it to
    /// <see cref="FilePath"/> - unless the current on-disk content no longer matches the last
    /// snapshot this instance loaded or saved, in which case the write is refused explicitly.
    /// </summary>
    /// <exception cref="ConfigurationValidationException">The settings fail validation.</exception>
    /// <exception cref="ConfigurationSizeLimitExceededException">The serialized settings exceed <see cref="MaxFileSizeBytes"/>.</exception>
    /// <exception cref="ConfigurationConcurrencyException">
    /// The target was changed, deleted or created outside this store since it was last loaded or saved,
    /// or it already exists and this instance never loaded it.
    /// </exception>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Serialize/validate before touching disk at all.
        var json = ConfigurationJson.Serialize(settings);
        var bytes = _strictUtf8.GetBytes(json);
        if (bytes.LongLength > MaxFileSizeBytes)
        {
            throw new ConfigurationSizeLimitExceededException(
                $"Serialized configuration is {bytes.LongLength} bytes, exceeding the {MaxFileSizeBytes}-byte limit.");
        }

        EnsureNoConcurrentExternalChange();

        var directory = Path.GetDirectoryName(FilePath) ?? string.Empty;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            WriteBoundedBytesDurably(tempPath, bytes);
            EnsureNoConcurrentExternalChange();
            File.Move(tempPath, FilePath, overwrite: _lastKnownBytes is not null);
        }
        catch (Exception primaryFailure)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
            {
                throw new AggregateException(
                    $"Configuration save failed and temporary-file cleanup also failed: {tempPath}",
                    primaryFailure, cleanupFailure);
            }
            throw;
        }

        _lastKnownBytes = bytes;
    }

    private void EnsureNoConcurrentExternalChange()
    {
        if (!File.Exists(FilePath))
        {
            if (_lastKnownBytes is not null)
            {
                throw new ConfigurationConcurrencyException(
                    $"Configuration file was deleted outside this store since it was last loaded/saved: {FilePath}");
            }

            // Never loaded and nothing on disk: creating a brand-new file is safe.
            return;
        }

        if (_lastKnownBytes is null)
        {
            throw new ConfigurationConcurrencyException(
                $"Configuration file already exists and was never loaded by this store; refusing to overwrite it silently: {FilePath}");
        }

        var currentBytes = ReadBoundedBytes(FilePath);
        if (!currentBytes.AsSpan().SequenceEqual(_lastKnownBytes))
        {
            throw new ConfigurationConcurrencyException(
                $"Configuration file changed outside this store since it was last loaded/saved: {FilePath}");
        }
    }

    private static byte[] ReadBoundedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileSizeBytes)
        {
            throw new ConfigurationSizeLimitExceededException(
                $"Configuration file is {stream.Length} bytes, exceeding the {MaxFileSizeBytes}-byte limit: {path}");
        }

        var buffer = new byte[stream.Length];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of stream while reading configuration file: {path}");
            }

            totalRead += read;
        }

        return buffer;
    }

    private static void WriteBoundedBytesDurably(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

}

/// <summary>The on-disk configuration target was modified, deleted or created outside this store.</summary>
public sealed class ConfigurationConcurrencyException : Exception
{
    public ConfigurationConcurrencyException(string message)
        : base(message)
    {
    }
}

/// <summary>A configuration read or write exceeded <see cref="ConfigurationFileStore.MaxFileSizeBytes"/>.</summary>
public sealed class ConfigurationSizeLimitExceededException : Exception
{
    public ConfigurationSizeLimitExceededException(string message)
        : base(message)
    {
    }
}
