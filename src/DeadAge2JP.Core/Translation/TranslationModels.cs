namespace DeadAge2JP.Core.Translation;

/// <summary>
/// 1 キー分の訳文。
/// </summary>
/// <param name="Text">日本語訳（ゲーム内タグ・差し込み記号は原文どおり保持すること）。</param>
/// <param name="Source">
/// 翻訳時点の英語原文のハッシュ（<see cref="SourceHash"/>）。原文そのものは配布物に含めないため、
/// ゲーム更新で原文が変わったかどうかの検出にだけ使う。未記録なら null。
/// </param>
public sealed record TranslationEntry(string Text, string? Source);

/// <summary>
/// 1 つのローカライズ表に対する訳文の集合（キー → 訳文）。
/// </summary>
public sealed record TranslationTable(string TableId, IReadOnlyDictionary<string, TranslationEntry> Entries)
{
    /// <summary>訳文が 1 件も無い空の表。</summary>
    public static TranslationTable Empty(string tableId) =>
        new(tableId, new Dictionary<string, TranslationEntry>(StringComparer.Ordinal));
}

/// <summary>
/// 全ローカライズ表の訳文（表 ID → 訳文の集合）。
/// </summary>
public sealed record TranslationSet(IReadOnlyDictionary<string, TranslationTable> Tables)
{
    /// <summary>表 ID に対応する訳文（無ければ空の表）を返す。</summary>
    public TranslationTable Get(string tableId) =>
        Tables.TryGetValue(tableId, out var table) ? table : TranslationTable.Empty(tableId);
}
