using System.Security.Cryptography;
using System.Text;

namespace ComCon.Core.Ipc;

internal sealed class BoundedJsonLinesFile(string path, string mutexPrefix, int maximumFiles = 3)
{
    public const int MaximumFileBytes = 1_048_576;
    private readonly string path = Path.GetFullPath(path);

    public long Size
    {
        get
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
    }

    public void Append(ReadOnlySpan<byte> bytes)
        => AppendBatch([bytes.ToArray()]);

    public void AppendBatch(IReadOnlyList<byte[]> records)
    {
        foreach (var record in records)
            if (record.Length > MaximumFileBytes)
                throw new ArgumentException("The JSONL record exceeds the file-size limit.", nameof(records));
        if (records.Count == 0) return;

        using var mutex = CreateMutex(path, mutexPrefix);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException exception)
            {
                acquired = true;
                throw new IOException("The JSONL log lock was abandoned; retry explicitly.", exception);
            }
            if (!acquired) throw new IOException("Timed out waiting for the JSONL log lock.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            foreach (var file in Files()) TrimOversizedFile(file);
            FileStream? stream = null;
            try
            {
                foreach (var record in records)
                {
                    if (stream is not null && stream.Length + record.Length > MaximumFileBytes)
                    {
                        stream.Dispose();
                        stream = null;
                    }
                    if (stream is null)
                    {
                        Rotate(record.Length);
                        stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    }
                    stream.Write(record);
                }
            }
            finally { stream?.Dispose(); }
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    public void Clear()
    {
        using var mutex = CreateMutex(path, mutexPrefix);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException exception)
            {
                acquired = true;
                throw new IOException("The JSONL log lock was abandoned; retry explicitly.", exception);
            }
            if (!acquired) throw new IOException("Timed out waiting for the JSONL log lock.");
            foreach (var file in Files())
                if (File.Exists(file)) File.Delete(file);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private void Rotate(int incomingLength)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + incomingLength <= MaximumFileBytes) return;
        File.Delete(path + "." + (maximumFiles - 1));
        for (var index = maximumFiles - 2; index >= 1; index--)
            if (File.Exists(path + "." + index))
                File.Move(path + "." + index, path + "." + (index + 1));
        File.Move(path, path + ".1");
    }

    private string[] Files() => Enumerable.Range(0, maximumFiles)
        .Select(index => index == 0 ? path : path + "." + index).ToArray();

    private static Mutex CreateMutex(string fullPath, string prefix)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())));
        return new Mutex(false, prefix + identity);
    }

    private static void TrimOversizedFile(string file)
    {
        if (!File.Exists(file) || new FileInfo(file).Length <= MaximumFileBytes) return;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length <= MaximumFileBytes) return;
        var start = stream.Length - MaximumFileBytes;
        stream.Position = start - 1;
        var startsOnLine = stream.ReadByte() == '\n';
        var tail = new byte[MaximumFileBytes];
        stream.ReadExactly(tail);
        if (tail[^1] != '\n')
            throw new IOException("An oversized JSONL log ends with an incomplete record.");
        var first = startsOnLine ? 0 : Array.IndexOf(tail, (byte)'\n') + 1;
        if (first >= tail.Length)
            throw new IOException("An oversized JSONL log has no complete record within the size limit.");
        stream.Position = 0;
        stream.Write(tail.AsSpan(first));
        stream.SetLength(tail.Length - first);
    }
}
