# 第三者ソフトウェアのライセンス

配布用 ZIP には、以下の第三者ソフトウェア・データ・フォントを**無改変のまま**同梱しています。
それぞれのライセンス文は、ZIP 内の `licenses` フォルダ（リポジトリでは [assets/licenses](assets/licenses) ほか）に収録しています。

| 名称 | 版 | ライセンス | 同梱場所 | ライセンス文 | 入手元 |
|---|---|---|---|---|---|
| .NET ランタイム | 10.0.11 | MIT | `DeadAge2JPPatcher.exe` に内包 | `dotnet-runtime-LICENSE.txt`<br>`dotnet-runtime-THIRD-PARTY-NOTICES.txt` | https://github.com/dotnet/runtime |
| .NET デスクトップ ランタイム（WPF） | 10.0.11 | MIT | `DeadAge2JPPatcher.exe` に内包 | `dotnet-wpf-LICENSE.txt`<br>`dotnet-wpf-THIRD-PARTY-NOTICES.txt` | https://github.com/dotnet/wpf |
| AssetsTools.NET / AssetsTools.NET.MonoCecil | 3.0.5 / 3.0.4 | MIT | `DeadAge2JPPatcher.exe` に内包 | `AssetsTools.NET-LICENSE.txt` | https://github.com/nesrak1/AssetsTools.NET |
| LZ4ps（lz4net。AssetsTools.NET に含まれる LZ4 の実装） | AssetsTools.NET 同梱版 | BSD 2-Clause | `DeadAge2JPPatcher.exe` に内包 | `LZ4ps-LICENSE.txt` | https://github.com/MiloszKrajewski/lz4net |
| LZMA SDK（AssetsTools.NET に含まれる LZMA の実装） | AssetsTools.NET 同梱版 | パブリックドメイン | `DeadAge2JPPatcher.exe` に内包 | （なし） | https://www.7-zip.org/sdk.html |
| Mono.Cecil | 0.11.3 | MIT | `DeadAge2JPPatcher.exe` に内包 | `Mono.Cecil-LICENSE.txt` | https://github.com/jbevain/cecil |
| AssetRipper Tpk（Unity の型情報データベース） | 2026-11-18 取得 | MIT | `data/typetree/classdata.tpk` | `AssetRipper-Tpk-LICENSE.txt` | https://github.com/AssetRipper/Tpk |
| BIZ UDPGothic Regular | 1.051 | SIL Open Font License 1.1 | `data/fonts/BIZUDPGothic-Regular.ttf` | `BIZ-UDPGothic-OFL.txt` | https://github.com/googlefonts/morisawa-biz-ud-gothic |

## 補足

- **.NET ランタイム**のライセンス文と第三者コンポーネントの表記は、同梱している版（10.0.11）のランタイムパックに含まれるものをそのまま収録しています。
  WPF の第三者コンポーネントの表記は dotnet/wpf のものです。
- **LZ4ps** は、AssetsTools.NET の DLL に含まれている LZ4 圧縮の実装です。BSD 2-Clause ライセンスの条件に従い、
  著作権表示とライセンス文を `LZ4ps-LICENSE.txt` に収録しています。
- **LZMA SDK** は、AssetsTools.NET の DLL に含まれている LZMA 圧縮の実装です。パブリックドメインで公開されています
  （表記の義務はありませんが、ここに記載しています）。
- **classdata.tpk** は、AssetRipper/Tpk が公開している型情報データベース（type_tree_tpk の LZ4 版 `lz4.tpk`）を改名したものです。
  Unity の各バージョンの型の構造情報（AssetRipper/TypeTreeDumps）から作られています。
- **BIZ UDPGothic** は、SIL Open Font License 1.1 に基づいて同梱しています。フォントファイルは改変していません
  （ゲームで表示するための組み込みは利用者の PC 上でだけ行い、その結果は配布していません）。
  ライセンス文は、リポジトリでは [assets/fonts/OFL.txt](assets/fonts/OFL.txt) です。
  「BIZ UDPGothic」は株式会社モリサワの商標です。

## 同梱していないもの

- Dead Age 2 本体のファイル（プログラム、画像、音声、英語の原文のテキストなど）は、一切含んでいません。
