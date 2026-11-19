using System.Diagnostics;
using DeadAge2JP.Core.Assets;
using DeadAge2JP.Core.IO;
using DeadAge2JP.Core.Localization;
using DeadAge2JP.Core.Settings;
using DeadAge2JP.Core.Steam;
using DeadAge2JP.Core.Translation;

namespace DeadAge2JP.Core.Patching;

/// <summary>
/// 日本語化の適用・元に戻す・状態判定を行うサービス。
/// <para>
/// 安全のための原則:
/// ・元ファイルは必ずバックアップし、ハッシュで一致を確認してから書き換える
/// ・書き換え結果は一時ファイルに作り、読み直して内容を検証してから置き換える
/// ・置き換えの途中で失敗したら、置き換え済みのファイルをバックアップから戻す
/// </para>
/// </summary>
public sealed class PatchService(AppPaths paths, IPatchLogger logger)
{
    private readonly PatchStateStore _store = new(paths);

    /// <summary>本ツールのバージョン（Directory.Build.props の Version）。</summary>
    public static string PatcherVersion => typeof(PatchService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>現在の適用状態を判定する（ファイルのハッシュを計算するため数秒かかることがある）。</summary>
    public async Task<PatchStatusReport> GetStatusAsync(GameInstallation game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var state = _store.Load(game.RootDir);
        if (state is null)
        {
            return new PatchStatusReport(PatchStatus.NotPatched, null);
        }
        var hashes = await HashFilesAsync(game, state.Files.Select(f => f.RelativePath), cancellationToken).ConfigureAwait(false);
        return new PatchStatusReport(PatchStatusEvaluator.Evaluate(state, hashes), state);
    }

    /// <summary>日本語化を適用する（重い処理はバックグラウンドスレッドで実行する）。</summary>
    public Task<ApplyOutcome> ApplyAsync(
        GameInstallation game,
        PatchData data,
        ApplyOptions options,
        IProgress<PatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => ApplyCoreAsync(game, data, options, progress, cancellationToken), cancellationToken);
    }

    /// <summary>パッチ適用前の状態に戻す（重い処理はバックグラウンドスレッドで実行する）。</summary>
    public Task<RestoreOutcome> RestoreAsync(
        GameInstallation game,
        IProgress<PatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        return Task.Run(() => RestoreCoreAsync(game, progress, cancellationToken), cancellationToken);
    }

    private async Task<ApplyOutcome> ApplyCoreAsync(
        GameInstallation game,
        PatchData data,
        ApplyOptions options,
        IProgress<PatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        Report(progress, "事前確認をしています…", 0.01);
        logger.Info($"日本語化を開始します（ツール v{PatcherVersion}、ゲーム {game.RootDir}、build {game.BuildId ?? "不明"}）。");
        using var operationLock = AcquireOperationLock();
        EnsureGameNotRunning();
        EnsureRequiredFiles(game);

        var status = await GetStatusAsync(game, cancellationToken).ConfigureAwait(false);
        if (status.Status is PatchStatus.Patched or PatchStatus.PartiallyPatched)
        {
            throw new PatchException("すでに日本語化されています。先に「元に戻す」を実行してください。");
        }
        if (status.State is not null)
        {
            // ゲーム更新や整合性確認で元に戻っている場合の古い記録・バックアップは不要なので片付ける
            logger.Warn("以前の適用記録が現在のファイルと一致しないため、記録と古いバックアップを破棄します。");
            DiscardState(game, status.State);
        }
        // 以前の処理が強制終了・停電などで中断していた場合の残り（一時ファイル・作業フォルダ・作りかけのバックアップ）を片付ける
        CleanUpLeftovers(game, current: null);

        EnsureWritable(game);
        EnsureFreeSpace(game);

        Report(progress, "翻訳データを読み込んでいます…", 0.03);
        var translations = TranslationRepository.LoadAll(data.TranslationsDir);
        var font = options.ReplaceFont ? await File.ReadAllBytesAsync(data.FontPath, cancellationToken).ConfigureAwait(false) : null;
        logger.Info($"翻訳: {string.Join("、", LocaTableSpecs.All.Select(s => $"{s.Id} {translations.Get(s.Id).Entries.Count} 件"))}／フォント差し替え: {(font is null ? "しない" : Path.GetFileName(data.FontPath))}");

        Report(progress, "元のファイルを確認しています…", 0.05);
        var originals = await HashFilesAsync(game, GameInstallation.PatchTargets, cancellationToken).ConfigureAwait(false);
        // 和暦設定の PC では "yyyy" が元号年になるため、日付はカルチャ非依存で書式化する
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var backupDir = Path.Combine(paths.BackupRoot, $"{game.BuildId ?? "unknown"}-{stamp}");

        var dataTemp = game.DataBundlePath + ".jptmp";
        var prefabsTemp = game.PrefabsBundlePath + ".jptmp";
        PatchState state;
        try
        {
            // バックアップも try の中で行う。途中で失敗（容量不足・読み取りエラー・検証失敗・キャンセル）したとき、
            // どの適用記録からも参照されない作りかけのバックアップ（数百 MB）が残らないよう catch で削除するため。
            // さらに、catch まで届かない強制終了に備えて「作りかけ」の印を置き、記録を保存した時点で外す
            // （印が残ったバックアップは、次回の処理の始めに CleanUpLeftovers が消す）
            Directory.CreateDirectory(backupDir);
            File.WriteAllText(Path.Combine(backupDir, IncompleteBackupMarker), "このバックアップは作成途中です（DeadAge2JP）。");
            await BackupAsync(game, originals, backupDir, progress, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(paths.WorkDir);
            var tableSummaries = ProcessBundle(GameConstants.DataBundleRelativePath,
                () => PatchDataBundle(game, data, translations, font, dataTemp, progress, cancellationToken));
            ProcessBundle(GameConstants.PrefabsBundleRelativePath,
                () => PatchPrefabsBundle(game, data, font, prefabsTemp, progress, cancellationToken));

            var tempOutputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GameConstants.DataBundleRelativePath] = dataTemp,
                [GameConstants.PrefabsBundleRelativePath] = prefabsTemp,
            };
            Report(progress, "仕上がったファイルを確認しています…", 0.91);
            var patchedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (relative, temp) in tempOutputs)
            {
                patchedHashes[relative] = await FileHash.Sha256Async(temp, cancellationToken).ConfigureAwait(false);
            }

