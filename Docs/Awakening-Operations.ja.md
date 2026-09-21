# Codexが担当するAwakening自動改善ループ

この手順はユーザーが改善ループの開始を依頼した後にだけ実行する。最初の実装を作る段階では起動しない。

実行主体は**このタスクで動いているCodex**。Botがコードを自己変更する仕組みではない。Codexは下記を自律的に反復する。単に同じBotを周回させるだけでは、この依頼を満たさない。

## 初回1周の監査（2026-09-18 開始依頼）

今回はCodexが対話タスク内で1周を監督し、制御の受付、build照合、監視、停止、結果保存とレビューを監査する。成功後の次Mapは自動開始しない。ユーザーがInsertで停止した場合、または問題を指摘した場合は再開せず、目視で何が起きたかフィードバックを求める。停止した試行と証拠を保持してから修正する。

将来のCodex CLIにも同じ手順を組み込み、各Wait後に新しい停止指示、世代、AttemptId、結果、期限、観測鮮度を確認する。CLIの無人反復や定期ジョブは、この1周監査に含めない。hostがソースをコンパイルした場合は、ローカルdotnet buildのDLLとMVIDが異なるため、コンパイル完了ログと実際のTemp DLL、実行中MVIDを照合する。

## 操作の入口

### 手動の連続周回（2026-09-20更新）

ExileAPIのAutoExile設定でActive modeをAwakening Boss Rushにし、Awakeningの`Allow manual Insert start`をONにする。Hideoutで停止中にInsertを押すと連続周回を開始し、動作中のInsertは停止する。成功・帰還・収納を確認した場合は次Mapへ進む。**死亡した場合もHideoutへ帰還・収納したうえで自動的に続行する**（2026-09-21変更。`Continue after death`設定、既定ON）。同一Mapの残ポータルがあれば再入場し、無ければ新Mapを開く。素材/Map不足、タイムアウト、操作失敗では停止し、Codex改善対象として保持する。Reloadで連続周回は解除される。連続成功遷移は実機未検証。Map内で停止した場合は先にF2などでHideoutに戻る。回復処理中・未終了の試行・ロード中は開始しない。

手動開始は直前結果を人間が引き受けて再試行する操作として記録する。`attempt.manual_started`を出し、Codexによる診断・コードレビューを行ったとは記録しない。自動改善の検証と、人間の操作やCodex画面操作による補助を混同しない。通常のCodexループは従来どおりReview→Reload→Arm→Beginを使う。現在この手動開始設定はユーザーの依頼に合わせてON。

### AppからCLIへ移す条件

現状はAppで画面の切り分けも可能な状態を維持する。CLI化そのものを節約効果とは見なさない。通常試行の制御・待機・期限・ログ抽出を決定的なスクリプトに任せ、失敗または性能退行が出た時だけ要約と該当コードを`codex exec`へ渡す構成を目指す。前面復帰、誤ったUI状態からの回復、Stashie重複検出などを画面操作なしで観測・制御できることが移行条件。CLIの常駐改善ジョブはまだ開始していない。

### 2026-09-21 午後 ボス構成・時給改善・運用手順

