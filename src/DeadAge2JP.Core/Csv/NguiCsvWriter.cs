using System.Text;

namespace DeadAge2JP.Core.Csv;

/// <summary>
/// <see cref="NguiCsvReader"/>（＝ゲームの ByteReader）で読み直したとき、全セルが元どおりになる形で CSV を書き出す。
/// <para>
/// 書き出し規則（ゲーム実データで往復変換の差分ゼロを確認済み）:
/// ・改行文字は 2 文字の "\n" に置き換えて 1 レコード 1 行にする（読み取り側が改行へ戻す）
/// ・カンマまたはダブルクォートを含むセルだけを引用符で囲み、内部の " は "" にする
/// ・行末のセルが空の場合は "" と書く（素の空だとゲームに列ごと落とされ、会話表ではキー名が表示されてしまう）
/// ・UTF-8（BOM なし）、改行は元ファイルと同じ CRLF
/// </para>
/// </summary>
public static class NguiCsvWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>全行を CSV バイト列にする。</summary>
    public static byte[] WriteAll(IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            AppendRow(builder, row);
            builder.Append("\r\n");
        }
        return Utf8NoBom.GetBytes(builder.ToString());
    }

    /// <summary>1 行分を書き出す。</summary>
    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> row)
    {
        for (var i = 0; i < row.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append(EscapeField(row[i], isLastField: i == row.Count - 1, isFirstField: i == 0));
        }
    }

    /// <summary>
    /// 1 セルをゲームの読み取り規則に合わせてエスケープする。
    /// </summary>
    /// <param name="value">セルの値（改行文字を含んでよい）。</param>
    /// <param name="isLastField">行末のセルかどうか（空セルの書き方が変わる）。</param>
    /// <param name="isFirstField">行頭のセルかどうか（制御文字で始まる場合は引用する）。</param>
    public static string EscapeField(string value, bool isLastField, bool isFirstField = false)
    {
        ArgumentNullException.ThrowIfNull(value);

        // CR や LF をそのまま書くと行が分断されるため、読み取り側が復元する "\n" 表記にそろえる
        var singleLine = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\\n", StringComparison.Ordinal);

        if (singleLine.Length == 0)
        {
            return isLastField ? "\"\"" : string.Empty;
        }

        // ゲームの読み取り器はフィールド途中の " でも引用を開始するため、" を含むセルは必ず引用する。
        // また、行頭の制御文字（コード 32 未満。タブ等）は空行扱いで読み飛ばされるため、行頭のセルがそれで始まる場合も
        // 引用して守る（引用しないとキーが変わったり、そのセルだけの行が消えて以降の行がずれたりする）
        var needsQuote = singleLine.Contains(',', StringComparison.Ordinal) || singleLine.Contains('"', StringComparison.Ordinal)
            || (isFirstField && singleLine[0] < ' ');
        return needsQuote ? "\"" + singleLine.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : singleLine;
    }
}
