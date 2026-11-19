using System.Security.Cryptography;
using System.Text;

namespace DeadAge2JP.Core.Translation;

/// <summary>
/// 英語原文のハッシュ。翻訳ファイルに原文そのものを入れずに「原文が変わったか」を判定するために使う。
/// </summary>
public static class SourceHash
{
    /// <summary>
    /// SHA-256 の先頭 16 桁（16 進小文字）を返す。原文の復元はできず、変更検出には十分な長さ。
    /// </summary>
    public static string Compute(string englishText)
    {
        ArgumentNullException.ThrowIfNull(englishText);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(englishText));
        return Convert.ToHexStringLower(digest)[..16];
    }
}
