# クリック遅延・HUD停止の調査（2026-09-13）

## ログで確認できた事実

参照元は既存の `ExileApi-Compiled-master/Logs/Info20260913.log`、
`Error20260913.log`、比較用の `Info20260912.log`。時刻は日本時間。

| 記録 | 確認内容 | 判定できる範囲 |
| --- | --- | --- |
| 9/13 18:56:44.662 → 18:57:02.521 | Monolithのpreflightからmouse-downまで約17.9秒。最初は距離116.4、5秒後も116.4、その後142.3、206.1、押下時5.0。最初の記録に `standardBusy=True` | 接近・カーソル操作を含む遅延。全時間がクリックAPI内の待ちとはいえない |
| 9/13 18:56:46.668 | `SetCursorPos failed` が `ResumeMovement → TickMovementLayer → BotCore.Tick → PerfomanceTick` まで伝播 | 移動再開時のWindowsカーソル操作失敗は確定。既存例外には数値エラーと呼び出し時間がない |
| 9/13 18:54:35 | 右クリックの `InputSequence` が `elapsed=167ms` で完了 | この例ではシーケンス全体は1秒未満。すべてのクリックが正常という証明ではない |
| 9/13 18:59:10.603 → 18:59:10.783 | Monolithの押下ログ後、約180msでactive状態を観測 | 短時間で開始できた例もある。手動介入の有無は記録されていない |
| 9/12 20:40:39.795 → 20:42:37.056 | `active=0,wave=11` の同じMonolithに39回要求、117.261秒後まで開始せず。20:42:37.572にactiveを観測 | 以前の実装での長い開始遅延。最新の固定座標クリック導入前の記録であり、現在のAPI所要時間の証明には使わない |

外側のログ時刻と内部の `leftDown` 時刻がずれる例がある。
例えば9/13 18:54:56.508の行で `leftDown=18:54:56.307`。
ログ出力がまとめられるため、新しい計測はイベント発生時刻 `at=` と
Stopwatchの `elapsedMs=` を保存する。

## 原因候補と修正

マウスの実装は `NativeMouseInput` からWindowsの `SetCursorPos` / `SendInput`
をP/Invokeで直接呼ぶ同期処理。キーボードは `ExileCore.Input.KeyDown/KeyUp`。
`async` と `ConfigureAwait(false)` だけでは、最初の未完了await以前の処理は
呼び出し元で動く。既存Monolith処理はカーソルがすでに合っていて入力間隔も
満たしていると、Tick内で `SendInput` まで同期実行できた。

- `InputSequenceGate` は所有権を確保後、`ForceYielding` で必ず実行を中断し、
  UIのSynchronizationContextを引き継がずにクリック本体を実行する。
  Monolithの移動キー・攻撃キー解放もその中で行う。
- キャンセル後もボタン解放終了まで所有権を保持する。5秒のキャンセル期限は
  実行中のWin32呼び出しを中断しない。期限だけでゲートを開けてクリックを重ねない。
- 移動・照準の `SetCursorPos` 失敗を数値エラー付きで記録し、250ms待って再試行する。
  失敗をTickへ投げず、移動再開では位置決め失敗後にキーを押さない。
- キー管理辞書をConcurrentDictionaryへ変更し、Tickと入力継続処理からの同時アクセスに対応。
- シーケンス中の押下が入力間隔制限に拒否された場合は、成功として続行せず、
  `input-dropped` と失敗結果を残して後続の通常再試行へ戻す。
- 入力の診断ログはメモリ上の上限1024件のキューへ記録し、Tickから既存の
  `ctx.Log` / ExileAPIロガーへ流す。1回の処理は24件・約2msを上限とし、
  単一ロガー呼び出しが遅い場合は `diagnostic-drain` として計測する。

低レベルマウスフックは登録元スレッドで実行されるため、そのメッセージ処理の
停滞も候補になる。Windows 10 1709以降のフックタイムアウト上限は1000ms。
ただし、今回のフリーズを起こしたフック・プラグインはまだ特定できていない。
新しいフックは追加していない。
[Microsoft: LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc)

