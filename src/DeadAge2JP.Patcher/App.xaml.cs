using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeadAge2JP.Core.Patching;
using DeadAge2JP.Core.Steam;

namespace DeadAge2JP.Patcher;

/// <summary>
/// アプリケーションの起動処理。
/// <list type="number">
/// <item>Dead Age 2（Steam版）のインストールを自動検出し、見つからなければメッセージを出して終了する</item>
/// <item>exe の隣の data フォルダ（翻訳・フォント等）を確認し、不足していればメッセージを出して終了する</item>
/// <item>問題なければメイン画面を表示する</item>
/// </list>
/// </summary>
public partial class App : Application
{
    /// <summary>ダイアログのタイトルに使うアプリ名。</summary>
    public const string AppTitle = "Dead Age 2 日本語化パッチ";

    private FileLogger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // ロガーの作成（ログフォルダの作成・ファイルを開く）が失敗した場合も、黙って落ちずにメッセージを出せるよう、
        // 想定外の例外の受け口を先に登録する（受け口側はロガーが無くても動く）
        DispatcherUnhandledException += OnUnhandledException;
        var paths = AppPaths.Default();
        _logger = new FileLogger(paths.LogDir);
        _logger.Info($"{AppTitle} v{PatchService.PatcherVersion} を起動しました。");

        // 1) インストール検出
        var located = SteamGameLocator.Locate();
        foreach (var line in located.Diagnostics)
        {
            _logger.Info(line);
        }
        if (located.Installation is null)
        {
            MessageBox.Show(
                "Dead Age 2（Steam版）のインストールが見つかりませんでした。\n\n" +
                "Steam で Dead Age 2 をインストールしてから、もう一度このツールを起動してください。",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        // 2) 配布データの確認
        PatchData data;
        try
        {
            data = PatchData.FromDataDirectory(Path.Combine(AppContext.BaseDirectory, "data"));
        }
        catch (PatchException ex)
        {
            _logger.Error("配布データが不足しています。", ex);
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        // 3) メイン画面
        var viewModel = new MainViewModel(located.Installation, data, paths, _logger);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Info($"終了します（コード {e.ApplicationExitCode}）。");
        _logger?.Dispose();
        base.OnExit(e);
    }

    /// <summary>想定外の例外はログに詳細を残し、ログの場所を案内してから終了する。</summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("予期しないエラーが発生しました。", e.Exception);
        MessageBox.Show(
            $"予期しないエラーが発生しました。\n\n{e.Exception.Message}\n\nログ: {_logger?.FilePath ?? "（ログを作成できませんでした）"}",
            AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(3);
    }
}
