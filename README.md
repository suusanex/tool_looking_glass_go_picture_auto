# Looking Glass Go 画像表示ツール

画像パスを並べたテキストファイルから深度画像を作り、Looking Glass Go に表示する Windows 向けツールです。画像の並びを一定間隔でループします。画像の再指定時は、リスト全体を検証・変換してから表示を置き換えます。

## Quick Start

Windows に [uv](https://docs.astral.sh/uv/) をインストールし、Looking Glass Go を PC に接続します。[Looking Glass Bridge](https://look.glass/bridge) をインストールして起動し、Go を Desktop Mode にしてからコマンドを実行してください。Python や仮想環境を別途用意する必要はありません。

```powershell
uv run lookgo.py show --list "C:\Images\playlist.txt"
uv run lookgo.py status
uv run lookgo.py stop
```

`show` はバックグラウンド worker を非表示で起動し、先頭画像の描画完了 ACK を受けてからコマンドを終了します。起動後に PowerShell を閉じても表示を続けます。既定の切替間隔は 10 秒です。複数枚を 15 秒ごとに切り替える例:

```powershell
uv run lookgo.py show --list "C:\Images\playlist.txt" --interval 15
```

同時に別の `show` が処理中の場合はエラーになります。`status` と `stop` は画像の変換中にも実行できます。リストの検証や画像変換に失敗したときは Bridge へ再生を要求せず、既存の表示を保って `show` だけが失敗します。プレイリストの作成や登録に失敗した場合も同じです。`play_playlist` を送ったあとに失敗した場合は、画面が旧リストのままか新しいリストへ変わったかを確定できないため、旧リストの再生は報告せず worker を止めます。再表示するときは `show` を再実行してください。デバイス切断や Bridge の状態確認失敗でもログを残して worker を終了します。自動再接続や、失敗したリストの自動復帰は行いません。

## GUI（再生操作・プレイリスト編集）

Windows 10/11 環境では、鑑賞中の再生操作とプレイリスト編集を 1 画面で行える WinUI 3 (`Microsoft.UI.Reactor`) GUI を利用できます。

### 前提環境と起動方法

- [.NET 10 SDK](https://dotnet.microsoft.com/) 以降
- Windows App Runtime 2.x（`Microsoft.WindowsAppSDK` 2.2.0 以上が動作する環境）
- `uv`、Looking Glass Bridge、Desktop Mode の Looking Glass Go

```powershell
dotnet run --project gui/LookGo.Gui/LookGo.Gui.csproj -p:Platform=x64
```

### 画面構成と主な動作

- **上段（鑑賞コントロール・表示中リスト）:** Python ワーカーが管理する現在の表示状態を正本として表示します。「前へ」「一時停止／再開」「次へ」「表示停止」「最新状態を取得」の各操作（単一 Bridge プレイリストに対する REST transport 制御）と、表示位置・状態バッジ・横方向フィルムストリップ型サムネイルを提供します。サムネイルをクリックすると該当画像へジャンプし、一時停止中の状態はそのまま維持されます（複数枚表示中の自動切替位置は Bridge ログから追従し、追跡不能時は「位置不明」と表示して再生を継続します。サムネイルをクリックして任意位置へジャンプすると追跡を再同期できます）。
- **下段（プレイリスト編集・下書き）:** 上段の再生状態とは独立した編集領域です。「開く...」「新規」「保存」「名前を付けて保存...」「再生中を読込」のほか、画像追加・空行追加・上下並べ替え・削除・間隔設定（秒）ができます。左側の DataGrid のパス列をクリックして直接編集でき、右側の編集用サムネイルグリッドと選択状態が同期します。同一画像パスの重複行もそれぞれ個別に選択・並べ替え・削除でき、DataGrid の列ソートは無効化して再生順との混同を防いでいます。
- **編集・保存と「表示へ反映」の分離:** 下段での編集や「保存」だけでは、現在 Looking Glass Go に表示中のスライドショーや上段のサムネイルは変わりません。「表示へ反映」を押すと、事前検証のうえで対象ファイルへ UTF-8（1 行 1 画像パス）で原子保存し、内容ハッシュ（SHA-256）を検証しながら Python バックエンドの完全置換処理を実行します。変換中も上段の鑑賞操作や「反映をキャンセル」を実行できます。検証・変換または `play_playlist` 前のプレイリスト登録に失敗した場合は失敗理由と未反映状態を明示して旧表示を保ち、`play_playlist` 送信後に失敗した場合は旧表示の継続を仮定せずワーカーを停止してエラー状態を上段に反映します。
- **GUI を閉じた後の動作:** GUI ウィンドウを閉じてもバックグラウンドの表示ワーカーは停止せず表示を継続します。表示を終了したい場合は GUI の「表示停止」ボタンまたは `uv run lookgo.py stop` を実行してください。

## Looking Glass と Bridge の準備

1. Looking Glass Go を付属 USB-C ケーブルで PC に接続します。PC の USB-C ポートは DisplayPort Alt Mode に対応している必要があります。非対応 PC では、Go 用 HDMI-to-USB-C adapter を使用して PC の映像出力に接続します。
2. [Looking Glass Bridge](https://look.glass/bridge) をインストールして起動します。Bridge は Go の PC 接続に必要です。
3. Go が Standalone Mode の場合は、本体の電源ボタンをダブルタップして Desktop Mode に切り替えます。ステータス LED が青色になり、Bridge が Go を認識したことを確認します。公式の [Go トラブルシューティング](https://lfdocs.lookingglassfactory.com/getting-started/looking-glass-go/troubleshooting) にも、モード切替は電源ボタンのダブルタップと記載されています。
4. Windows の **設定 > システム > ディスプレイ** で Go を拡張ディスプレイにし、解像度を **1440 × 2560**、向きを **縦**、拡大縮小を **100%** に設定します。
5. Go が Desktop Mode で表示されてから `show` を実行します。

詳細は公式の [Go トラブルシューティング](https://lfdocs.lookingglassfactory.com/getting-started/looking-glass-go/troubleshooting) と [Windows のディスプレイ設定](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge/display-settings-on-windows) を参照してください。USB-C ケーブル接続では PC 側の DisplayPort Alt Mode 対応を確認してください。公式資料では Go 用 HDMI adapter も案内されています。

画像の表示には、インストール済みで起動中の Looking Glass Bridge が `http://127.0.0.1:33334` で提供するローカル REST API を使います。Bridge Python SDK の native DLL は、同梱版 2.6.2 と公式配布版 2.6.3 の両方で初期化前にプロセスを終了させることを確認したため、表示経路には使いません。公式の [Bridge.js](https://github.com/Looking-Glass/bridge.js) の API 利用方法を参考にしています。表示には playlist 再生の開始に加えて `show_window(true)` が必要で、Go 本体も Desktop Mode で起動している必要があります。`show_window(true)` は、Bridge のログに対象画像の `Showing` が出てから送ります。`play_playlist` の応答も `New Item Playing` も、そのプロセスで最初の OpenGL 初期化より先に届きます。初期化中に `show_window` を送ると REST が応答しなくなり、Bridge を再起動するまで接続できません。これらを満たして表示した既知の RGBD テストパターン（赤・緑・青の帯と白い円）は実機で確認済みです。RGB が左、深度が右となる `depth_loc=2` の配置も確認済みです。

## 初回起動と GPU / CPU

初回の `show` では Hugging Face から深度推定モデルと関連ファイルをダウンロードします。初回はネットワーク接続が必要で、ダウンロード完了後はローカルキャッシュを使います。モデルは `depth-anything/Depth-Anything-V2-Small-hf` の固定 revision を利用します。モデルファイルは `%LOCALAPPDATA%\lookgo\models` に保存されます。このモデルキャッシュは下記の画像変換キャッシュとは別です。

CUDA 対応 PyTorch が利用でき、CUDA が認識される環境では GPU を使用します。CUDA が使えない場合は CPU で処理します。実装時に固定 revision のモデルをダウンロードし、256×256 入力の GPU 推論が CUDA 上で約 41.7 秒で完了することを確認しました。CPU では画像変換に時間がかかることがあります。この推論確認は lookgo CLI による自動深度変換・表示の実機確認とは別です。

## 画像リスト

UTF-8 テキストファイルに画像パスを1行ずつ記述します。UTF-8 BOM 付きも読み込めます。

```text
photo-01.jpg
photo-02.png
artwork.webp
```

画像のフルパスまたはリストファイルからの相対パスを記述してください。例えば:

```text
front.jpg
C:\Users\me\Pictures\sample image.png
..\shared\illustration.webp
```

- 空行は無視します。
- 相対パスはリストファイルの場所を基準に解決します。
- 記述順と重複をそのまま保ちます。
- Windows の日本語や空白を含むパスを使えます。
- PNG、JPEG、WebP の静止画に対応します。リスト全体を変換できてから表示を置き換えます。
- コメント行や JSON 形式には対応しません。
- 画像1枚の場合は、表示開始の直後に Bridge の再生を一時停止して固定します。停止するか、別のリストの表示が成功するまで、その画像を読み直しません。
- 複数枚の場合は、確定したリストにつき Bridge のプレイリストを1つだけ作り、指定した間隔で Bridge が順番にループします。切替のたびにプレイリストを作り直しません。
- `status` の `index` は、複数枚の表示中だけ Bridge ログの画像切替に追従します。ログを読めなくなった場合、`index` は空になり、表示自体は続けます。
- EXIF の向きを適用し、透過部分は黒で合成します。縦横比を保って表示領域に収めます。

## 状態、ログ、キャッシュ

ツールの状態・ログ・モデルファイル・画像変換キャッシュは次の場所に保存します。

```text
%LOCALAPPDATA%\lookgo\
  state.json
  lookgo.log
  models\
  cache\
```

`uv run lookgo.py status` は実行状態とログファイルの場所を表示します。詳細は `lookgo.log` を確認してください。画像変換キャッシュは画像内容、モデル revision、変換設定をもとに再利用し、現在の表示リストとは独立しています。キャッシュ容量を空ける場合は、先に `uv run lookgo.py stop` を実行してから `%LOCALAPPDATA%\lookgo\cache` を削除してください。次回表示時に必要な画像は再変換されます。モデルを再ダウンロードさせたい場合は、worker を停止してから `%LOCALAPPDATA%\lookgo\models` を削除してください。

`stop` は worker の処理と関連リソースを終了します。Go 本体の画面が消灯するかどうかは、接続機器や Desktop Mode の動作に依存します。

## 依存ライセンス

- 本ツール: MIT License（[LICENSE](LICENSE)）
- Depth Anything V2 Small Hugging Face モデル: Apache License 2.0。
- Python の依存パッケージは `lookgo.py` の PEP 723 metadata に記載しています。各パッケージのライセンスは配布元の情報を参照してください。
- GUI の依存パッケージ (`Microsoft.UI.Reactor` / `Microsoft.UI.Reactor.Advanced` 0.1.0-preview.18、MIT License、および `Microsoft.WindowsAppSDK` 2.2.0) は [gui/LookGo.Gui/LookGo.Gui.csproj](gui/LookGo.Gui/LookGo.Gui.csproj) に記載しています。
- Looking Glass Bridge は別途公式サイトからインストールしてください。このツールには Bridge 本体や native SDK runtime を同梱しません。

## テスト

開発環境で Python テスト・C# 結合テスト・GUI ビルドを確認する場合:

```powershell
uv run lookgo.py _test -q
dotnet test gui/LookGo.Core.Tests/LookGo.Core.Tests.csproj
dotnet build gui/LookGo.Gui/LookGo.Gui.csproj -p:Platform=x64
```

## 表示確認状況

Bridge REST API と既知の RGBD テストパターンによる物理表示、`depth_loc=2` の RGB 左・深度右の配置は実機で確認済みです。深度推定モデルの CUDA 推論も確認済みです。起動直後の Bridge で既存の RGBD キャッシュを表示し、ログの `Showing` の後に `show_window` を送っても REST が応答し続けることは確認済みです。

複数枚を切替ごとに別プレイリストへ作り直すと、Bridge 2.6.3 の Private Bytes が切替のたびに増えました。同じ3枚を1つのプレイリストで10分ループしたときは、3枚が載った後の約1.4 GBから周回数に比例する増加は見えませんでした。これは数時間の計測ではなく、Bridge 内部の未解放オブジェクトを特定したわけでもありません。

2026-11-23 に、修正後の CLI で単色画像3枚を2秒間隔で表示し、続けて1枚のリストへ置き換えました。3枚のあいだ `status` の index は 1、2、0、1、2、0 と進み、Bridge ログの Loading も同じ順の繰り返しでした。Private Bytes は3枚の読込後に約 1,604 MB から約 1,407 MB まで下がり、その短い周回では切替ごとの増加は見えませんでした。1枚へ置き換えたあとは、その画像だけが `playing` で index は 0 のまま、Bridge は `transport_control_pause` を受け、観測した数秒では次の Loading はありませんでした。`stop` 後に worker は終了しました。人の目による奥行き、数時間のメモリ、デバイス切断、`show` の端末を閉じたあとの継続は未確認です。

以下の lookgo CLI および GUI による実機動作は、実機で未確認です。

- 写真とイラストで奥行き方向が意図どおりか確認する。
- 同じリストを再指定して表示が置き換わることを確認する。
- `show` の端末を閉じた後も表示が継続することを確認する。
- 表示中にデバイスを切断した場合のログと終了状態を確認する。
- `stop` 後の Go 本体の表示状態を確認する。
- GUI 上段の「前へ」「次へ」およびフィルムストリップからのジャンプで、`transport_control_seek_to_index` により Go 実機の表示が切り替わり、自動切替位置の追跡も追従することを確認する。
- GUI で複数枚を「一時停止」「再開」および一時停止中の前後移動・ジャンプを行ったとき、`transport_control_pause` / `transport_control_play` で一時停止の維持と解除が正しく動作することを Go 実機で確認する。
- GUI で素早い前後移動や連続ジャンプを行っても描画と表示位置がずれないことを確認する。
- GUI 下段でリスト編集後に「表示へ反映」を実行し、Go 実機の表示が単一プレイリストで完全置換されること、および失敗時に未反映状態と実表示状態が一致することを確認する。