`SendInput` の戻り値は入力列へ挿入されたイベント数であり、ゲームの処理完了ではない。
UIPIによる拒否は戻り値・エラーだけでは特定できない。
[Microsoft: SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

完了済みTaskでも明示的にyieldする指定の仕様：
[Microsoft: ConfigureAwaitOptions.ForceYielding](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.configureawaitoptions?view=net-10.0)

## 次回ログの読み方

出力先は引き続き `Logs/InfoYYYYMMDD.log`。起動時に
`[InputTrace] ... event=enabled version=input-latency-v1` が出れば今回の版が動いている。

| 記録 | 意味 |
| --- | --- |
| `click-sequence` begin → `event=dispatched` | 受付からワーカースレッドが動き始めるまで。ここが長ければ実行待ちを疑う |
| `SendInput.mouse` begin/end、`flags`、`inserted`、`win32` | ボタン押下・解放APIの時間と戻り値。`0x2/0x4` は左Down/Up、`0x8/0x10` は右Down/Up |
| `event=button-target`、`stage=desktop-snapshot` | 押下直前の実際のカーソル位置・対象ウィンドウ。OS状態の照会自体が遅い場合も別に計測 |
| `stage=SetCursorPos`、`ExileCore.Input.KeyDown/KeyUp`、`input-gap` | 50ms以上かかった処理、またはカーソル操作エラー。API、キー解放、入力間隔待ちを分ける |
| `event=still-running` | 500ms以上継続中の操作。専用監視スレッドが100ms間隔で確認し、同一操作は約1秒間隔で報告 |
| `event=host-stall`、`event=frame-gap` | 有効な実行中にTick/Renderが500ms以上来なかった区間。Tick内の処理停止とホスト側の呼び出し停止を区別する |
| `stage=Tick.mode/Tick.navigation/Tick.input-maintenance/Render/diagnostic-drain` | AutoExileのどの処理に滞在していたか |
| `event=monolith-request`、`wave-unconfirmed`、`wave-active-observed` | 要求からゲーム状態変化までの時間。`sinceFirstMs` は再試行を含む累積時間、`sinceLatestMs` は最後の要求からの時間 |
| `desktop=[...]` | 前面ウィンドウ・カーソル下のウィンドウのHWND/PID、OSのボタン・修飾キー状態。`overlayHit=True` はカーソル下のウィンドウがExileAPIのプロセスであることを示す |

`seq` と `inputSeq` で同じクリックを結び、`op` で個々のAPI呼び出しを結ぶ。
操作の `thread` は開始スレッド。`dispatched` の `thread` は実行先スレッド。
`osButtons` は物理入力と注入入力を区別しない。
`overlayHit` だけではクリックが遮断されたと断定しない。
`wave-active-observed` は手動クリックによる開始も含みうる。

監視スレッドはゲームのメモリを読まず、OSの非侵襲的な照会だけを行う。
ホスト復帰後にキューをファイルへ流すため、プロセスを強制終了すると未出力分は失われる。
GCで全スレッドが止まった区間は監視スレッドも動けないが、復帰後のframe-gapで検出できる。

## 検証

既存の純粋ロジック・ナビゲーション・Win32 INPUT構造体の回帰確認に加え、
ブロックする同期処理を模擬してUI呼び出し元が返ること、所有権が維持されること、
UIのメッセージ処理なしで完了すること、監視記録・相関ID・キュー上限・ロガー例外を確認。
テストでは実際のマウス・キーボード入力は送らない。

結果：純粋ロジック・入力診断30件、ナビゲーション・INPUT構造体・埋め込み資源5件が成功。
通常の `dotnet build --no-restore` でDLLを生成し、ダッシュボードとアイテム資源の
埋め込みも確認した。コンパイルのエラーは0、警告は58件。
通常ビルドのTemp自動コピーは書き込み制限で失敗したため、検証後に承認された
コピー処理でDLL・PDB・deps.jsonを配置し、各ファイルのSHA256一致を確認した。

配置した `Plugins/Temp/AutoExile/AutoExile.dll` のSHA256：
`E9A60293EAC641054C7C7A0B728E1D648F824D7ABF58C47F6C7B11771B06CE42`

旧3ファイルは同じディレクトリ内で、元のファイル名に
`.before-input-latency-cd55bbb8d44c4ac8ae10141511c6acd9.bak` を付けて保存。

実ゲームでのホワイトアウト解消は未確認。次回の実行で再発すれば、上記の記録から
API待ち・Tick/Render待ち・ゲーム状態反映待ちのどれかを絞り込める。
