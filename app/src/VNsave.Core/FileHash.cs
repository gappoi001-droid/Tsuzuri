using System.Security.Cryptography;

namespace VNsave.Core;

public sealed record FileFingerprint(long Size, string Sha256, string ContentHash);

public static class FileHash
{
    public const int BlockSize = 4 * 1024 * 1024;

    public static async Task<FileFingerprint> ComputeAsync(Stream stream, CancellationToken ct = default)
    {
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var blocks = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BlockSize];
        long size = 0;
        while (true)
        {
            int count = 0;
            while (count < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count), ct);
                if (read == 0) break;
                count += read;
            }
            if (count == 0) break;
            size += count;
            whole.AppendData(buffer, 0, count);
            blocks.AppendData(SHA256.HashData(buffer.AsSpan(0, count)));
        }
        return new(size, Convert.ToHexStringLower(whole.GetHashAndReset()), Convert.ToHexStringLower(blocks.GetHashAndReset()));
    }

    public static async Task<FileFingerprint> ComputeFileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await ComputeAsync(stream, ct);
    }

    public static async Task CreateRandomFileAsync(string path, long size, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        var buffer = new byte[1024 * 1024];
        while (size > 0)
        {
            int count = (int)Math.Min(buffer.Length, size);
            RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
            await file.WriteAsync(buffer.AsMemory(0, count), ct);
            size -= count;
        }
    }
}