- ボス構成（poewiki、3.28以降）: Horned Scarab of Awakeningは「T16+エリアにランダムなMaven's Invitationのボス群」を追加する。候補はFormed（Hydra/Phoenix/Minotaur/Chimera）、Twisted（Eradicator/Constrictor/Purifier/Enslaver）、Forgotten（Augmented/Altered/Twisted/Rewritten Synthete）、Remembered（Neglected Flame/Cardinal of Fear/Deceitful God）、Elderslayers（Baran/Drox/Veritania/Al-Hezmin）、Feared（Shaper/Elder/Synthete Nightmare(Masterpiece)/Sirus/Incarnation of Dread）。3.29.0でRememberedとFearedのドロップ遅延が削除された。
- 実測のメタデータ: Remembered = `AtlasMemory/FragmentOf{Ignorance,Anger,Benevolence}STANDALONE`（順にNeglected Flame, Cardinal of Fear, Deceitful God）、Feared = `AtlasExile5Standalone`(Sirus) / `AtlasMemory/BenevolenceBossStandalone`(Dread) / `LeagueSynthesis/SynthesisSoulstealerBoss2Standalone`(Nightmare)。
- 11:38の不具合: 旧Feared名簿（Atziri/Chayula/Cortex）に一致せず、ボス撃破後も未完了扱いでMap全域を探索していた。修正後は「追跡中ボスが全員死亡」で即Lootへ移り、名簿一致・人数一致・10秒間新規ボスなし（少数なら25秒）のいずれかで完了とする（`encounter.complete`のbasisに記録）。名簿外の`Standalone`ユニークは`unknown`として追跡し、倒すまでLootに入らない。
- Loot閾値は1c（API `awakening.minStackChaos`で設定。プロファイルに保存されるため設定JSONの直接編集は効かない）。拾えないアイテムは3回で`loot.skipped`にしてMapを止めない。
- 時給改善（ログ実測）: ラベルがHUD領域（`IsBlockedByUI`）に重なると毎回12秒待ち×3回で1Map約60秒失っていた。`blocked by UI`は即座にカメラ移動（`loot.unblock_reposition`、ラベルが画面中央へ寄る地点へ歩く）で解消する。Scoutでは被弾しないHP1のBestiary獣・無敵の雑魚を追わない（`scout.ignore_stragglers`）。
- 12:07-12:19 JSTの実測: 4Map連続Success、死亡0。Map滞在45〜90秒、Hideout往復20〜40秒、収益39c/90c/132c/591c。
- 運用: AutoExileのTick（コマンド処理を含む）はPoEが前面のときだけ動く。Codex/Claudeの画面操作ツールはLoaderウィンドウを隠してホストを止めるため使わない。停止は`awakening.stop_after_map`（今のMapを走り切ってHideoutで止まる）を使い、開いたMapを捨てない。再起動は`POST /api/control {"action":"host.restart"}`（ループバック限定、Bot稼働中は拒否）で`Tools/RestartExileApi.ps1`が起動し、Loader再起動→ビルド確認→PoEを前面化し、結果を`Tools/RestartExileApi.last.txt`へ書く。管理者権限は要求しない。
- 13:00-13:25 追加修正: 素材はMap Deviceの5スロット（開いた直後は空に見えるため最大2.5秒読み直す）→インベントリ→Stashの順に探す。ボススキルの分身（`ElderGuardian3Clone`等）はボスとして数えない。道中の10c以上のドロップは周囲に敵がいないとき半径60で拾う（`loot.en_route`）。構成人数が足りない既知グループは残りを探し、最後の新規ボスから60秒で打ち切る（`encounter.roster_short`）。
- 死亡後の再入場: Loot開始時のボス中心を`Run.DropSite`に保存し、再入場後はそこへ直行する。移動中は35グリッド以内の敵だけ迎撃し、ダメージが通らない集団は6秒無視して通り抜ける（`scout.push_to_drops`）。Lootは`DropSite`到着前に完了判定しない。
- 13:07-13:16のMap（Elemental Weakness呪い・追加雷ダメージ・モンスターダメージ増）で6回死亡（探索中の即死2回、Cardinal of Fearの投射物1回、Exarch招待状のためのMapボス戦3回）。Map/ボス方針は未決定。
- コミット注意: 同じ出力パスを再利用すると古い内容が書き込まれることがあった。毎回新しいファイル名で書き出し、書き込み後にサイズを確認する。
- NG条件の変更（ユーザー判断）: Less Armour / Less Area of Effect / Less Cooldown Recoveryは許可。Tmpに適合T16が無いときは4秒で`supplies_exhausted:no_acceptable_T16_in_Tmp`として止める。

### 2026-09-21 死亡後の連続続行とLoot取りこぼしの修正

