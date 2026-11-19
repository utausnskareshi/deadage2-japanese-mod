using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeadAge2JP.Core.Patching;

/// <summary>
/// ログをファイルへ書き出し、必要なら画面にも転送するロガー（スレッドセーフ）。
/// 不具合報告時に原因を追えるよう、例外は詳細（スタックトレース）まで記録する。
/// <para>
/// ログは起動 1 回につき 1 ファイル作る。たまり続けないよう、作るときに同じフォルダの古いログを消し、
/// 今回の分を含めて新しいものから <see cref="DefaultRetainCount"/> 個（既定）だけ残す。
/// また、1 行も書かずに閉じたログ（DevTools の status など、記録することが無い実行）は、空のファイルを残さず消す。
/// </para>
/// </summary>
public sealed partial class FileLogger : IPatchLogger, IDisposable
{
    /// <summary>残すログの数の既定値（今回の分を含む）。1 回あたり 1〜3 KB なので、20 個でも数十 KB に収まる。</summary>
    public const int DefaultRetainCount = 20;

    private readonly StreamWriter _writer;
    private readonly Action<string>? _sink;
    private readonly Lock _lock = new();

    /// <summary>1 行でも書いたか（書かずに閉じた空のログを消すため）。<see cref="_lock"/> の中で読み書きする。</summary>
    private bool _written;

    /// <summary>破棄済みか（2 回目の Dispose で何もしないため）。<see cref="_lock"/> の中で読み書きする。</summary>
    private bool _disposed;

    /// <summary>
    /// 本ツールが作るログのファイル名（patcher-年月日-時分秒-プロセスID.log）。
    /// 古いログの整理はこの名前のファイルだけを対象にし、利用者が置いた別のファイルには触れない。
    /// </summary>
    [GeneratedRegex(@"\Apatcher-\d{8}-\d{6}-\d+\.log\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LogFileName();

    /// <summary>ログファイルのパス。</summary>
    public string FilePath { get; }

    /// <param name="logDir">ログフォルダ（無ければ作成）。</param>
    /// <param name="sink">各行を受け取る転送先（画面表示用。任意）。</param>
    /// <param name="retainCount">残すログの数（今回の分を含む。1 以上）。</param>
    public FileLogger(string logDir, Action<string>? sink = null, int retainCount = DefaultRetainCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retainCount, 1);
        Directory.CreateDirectory(logDir);
        // 同じ秒に 2 つのプロセスが起動しても（exe の 2 重起動、DevTools の連続実行など）ファイル名が衝突しないよう、
        // プロセス ID を付ける。衝突すると後から開く側が「使用中」で失敗するか、先のログを上書きしてしまう
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        FilePath = Path.Combine(logDir, $"patcher-{stamp}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.log");
        // ファイルはここで開く（開けない場合は従来どおり作成時に例外にする。呼び出し側はロガー無しで失敗を案内できる）
        _writer = new StreamWriter(FilePath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        _sink = sink;
        // 古いログの整理は、今回のログを開いた後に行う（消せなかったときに、その旨を今回のログへ書き残せるように）
        DeleteExpiredLogs(logDir, retainCount);
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>
    /// 消すべき古いログのファイル名を選ぶ（副作用なし）。
    /// <para>
    /// 対象は本ツールの命名規則に合うファイルだけ。名前に起動日時（年月日-時分秒）が入っているので、
    /// 名前の降順を新しい順とみなし、今回のログ（<paramref name="currentFileName"/>）を除いて
    /// <paramref name="retainCount"/> - 1 個を残し、それより古いものを返す。
    /// 今回のログは対象から外すので、時計の巻き戻しなどで名前の順が逆転していても消さない。
    /// </para>
    /// </summary>
    /// <param name="fileNames">ログフォルダにあるファイル名（パスを含まない）。</param>
    /// <param name="currentFileName">今回のログのファイル名。</param>
    /// <param name="retainCount">残すログの数（今回の分を含む。1 以上）。</param>
    public static IReadOnlyList<string> SelectExpiredLogs(IEnumerable<string> fileNames, string currentFileName, int retainCount)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        ArgumentNullException.ThrowIfNull(currentFileName);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainCount, 1);
        return fileNames
            .Where(name => LogFileName().IsMatch(name) && !string.Equals(name, currentFileName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
            .Skip(retainCount - 1)
            .ToList();
    }

    /// <summary>
    /// 保持数を超えた古いログを消す。消せないもの（別のプロセスが書き込み中、権限が無いなど）は、
    /// 理由を警告として今回のログに書き残して飛ばす（次回の起動で再び消す対象になる）。
    /// ログの整理に失敗しても、本来の処理（日本語化・元に戻す）は止めない。
    /// </summary>
    private void DeleteExpiredLogs(string logDir, int retainCount)
    {
        IReadOnlyList<string> expired;
        try
        {
            expired = SelectExpiredLogs(
                Directory.EnumerateFiles(logDir).Select(path => Path.GetFileName(path)), Path.GetFileName(FilePath), retainCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"古いログの一覧を取得できなかったため、古いログは消さずに残します（{ex.Message}）");
            return;
        }
        foreach (var name in expired)
        {
            try
            {
                File.Delete(Path.Combine(logDir, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Warn($"古いログを削除できませんでした: {name}（{ex.Message}）");
            }
        }
    }

    private void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} [{level}] {message}";
        lock (_lock)
        {
            _writer.WriteLine(line);
            if (exception is not null)
            {
                _writer.WriteLine(exception.ToString());
            }
            _written = true;
        }
        _sink?.Invoke(exception is null ? line : $"{line}（{exception.Message}）");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _writer.Dispose();
            if (!_written)
            {
                DeleteEmptyLog();
            }
        }
    }

    /// <summary>
    /// 1 行も書かなかったログを消す（空のファイルを残さない）。
    /// Dispose から例外を投げると、using の中で起きた本来の例外を覆い隠してしまうので投げない。
    /// 消せなかった場合は転送先（DevTools では画面）に理由を伝える。空のファイルが 1 つ残るだけで実害はなく、
    /// 保持数を超えれば次回以降の整理で消える。
    /// </summary>
    private void DeleteEmptyLog()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _sink?.Invoke($"空のログを削除できませんでした: {FilePath}（{ex.Message}）");
        }
    }
}
