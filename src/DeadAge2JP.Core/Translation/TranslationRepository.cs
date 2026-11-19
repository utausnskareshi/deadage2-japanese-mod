using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadAge2JP.Core.Localization;

namespace DeadAge2JP.Core.Translation;

/// <summary>
/// 翻訳ファイル（JSON）の読み書き。
/// <para>
/// 配置: {translationsRoot}/{表ID}/*.json（例: translations/ui/menu.json）。
/// 1 つの表を複数ファイルに分割してよい（同じキーが複数ファイルにあればエラー）。
/// </para>
/// <para>
/// 形式: { "table": "ui", "entries": { "キー": { "text": "訳文", "source": "原文ハッシュ" } } }
/// 英語原文は著作物なので、このファイルには絶対に入れない。
/// </para>
/// </summary>
public static class TranslationRepository
{
    /// <summary>
    /// 読み書き共通の JSON 設定（日本語をエスケープせず読みやすく、未知の項目は誤記として拒否）。
    /// 同じファイル内の重複キーも拒否する（既定では後の値で黙って上書きされ、人が編集したときの重複に気づけないため）。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowDuplicateProperties = false,
    };

    /// <summary>翻訳フォルダから全表の訳文を読み込む（フォルダが無い表は空扱い）。</summary>
    public static TranslationSet LoadAll(string translationsRoot)
    {
        if (!Directory.Exists(translationsRoot))
        {
            throw new DirectoryNotFoundException($"翻訳フォルダが見つかりません: {translationsRoot}");
        }
        var tables = LocaTableSpecs.All.ToDictionary(
            spec => spec.Id,
            spec => LoadTable(Path.Combine(translationsRoot, spec.Id), spec.Id),
            StringComparer.Ordinal);
        return new TranslationSet(tables);
    }

    /// <summary>1 つの表のフォルダ内にある全 JSON を読み込み、キー重複を検査して統合する。</summary>
    public static TranslationTable LoadTable(string tableDir, string tableId)
    {
        if (!Directory.Exists(tableDir))
        {
            return TranslationTable.Empty(tableId);
        }

        var merged = new Dictionary<string, TranslationEntry>(StringComparer.Ordinal);
        var origin = new Dictionary<string, string>(StringComparer.Ordinal);
        // ファイル順で結果が変わらないよう名前順に処理する
        foreach (var file in Directory.EnumerateFiles(tableDir, "*.json").Order(StringComparer.Ordinal))
        {
            var dto = ReadFile(file);
            if (!string.Equals(dto.Table, tableId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"翻訳ファイル {file} の table が '{dto.Table}' ですが、フォルダは '{tableId}' 用です。");
            }
            foreach (var (key, entry) in dto.Entries)
            {
                if (origin.TryGetValue(key, out var firstFile))
                {
                    throw new InvalidDataException($"翻訳キー '{key}' が {firstFile} と {file} で重複しています。");
                }
                origin[key] = file;
                merged[key] = new TranslationEntry(entry.Text, entry.Source);
            }
        }
        return new TranslationTable(tableId, merged);
    }

    /// <summary>
    /// 翻訳ファイル 1 つを読み込む（形式不正は、ファイル名と原因が分かるメッセージの InvalidDataException にする）。
    /// 人がレビューで直接編集するファイルなので、null や重複などの書き損じもここで検出する
    /// （見逃すと、パッチ適用時や GUI の起動時に NullReferenceException になる）。
    /// </summary>
    public static TranslationFile ReadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var dto = JsonSerializer.Deserialize<TranslationFile>(stream, JsonOptions)
                ?? throw new InvalidDataException($"翻訳ファイル {path} の中身が null です。");
            if (dto.Entries is null)
            {
                throw new InvalidDataException($"翻訳ファイル {path} に entries がありません。");
            }
            foreach (var (key, entry) in dto.Entries)
            {
                if (key.Length == 0)
                {
                    throw new InvalidDataException($"翻訳ファイル {path} に空のキーがあります。");
                }
                if (entry?.Text is null)
                {
                    throw new InvalidDataException($"翻訳ファイル {path} のキー '{key}' に text がありません。");
                }
            }
            return dto;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"翻訳ファイル {path} の JSON 形式が不正です: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 翻訳ファイルを書き出す。差分が読みやすいようキーは序数順に並べる。
    /// </summary>
    public static void WriteFile(string path, string tableId, IReadOnlyDictionary<string, TranslationEntry> entries)
    {
        var sorted = new SortedDictionary<string, TranslationFileEntry>(StringComparer.Ordinal);
        foreach (var (key, entry) in entries)
        {
            sorted[key] = new TranslationFileEntry { Text = entry.Text, Source = entry.Source };
        }
        var dto = new TranslationFile { Table = tableId, Entries = new Dictionary<string, TranslationFileEntry>(sorted, StringComparer.Ordinal) };

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
        // 書き込み途中で壊れたファイルが残らないよう一時ファイル経由で置き換える
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(dto, JsonOptions) + "\n", new System.Text.UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 置き換えに失敗したら一時ファイルを残さない（翻訳フォルダに *.tmp が残ると、配布物に紛れ込むため）。
            // 一時ファイルの削除に失敗しても、利用者に伝えるべきなのは元の失敗なので、元の例外をそのまま投げ直す
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{path} を書き込めず、一時ファイル {temp} も削除できませんでした: {ex.Message}", ex);
            }
            throw;
        }
    }

    /// <summary>翻訳ファイルの JSON 構造。</summary>
    public sealed class TranslationFile
    {
        /// <summary>対象の表 ID（"ui" / "dialogue"）。</summary>
        public string Table { get; set; } = string.Empty;

        /// <summary>キー → 訳文。</summary>
        public Dictionary<string, TranslationFileEntry> Entries { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>翻訳ファイル内の 1 エントリ。</summary>
    public sealed class TranslationFileEntry
    {
        /// <summary>日本語訳。</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>英語原文のハッシュ（任意）。</summary>
        public string? Source { get; set; }
    }
}