- 実測（09:51 Attempt `710b17b8`）: ボス4体を討伐しMaven's Chisel of Proliferationを1個回収した直後、旧Loot防御（60グリッド以内に敵がいれば`Scout(lootDefense)`へ）が発動した。無ダメージ再配置が敵集団の方向へ約170グリッド移動させ、足元のChisel of Avarice×2、Crescent Splinter×4、Proliferation×1を残したまま死亡した。Chiselが「拾われない」原因はLoot方針ではなく、この防御が拾得より優先されたこと。
- 修正: Loot防御は半径`Loot defense radius`（既定30グリッド）内の敵だけを対象にし、その場でSparkを維持する。敵の方向へ移動しない。6秒以上ダメージが入らない、または15秒続く場合は10秒間防御を抑止して拾得を優先する（`loot.defense_started/clear/suppressed`を記録）。拾得順は「価値÷（距離+15）」の降順で、途中で死んでも高額品を先に確保する。
- 死亡後の続行: 死亡→F2でHideout帰還→収納を確認したら`review`→`begin`を状態機械が発行して続行する（`manual_loop.death_recovery` / `manual_loop.continued`）。ポータルEntity IDは帰還で変わるため、既にEnter済みのMap（`Run.Instance != 0`）で可視ポータルが全てDunesなら自動で`Run.PortalIds`を付け替える（`portals.auto_rebound`）。Complete表示のポータルは読めた場合だけ除外する。入場後のinstance hash照合は従来どおり。旧版では09:58に`unrecognized_portal_set`で停止していた。
- 実機確認（11:21）: 死亡後のreview→arm→begin、`portals.auto_rebound`、同一Mapへの再入場（instance hash一致）、残った敵無しでのLoot→F2帰還まで動作。ただし同一Mapは既に討伐・回収済みで、Chisel等の高額品は残っていなかった。
- 修正: F2帰還直後の11:22:08に`unexpected_area_change`で誤停止した。`CurrentAreaHash`がHideoutに変わっても`Area.CurrentArea`の更新が遅れ、一瞬「別instanceのMap内」に見えるレース。Return/OpenStash/ExternalStash中は判定せず、それ以外でも2秒以上継続した不一致だけを失敗とする。
- 未検証: 新Map取得を含む死亡後の完全な通しテスト、Chisel回収を伴う新Loot防御の実戦、Complete表示の文字列取得（読めなくても動作する）。

### 2026-09-20 実戦監査と修正

- 高速キー連打は禁止。Sparkはキー保持を維持し、停止・移動時に解除する。HoldKey/HoldKeyAtの保持済み要求は押し直さない。ScoutはSimulacrum共通のHP進捗追跡を使い、無ダメージ3秒・同一位置12秒で再配置。ボスは雑魚処理より優先し、同一位置3秒で再配置を試す。
- 同一DunesのElderslayers 4体（Veritania/Al-Hezmin/Drox/Baran）を実測し、全員の死亡を有効Entity・HP/ESゼロで確認。Attempt `167c0aae31fe4181b4c2ecf8993b2b12` は討伐後にMaven's Crescent 2個、13.38cを回収したがLoot中に死亡した。完走とは扱わない。Loot防御処理と、再入場後に討伐地点へ戻る処理を追加した。
- 帰還でポータルEntity IDが変化する場合がある。`Rebind -Execute` はCodexが未完了Dunesの表示を確認し、停止・レビュー済み・既存Mapの状態でのみ使用する。再入場時には保存したinstance hashも照合し、不一致なら自動帰還する。新規MapやComplete表示を安易に再登録しない。
- 個人StashのラベルIDとmetadataを照合してクリックする。接近が障害物で止まることが実測されたため、同じ位置で2回止まれば別の歩行可能位置へ移動して再試行する。09:22のスタッシュ開放はCodexの画面操作による補助であり、Bot単独成功と混同しない。
- F3不作動の原因として、旧Compiled/StashieとSource/StashieV2の二重初期化を確認。旧版は削除せず、ExileAPI root下 `PluginBackups/Stashie-legacy-20260920` に退避した。復元時はhost停止後に元のCompiled/Stashieへ戻すが、StashieV2との同時読込は避ける。
- 09:26:42にF3を一度発行し、09:26:45に右端列以外のインベントリ空・Stashie idleを確認。収納は動作した。同じinstance `1980249396` へ09:26:54に再入場し、残Loot回収を検証中。
- VerboseとAwakeningData/events.jsonlに、初回Unique観測、無ダメージ再配置、Stashラベルクリック、接近位置変更、F3発行、収納完了を記録。evidenceには一般ラベルの表示状態・座標と直近raw inputも含める。

### 過去の引継ぎ：正常入場達成（2026-09-19 23:25 JST）

