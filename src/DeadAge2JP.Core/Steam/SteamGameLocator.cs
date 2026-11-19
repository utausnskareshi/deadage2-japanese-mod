using System.Globalization;
using Microsoft.Win32;

namespace DeadAge2JP.Core.Steam;

/// <summary>検出結果（見つからなかった場合の調査ログも保持する）。</summary>
/// <param name="Installation">検出したインストール（見つからなければ null）。</param>
/// <param name="Diagnostics">探索した場所と結果のログ（エラー報告・ログファイル用）。</param>
public sealed record GameLocateResult(GameInstallation? Installation, IReadOnlyList<string> Diagnostics);

/// <summary>
/// Steam 版 Dead Age 2 のインストール先を自動検出する。
/// <para>
/// 探索順: レジストリの Steam パス → libraryfolders.vdf の各ライブラリ → appmanifest_951430.acf の installdir。
/// 見つからない場合はアンインストール情報（Steam App 951430）の InstallLocation も確認する。
/// </para>
/// </summary>
public static class SteamGameLocator
{
    /// <summary>このPCから Dead Age 2 を探す。</summary>
    public static GameLocateResult Locate()
    {
        var log = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            log.Add("Windows 以外では自動検出に対応していません。");
            return new GameLocateResult(null, log);
        }

        foreach (var steamRoot in FindSteamRoots(log))
        {
            foreach (var library in EnumerateLibraries(steamRoot, log))
            {
                var found = TryLibrary(library, log);
                if (found is not null)
                {
                    return new GameLocateResult(found, log);
                }
            }
        }

        // Steam のライブラリ情報から辿れない場合の保険としてアンインストール情報を見る
        foreach (var location in FindUninstallLocations())
        {
            var missing = GameInstallation.FindMissingFiles(location);
            log.Add($"アンインストール情報の場所 {location}: {(missing.Count == 0 ? "OK" : "不足 " + string.Join(", ", missing))}");
            if (missing.Count == 0)
            {
                return new GameLocateResult(new GameInstallation(location, null, null, null), log);
            }
        }

