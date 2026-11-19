using System.ComponentModel;
using System.Windows;

namespace DeadAge2JP.Patcher;

/// <summary>
/// メイン画面。処理は MainViewModel に任せ、ここでは表示まわりだけを扱う。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        // ログ欄は常に最新行が見えるようにする
        LogBox.TextChanged += (_, _) => LogBox.ScrollToEnd();
    }

    /// <summary>ファイルの書き換え中に閉じると中途半端な状態になるため、処理中は閉じさせない。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "処理中は閉じられません。完了するまでお待ちください。", App.AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        base.OnClosing(e);
    }
}
