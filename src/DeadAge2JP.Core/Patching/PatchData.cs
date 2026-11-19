namespace DeadAge2JP.Core.Patching;

/// <summary>
/// パッチに必要な配布データ（翻訳・日本語フォント・型情報データベース）の場所。
/// </summary>
/// <param name="TranslationsDir">翻訳 JSON のルート（{表ID}/*.json を含む）。</param>
/// <param name="FontPath">差し替える日本語フォント（TTF/OTF）。</param>
/// <param name="ClassDataPath">Unity 組み込み型の型情報データベース（classdata.tpk）。</param>
public sealed record PatchData(string TranslationsDir, string FontPath, string ClassDataPath)
{
    /// <summary>
    /// 配布Zipの data フォルダ構成から読み込む:
    /// data/translations/{ui,dialogue}/*.json, data/fonts/*.ttf, data/typetree/classdata.tpk
    /// </summary>
    public static PatchData FromDataDirectory(string dataDir) =>
        Create(
            Path.Combine(dataDir, "translations"),
            Path.Combine(dataDir, "fonts"),
            Path.Combine(dataDir, "typetree", "classdata.tpk"),
            dataDir);

    /// <summary>
    /// リポジトリの構成から読み込む（開発・CLI 用）:
    /// translations/..., assets/fonts/*.ttf, assets/typetree/classdata.tpk
    /// </summary>
    public static PatchData FromRepository(string repoRoot) =>
        Create(
            Path.Combine(repoRoot, "translations"),
            Path.Combine(repoRoot, "assets", "fonts"),
            Path.Combine(repoRoot, "assets", "typetree", "classdata.tpk"),
            repoRoot);

    private static PatchData Create(string translationsDir, string fontDir, string classDataPath, string displayRoot)
    {
        var problems = new List<string>();
        if (!Directory.Exists(translationsDir))
        {
            problems.Add($"翻訳フォルダ {translationsDir}");
        }
        if (!File.Exists(classDataPath))
        {
            problems.Add($"型情報データベース {classDataPath}");
        }

        var fonts = Directory.Exists(fontDir)
            ? Directory.EnumerateFiles(fontDir)
                .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];
        if (fonts.Count != 1)
        {
            problems.Add($"フォント（{fontDir} に TTF/OTF がちょうど 1 つ必要。見つかった数: {fonts.Count}）");
        }

        if (problems.Count > 0)
        {
            throw new PatchException(
                $"パッチ用データが不足しています（{displayRoot}）。配布Zipをすべて展開してから実行してください。\n不足: " +
                string.Join("、", problems));
        }
        return new PatchData(translationsDir, fonts[0], classDataPath);
    }
}