        return new GameLocateResult(null, log);
    }

    /// <summary>
    /// 指定フォルダを Dead Age 2 のゲームフォルダとして検証して返す（手動指定や CLI 用）。
    /// </summary>
    public static GameInstallation? FromDirectory(string rootDir)
    {
        var full = Path.GetFullPath(rootDir);
        if (GameInstallation.FindMissingFiles(full).Count > 0)
        {
            return null;
        }
        // ゲームフォルダの 2 つ上が steamapps なら appmanifest からビルド情報を補う
        var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(full));
        var manifestPath = steamapps is null ? null : Path.Combine(steamapps, $"appmanifest_{GameConstants.SteamAppId}.acf");
        if (manifestPath is not null && File.Exists(manifestPath))
        {
            var manifest = ParseAppManifest(File.ReadAllText(manifestPath));
            return new GameInstallation(full, manifest.BuildId, manifest.BetaBranch, manifest.StateFlags);
        }
        return new GameInstallation(full, null, null, null);
    }

    /// <summary>appmanifest から読み取る情報。</summary>
    public sealed record AppManifestInfo(string? InstallDir, string? BuildId, string? BetaBranch, int? StateFlags);

    /// <summary>libraryfolders.vdf を解析してライブラリのパス一覧を返す（純粋関数）。</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string vdfText)
    {
        var root = KeyValuesParser.Parse(vdfText);
        // ライブラリは "0", "1", ... の数値キー。新形式は { "path" "..." }、旧形式は値に直接パスが入る
        return root.Children
            .Where(child => int.TryParse(child.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(child => child.Value ?? child.ChildValue("path"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();
    }

    /// <summary>appmanifest_*.acf を解析する（純粋関数）。</summary>
    public static AppManifestInfo ParseAppManifest(string acfText)
    {
        var root = KeyValuesParser.Parse(acfText);
        var beta = root.Child("UserConfig")?.ChildValue("BetaKey") ?? root.Child("MountedConfig")?.ChildValue("BetaKey");
        int? flags = int.TryParse(root.ChildValue("StateFlags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : null;
        return new AppManifestInfo(
            root.ChildValue("installdir"),
            root.ChildValue("buildid"),
            string.IsNullOrWhiteSpace(beta) || beta == "public" ? null : beta,
            flags);
    }

    /// <summary>レジストリから Steam のインストール先候補を集める（重複除去）。</summary>
    private static IEnumerable<string> FindSteamRoots(List<string> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }
        var candidates = new List<string?>
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string,
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }
            var normalized = Path.GetFullPath(candidate.Replace('/', Path.DirectorySeparatorChar));
            if (seen.Add(normalized))
            {
                log.Add($"Steam の場所（レジストリ）: {normalized}");
                yield return normalized;
            }
        }
        if (seen.Count == 0)
        {
            log.Add("レジストリに Steam のインストール情報がありません。");
        }
    }

    /// <summary>Steam 本体のフォルダと libraryfolders.vdf に記載された全ライブラリを列挙する。</summary>
    private static IEnumerable<string> EnumerateLibraries(string steamRoot, List<string> log)
    {
        var libraries = new List<string> { steamRoot };
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            try
            {
                libraries.AddRange(ParseLibraryFolders(File.ReadAllText(vdfPath)));
            }
            catch (FormatException ex)
            {
                // 解析できなくても Steam 本体のライブラリだけで探索を続けられるよう、記録して続行する
                log.Add($"libraryfolders.vdf を解析できませんでした（{vdfPath}）: {ex.Message}");
            }
        }
        else
        {
            log.Add($"libraryfolders.vdf がありません: {vdfPath}");
        }
        return libraries
            .Select(p => Path.GetFullPath(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>1 つのライブラリに Dead Age 2 があるか確認する。</summary>
    private static GameInstallation? TryLibrary(string library, List<string> log)
    {
        var manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{GameConstants.SteamAppId}.acf");
        if (!File.Exists(manifestPath))
        {
            log.Add($"ライブラリ {library}: Dead Age 2 の appmanifest なし");
            return null;
        }

        AppManifestInfo manifest;
        try
        {
            manifest = ParseAppManifest(File.ReadAllText(manifestPath));
        }
        catch (FormatException ex)
        {
            log.Add($"appmanifest を解析できませんでした（{manifestPath}）: {ex.Message}");
            return null;
        }

        var installDir = string.IsNullOrWhiteSpace(manifest.InstallDir) ? "Dead Age 2" : manifest.InstallDir;
        var gameDir = Path.Combine(library, "steamapps", "common", installDir);
        var missing = GameInstallation.FindMissingFiles(gameDir);
        if (missing.Count > 0)
        {
            log.Add($"ゲームフォルダ {gameDir}: 必須ファイル不足（{string.Join(", ", missing)}）");
            return null;
        }

        log.Add($"検出: {gameDir}（build {manifest.BuildId ?? "不明"}, branch {manifest.BetaBranch ?? "public"}, StateFlags {manifest.StateFlags?.ToString(CultureInfo.InvariantCulture) ?? "不明"}）");
        return new GameInstallation(gameDir, manifest.BuildId, manifest.BetaBranch, manifest.StateFlags);
    }

    /// <summary>Windows のアンインストール情報に登録された InstallLocation を集める。</summary>
    private static IEnumerable<string> FindUninstallLocations()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }
        string[] keys =
        [
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {GameConstants.SteamAppId}",
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {GameConstants.SteamAppId}",
        ];
        foreach (var key in keys)
        {
            if (Registry.GetValue(key, "InstallLocation", null) is string location && !string.IsNullOrWhiteSpace(location))
            {
                yield return Path.GetFullPath(location);
            }
        }
    }
}
