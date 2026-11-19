using System.Windows;

// WPF のテーマ用リソースの探し方を指定する（WPF プロジェクトのテンプレート既定値のまま）。
[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            // テーマ別のリソース辞書（Themes\Aero.NormalColor.xaml 等）は持たない。
                                                // ページ・アプリのリソースで見つからない場合に探す場所
    ResourceDictionaryLocation.SourceAssembly   // 汎用のリソース辞書（Themes\Generic.xaml）はこのアセンブリ内を探す。
                                                // ページ・アプリ・テーマ別の辞書のどれにも無い場合に使われる
)]
