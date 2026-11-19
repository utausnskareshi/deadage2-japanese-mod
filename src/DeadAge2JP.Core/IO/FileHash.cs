using System.Security.Cryptography;

namespace DeadAge2JP.Core.IO;

/// <summary>
/// ファイルの SHA-256 計算（パッチ前後・バックアップの同一性確認に使う）。
/// </summary>
public static class FileHash
{
    /// <summary>ファイル全体の SHA-256 を 16 進小文字で返す。</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>バイト列の SHA-256 を 16 進小文字で返す。</summary>
    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
