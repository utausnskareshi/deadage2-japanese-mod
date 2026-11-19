using System.Text.Encodings.Web;
using System.Text.Json;
using DeadAge2JP.Core.Steam;

namespace DeadAge2JP.Core.Patching;

/// <summary>
/// パッチ適用記録（state.json）の保存と読み込み。
/// %LOCALAPPDATA% の本体が失われても状態を判別できるよう、ゲームフォルダにも写しを置く。
/// </summary>
public sealed class PatchStateStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// このゲームフォルダの適用記録を読む（本体 → ゲームフォルダの写しの順。どちらも無ければ null）。
    /// 別のゲームフォルダの記録は返さない（<see cref="BelongsTo"/>）。
    /// </summary>
    public PatchState? Load(string gameRoot)
    {
        PatchException? firstError = null;
        foreach (var path in new[] { paths.StateFile, MirrorPath(gameRoot) })
        {
            PatchState? state;
            try
            {
                state = Read(path);
            }
            catch (PatchException ex)
            {
                // 片方が壊れていても、もう片方（同じ内容を同時に書いた写し）が正しければそれを使う。
                // 両方とも使えないときだけ、最初の壊れた記録を原因として伝える
                firstError ??= ex;
                continue;
            }
            if (state is not null && BelongsTo(state, gameRoot))
            {
                return state;
            }
        }
        if (firstError is not null)
        {
            throw firstError;
        }
        return null;
    }

    /// <summary>
    /// 記録がこのゲームフォルダのものかを判定する。
    /// <para>
    /// 本体の記録（%LOCALAPPDATA%）は 1 つしかないため、別の場所のゲーム（複製や別のインストール）に対して
    /// その記録を使うと、別の場所のバックアップを「古い記録」として削除してしまう。そのため、記録のフォルダに
    /// 今もゲームがある場合は別のインストールとみなして使わない。
    /// 記録のフォルダにゲームがもう無い場合（Steam の「インストールフォルダを移動」など）は、同じゲームが
    /// 移動したものとして引き継ぐ（ファイルのハッシュで状態を判定するので、移動後も正しく扱える）。
    /// </para>
    /// </summary>
    public static bool BelongsTo(PatchState state, string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(gameRoot);
        return IsSameDirectory(state.GameRoot, gameRoot) || GameInstallation.FindMissingFiles(state.GameRoot).Count > 0;
    }

    /// <summary>適用記録を保存する（本体と写しの両方）。</summary>
    public void Save(PatchState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        WriteAtomically(paths.StateFile, json);
        WriteAtomically(MirrorPath(state.GameRoot), json);
    }

    /// <summary>
    /// このゲームフォルダの適用記録を削除する（本体と写しの両方）。
    /// 本体が別のゲームフォルダの記録になっている場合は、そちらの「元に戻す」に必要なので残す。
    /// </summary>
    public void Delete(string gameRoot)
    {
        if (File.Exists(paths.StateFile) && !IsRecordOfAnotherGame(paths.StateFile, gameRoot))
        {
            File.Delete(paths.StateFile);
        }
        var mirror = MirrorPath(gameRoot);
        if (File.Exists(mirror))
        {
            File.Delete(mirror);
        }
    }

    /// <summary>記録ファイルを読む（無い・形式の版が違う場合は null、壊れていれば原因と直し方の分かる例外）。</summary>
    private static PatchState? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        PatchState? state;
        try
        {
            state = JsonSerializer.Deserialize<PatchState>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw Corrupted(path, ex.Message, ex);
        }
        if (state is null || state.SchemaVersion != PatchState.CurrentSchemaVersion)
        {
            return null;
        }
        // JSON として正しくても中身が欠けた記録（手作業での編集など）をそのまま使うと、状態確認で
        // NullReferenceException 等になり、GUI が起動のたびに落ちて元に戻すこともできなくなるため、壊れた記録として扱う
        return FindProblem(state) is { } problem ? throw Corrupted(path, problem, null) : state;
    }

    /// <summary>
    /// 記録の中身を検査し、問題があればその説明を返す（問題なければ null）。
    /// 対象ファイルは本ツールが書き換える 2 つに限る。記録は利用者の権限で書き換えられるため、ほかのパス
    /// （..\ を含む相対パスや絶対パス）を許すと、管理者として再起動した本ツールに任意の場所を上書きさせる踏み台になる。
    /// </summary>
    private static string? FindProblem(PatchState state)
    {
        if (string.IsNullOrWhiteSpace(state.GameRoot) || string.IsNullOrWhiteSpace(state.BackupDir))
        {
            return "ゲームフォルダまたはバックアップの場所がありません。";
        }
        // NUL などパスに使えない文字を含むと、場所の比較（Path.GetFullPath）が ArgumentException になる
        var recordedPaths = new[] { state.GameRoot, state.BackupDir }
            .Concat(state.Files?.Where(f => f is not null).Select(f => f.BackupPath ?? string.Empty) ?? [])
            .Append(state.Settings?.SettingsPath ?? string.Empty);
        if (recordedPaths.Any(p => p.IndexOfAny(Path.GetInvalidPathChars()) >= 0))
        {
            return "パスに使えない文字が含まれています。";
        }
        // 対象ファイルが 0 件の記録は、状態判定で「未適用」、元に戻すで「何もしない」になるだけで害が無いので許す
        if (state.Files is null
            || state.Files.Any(f => f is null || f.OriginalSha256 is null || f.PatchedSha256 is null || string.IsNullOrWhiteSpace(f.BackupPath)))
        {
            return "対象ファイルの記録が欠けています。";
        }
        if (state.Files.Any(f => !GameInstallation.PatchTargets.Contains(f.RelativePath, StringComparer.OrdinalIgnoreCase))
            || state.Files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Files.Count)
        {
            return "対象ファイルが本ツールの書き換えるファイルと一致しません。";
        }
        if (state.Tables is null || state.Tables.Any(t => t is null || t.TableId is null))
        {
            return "翻訳の適用件数の記録が欠けています。";
        }
        if (state.Settings is { } settings && (string.IsNullOrWhiteSpace(settings.SettingsPath) || string.IsNullOrWhiteSpace(settings.OriginalLanguage)))
        {
            return "言語設定の記録が欠けています。";
        }
        return null;
    }

    /// <summary>壊れた記録の例外（原因と、利用者ができる直し方を添える）。</summary>
    private static PatchException Corrupted(string path, string detail, Exception? inner)
    {
        var message =
            $"パッチ適用記録が壊れています: {path}\n{detail}\n" +
            "このファイルを削除してから、Steam のライブラリで Dead Age 2 を右クリック →「プロパティ」→「インストール済みファイル」→" +
            "「ゲームファイルの整合性を確認」を実行すると、元の状態に戻せます。";
        return inner is null ? new PatchException(message) : new PatchException(message, inner);
    }

    /// <summary>
    /// 記録ファイルが、今も存在する別のゲームフォルダの有効な記録かを判定する。
    /// 壊れた記録はどのゲームにも使えない（読むたびに例外になる）ので、削除してよい側（false）に倒す。
    /// </summary>
    private static bool IsRecordOfAnotherGame(string path, string gameRoot)
    {
        try
        {
            return Read(path) is { } state && !BelongsTo(state, gameRoot);
        }
        catch (PatchException)
        {
            return false;
        }
    }

    /// <summary>2 つのフォルダパスが同じ場所を指すか（大文字小文字・末尾の区切り文字・相対表記の違いを無視する）。</summary>
    private static bool IsSameDirectory(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static string MirrorPath(string gameRoot) => Path.Combine(gameRoot, GameConstants.StateMirrorRelativePath);

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
        var temp = path + ".jptmp";
        File.WriteAllText(temp, content, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