- ユーザーは5時間枠内の正常入場を最優先し、達成後のMapping改善継続を許可した。従来の1周成功後に必ず終了する制限より、この最新指示を優先する。別CLI・定期ジョブは起動していない。
- Attempt `b98e6b3809bd42aab7281feb72fa32e6` は素材5種の取得、NG Map除外、適合T16 Dunes装填、23:23:20開封確認、23:23:26正常入場を達成。これは既存Completedポータルへの誤入場ではない。
- 23:23:43に死亡、23:23:49に自動復帰を確認。Scoutは通常敵26→110体に対し詠唱せず進行していた。最後の一撃の特定は未達。防御的なSpark処理（90グリッド以内の敵がいる間は移動停止して詠唱、掃討後に探索再開）を追加しロード済みだが実戦未検証。
- 最新host PID38320、MVID `2216cced-92c2-4f5e-aa97-80dc0c83c94f`。実行時には必ず再取得する。BotはHideout停止中、直前死亡はレビュー済み。5時間枠残り3%で新試行は開始せず監視中断を回避。次は同一Mapの記録済み残ポータルから再試行する。
- Fragmentの内部ラベルは `General` / `Scarab`。StashIndexerは指定タブだけを走査し、この2分類を読み分ける。StashSystemも抽出時に切り替える。ボタンは深さ11に存在したため探索上限14、可視ノード2500件に制限。全素材解決は約2秒、抽出は約10秒を実測。
- GキーのAtlasは閲覧用でありDunes開封パネルにならない。Gを使う変更は撤回済み。DeviceラベルまたはRender.InteractCenterNumを優先し、Awakeningの接近では未確認のクリックフォールバックを禁止。開封前の予期しないMap入場は失敗として即復帰する。
- 入場成功後の検証：ビルド成功、Awakening99件、制御HTTP模擬25件通過。ボス分類・全滅確定・Loot・F3収納・時給改善はまだ実機未完了。BossCatalogValidated=falseを根拠なく解除しない。

### Map入場までの優先課題（2026-09-19）

この時点ではMapの開封・入場はまだ一度も確認できていない。最新DLLはロード済みでBot停止中。停止・レビュー・再ビルド・ホスト再起動・ロード照合は実機で進めたが、1周全体の監査完了ではない。

- 素材取得: Simulacrumは設定されたFragment Tab（現在Delirium）を直接開き、Metadata Pathで抽出し、インベントリ増加を確認する。Awakeningは全19タブの走査に約24秒を費やし、Sacrifice at Dawn未発見で停止した。実際の保管タブとFragmentタブ内の分類を確認し、指定タブへの直接アクセスを優先する。未発見だけで在庫ゼロとは判断しない。
- 既存装填: Deviceで5種の素材スタックを観測済み。再利用経路を実装し、Map Deviceの名称だけでなくMappingDeviceのMetadataでも探す修正をロード済み。修正後の実機確認は未実施。毎回Stashを経由する必要はない。
- Atlas操作: DunesノードのUIオフセット3とExarch選択位置2は校正済み。NG Mapの取り出しと適合T16 Mapの差し替え、6スロット照合、Activateから新規ポータル入場までは未検証。Simulacrumの右クリックによる自動開封はDunes選択を伴わないため、そのまま流用しない。
- 抽出の再利用: 共通StashSystemの複数素材抽出は未発見素材をスキップする。Awakeningでは全5種の実在庫と装填結果を別途必ず検証する。現在のSupplyTab設定は抽出先を指定するだけで全タブ走査を省略しないため、設定だけでは短縮されない。
- 観測停止: PoEが非アクティブになるとホスト側で更新が止まり、HTTPが古いstatusを返す場合がある。開始時にも観測鮮度を確認する。Reloadの成功とゲーム操作の進行を混同しない。

入場を優先する順序は、装填済み素材での入場確認、指定タブから不足素材を直接補充する経路、補充を含む次回入場の検証とする。1周監査の範囲とユーザー停止時の扱いは変更しない。

ユーザー確認（2026-09-19）: 必要素材は名前がFragmentの特別タブにある。取得優先順位はDevice残スタック→Stash探索→Faustus購入（将来対応）。SimulacrumのStash探索・抽出は動くが、入場までの一連動作は未安定で、インベントリのSimulacrumを右クリックしActivate画面まで出すと動く傾向がある。したがってSimulacrum全体を動作保証済みとは扱わず、抽出と開封を分けて再利用する。Fragmentタブの内部分類はユーザーへの追加質問ではなく、公開資料と実UI/APIの観測で調査する。