            state = new PatchState(
                PatchState.CurrentSchemaVersion,
                PatcherVersion,
                DateTimeOffset.Now,
                game.RootDir,
                game.BuildId,
                backupDir,
                font is not null,
                GameInstallation.PatchTargets.Select(rel => new PatchedFileRecord(
                    rel, originals[rel], patchedHashes[rel], Path.Combine(backupDir, rel))).ToList(),
                Settings: null,
                tableSummaries);
            // 置き換えの前に記録しておけば、途中で強制終了しても「元に戻す」で復旧できる
            _store.Save(state);
            // 記録がこのバックアップを参照したので、作りかけの印を外す（以後は CleanUpLeftovers の対象外）
            TryDeleteFile(Path.Combine(backupDir, IncompleteBackupMarker));

            Report(progress, "ゲームのファイルを置き換えています…", 0.93);
            SwapIn(game, tempOutputs, backupDir);
        }
        catch (Exception ex)
        {
            if (ex is not PatchException { KeepBackup: true })
            {
                // ゲームのファイルは元のまま（または自動で復旧済み）なので、記録とバックアップは不要
                _store.Delete(game.RootDir);
                TryDeleteDirectory(backupDir);
            }
            throw;
        }
        finally
        {
            TryDeleteFile(dataTemp);
            TryDeleteFile(prefabsTemp);
            TryDeleteDirectory(paths.WorkDir);
        }

        Report(progress, "ゲームの言語設定を確認しています…", 0.97);
        if (options.SwitchGameLanguage)
        {
            state = state with { Settings = TrySwitchLanguage(notes) };
            _store.Save(state);
        }

        Report(progress, "日本語化が完了しました。", 1.0);
        logger.Info("日本語化が完了しました。");
        return new ApplyOutcome(state, notes);
    }

    /// <summary>data.unity3d のローカライズ表とフォントを書き換えて一時ファイルへ保存し、検証する。</summary>
    private IReadOnlyList<TableSummary> PatchDataBundle(
        GameInstallation game,
        PatchData data,
        TranslationSet translations,
        byte[]? font,
        string outputPath,
        IProgress<PatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, "テキストデータを展開しています…（data.unity3d）", 0.16);
        var summaries = new List<TableSummary>();
        var expectedTableHashes = new Dictionary<string, string>(StringComparer.Ordinal);

        using (var editor = UnityBundleEditor.Open(game.DataBundlePath, data.ClassDataPath, game.ManagedDir, paths.WorkDir))
        {
            logger.Info($"data.unity3d を開きました（Unity {editor.EngineVersion}）。");
            Report(progress, "テキストを日本語に書き換えています…", 0.25);
            foreach (var spec in LocaTableSpecs.All)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var table = LocaTable.Parse(LocaAssets.ReadTable(editor, spec));
                var isUi = spec == LocaTableSpecs.Ui;
                if (isUi && IsAlreadyPatched(table))
                {
                    // 記録を失った状態で再適用すると、日本語化済みのファイルを「元ファイル」としてバックアップしてしまうため止める
                    throw new PatchException(
                        "このゲームのファイルはすでに日本語化されていますが、適用記録が見つかりません。\n" +
                        "Steam のライブラリで Dead Age 2 を右クリック →「プロパティ」→「インストール済みファイル」→" +
                        "「ゲームファイルの整合性を確認」で元に戻してから、もう一度実行してください。");
                }
                var result = TranslationApplier.Apply(
                    table,
                    translations.Get(spec.Id),
                    GameConstants.TargetLanguageColumn,
                    GameConstants.EnglishColumn,
                    isUi ? GameConstants.LanguageLabelKey : null,
                    isUi ? GameConstants.JapaneseLanguageLabel : null);
                var bytes = result.Table.Serialize();
                LocaAssets.WriteTable(editor, spec, bytes);
                expectedTableHashes[spec.Id] = FileHash.Sha256(bytes);

                var report = result.Report;
                logger.Info($"表 {spec.Id}: キー {report.KeyedRows} 件中 {report.Translated} 件を日本語化、{report.FallbackToEnglish} 件は英語のまま"
                    + $"（原文が空 {report.EmptySourceRows} 件、原文変更 {report.StaleTranslations} 件、ゲームに無いキー {report.UnknownKeys.Count} 件）。");
                if (report.UnknownKeys.Count > 0)
                {
                    logger.Warn($"表 {spec.Id} のゲームに無いキー（先頭 20 件）: {string.Join(", ", report.UnknownKeys.Take(20))}");
                }
                summaries.Add(new TableSummary(
                    spec.Id, report.KeyedRows, report.Translated, report.StaleTranslations, report.UnknownKeys.Count, report.EmptySourceRows));
            }

            Report(progress, "フォント設定を書き換えています…", 0.30);
            var fontReport = CjkFontPatcher.Apply(editor, font);
            logger.Info($"data.unity3d のフォント: 差し替え {fontReport.FontsReplaced}、マルチアトラス化 {fontReport.DynamicAssetsUpdated}、静的表クリア {fontReport.StaticAssetsCleared}。");

            editor.Save(outputPath, new Progress<double>(p => Report(progress, "テキストデータを圧縮しています…（data.unity3d）", 0.32 + 0.28 * p)));
        }

        Report(progress, "書き換え結果を検証しています…（data.unity3d）", 0.62);
        VerifyBundle(outputPath, data, game.ManagedDir, expectedTableHashes, font);
        logger.Info("data.unity3d の検証に成功しました。");
        return summaries;
    }

    /// <summary>prefabs バンドルのフォントを書き換えて一時ファイルへ保存し、検証する。</summary>
    private void PatchPrefabsBundle(
        GameInstallation game,
        PatchData data,
        byte[]? font,
        string outputPath,
        IProgress<PatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, "UI データを展開しています…（prefabs）", 0.68);
        using (var editor = UnityBundleEditor.Open(game.PrefabsBundlePath, data.ClassDataPath, game.ManagedDir, paths.WorkDir))
        {
            var fontReport = CjkFontPatcher.Apply(editor, font);
            logger.Info($"prefabs のフォント: 差し替え {fontReport.FontsReplaced}、マルチアトラス化 {fontReport.DynamicAssetsUpdated}、静的表クリア {fontReport.StaticAssetsCleared}。");
            editor.Save(outputPath, new Progress<double>(p => Report(progress, "UI データを圧縮しています…（prefabs）", 0.72 + 0.15 * p)));
        }

        Report(progress, "書き換え結果を検証しています…（prefabs）", 0.88);
        VerifyBundle(outputPath, data, game.ManagedDir, expectedTableHashes: null, font);
        logger.Info("prefabs の検証に成功しました。");
    }

    /// <summary>
    /// UI 表の言語名が本ツールの日本語表示名になっていれば、すでに日本語化済みのファイルと判断する。
    /// </summary>
    private static bool IsAlreadyPatched(LocaTable uiTable)
    {
        var target = uiTable.ColumnIndex(GameConstants.TargetLanguageColumn);
        var labelRow = uiTable.KeyedRows.FirstOrDefault(r => r[0] == GameConstants.LanguageLabelKey);
        return labelRow is not null && LocaTable.CellOrNull(labelRow, target) == GameConstants.JapaneseLanguageLabel;
    }

    /// <summary>
    /// 書き出したバンドルを開き直し、テキストとフォントが意図どおりになっているかを確認する。
    /// </summary>
    private void VerifyBundle(
        string bundlePath,
        PatchData data,
        string managedDir,
        IReadOnlyDictionary<string, string>? expectedTableHashes,
        byte[]? font)
    {
        using var editor = UnityBundleEditor.Open(bundlePath, data.ClassDataPath, managedDir, paths.WorkDir);
        if (expectedTableHashes is not null)
        {
            foreach (var spec in LocaTableSpecs.All)
            {
                var actual = FileHash.Sha256(LocaAssets.ReadTable(editor, spec));
                if (actual != expectedTableHashes[spec.Id])
                {
                    throw new PatchException($"書き換え後の検証に失敗しました（表 {spec.Id} の内容が一致しません）。ゲームのファイルは変更していません。");
                }
            }
        }

        var state = CjkFontPatcher.ReadState(editor);
        var notMulti = state.DynamicMultiAtlas.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
        if (notMulti.Count > 0 || state.DynamicMultiAtlas.Count < GameConstants.DynamicCjkFontAssetNames.Count)
        {
            throw new PatchException($"書き換え後の検証に失敗しました（マルチアトラス未設定: {string.Join(", ", notMulti)}）。ゲームのファイルは変更していません。");
        }
        if (font is not null)
        {
            if (state.FontDataLength != font.LongLength)
            {
                throw new PatchException("書き換え後の検証に失敗しました（フォントが差し替わっていません）。ゲームのファイルは変更していません。");
            }
            var notCleared = state.CharacterCounts.Where(kv => kv.Value != 0).Select(kv => kv.Key).ToList();
            if (notCleared.Count > 0)
            {
                throw new PatchException($"書き換え後の検証に失敗しました（文字表が残っています: {string.Join(", ", notCleared)}）。ゲームのファイルは変更していません。");
            }
        }
    }

    private async Task<RestoreOutcome> RestoreCoreAsync(GameInstallation game, IProgress<PatchProgress>? progress, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        Report(progress, "事前確認をしています…", 0.02);
        logger.Info($"元に戻す処理を開始します（ゲーム {game.RootDir}）。");
        using var operationLock = AcquireOperationLock();
        EnsureGameNotRunning();

        var state = _store.Load(game.RootDir) ?? throw new PatchException(
            "日本語化の適用記録が見つかりません。\n" +
            "Steam のライブラリで Dead Age 2 を右クリック →「プロパティ」→「インストール済みファイル」→" +
            "「ゲームファイルの整合性を確認」を実行すると、元の状態に戻せます。");
        EnsureWritable(game);
        // 以前の処理が中断していた場合の残りを片付ける（この記録が参照するバックアップは残す）
        CleanUpLeftovers(game, current: state);

        var current = await HashFilesAsync(game, state.Files.Select(f => f.RelativePath), cancellationToken).ConfigureAwait(false);
        var index = 0;
        foreach (var file in state.Files)
        {
            index++;
            Report(progress, $"元のファイルを復元しています…（{index}/{state.Files.Count}）", 0.1 + 0.7 * (index - 1) / state.Files.Count);
            var target = Path.Combine(game.RootDir, file.RelativePath);
            var now = current.GetValueOrDefault(file.RelativePath);
            if (now == file.OriginalSha256)
            {
                logger.Info($"{file.RelativePath} はすでに元の状態です。");
                continue;
            }
            if (now is not null && now != file.PatchedSha256)
            {
                // Steam の更新で別内容に置き換わったファイルを古いバックアップで上書きすると壊れるため触らない
                logger.Warn($"{file.RelativePath} はパッチ適用後に変更されています（ゲーム更新の可能性）。復元せずそのままにします。");
                notes.Add($"{file.RelativePath} はゲームの更新で置き換わっていたため、そのままにしました。");
                continue;
            }
            if (now is null)
            {
                // ファイルが消えている（ウイルス対策ソフトや手作業での削除など）場合は、ゲーム更新ではないので
                // バックアップ（＝元ファイル）から戻す。ここで飛ばすと、直後の後片付けで唯一のバックアップを消してしまう
                logger.Warn($"{file.RelativePath} が見つからないため、バックアップから復元します。");
            }
            if (!File.Exists(file.BackupPath))
            {
                throw new PatchException(
                    $"バックアップが見つかりません: {file.BackupPath}\n" +
                    "Steam の「ゲームファイルの整合性を確認」を実行すると、元の状態に戻せます。");
            }

            var temp = target + ".jptmp";
            try
            {
                File.Copy(file.BackupPath, temp, overwrite: true);
                var copied = await FileHash.Sha256Async(temp, cancellationToken).ConfigureAwait(false);
                if (copied != file.OriginalSha256)
                {
                    throw new PatchException($"バックアップの内容が記録と一致しません: {file.BackupPath}\nSteam の「ゲームファイルの整合性を確認」で元に戻してください。");
                }
                File.Move(temp, target, overwrite: true);
                logger.Info($"{file.RelativePath} を復元しました。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // フォルダへの書き込み権限は EnsureWritable で、読み取り専用属性もそこで確認済み。
                // それでも置き換えられないのは、ほかのプログラムがファイルを使用中の場合がほとんど
                // （使用中のファイルへの置き換えは「アクセス拒否」になるため、権限不足と誤解して管理者での再起動を勧めない）。
                // 記録とバックアップは残るので、しばらく待ってからもう一度「元に戻す」を実行すれば続きから戻せる
                throw new PatchException(
                    $"ゲームのファイルを元に戻せませんでした: {target}\n" +
                    "ほかのプログラム（ゲーム本体・Steam・ウイルス対策ソフトなど）がファイルを使用中の可能性があります。" +
                    "しばらく待ってから、もう一度「元に戻す」を実行してください。\n詳細: " + ex.Message, ex);
            }
            finally
            {
                TryDeleteFile(temp);
            }
        }

        Report(progress, "後片付けをしています…", 0.9);
        if (state.Settings is not null)
        {
            TryRevertLanguage(state.Settings, notes);
        }
        NoteIfLanguageLeftOnTarget(notes);
        DiscardState(game, state);

        Report(progress, "元に戻しました。", 1.0);
        logger.Info("元に戻す処理が完了しました。");
        return new RestoreOutcome(notes);
    }

    /// <summary>対象ファイルをバックアップし、ハッシュで一致を確認する。</summary>
    private async Task BackupAsync(
        GameInstallation game,
        IReadOnlyDictionary<string, string> originals,
        string backupDir,
        IProgress<PatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var index = 0;
        foreach (var (relative, hash) in originals)
        {
            index++;
            Report(progress, $"元のファイルをバックアップしています…（{index}/{originals.Count}）", 0.06 + 0.09 * (index - 1) / originals.Count);
            var source = Path.Combine(game.RootDir, relative);
            var destination = Path.Combine(backupDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            var copied = await FileHash.Sha256Async(destination, cancellationToken).ConfigureAwait(false);
            if (copied != hash)
            {
                throw new PatchException($"バックアップの検証に失敗しました: {destination}");
            }
            logger.Info($"バックアップしました: {relative} → {destination}");
        }
    }

    /// <summary>
    /// 一時ファイルを本来の場所へ移動する。途中で失敗したら、移動済みのファイルをバックアップから戻す。
    /// </summary>
    private void SwapIn(GameInstallation game, IReadOnlyDictionary<string, string> tempOutputs, string backupDir)
    {
        var swapped = new List<string>();
        try
        {
            foreach (var (relative, temp) in tempOutputs)
            {
                File.Move(temp, Path.Combine(game.RootDir, relative), overwrite: true);
                swapped.Add(relative);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error("ファイルの置き換えに失敗したため、置き換え済みのファイルをバックアップから戻します。", ex);
            try
            {
                foreach (var relative in swapped)
                {
                    File.Copy(Path.Combine(backupDir, relative), Path.Combine(game.RootDir, relative), overwrite: true);
                }
            }
            catch (Exception rollbackEx) when (rollbackEx is IOException or UnauthorizedAccessException)
            {
                logger.Error("自動での復旧にも失敗しました。バックアップを残します。", rollbackEx);
                throw new PatchException(
                    $"ゲームのファイルを置き換えられず、自動での復旧にも失敗しました。\nバックアップ: {backupDir}\n" +
                    "このツールの「元に戻す」、または Steam の「ゲームファイルの整合性を確認」で元に戻してください。", rollbackEx)
                {
                    KeepBackup = true,
                };
            }
            // フォルダへの書き込み権限と読み取り専用属性は EnsureWritable で確認済みなので、ここでの失敗は
            // ほかのプログラムがファイルを使用中の場合がほとんど（使用中のファイルへの置き換えは「アクセス拒否」になる）。
            // 管理者で再起動しても解決しないため、再起動は勧めず、待ってからの再実行を案内する
            throw new PatchException(
                "ゲームのファイルを置き換えられませんでした（変更は取り消しました）。\n" +
                "ほかのプログラム（ゲーム本体・Steam・ウイルス対策ソフトなど）がファイルを使用中の可能性があります。" +
                "しばらく待ってから、もう一度実行してください。\n詳細: " + ex.Message, ex);
        }
    }

    /// <summary>設定ファイルがあれば言語を日本語枠へ切り替え、元の値を記録する。</summary>
    private SettingsChangeRecord? TrySwitchLanguage(List<string> notes)
    {
        var path = paths.GameSettingsFilePath;
        const string ManualHint = "ゲーム内の「オプション」→ 言語で「日本語 (JA)」を選んでください。";
        try
        {
            var current = GameSettingsFile.TryReadLanguage(path);
            if (current is null)
            {
                notes.Add("ゲームの設定ファイルがまだ無いため、言語は自動で切り替えていません。" + ManualHint);
                return null;
            }
            if (current == GameConstants.TargetLanguageColumn)
            {
                notes.Add("ゲームの言語はすでに「日本語 (JA)」です。");
                return null;
            }
            GameSettingsFile.WriteLanguage(path, GameConstants.TargetLanguageColumn);
            logger.Info($"設定ファイルの言語を {current} → {GameConstants.TargetLanguageColumn} に切り替えました: {path}");
            notes.Add("ゲームの言語設定を「日本語 (JA)」に切り替えました。");
            return new SettingsChangeRecord(path, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // 言語切替は補助機能なので、失敗しても日本語化自体は成功扱いにし、手動での切替を案内する
            logger.Warn($"設定ファイルの言語を切り替えられませんでした（{path}）: {ex.Message}");
            notes.Add("ゲームの言語設定を自動で切り替えられませんでした。" + ManualHint);
            return null;
        }
    }

    /// <summary>適用時に切り替えた言語を元に戻す（利用者が別の言語に変えていた場合はそのまま）。</summary>
    private void TryRevertLanguage(SettingsChangeRecord record, List<string> notes)
    {
        try
        {
            // 記録の場所が、今の利用者のゲーム設定ファイルと違う場合は書き換えない。
            // 記録（state.json）は利用者の権限で書き換えられるため、管理者として再起動した本ツールに
            // 任意のファイルを書き換えさせる踏み台にされないよう、決まった場所だけを扱う。
            // （場所の取得や比較の失敗も下の catch で扱う。ここで例外が漏れると、ファイルを戻した後に
            // 「元に戻す」全体が失敗し、記録が残ってやり直すたびに同じところで止まってしまう）
            if (!string.Equals(Path.GetFullPath(record.SettingsPath), Path.GetFullPath(paths.GameSettingsFilePath), StringComparison.OrdinalIgnoreCase))
            {
                logger.Warn($"記録の設定ファイルの場所（{record.SettingsPath}）が現在の設定ファイルと違うため、言語は自動で戻しません。");
                notes.Add("ゲームの言語設定は自動で戻していません。ゲーム内の「オプション」から言語を選び直してください。");
                return;
            }
            var current = GameSettingsFile.TryReadLanguage(record.SettingsPath);
            if (current == GameConstants.TargetLanguageColumn)
            {
                GameSettingsFile.WriteLanguage(record.SettingsPath, record.OriginalLanguage);
                logger.Info($"設定ファイルの言語を {record.OriginalLanguage} に戻しました。");
                notes.Add("ゲームの言語設定を元に戻しました。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            logger.Warn($"設定ファイルの言語を元に戻せませんでした（{record.SettingsPath}）: {ex.Message}");
            notes.Add("ゲームの言語設定を自動で戻せませんでした。ゲーム内の「オプション」から言語を選び直してください。");
        }
    }

    /// <summary>
    /// 元に戻した後もゲームの言語が日本語化していた枠（中国語（簡体字））のままなら、選び直すよう案内する。
    /// 適用時に設定ファイルが無く、後から手動で「日本語 (JA)」を選んだ場合などは自動で戻す記録が無いため、
    /// 何も伝えないと、元に戻した直後のゲームが中国語で表示されて「壊れた」と誤解されてしまう。
    /// 設定は変えず、案内だけにする（元から中国語で遊んでいた利用者の設定を勝手に変えないため）。
    /// </summary>
    private void NoteIfLanguageLeftOnTarget(List<string> notes)
    {
        string? path = null;
        try
        {
            path = paths.GameSettingsFilePath;
            if (GameSettingsFile.TryReadLanguage(path) == GameConstants.TargetLanguageColumn)
            {
                notes.Add("ゲームの言語設定が、日本語化に使っていた「中国語（簡体字）」の枠のままです。" +
                    "ゲーム内の「オプション」→ 言語で、English など、お使いの言語に切り替えてください。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            // 案内だけの補助機能なので、読めなくても元に戻す処理は成功扱いにする（記録だけ残す）
            logger.Warn($"ゲームの言語設定を確認できませんでした（{path ?? "設定ファイルの場所が不明"}）: {ex.Message}");
        }
    }

    /// <summary>作りかけのバックアップに置く印のファイル名（記録を保存した時点で消す）。</summary>
    private const string IncompleteBackupMarker = ".incomplete";

    /// <summary>
    /// 以前の処理が強制終了・停電などで catch や finally まで届かずに中断した場合の残りを片付ける。
    /// <list type="bullet">
    /// <item>作業フォルダ（展開済み・変更済みのバンドルで数百 MB になる）</item>
    /// <item>ゲームフォルダの一時ファイル（対象ファイル名 + .jptmp。元に戻すだけの利用者だと、次の適用まで残り続ける）</item>
    /// <item>作りかけの印が残ったバックアップ（どの記録からも参照されず、ほかに消す機会が無い）</item>
    /// </list>
    /// 処理中のロックを取った後に呼ぶので、ほかの処理が使っている最中のものを消すことはない。
    /// </summary>
    /// <param name="game">対象のゲーム。</param>
    /// <param name="current">いま有効な適用記録（そのバックアップは、印が残っていても消さない）。</param>
    private void CleanUpLeftovers(GameInstallation game, PatchState? current)
    {
        TryDeleteDirectory(paths.WorkDir);
        foreach (var relative in GameInstallation.PatchTargets)
        {
            TryDeleteFile(Path.Combine(game.RootDir, relative) + ".jptmp");
        }
        if (!Directory.Exists(paths.BackupRoot))
        {
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(paths.BackupRoot))
        {
            var isCurrent = current is not null && string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.BackupDir)),
                StringComparison.OrdinalIgnoreCase);
            if (!isCurrent && File.Exists(Path.Combine(directory, IncompleteBackupMarker)))
            {
                logger.Warn($"前回の中断で残った作りかけのバックアップを削除します: {directory}");
                TryDeleteDirectory(directory);
            }
        }
    }

    /// <summary>
    /// バンドルの読み書き中に起きた想定外の例外を、利用者向けの説明つきの PatchException にする。
    /// 事前確認を通った後でここが失敗するのは、ファイルが壊れているか（途中までのダウンロード等）、ゲームの更新で
    /// 形式が変わった場合がほとんど。そのまま「予期しないエラー」と出すと不具合と区別できず、直し方も伝わらないため、
    /// Steam の整合性確認を案内する（元の例外は内側に残し、ログに詳細を記録する）。
    /// 一般的な入出力エラー（容量不足など。EndOfStream を除く）とキャンセルは、原因がそのまま伝わるよう包まない。
    /// </summary>
    private static T ProcessBundle<T>(string relativePath, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not (PatchException or OperationCanceledException or UnauthorizedAccessException)
            && (ex is not IOException || ex is EndOfStreamException))
        {
            throw new PatchException(
                $"ゲームのファイル（{relativePath}）を読み込めませんでした。ゲームのファイルは変更していません。\n" +
                "ファイルが壊れているか、ゲームの更新で形式が変わった可能性があります。Steam のライブラリで Dead Age 2 を右クリック →" +
                "「プロパティ」→「インストール済みファイル」→「ゲームファイルの整合性を確認」を実行してから、もう一度お試しください。" +
                "それでも直らない場合は、このツールの更新をお待ちください。\n詳細: " + ex.Message, ex);
        }
    }

    /// <summary><see cref="ProcessBundle{T}(string, Func{T})"/> の戻り値なし版。</summary>
    private static void ProcessBundle(string relativePath, Action action) =>
        ProcessBundle(relativePath, () =>
        {
            action();
            return 0;
        });

    /// <summary>適用記録とバックアップを削除する。</summary>
    private void DiscardState(GameInstallation game, PatchState state)
    {
        TryDeleteDirectory(state.BackupDir);
        _store.Delete(game.RootDir);
        logger.Info("適用記録とバックアップを削除しました。");
    }

    /// <summary>指定ファイル群の SHA-256 を計算する（存在しないファイルは結果に含めない）。</summary>
    private static async Task<IReadOnlyDictionary<string, string>> HashFilesAsync(
        GameInstallation game,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in relativePaths)
        {
            var full = Path.Combine(game.RootDir, relative);
            if (File.Exists(full))
            {
                result[relative] = await FileHash.Sha256Async(full, cancellationToken).ConfigureAwait(false);
            }
        }
        return result;
    }

    /// <summary>
    /// 適用・元に戻すの実行中であることを示す排他ロックを取る（戻り値を破棄するとロックが外れる）。
    /// <para>
    /// 2 重起動したパッチャーや、GUI と DevTools が同じゲームを同時に書き換えると、
    /// 一時ファイル（*.jptmp）・作業フォルダ・適用記録を奪い合い、片方の失敗処理がもう片方の記録を消してしまう。
    /// それを防ぐため、%LOCALAPPDATA%\DeadAge2JP のロックファイルを共有なしで開いている間だけ処理する。
    /// ロックファイルは閉じると自動で削除され、異常終了しても OS がハンドルを閉じるので残らない。
    /// </para>
    /// </summary>
    private FileStream AcquireOperationLock()
    {
        Directory.CreateDirectory(paths.RootDir);
        try
        {
            return new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            throw new PatchException("別の日本語化ツール（このツールの別のウィンドウなど）が処理中です。終わってから、もう一度実行してください。", ex);
        }
    }

    /// <summary>ほかのプロセスがファイルを使用中であることによる失敗か（ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION）。</summary>
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private static void EnsureGameNotRunning()
    {
        var processes = Process.GetProcessesByName(GameConstants.ProcessName);
        try
        {
            if (processes.Length > 0)
            {
                throw new PatchException("Dead Age 2 が起動中です。ゲームを終了してから、もう一度実行してください。");
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void EnsureRequiredFiles(GameInstallation game)
    {
        var missing = GameInstallation.FindMissingFiles(game.RootDir);
        if (missing.Count > 0)
        {
            throw new PatchException($"ゲームのファイルが不足しています: {string.Join("、", missing)}\nSteam の「ゲームファイルの整合性を確認」を実行してください。");
        }
    }

    /// <summary>書き換え先フォルダへ書き込めるかを試す（Program Files 配下などで権限が無い場合に早期に知らせる）。</summary>
    private static void EnsureWritable(GameInstallation game)
    {
        foreach (var relative in GameInstallation.PatchTargets)
        {
            var directory = Path.GetDirectoryName(Path.Combine(game.RootDir, relative))!;
            var probe = Path.Combine(directory, $".deadage2jp-write-test-{Guid.NewGuid():N}");
            try
            {
                File.WriteAllBytes(probe, []);
                File.Delete(probe);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new PatchException($"ゲームフォルダに書き込む権限がありません: {directory}\n管理者として実行してください。", ex) { MayNeedElevation = true };
            }

            // 対象ファイル自体が読み取り専用だと、数分かけて書き換えた最後の置き換えで「アクセス拒否」になり、
            // 管理者で実行しても直らない。バックアップにも属性が写って後片付けもできなくなるため、最初に止めて直し方を伝える
            var target = Path.Combine(game.RootDir, relative);
            if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
            {
                throw new PatchException(
                    $"ゲームのファイルが読み取り専用になっています: {target}\n" +
                    "エクスプローラーでこのファイルを右クリック →「プロパティ」で「読み取り専用」のチェックを外してから、もう一度実行してください。");
            }
        }
    }

    /// <summary>
    /// 必要な空き容量を確認する。展開後のデータは圧縮時の約 3 倍あるため、余裕を見て 4 倍で見積もる。
    /// </summary>
    private void EnsureFreeSpace(GameInstallation game)
    {
        var sizes = GameInstallation.PatchTargets.Select(rel => new FileInfo(Path.Combine(game.RootDir, rel)).Length).ToList();
        var total = sizes.Sum();
        var largest = sizes.Max();

        // 同じドライブへの要求は合算して判定する
        var requirements = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void Need(string path, long bytes)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            requirements[root] = requirements.GetValueOrDefault(root) + bytes;
        }
        Need(paths.WorkDir, largest * 4 * 2);   // 展開済み + 変更済みの非圧縮バンドル
        Need(Path.GetTempPath(), largest);      // 圧縮時の一時ファイル
        Need(paths.BackupRoot, total);          // バックアップ
        Need(game.RootDir, total);              // 置き換え前の一時出力

        foreach (var (root, bytes) in requirements)
        {
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < bytes)
            {
                throw new PatchException($"ドライブ {root} の空き容量が足りません（必要: 約 {bytes / 1_048_576:N0} MB、空き: {free / 1_048_576:N0} MB）。");
            }
        }
    }

    private void Report(IProgress<PatchProgress>? progress, string message, double fraction)
    {
        progress?.Report(new PatchProgress(message, Math.Clamp(fraction, 0, 1)));
    }

    /// <summary>
    /// 本ツールが作ったファイル（一時ファイル・印）を削除する。読み取り専用属性が付いていても消せるよう、先に外す。
    /// 失敗は本処理の結果を変えないため、記録して続行する。
    /// </summary>
    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 後片付けの失敗は本処理の結果を変えないため、記録して続行する
            logger.Warn($"一時ファイルを削除できませんでした: {path}（{ex.Message}）");
        }
    }

    /// <summary>
    /// 本ツールが作ったフォルダ（バックアップ・作業フォルダ）を丸ごと削除する。
    /// 読み取り専用の元ファイルを File.Copy したバックアップには属性が写り、そのままでは Directory.Delete が
    /// 「アクセス拒否」で失敗して数百 MB が残るため、先に中のファイルの属性を外す。失敗は記録して続行する。
    /// </summary>
    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn($"フォルダを削除できませんでした: {path}（{ex.Message}）");
        }
    }
}
