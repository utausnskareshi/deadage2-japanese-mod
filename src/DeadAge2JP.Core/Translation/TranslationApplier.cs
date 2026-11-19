using DeadAge2JP.Core.Localization;

namespace DeadAge2JP.Core.Translation;

/// <summary>
/// 1 つの表に訳文を適用した結果の集計。
/// </summary>
/// <param name="KeyedRows">キーを持つ行の総数。</param>
/// <param name="Translated">日本語訳を入れた行数。</param>
/// <param name="FallbackToEnglish">訳が無いため英語をコピーした行数（英語原文が空の行は含まない）。</param>
/// <param name="StaleTranslations">訳はあるが、翻訳時から英語原文が変わっている行数（訳は適用している）。</param>
/// <param name="UnknownKeys">訳文側にあるが、ゲームの表に存在しないキー（ゲーム更新で消えた等）。</param>
/// <param name="EmptySourceRows">
/// 訳が無く、英語原文も空（空白だけを含む）の行数。訳すものが無いので「英語のまま」ではなく、日本語化率の分母からも除く
/// （ゲームの表には原文が空のキーが UI に 2 件・会話に 6 件あり、これを未訳と数えると全訳でも 99.9% と表示されていた）。
/// </param>
public sealed record TableApplyReport(
    int KeyedRows,
    int Translated,
    int FallbackToEnglish,
    int StaleTranslations,
    IReadOnlyList<string> UnknownKeys,
    int EmptySourceRows);

/// <summary>適用後の表と集計。</summary>
public sealed record TableApplyResult(LocaTable Table, TableApplyReport Report);

/// <summary>
/// 訳文をローカライズ表の対象言語列（中国語簡体字列）へ書き込む純粋関数群。
/// 入力の表は変更せず、新しい表を返す。
/// </summary>
public static class TranslationApplier
{
    /// <summary>
    /// 訳文を適用する。
    /// <list type="bullet">
    /// <item>訳がある行: 対象列に訳文を入れる</item>
    /// <item>訳が無い行（訳が空・空白だけの場合も含む）: 対象列に英語をコピーする（中国語のまま残さないため。
    /// また "～ Controller" キーは空欄だと英語にフォールバックしないので、空欄にもしない）</item>
    /// <item>言語名キー（labelKey）: 言語選択で「日本語」と表示されるよう、キー以外の全列に labelText を入れる</item>
    /// </list>
    /// </summary>
    public static TableApplyResult Apply(
        LocaTable source,
        TranslationTable translations,
        string targetColumn,
        string englishColumn,
        string? labelKey = null,
        string? labelText = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translations);

        var targetIndex = source.ColumnIndex(targetColumn);
        var englishIndex = source.ColumnIndex(englishColumn);

        var keyed = 0;
        var translated = 0;
        var fallback = 0;
        var emptySource = 0;
        var stale = 0;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var newRows = new List<IReadOnlyList<string>>(source.Rows.Count);

        foreach (var row in source.Rows)
        {
            // キーの無い行はゲームも無視するので、そのまま残す
            if (row.Count == 0 || row[0].Length == 0)
            {
                newRows.Add(row);
                continue;
            }

            keyed++;
            var key = row[0];
            seenKeys.Add(key);
            var english = LocaTable.CellOrNull(row, englishIndex) ?? string.Empty;

            // 対象列が行に存在するよう必要な分だけ空セルで延長する（既存セルの位置は変えない）
            var width = Math.Max(row.Count, targetIndex + 1);
            if (labelKey is not null && key == labelKey)
            {
                width = Math.Max(width, source.Header.Count);
            }
            var cells = new string[width];
            for (var i = 0; i < width; i++)
            {
                cells[i] = i < row.Count ? row[i] : string.Empty;
            }

            if (labelKey is not null && labelText is not null && key == labelKey)
            {
                for (var i = 1; i < cells.Length; i++)
                {
                    cells[i] = labelText;
                }
                translated++;
            }
            // 空白だけの訳（" "・全角空白・改行のみ）は書き損じなので、空の訳と同じく英語にする。
            // そのまま入れるとゲームでは空欄になる（取り込み時の import も、空白だけの訳は拒否している）
            else if (translations.Entries.TryGetValue(key, out var entry) && !string.IsNullOrWhiteSpace(entry.Text))
            {
                cells[targetIndex] = entry.Text;
                translated++;
                if (entry.Source is not null && entry.Source != SourceHash.Compute(english))
                {
                    stale++;
                }
            }
            else
            {
                // 書き込む内容は原文の有無にかかわらず英語のコピー（空なら空のまま）。数え方だけを分ける
                cells[targetIndex] = english;
                if (string.IsNullOrWhiteSpace(english))
                {
                    emptySource++;
                }
                else
                {
                    fallback++;
                }
            }

            newRows.Add(cells);
        }

        var unknown = translations.Entries.Keys
            .Where(k => !seenKeys.Contains(k))
            .Order(StringComparer.Ordinal)
            .ToList();

        return new TableApplyResult(
            source with { Rows = newRows },
            new TableApplyReport(keyed, translated, fallback, stale, unknown, emptySource));
    }
}