公開資料: [Fragment Stash Tab](https://www.poewiki.net/wiki/Fragment_Stash_Tab)はScarabs等の内部サブタブを説明している。現行コードはVisibleStash.VisibleInventoryItemsを読むのみで、内部切り替えの実装はない。既存StashIndexのFragmentStashは21種のボス関連素材を記録したが、SacrificeとScarabの未発見を在庫切れの証拠にはしない。

開始時の既存ポータルをPriorPortalIdsに記録する修正を実機確認した（18:23:46、152/153/154）。Botが開封したMapの再試行と開封結果不明の場合は、この初期化を行わず元の所有情報を保持する。

18:27の実測では画面内Deviceへのクリックによる接近・Atlas表示が進み、FragmentタブからSacrificeの4種を読めた。Awakening Scarab未発見で停止し、Mapは未開封。これを受け、StashIndexerの任意指定タブ走査とFragments/Scarabs切り替え、StashSystemのSacrifice/Scarab抽出時の内部切り替えを追加した。内部ボタンは可視テキストから探索し、未発見ならタイムアウトで停止する。通常のStart()呼び出しは従来の全タブ走査を維持する。Deviceに一部素材がある場合も記録し、抽出時に重複取得しない。これらの新経路は実機検証中であり、対応完了とはまだ扱わない。

`Tools/AwakeningIteration.ps1` は短時間で返る制御用スクリプト。ゲーム操作／制御を伴うActionは `-Execute` を付けない限り何もしない。標準URLは127.0.0.1:9876で、必要なら `-BaseUrl` を指定する。

| Action | 役割 |
|---|---|
| Status | 現在の状態・Run/Attempt・結果・MVID・世代を読む |
| Build | Tempへのコピーを無効にしてbuild |
| VerifyBuild | ディスク上のDLLのMVID/SHA256を読む |
| Evidence | ゲームの読取snapshotを保存。入力はしない |
| Inspect | HideoutのMap Deviceを開いてAtlas内Mapの証拠を保存し、停止する。素材投入・開封はしない |
| Arm | 新モードを選択し、Codexからの開始を許可。これだけでは周回しない |
| Begin | 現在の世代とbuildを確認し、1試行だけ開始 |
| Wait | 最大30秒待って進捗または終端を返す。Codexは結果を確認して再度Waitする |
| Return | 終端済み試行をF2でHideoutへ戻す。戦闘の再開ではない |
| Recover | Reloadで中断された未終端Attemptを失敗として評価可能にする |
| Timeout | 外側の監視で300秒超過を検出した試行を終端にし、帰還へ移す |
| Review | Codexが作成したレビューJSONを保存し、次試行への条件を満たす |
| Reload | 対象Loader.exeだけを再起動して新DLLを読み込み、実行中MVIDと新世代を照合 |
| Stop | 改善ループの武装を解除し、入力を停止 |

ReloadはExileAPIホスト再起動という同等手段を用意した。`-HostProcessId` は実行ファイルの絶対パスを確認したLoader.exeのPIDに限る。ゲームプロセスは終了しない。初回の旧DLLからの導入でも利用できるが、実行時はBotが停止中である必要がある。既存ホストのRecompile操作を別手段で行った場合も、VerifyBuildとStatusのMVID一致を確認する。

## 開始前の校正

最初の版は、現行ゲームデータでまだ確認していない条件を成功扱いしない。

- Arm→Inspect→WaitでAtlasを開き、MapのRaw Mod/Stat、Deviceスロット、Exarch選択状態を保存する。Atlasが既に開いていればEvidenceだけでもよい。現在の参照DLLに型が無くても、現行ホストのMapKey・PrimordialBossSelector APIを読取アダプターが利用する。
- 17個のNG条件とStatの対応を確認し、不足をAwakeningMapPolicyへ追加してから `awakening.modCatalogValidated` を有効にする。
- 開封前と入場後のExarch進捗を照合して、`awakening.exarchReadyCounter` を設定する。-1は不明を表す。
- ボス分類は初期Metadata/英名ヒントを持つ。現在の敵データで照合して修正し、全滅根拠を確定してから `awakening.bossCatalogValidated` を有効にする。この確認前も戦闘観測は可能だが、成功の確定は保留する。
- 必須素材はStashの実アイテムからPathを取得する。ローカライズによって名称照合できなければEvidenceの実データを使って照合処理を追加する。推測したPathで素材を消費しない。

これらの校正は開始後のCodexの作業であり、毎回ユーザーへ作業を返す理由にはしない。ゲームデータから意味を特定できないユーザー指定に限って確認する。

## 正式な反復手順

1. ソース・設定・既存ポータル・停止理由を確認する。前回のユーザーからの終了指示があれば再開しない。
2. 最小実装／修正をbuild・テストし、Reloadを行う。更新されたファイルだけでなく、実行中のMVIDが期待したDLLと一致していることを確認する。
3. Arm→Begin。Botは1試行のみ実行し、成功時もレビュー待ちで停止する。
4. CodexはWaitを繰り返し、成功してHideoutへ到着／死亡してHideoutへ到着／300秒でもMap内／ユーザー介入、のいずれかまで監視する。通信不能は成功ではない。プロセスとVerboseも調べる。Waitがタイムアウトを返しても改善ループを終了したことにはしない。
   `deadlineExpired` は外側の監視での超過、`observationStale` は観測更新の停止、`recoveryStalled` は帰還の停滞を表す。Wait自体は入力を送らない。現在の状態を確認し、必要な場合はTimeoutまたはReturnをCodexが発行する。300秒経過だけで次のMapを開かない。
5. 成功時は探索・戦闘・Loot・収納の時間内訳、入力停止、詠唱維持、利益と原価を読む。Bot側の無駄が支配的でなくなるまで、コード／ログを改善する。
6. 死亡・Timeout・介入時は、該当Run/AttemptのVerboseと直前30秒の診断履歴を読む。観測事実と原因候補を区別し、目標達成につながるコードとログを変更する。必要ならReturnでHideoutへ戻す。ユーザーが改善作業自体を止めた場合はReturnも再試行もしない。
7. レビューJSONに `observations / diagnosis / changes / validation / nextAction` を書く。実際のログ証拠、変更内容、実行したテストを記載する。コード変更が不要な成功例なら、その根拠を記載する。評価やテストを実行したふりをしない。
8. Review→必要なbuild・検証→Reload確認→Arm→Begin。成功時は新規Map、失敗時は記録した残ポータルを優先する。残ポータル消滅を確認した場合のみ新規Map。
9. 追加NG候補の統計、死亡と純利益/時、性能基準を確認して、最も損失の大きい問題から改善を続ける。

レビュー例（値は実際の観測で書き換える）:

```json
{
  "observations": "対象AttemptのVerbose時刻・観測内容",
  "diagnosis": "根拠、原因候補と不確実性",
  "changes": "変更したファイルと動作、または変更不要の根拠",
  "validation": "実行した検証と結果、未検証項目",
  "nextAction": "retry_existing_portal または new_map または stop"
}
```

スクリプトはCodexの推論・コード編集を代行しない。Codexがこの手順を読み、ツールを使って継続する。今回の実装で別のCodexプロセス、定期Automation、背景の監視ジョブは作成・起動しない。

## 保存と確認

既存ExileAPI `Logs/VerboseYYYYMMDD.log` が主な診断ログ。`[Awakening]` とRunId/AttemptIdで抽出する。

プラグインフォルダーの `AwakeningData/` にcheckpoint、イベントJSONL、試行ごとのEvidenceを保存する。DLL ReloadでArmedは必ず解除され、未評価の終端を保持する。Reviewの前にBeginを送っても拒否される。ファイル破損・書込失敗時は入力を停止し、空の状態として再開しない。

Windowsでcheckpointの置換だけが一時的にアクセス拒否／共有違反になる場合は、flush済みファイルの置換を最大3回、合計85msの待機で再試行する。ゲーム操作や制御コマンドは再実行しない。失敗が続けば武装解除し、原因と再試行回数をstatus／Verboseへ記録する。

現在の統計実装はRun単位の重複排除、Build／コード／ボス群／IIQ帯で層別した死亡率、Wilson区間、保守的な片側検定と多重比較補正、戦闘時間・推定利益の差を出す。小標本はInsufficientEvidence。候補は表示のみで、指定NGへ自動追加しない。多変量モデル、交互作用の原因特定、戦闘時間／利益のbootstrap区間は初期版では未実装であり、データが得られた段階で追加する。

戦闘時間の中央値/P90、Timeout率、Mod効果値、常に同時出現して単独に帰属できないModも返す。現在のfingerprintは保存したBuild／Awakening／Threat設定に基づく。装備変更の自動検出と開発休止時間を含むセッション全体の利益集計は追加対象であり、装備を変更した実測を同一条件として混ぜない。原価または必須Lootの価格が不明なら純利益/時はnullになる。価値はpoe.ninjaによる推定で、売却実績ではない。

## 初期実装の到達点（2026-09-16〜17）

独立モード、厳密な素材照合と開封記録、ボス探索／観測／Spark維持、討伐位置のLoot確認、条件付きExarchボス、F2/F3連携、結果保持、世代付き制御API、Codex用の有界監視・Reload・レビュー・再試行手順を実装した。既存Wave Farmingの通常経路には厳密レシピを適用しない。

最初の実測で校正するのは、17ルールの現行Stat対応、ボスMetadataと全滅の観測、Exarchカウンター／選択UI、実素材の名称・Path。ModCatalogValidated=false、ExarchReadyCounter=-1の既定値では開封前に停止する。安全条件を黙って飛ばすためのフラグではなく、Codexが実データを照合してから設定する。

今回、実機への配備・Reload・Bot起動・改善ループ開始は行っていない。次の開始依頼を受けたCodexが、先に上記校正を行ってから反復を開始する。実機での討伐成功や時給改善はまだ未検証。

入力を送らない検証コマンド（プロジェクトルート、PowerShell 7）:

```powershell
dotnet build AutoExile.csproj --no-restore -p:SkipPluginDeploy=true -p:ExapiPackage=..\..\.. -clp:ErrorsOnly
dotnet run --project Tests/AwakeningRegression/AwakeningRegression.csproj
./Tests/AwakeningRegression/ControlRegression.ps1
./Tools/AwakeningIteration.ps1 -Action VerifyBuild
```

`ControlRegression.ps1` はHTTPをすべて模擬関数に置き換えて確認する。実行中ExileAPIへの通信・キー送信・ホスト終了は行わない。通常のcsprojビルドには従来のTempへのコピー処理が残るため、準備／検証では必ずSkipPluginDeploy=trueを指定する。

引渡し時の検証は、本体ビルド成功（エラー0）、新規ドメイン回帰99件、制御スクリプトの模擬検証20件、既存Simulacrum回帰30件とNavigation回帰の成功。実機テストは含まない。

### 2026-09-20 15:40時点の引継ぎ
新MapでElderslayers4体討伐、累積270.31cのLoot取得を確認。Baited Expectationsはフィルター非表示だったが、近傍world itemを根拠にAltを保持し、表示後hover検証クリックとインベントリ差分で取得できた。最後のVeritaniaドロップ位置の再確認で(424,580)に停滞。完走Successは未確認。Codex枠残量のため停止・F2帰還し、371d1b試行のレビューを保存。手動連続周回は成功時のみ自動継続する実装を追加、ビルド成功。次回は最終sweepの障害物対応と連続2Mapの実機確認を優先。


### 2026-09-20 20:32 実機再検証

新Map開封の5099c3試行は、ポータル消失がLoadingより先に観測されて誤失敗した。実際の入場を確認しF2帰還。修正後は開封確認時に専用EnterPortalへ移行し、近接移動不要のhover検証クリックを新Mapにも利用する。4d8daf試行で同じMapへの再入場を確認。

4d8dafは入場地点(1273,1369)から93秒移動しなかった。現行メモリのskillBarではT=SummonStoneGolem、W=SparkAltX。移動のfallback Tが現在のスキル割り当てと衝突した。Codex停止後F2でHideoutに帰還しレビュー保存。Tを移動のみに変更するか別キーを使うかユーザーへ確認中。回答前に割り当てを変更していない。

修正済み・Reload済み: 移動キーが実スキルに割り当てられていれば開封前に停止。HoldKeyは移動解除直後の入力レート制限が解除されるまでfalseを返し、送信されていない攻撃を保持済みと記録しない。quietなLootスキャン後の死亡位置確認範囲を60gにし岩越しの無駄な接近を減らす。取得確認済みアイテムの未解決名を除去。別インスタンスへの誤入場はレビュー後に新Runへ移し古い討伐情報を引き継がない。

最新検証: ビルド0エラー、Awakening101・制御25・Navigation5チェック成功。MVID=df4b09d4-f5ff-42cf-91c2-b7e1f29048e4。BotはHideout停止中。完走Successと連続2Mapは未検証。manualContinuousはstatusに公開し、manual_loop.continued/stoppedをVerbose/JSONLへ記録する。連続周回のレビューは状態機械による成功確認であり、Codexのコード改善を自動実行したという意味ではない。

## 2026-09-21 夜 ゲーム内マーケットでのMap自動購入

目的: Atlas・Tmpタブの T16 マップが尽きたときに、ゲーム内マーケット（`/`）から自動で補充して周回を止めないため。

### 事前設定（手動・1回だけ）
- マーケットの検索条件はゲーム側に保存されるので、一度入力すれば次回以降も残る（ただしカテゴリ変更などで Map/Chart フィルターの値が消えることがある）。
- Type Filters: Item Category = Map（指定しないとジェムなどが混ざる）。
- Map/Chart Filters: Map Tier 16〜16、Increased Item Quantity Min 115、Increased Packsize Min 40。
- Stat Filters に NOT グループ: Area is influenced by The Shaper（シェイパーガーディアンマップ除外。5c で大量に並ぶ）＋ AwakeningMapPolicy の NG mod 一式。
- 3.29 の通常マップは基底名が「Map (Tier 16)」。レイアウト（Dunes）は Atlas で選んだノードで決まるので、マップ名での絞り込みは不要（名前欄は空）。

### bot の動作（AwakeningMarketBuyer）
1. `/` でマーケットを開き、下部の search を押す（上部タブの search と区別するため Y 座標が大きい方）。
2. 検索結果（IngameUi の "Search Results" ペイン、[0,3,1,0] が一覧）を読み、各行の価格 `~b/o N chaos`、IIQ、Pack Size、P/S タグ数、mod 文面を bot 自身の NG ルールで再チェック。安い順に最初の合格行の売り手へ移動ボタン [0,1,2,0,4,0] で飛ぶ。
3. 売り手の "Select Items To Buy" 窓（[8,1,表示中タブ,0] がグリッド）で、各アイテムのエンティティを直接読み（価格は Base.PublicPrice）、T16・NG なし・IIQ/Pack・mod 数を確認し、最初の価格 + MapFollowUpOverChaos 以内、かつ MapMaxUnitChaos 以下なら Ctrl+左クリックで購入（価格が同じならダイアログなし）。グリッドの要素数が 1 減ったことで購入成功を確認。
4. 目標数に達する／この売り手に条件内の品が無くなる／インベントリ満杯 で `/hideout` により帰宅。不足分は次に安い売り手で繰り返し（検索は最大 6 回）。
5. 購入価格は economy.jsonl に purchase / price として記録（Invest はマップ使用時に計上される既存の仕組み）。

### 周回への組み込み
- OpenMap で「マップが無い」→ RestockMap（Tmp タブ）→ Tmp にも合格マップが無い → `awakening.economy.autoBuyMaps` が true なら MarketBuy フェーズで MapBuyCount 枚購入 → インベントリのマップで OpenMap を再開。購入できなければ `supplies_exhausted:market_<理由>` で停止。
- 手動テスト: `awakening.market_buy`（value = 枚数）、中止は `awakening.market_cancel`。状態は `/api/status` の `awakening.market`。
- 2026-09-21 21:34 実機確認: 2 枚購入（6c + 7c）、売り手 2 人を巡回して自宅へ戻ることを確認。

### 実装メモ
- UI 調査用に `inspect_ui` へ `|hidden`（非表示要素も出力）、`inspect_items`（グリッドのアイテムをエンティティ付きで出力）を追加。
- `ui_type` は入力前に End + Back×40 で全消去。括弧は JIS 配列なので Shift+8 = `(`、Shift+9 = `)`。
- 移動ボタンのダイアログやドロップダウンの選択肢は ExileAPI から文字が読めない（Trade Filters の Buyout 通貨などは結果側の `~b/o N chaos` で判定）。
