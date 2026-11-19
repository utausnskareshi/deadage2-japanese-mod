using DeadAge2JP.Core.Csv;

namespace DeadAge2JP.Core.Localization;

/// <summary>
/// ローカライズ表（見出し行 + データ行）の不変表現。
/// 1 列目がキー、以降が言語列（会話表では DevComment / Speaker 列も含む）。
/// </summary>
public sealed record LocaTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    /// <summary>ゲームと同じ規則で CSV バイト列を解析する。</summary>
    public static LocaTable Parse(byte[] csvBytes)
    {
        var all = NguiCsvReader.ReadAll(csvBytes);
        if (all.Count == 0)
        {
            throw new InvalidDataException("ローカライズ表が空です（見出し行がありません）。");
        }
        return new LocaTable(all[0], all.Skip(1).ToList());
    }

    /// <summary>ゲームが読める形式の CSV バイト列に戻す。</summary>
    public byte[] Serialize() => NguiCsvWriter.WriteAll(Rows.Prepend(Header));

    /// <summary>見出し名から列番号を引く（存在しなければ意味のあるメッセージで例外）。</summary>
    public int ColumnIndex(string columnName)
    {
        for (var i = 0; i < Header.Count; i++)
        {
            if (string.Equals(Header[i], columnName, StringComparison.Ordinal))
            {
                return i;
            }
        }
        throw new InvalidDataException($"ローカライズ表に列 '{columnName}' がありません（見出し: {string.Join(", ", Header)}）。");
    }

    /// <summary>行の指定列の値（行が短くて列が無い場合は null）。</summary>
    public static string? CellOrNull(IReadOnlyList<string> row, int columnIndex) =>
        columnIndex < row.Count ? row[columnIndex] : null;

    /// <summary>キーを持つ行（ゲームが辞書に登録する行）だけを列挙する。</summary>
    public IEnumerable<IReadOnlyList<string>> KeyedRows => Rows.Where(r => r.Count > 0 && r[0].Length > 0);
}
