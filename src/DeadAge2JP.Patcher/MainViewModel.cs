using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using DeadAge2JP.Core.Localization;
using DeadAge2JP.Core.Patching;
using DeadAge2JP.Core.Steam;
using DeadAge2JP.Core.Translation;

namespace DeadAge2JP.Patcher;

/// <summary>
/// メイン画面の状態と操作。
/// 主ボタンは状態に応じて「日本語化する」と「元に戻す」を切り替える。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly GameInstallation _game;
    private readonly PatchData _data;
    private readonly AppPaths _paths;
    private readonly IPatchLogger _logger;
    private readonly PatchService _service;
    private readonly StringBuilder _log = new();

    private StatusView _statusView = StatusPresentation.For(PatchStatus.NotPatched, null);
    private bool _statusLoaded;
    private bool _isBusy;
    private double _progressValue;
    private string _progressText = string.Empty;
    private string _translationText = string.Empty;

    /// <summary>起動時に分かる注意（Steam の更新待ち）。画面を閉じるまで変わらない。</summary>
    private readonly string? _steamNotice;

    /// <summary>直近の状態確認の失敗内容（成功したら消す）。</summary>
    private string? _statusError;

    private bool _useJapaneseFont = true;
    private bool _switchLanguage = true;

    public MainViewModel(GameInstallation game, PatchData data, AppPaths paths, FileLogger fileLogger)
    {
        _game = game;
        _data = data;
        _paths = paths;
        _logger = new ForwardingLogger(fileLogger, AppendLog);
        _service = new PatchService(paths, _logger);

        ToggleCommand = new AsyncRelayCommand(ToggleAsync, () => _statusLoaded && !IsBusy);
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(_paths.LogDir));
        OpenLicensesCommand = new RelayCommand(() => OpenFolder(Path.Combine(AppContext.BaseDirectory, "licenses")));

        if (game.HasPendingSteamUpdate)
        {
            _steamNotice = "Steam でゲームの更新またはダウンロードが完了していないようです。更新を済ませてから実行してください。";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncRelayCommand ToggleCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand OpenLicensesCommand { get; }

    public string VersionText => $"v{PatchService.PatcherVersion}";

    public string GameDir => _game.RootDir;

    public string BuildText => _game.BuildId is null
        ? "不明"
        : $"{_game.BuildId}{(_game.BetaBranch is null ? string.Empty : $"（ベータ: {_game.BetaBranch}）")}";

    public string StatusText => _statusLoaded ? _statusView.Text : "状態を確認しています…";

    public Brush StatusBrush => (Brush)Application.Current.Resources[_statusView.BrushKey];

    public string ButtonText => _statusView.ButtonText;

    /// <summary>オプション（フォント・言語切替）は未適用時にだけ変更できる。</summary>
    public bool CanChangeOptions => _statusLoaded && !IsBusy && !_statusView.IsRestoreMode;

    public string TranslationText
    {
        get => _translationText;
        private set => Set(ref _translationText, value);
    }

    /// <summary>
    /// 画面の注意書き。Steam の更新待ちと状態確認の失敗は別々に持ち、両方あれば並べて出す
    /// （1 つの欄を上書きし合うと、片方が消えたり、解消済みの失敗が残り続けたりするため）。
    /// </summary>
    public string? NoticeText =>
        string.Join("\n", new[] { _steamNotice, _statusError }.Where(text => !string.IsNullOrEmpty(text))) is { Length: > 0 } notice
            ? notice
            : null;

    public bool HasNotice => !string.IsNullOrEmpty(NoticeText);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            Set(ref _isBusy, value);
            OnPropertyChanged(nameof(CanChangeOptions));
        }
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public string LogText => _log.ToString();

    public bool UseJapaneseFont
    {
        get => _useJapaneseFont;
        set => Set(ref _useJapaneseFont, value);
    }

    public bool SwitchLanguage
    {
        get => _switchLanguage;
        set => Set(ref _switchLanguage, value);
    }

    /// <summary>画面表示直後に呼ぶ（状態判定にはハッシュ計算で数秒かかるため非同期）。</summary>
    public async Task InitializeAsync()
    {
        AppendLog($"ゲームフォルダ: {_game.RootDir}");
        await RefreshStatusAsync();
    }

    /// <summary>主ボタン: 状態に応じて日本語化または元に戻すを実行する。</summary>
    private async Task ToggleAsync()
    {
        var restoring = _statusView.IsRestoreMode;
        IsBusy = true;
        ProgressValue = 0;
        var progress = new Progress<PatchProgress>(p =>
        {
            ProgressValue = p.Fraction * 100;
            ProgressText = p.Message;
        });

        try
        {
            if (restoring)
            {
                var outcome = await _service.RestoreAsync(_game, progress);
                ShowMessage(BuildMessage("元に戻しました。", outcome.Notes), MessageBoxImage.Information);
            }
            else
            {
                var outcome = await _service.ApplyAsync(_game, _data, new ApplyOptions(UseJapaneseFont, SwitchLanguage), progress);
                var notes = outcome.Notes.Prepend($"日本語化率: {StatusPresentation.Coverage(outcome.State)}").ToList();
                ShowMessage(BuildMessage("日本語化が完了しました。", notes), MessageBoxImage.Information);
            }
        }
        catch (PatchException ex)
        {
            _logger.Error(ex.Message, ex.InnerException);
            ProgressText = "失敗しました。";
            if (ex.MayNeedElevation && Ask(ex.Message + "\n\n管理者として再起動しますか？"))
            {
                RestartElevated();
            }
            else
            {
                ShowMessage(ex.Message, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            // 想定外の失敗もアプリを落とさず、原因調査に必要なログの場所を案内する
            _logger.Error("予期しないエラーが発生しました。", ex);
            ProgressText = "失敗しました。";
            ShowMessage($"予期しないエラーが発生しました。\n\n{ex.Message}\n\nログフォルダ: {_paths.LogDir}", MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            await RefreshStatusAsync();
        }
    }

    /// <summary>状態を判定し、表示とボタンを更新する。</summary>
    private async Task RefreshStatusAsync()
    {
        _statusLoaded = false;
        RaiseStatusChanged();
        try
        {
            var report = await _service.GetStatusAsync(_game);
            _statusView = StatusPresentation.For(report.Status, report.State);
            TranslationText = report.Status == PatchStatus.Patched && StatusPresentation.Coverage(report.State) is { } coverage
                ? $"日本語化率: {coverage}"
                : $"収録している訳文: {CountTranslations()}";
            _logger.Info($"状態: {report.Status}");
            SetStatusError(null);
        }
        catch (Exception ex)
        {
            // 状態の確認は読み取りだけの処理なので、想定外の例外でもアプリを終了させず、原因を画面とログに出す。
            // （終了させると、壊れた記録などが原因の場合に起動のたびに落ち、ログの場所の案内も元に戻す操作もできなくなる）
            _logger.Error("状態の確認に失敗しました。", ex);
            SetStatusError($"状態の確認に失敗しました: {ex.Message}");
        }
        finally
        {
            _statusLoaded = true;
            RaiseStatusChanged();
        }
    }

    /// <summary>同梱の訳文の件数を表ごとに数える。</summary>
    private string CountTranslations()
    {
        try
        {
            var set = TranslationRepository.LoadAll(_data.TranslationsDir);
            return string.Join(" / ", LocaTableSpecs.All.Select(s => $"{StatusPresentation.TableName(s.Id)} {set.Get(s.Id).Entries.Count:N0} 件"));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            _logger.Error("翻訳データの読み込みに失敗しました。", ex);
            return $"読み込めません（{ex.Message}）";
        }
    }

    /// <summary>状態確認の失敗内容を設定する（null で消す）。注意書きの表示を更新する。</summary>
    private void SetStatusError(string? message)
    {
        _statusError = message;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    private void RaiseStatusChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(CanChangeOptions));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>ログを画面に追記する（別スレッドから呼ばれても UI スレッドで反映する）。</summary>
    private void AppendLog(string line)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }
        dispatcher.BeginInvoke(() =>
        {
            _log.AppendLine(line);
            OnPropertyChanged(nameof(LogText));
        });
    }

    private static string BuildMessage(string headline, IEnumerable<string> notes)
    {
        var lines = notes.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => "・" + n).ToList();
        return lines.Count == 0 ? headline : headline + "\n\n" + string.Join("\n", lines);
    }

    private static void ShowMessage(string message, MessageBoxImage image) =>
        MessageBox.Show(Application.Current.MainWindow, message, App.AppTitle, MessageBoxButton.OK, image);

    private static bool Ask(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, App.AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>管理者権限でこのツールを起動し直す（UAC でキャンセルされたら案内だけする）。</summary>
    private void RestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            ShowMessage("実行ファイルの場所を特定できないため、再起動できません。手動で「管理者として実行」してください。", MessageBoxImage.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _logger.Warn("管理者としての再起動はキャンセルされました。");
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.Error($"フォルダを開けませんでした: {path}", ex);
            ShowMessage($"フォルダを開けませんでした: {path}\n{ex.Message}", MessageBoxImage.Warning);
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>ファイルへのログ出力と同時に、画面のログ欄へも流すロガー。</summary>
    private sealed class ForwardingLogger(FileLogger inner, Action<string> sink) : IPatchLogger
    {
        public void Info(string message)
        {
            inner.Info(message);
            sink(message);
        }

        public void Warn(string message)
        {
            inner.Warn(message);
            sink("［注意］" + message);
        }

        public void Error(string message, Exception? exception = null)
        {
            inner.Error(message, exception);
            sink("［エラー］" + message + (exception is null ? string.Empty : $"（{exception.Message}）"));
        }
    }
}
