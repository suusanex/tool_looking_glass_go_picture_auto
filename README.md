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

同時に別の `show` が処理中の場合はエラーになります。`status` と `stop` は画像の変換中にも実行できます。変換エラー時は既存の表示を保ち、エラー終了します。デバイス切断などで描画に失敗した場合はログを残して worker を終了します。自動再接続は行いません。

## Looking Glass と Bridge の準備

1. Looking Glass Go を付属 USB-C ケーブルで PC に接続します。PC の USB-C ポートは DisplayPort Alt Mode に対応している必要があります。非対応 PC では、Go 用 HDMI-to-USB-C adapter を使用して PC の映像出力に接続します。
2. [Looking Glass Bridge](https://look.glass/bridge) をインストールして起動します。Bridge は Go の PC 接続に必要です。
3. Go が Standalone Mode の場合は、本体の電源ボタンをダブルタップして Desktop Mode に切り替えます。ステータス LED が青色になり、Bridge が Go を認識したことを確認します。公式の [Go トラブルシューティング](https://lfdocs.lookingglassfactory.com/getting-started/looking-glass-go/troubleshooting) にも、モード切替は電源ボタンのダブルタップと記載されています。
4. Windows の **設定 > システム > ディスプレイ** で Go を拡張ディスプレイにし、解像度を **1440 × 2560**、向きを **縦**、拡大縮小を **100%** に設定します。
5. Go が Desktop Mode で表示されてから `show` を実行します。

詳細は公式の [Go トラブルシューティング](https://lfdocs.lookingglassfactory.com/getting-started/looking-glass-go/troubleshooting) と [Windows のディスプレイ設定](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge/display-settings-on-windows) を参照してください。USB-C ケーブル接続では PC 側の DisplayPort Alt Mode 対応を確認してください。公式資料では Go 用 HDMI adapter も案内されています。

画像の表示には、インストール済みで起動中の Looking Glass Bridge が `http://127.0.0.1:33334` で提供するローカル REST API を使います。Bridge Python SDK の native DLL は、同梱版 2.6.2 と公式配布版 2.6.3 の両方で初期化前にプロセスを終了させることを確認したため、表示経路には使いません。公式の [Bridge.js](https://github.com/Looking-Glass/bridge.js) の API 利用方法を参考にしています。表示には playlist 再生の開始に加えて `show_window(true)` が必要で、Go 本体も Desktop Mode で起動している必要があります。これらを満たして表示した既知の RGBD テストパターン（赤・緑・青の帯と白い円）は実機で確認済みです。RGB が左、深度が右となる `depth_loc=2` の配置も確認済みです。

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
- 画像1枚の場合はその画像を固定表示し、複数枚の場合は順番にループします。
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
- Looking Glass Bridge は別途公式サイトからインストールしてください。このツールには Bridge 本体や native SDK runtime を同梱しません。

## テスト

開発環境でテストを実行する場合:

```powershell
uv run lookgo.py _test -q
```

## 表示確認状況

Bridge REST API と既知の RGBD テストパターンによる物理表示、`depth_loc=2` の RGB 左・深度右の配置は実機で確認済みです。深度推定モデルの CUDA 推論も確認済みです。以下の lookgo CLI による動作は、実機で未確認です。

- 写真とイラストで奥行き方向が意図どおりか確認する。
- A/B/C のリストを表示し、複数周の切替順が A→B→C→A になることを確認する。
- 表示中に D/E の別リストを指定し、古い表示が残らず置き換わることを確認する。
- 画像1枚のリストで固定表示になることを確認する。
- 同じリストを再指定して表示が置き換わることを確認する。
- `show` の端末を閉じた後も表示が継続することを確認する。
- 表示中にデバイスを切断した場合のログと終了状態を確認する。
- `stop` 後に worker が終了すること、および Go 本体の表示状態を確認する。
