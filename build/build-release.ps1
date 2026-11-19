<#
  配布用 Zip を作成するスクリプト。

  1. パッチャーを .NET ランタイム同梱の単一 exe として発行する（利用者側で .NET のインストールは不要）
  2. exe・data（翻訳/フォント/型情報）・licenses と、README.md・LICENSE・THIRD-PARTY-NOTICES.md を
     まとめて out\DeadAge2JP-v<版>.zip を作る
  3. Zip の SHA-256 を out\DeadAge2JP-v<版>.zip.sha256 に書き出す（配布ページで公開して改ざん確認に使う）

  使い方（リポジトリのルートで）:
    powershell -ExecutionPolicy Bypass -File build\build-release.ps1
#>
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# パスはすべて -LiteralPath で渡す（-Path だと [ ] などがワイルドカードとして解釈され、
# 角括弧を含むフォルダに置いたリポジトリでは「存在しない」扱いや、空の Zip のまま気づかない失敗になるため）
# バージョンは Directory.Build.props の <Version> から取得する
[xml] $props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Encoding UTF8
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'Directory.Build.props に Version がありません。' }

$name = "DeadAge2JP-v$version"
$outDir = Join-Path $root 'out'
$publishDir = Join-Path $outDir $name
$zipPath = Join-Path $outDir "$name.zip"

Write-Host "== 発行します: $publishDir"
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
# ContinuousIntegrationBuild: exe に埋め込むデバッグ情報のソースのパスを「/_/…」に置き換える
# （ビルドした PC のフォルダ名やユーザー名を配布物に残さないため。行番号付きのエラー情報はそのまま得られる）
dotnet publish (Join-Path $root 'src\DeadAge2JP.Patcher\DeadAge2JP.Patcher.csproj') `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -p:ContinuousIntegrationBuild=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "発行に失敗しました（終了コード $LASTEXITCODE）。" }

# 単一 exe に含まれない余計なファイル（デバッグ情報など）が残っていれば除く
Get-ChildItem -LiteralPath $publishDir -File -Filter '*.pdb' | Remove-Item -Force

# 説明書・本ツールのライセンス・第三者ソフトウェアの一覧を、exe と同じ場所に置く
foreach ($document in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $root $document) -Destination (Join-Path $publishDir $document)
}

# 配布物に必須のファイルがそろっているかを確認する
$required = @(
    'DeadAge2JPPatcher.exe',
    'README.md',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'data\typetree\classdata.tpk',
    'data\fonts\BIZUDPGothic-Regular.ttf',
    'licenses\BIZ-UDPGothic-OFL.txt',
    'licenses\LZ4ps-LICENSE.txt'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $relative))) { throw "発行物に $relative がありません。" }
}
if (-not (Get-ChildItem -LiteralPath (Join-Path $publishDir 'data\translations') -Recurse -Filter '*.json')) {
    throw '発行物に翻訳ファイルがありません。'
}

Write-Host "== Zip を作成します: $zipPath"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
# Compress-Archive（Windows PowerShell 5.1 付属）は出力先の親フォルダをワイルドカードとして解決するため、
# 角括弧を含むフォルダでは止まってしまう。.NET の ZipFile は文字どおりのパスで扱える
# （Zip 内の構成は従来と同じく「版名のフォルダ\…」。最後の $true が版名のフォルダを含める指定）
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zipPath.sha256" -Value "$hash  $name.zip" -Encoding ASCII

$sizeMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
Write-Host "== 完了: $zipPath（$sizeMb MB）"
Write-Host "   SHA-256: $hash"
