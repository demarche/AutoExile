# 復元ポイント: 非Duo（ソロ）安定版 2026-09-25

Duo（Aurabot連携）改修を始める直前の状態です。ソロで Awakening Boss Rush を回す設定・コードをここに保存しています。

- コード: この README と同じコミット（コミットメッセージ「復元ポイント: 非Duo安定版 2026-09-25」）。
  戻すとき: `git checkout <そのコミット> -- .` の後に ExileAPI を再起動（host.restart）。
- 設定: `AutoExile_settings.json` を `ExileApi-Compiled-master\config\global\AutoExile_settings.json` に上書きし、ExileAPI を再起動。
- 読み込み済みビルド mvid: f268e49c-b4fc-4949-a2a8-298b275d5d21

Duo 改修後も、パーティを組んでいない（またはAurabotとの通信が確立していない）ときはこのソロ動作に自動で戻る設計にします。
