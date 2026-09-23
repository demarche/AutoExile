# DmarX（Inquisitor Lv95）ビルド／Atlas 改善レポート（改訂版）— Awakening Boss Rush の chaos/時 最大化

- 作成: 2026-09-23（前版 2026-09-23 を全面改訂）
- 対象: アカウント Dmarch#1202 / キャラクター DmarX / Allflame（3.29 Curse of the Allflame、2026-07-17 開始）/ Japan Realm
- 根拠データ:
  - `runs.jsonl`: 2026-09-21 12:37Z〜09-23 04:52Z。242 試行、82 Run、うちマップに入った 81 Run
  - `events.jsonl`: 09-23 03:35〜04:52Z、8,893 イベント。死亡 13 件、バースト 18 件
  - `Verbose20260920/21.log`: 09-19〜09-21 の死亡 11 件。ES 推移の補強に使った
  - `checkpoint.json`: 試行履歴 306 件とビルド設定
  - `economy.jsonl`、`Beasts_settings.json`
  - 公式の公開 API（装備・パッシブ、09-23 取得）、poe.ninja 経済 API（09-23 取得）、Web
- 表記:
  - 【事実】ログ／API で確認した内容
  - 【推定】データからの推論
  - 【仮説】未検証の提案（PoB または A/B での確認が必要）
- 金額: 特記のない限り poe.ninja 2026-09-23 時点の価格。1 div = 356.8c
- 時給: 「稼働時間（operatingSeconds）」を基準にしている。開発停止や Reload を含む実時間は、09-23 の区間で稼働時間の約 1.9 倍だった

---

## 0. 要点：上位 5 案

**ベースライン**（直近セッション 09-22 09:30Z〜09-23 04:52Z、47 マップ、開発用の外れ値を除く）【事実＋モデル】

| 指標 | 値 |
|---|---|
| 死亡 | **1.48 回/マップ** |
| 3 回死亡での放棄率（モデル） | 18.6% |
| 1 マップあたりの稼働時間 | 約 250 秒 |
| 稼働 1 時間あたりのマップ数 | **約 14.5** |
| 純益（現在価格） | **約 1,600c/稼働時（≈ 4.5 div）** |

| # | 改善案 | 担当 | 死亡率 | マップ/時 | chaos/時 | 概算コスト | 確度 |
|---|---|---|---|---|---|---|---|
| 1 | **死亡時の放棄上限を 3 回から 4〜5 回へ**。ボス未発見のまま 3 回死んだ場合だけ即放棄 | Bot 設定 | 変わらない（放棄率は 18.6% → 2〜6%） | −1% | **+19〜26%** | 0c（死亡ごとの経験値ロスあり） | 中〜高。実績では、3 回死んだ後も継続した場合の限界収益が約 2,400c/稼働時 |
| 2 | **生存行動をまとめて改善**: Coruscating Elixir の常時維持、ミニオンを連れて進む、連続バースト時の後退、赤ビースト狩りの制限、危険スキルの回避リスト | Bot | −20〜35%【仮説】 | +3〜5% | **+14〜24%** | 0c（Bot 実装のみ） | 中。仕組みの裏付けはあるが、効果量は仮定 |
| 3 | **NG MOD に「Monster Fast（Fleet）」と「Monsters fire 2 additional Projectiles」を追加**し、40〜100 マップで A/B 評価 | Bot 設定＋ユーザー判断 | 1.25 → 1.05（縮小推定）〜0.58（実測） | +2〜8% | **+2〜31%** | 地図代 +3〜10c/枚（≈ +45〜150c/稼働時） | 低〜中。多重比較の補正後は有意でない（q = 0.10〜0.16） |
| 4 | **ビルドの防御強化**: Steelskin を Immortal Call に置換、Whirling Barrier の取得、胴を Eldritch 化／最大耐性・ES の上積み、パンテオンの見直し | プレイヤー（PoB 必須） | −15〜25%【仮説】 | +2〜3% | **+11〜17%** | IC・リスペックは数十 c 程度。胴は数 div〜（相場は要確認） | 中。仕組みは確かだが、数値は PoB 待ち |
| 5 | **物流をまとめ買い化し、運転資金を確保**: スカラベ 5 → 20、Sacrifice 20 → 60、地図 10 → 20、Chaos 残高 2,000〜3,000c を維持 | Bot 設定＋プレイヤー | 変わらない | **+11〜22%** | **+11〜22%** | 運転資金 約 3,000c（消費ではない） | 中〜高。09-23 は Chaos 不足で購入失敗が 4 回あった |

- 5 案すべてを入れた場合の見積もり（効果は加算されないため別途計算）: 保守的に +46%（約 2,300c/稼働時）、楽観的に +76%（約 2,800c/稼働時）【推定・モデル】
- 各案の要旨:
  1. 3 回死亡に達した 11 マップのうち 5 マップは、その後に成功していた（平均 230c）。3 回で放棄すると、投資 129c と期待収益 272c をまとめて捨てることになる。
  2. 直近 13 件の死亡では次のことが確認できた（数値は「死亡直前 5 秒 / 平常時」）。
     - カオス貫通による死亡が 1 件（ES 9,606 を残してライフ 0、Coruscating 非発動）
     - ミニオン由来の ES オーラ（+1,359 ES）: 稼働率 60% / 76%
     - Fortify（10 スタック）: 45% / 61%
     - Bestiary の赤ビースト由来のスキル・個体が死亡直前に存在: 24 件中 7 件
  3. 調整済みの率比: Fast 1.88 [1.21–2.92]、Multiple Projectiles 1.77 [1.09–2.86]。ただし偶然の可能性を排除できないため、試験導入としている。
  4. 現状は ES 10.1k〜11.5k とライフ 432 が被ダメの受け皿で、物理軽減はほぼ無い（アーマー 0）。死亡の約半数は ES 満タンから 1 秒以内。
  5. 物流時間は直近セッションで 48 秒/マップ、09-23 の区間では 97 秒/マップ。

> 前版からの主な訂正【事実】
> - ES は「約 6〜7k」ではなく 10,131（ミニオンの ES オーラなし）/ 11,490（あり）だった。
> - 「ES 満タンから 1 秒以内の一撃死」に当てはまるのは 23 件中 11 件だけで、残りは 1〜2.7 秒の連続被弾だった。
> - 全期間の死亡フェーズで最も多いのは Fight（45%）。Scout が最多なのは直近 1.3 時間に限られる。
> - 成功マップの平均収益は 234c（拾得時の価格）。約 290c は 1 時間分の値だった。

---

## 1. データと方法

- **Run と試行**: `runs.jsonl` の各行は 1 試行（入場単位）を表す。`deaths`・`revenue`・`phaseSeconds` は Run 単位の累積値なので、差分を取って試行ごとの値にした。死亡数は Run 内の `Death` 行の数と一致した（81 マップで 101 件）。
- **死亡フェーズの推定**: 死亡した試行の中で、最も進んだマップ内フェーズ（EnterPortal < Scout < Fight < Loot < MapBoss）を死亡フェーズとした。`death.context` がある 13 件で照合すると 12 件が一致した（92%）。外れた 1 件は、Loot の後に Scout へ戻って死んだケース。
- **ES 推移**: `diagnostic.sample`（約 0.27 秒間隔のリングバッファ、死亡時に書き出し）を 09-23 分と Verbose ログ 09-19〜21 分から復元した。対象は死亡 23 件。
- **MOD 分析**: 81 マップを単位にした。行った手順は次のとおり。
  - MOD ID の Tier 番号を除いてグループ化
  - 死亡率の Poisson 95% 信頼区間
  - 並べ替え検定（4,000 回）と Benjamini–Hochberg 補正（n ≥ 3 の 51 MOD）
  - ボス群と期間を共変量にした Poisson 回帰。過分散（φ = 1.32）で標準誤差を補正
  - 全 MOD 同時の L2 正則化 Poisson 回帰。罰則は LOO で選択（α = 8）
- **chaos/時 モデル**【推定】: `chaos/h = 3600 × {(1−p_放棄)·R + p_放棄·0.35R − C} / {T0 + E[min(死亡数, 上限)]·Td}`。放棄は Poisson(λ) の死亡数が上限に達した場合とした。前提値は次のとおり。
  - R（完走時の収益）= 272c
  - C（1 マップの原価）= 129c
  - T0（死亡以外の所要時間）= 213 秒
  - Td（1 回の死亡で増える時間）= 25 秒
  - λ（死亡率）= 1.48 回/マップ
  - 前提値の出典は §2 を参照。実測は過分散（分散/平均 = 1.59）なので、放棄率はモデルよりやや高く出る可能性がある。

---

## 2. 現状の数字（根拠）

### 2.1 全体 KPI【事実】

| 指標 | 値 | 備考 |
|---|---|---|
| 期間 | 2026-09-21 12:37Z〜09-23 04:52Z | 242 試行、82 Run（入場 81） |
| 死亡 | 101 回。1.25 回/マップ（95%CI 1.02–1.52） | 分散/平均 = 1.59（過分散） |
| 1 回以上死んだマップ | 51/81 = 63%（Wilson 95%CI 52–73%） | |
| マップ別の死亡数 | 0 回: 30 マップ、1 回: 23、2 回: 17、3 回: 6、4 回: 2、6 回: 3 | |
| 成功 | 70/81 = 86%（77–92%） | 非成功 11 件の内訳: 死亡打ち切り 6、Timeout 2、運用失敗 2、手動 1 |
| セッション別の死亡率 | 前半（09-21〜22 朝、33 マップ）0.91 [0.61–1.30]、後半（09-22 09:30〜、48 マップ）1.48 [1.16–1.87] | 後半のほうが有意に高い（p = 0.026） |
| 成功マップの収益（拾得時の評価額） | 平均 234c、中央値 216c、P10 69c、P90 392c、最大 1,021c | 実売ではなく poe.ninja 評価 |
| 原価 | 平均 117c/マップ（当時）。現在は約 129c | Horned Scarab of Awakening 115.8c、Sacrifice 4 種で 4.3c、地図 7〜11c |
| 稼働あたり | 11.4 マップ/稼働時、純益 1,071c/稼働時（全期間。1,500 秒超の開発用 Run は除外） | 直近セッションは 14.4 マップ、1,565c |
| 実時間（09-23 02:59〜04:52Z） | 13 マップ / 1.87 時間 = 7.0 マップ/時、純益 1,681c（≈ 900c/実時間） | 同じ区間でプラグインの Reload が 9 回、停止要求が 3 回あった |

### 2.2 時間配分（1 マップあたりの秒数）【事実】

| フェーズ | 直近セッション（47 マップ） | 09-23 区間（13 マップ） |
|---|---|---|
| Scout（ボス探索） | 69.1 秒（27.6%） | 43.7 秒（15.7%） |
| Loot | 60.6 秒（24.2%） | 44.7 秒（16.1%） |
| Fight | 16.0 秒（6.4%） | 17.1 秒（6.1%） |
| MapBoss（Exarch 招待状用） | 2.7 秒（1.1%） | 5.2 秒（1.9%） |
| EnterPortal | 10.0 秒 | 12.7 秒 |
| MarketBuy＋Restock（Faustus）＋RestockMap＋IndexStash＋Withdraw（物流） | **48.4 秒（19%）** | **97.4 秒（35%）** |
| Prepare＋OpenMap＋Return＋OpenStash＋ExternalStash | 43.7 秒 | 57.5 秒 |
| 合計 | 約 250 秒 | 約 278 秒 |

- 1 回の死亡で増える時間: 再入場までに 11〜30 秒（13 件の実測）。回帰の傾き（Theil–Sen）は 21 秒/死亡 [2–38]。
- 死亡そのものの時間コストは小さい。損失の大部分は放棄（投資と期待収益の喪失）から来る（§2.8）。

### 2.3 収益の中身（81 マップ全体）【事実】

| アイテム | 数量 | マップあたり | 当時の評価額計 | 現在単価 | 現在価格でのマップあたり |
|---|---|---|---|---|---|
| Maven's Chisel of Avarice | 52 | 0.64 | 7,896c（収益の 46%） | 219.3c | **140.8c** |
| Maven's Chisel of Scarabs | 40 | 0.49 | 2,002c | 51.7c | 25.5c |
| Maven's Chisel of Proliferation | 45 | 0.56 | 1,809c | 46.0c | 25.6c |
| Maven's Chisel of Divination | 26 | 0.32 | 902c | 25.4c | 8.2c |
| Maven's Chisel of Procurement | 73 | 0.90 | 263c | 5.1c | 4.6c |
| Crescent Splinter | 453 | 5.59 | 1,142c | 2.6c | 14.7c |
| その他（中央値 4c/マップ。Horned Scarab of Bloodlines 712c の 1 回を含む） | — | — | 3,218c | — | 約 31〜40c |

- 上 6 品目の合計は現在価格で **219c/マップ**（全マップ平均）、成功マップに限ると 241c。これに「その他」を足した完走時の収益を R = 272c とした。
- ボス群ごとの Maven 系収益（現在価格、全マップ平均、複合 2 マップを含む）: Feared 320c、Elderslayers 232c、Formed 203c、Remembered 192c、Twisted 166c、Forgotten 163c。Feared の Avarice は 1.06 個/マップ。
- Exarch 招待状（Incandescent Invitation）: 81 マップで 2 枚。現在 195c なので **約 4.8c/マップ** の価値。
- 参考戦略との比較（wealthyexile、2026-08、人手で 36 マップ/時）: 収益 41 div/時、原価 16 div/時。1 マップあたり約 406c の収益だが、Maven's Invitation の実施、Templar Astrolabe、傭兵ドロップを含む。

### 2.4 死亡のフェーズとボス群【事実】

**フェーズ別**（推定手法の検証一致率 92%）

| フェーズ | 前半 33 マップ | 後半 48 マップ | 計 | 割合（Wilson 95%CI） |
|---|---|---|---|---|
| Fight | 12 | 33 | **45** | 45%（35–54%） |
| Scout | 7 | 18 | 25 | 25%（17–34%） |
| Loot | 7 | 17 | 24 | 24%（17–33%） |
| MapBoss | 4 | 3 | 7 | 7%（3–14%） |

**ボス群別**

| ボス群 | マップ | 死亡 | 死/マップ（95%CI） | Fight/Loot/Scout/MapBoss | 成功率 |
|---|---|---|---|---|---|
| **Feared**（Sirus, Shaper, Elder, Incarnation of Dread, Synthete Nightmare） | 16 | **36** | **2.25（1.58–3.11）** | 25 / 6 / 5 / 0 | 69% |
| Twisted | 7 | 10 | 1.43（0.69–2.63） | 0 / 2 / 4 / 4 | 71% |
| Elderslayers | 14 | 18 | 1.29（0.76–2.03） | 8 / 8 / 2 / 0 | 93% |
| Formed | 18 | 18 | 1.00（0.59–1.58） | 4 / 5 / 9 / 0 | 94% |
| Remembered | 12 | 12 | 1.00（0.52–1.75） | 5 / 2 / 2 / 3 | 92% |
| Forgotten | 11 | 4 | 0.36（0.10–0.93） | 3 / 1 / 0 / 0 | 100% |
| 未発見（Scout で 3 回死亡） | 1 | 3 | — | 0 / 0 / 3 / 0 | 0% |

- Feared はマップ数の 20% で、死亡の 36%（36/101）を占める。二項検定で p = 0.0002 と明確に高い。
- Fight での死亡 45 件のうち 25 件が Feared だった。
- ボス群と期間を入れた回帰では、Feared の率比は 2.31 [1.21–4.43]。
- 観測したボス HP: Sirus 1.19 億、Shaper 9,500 万、Dread 8,860 万、Nightmare 6,160 万、Elder 4,710 万、Remembered 各 4,960 万、Elderslayers 各 3,290 万。

### 2.5 死亡直前の ES 推移（23 件）【事実】

- **最大 ES**:
  - 10,131（ミニオン由来の `player_aura_energy_shield` がない時）
  - 11,490（ある時。このバフの有無は 99% 対 1% の割合できれいに分かれた）
  - 09-21 には最大 12,010 も観測
- **ライフ**: 432 で一定。
  - 戦闘中のフラスコ（`fight.es_retreat`）が記録したプール最大値は 13,385。ここから最大ライフは約 1,895、そのうち約 77% が予約済みと推定できる【推定】。
- **ES が満タン付近（85% 以上）から 0 になるまでの時間**（サンプル間隔は約 0.27〜0.4 秒）:

| 所要時間 | 件数 | 例 |
|---|---|---|
| 0.5 秒以下（ほぼ 1 発） | 4 | 10,770 → 0（Elderslayers 戦） |
| 0.5〜1.0 秒 | 7 | 10,118 → 5,468 → 1,028 → 0（Scout・Bandit 系パック） |
| 1.0〜2.0 秒 | 8 | 11,471 → 10,110 → 8,292 → 6,411 → 2,945 → 0（MapBoss 中の Living Coral） |
| 2.0 秒超 | 4 | 10,323 → 4,962 → 2,542 → 173 → 0（Scout・Void Skulker など） |
| **中央値 1.02 秒** | 計 23 | |

- **例外 1 件**（09-23 03:37:39、Feared マップの Loot 中、周囲に敵なし）:
  - ES 11,490 → 11,327 → 11,272 → **9,606 のままライフが 432 → 325 → 289 → 0**。ES を貫通するカオスダメージによる死亡である。
  - 直前 12 秒間、Coruscating Elixir のバフ（`unique_flask_chaos_damage_damages_es`）はなかった。
- **ES の回復速度**（ES 9,500 未満から増えている区間）:
  - リーチなし: 中央値 1,540 ES/秒
  - ES リーチ中: 中央値 3,770 ES/秒
  - つまり 4〜7 秒あれば満タンに戻る。問題は回復量ではなく「1〜3 秒以内の総被ダメ」である。
- **バースト**（`safety.burst_context`）: 18 件中 11 件は生存、7 件は 5 秒以内に死亡した。
  - 死亡 7 件のうち 2 件は、直前 10 秒以内に 1 回目のバーストを受けた後の 2 回目で死んでいた。

### 2.6 死亡・バースト時の周囲と直前の敵スキル【事実】

**09-23 の死亡 13 件**（近くの敵、直近 3 秒の脅威、`boss.cast` / `threat.dodge` から分類。重複あり）

| 分類 | 件数 | 具体例 |
|---|---|---|
| Awakening のボス | 4 | Sirus（`EmptyActionSpellOrionMultiBeam2` の 0.1 秒後に死亡、`AtlasExileOrionBeamBomb`）、Elderslayers（Baran `AtlasExilesCrusaderSlam` / `CrusaderRuneDetonation`、Veritania `IceShardProjectileBarrage`、Al-Hezmin） |
| Exarch 系の通常モンスター（Cleansing / BlackStar 系メタデータ） | 4 | Molten Golem、Molten Wretch、Fetid Shambler（CleansingFire）、Void Skulker、Void Construct |
| Bestiary の赤ビースト | 4 | **Black Mórrigan**（`GAAzmeriGullGoliathGroundSlam`。04:01:36 に Bot が 665c の獲物として追跡を開始し、04:01:40 に死亡）、Living Coral（`GADeepwaterCoralGolemGroundSlam`）、Craicic Watcher |
| Bandit 系のパック（出所は未確定） | 3 | Oak's Devoted（`WarlordsMarkMaul`）、Boar / Bear Acolyte |
| Elder-Blessed 系 / Null Portal | 2 | `TentaclePortalSummonHumanoids`（人型を召喚） |
| Kitava パック（MAP MOD） | 1 | `KitavaDemonLeapSlam` / `Cleave`（Impale 4〜5 スタック中） |
| 敵を特定できず | 3 | うち 1 件は前述のカオス貫通 |

- **09-19〜21 の死亡 11 件**（Verbose ログ）: Harvest 系のカニ（`HarvestCrabDashSlam` / `AbyssSlam` / `NessaCrabScreech`）で 2 件、Living Coral で 1 件が加わる。
  - 合わせると **死亡 24 件中 7 件で、Bestiary の赤ビースト由来のスキルか個体が死亡直前にいた**【事実】。
  - Harvest 系は `Beasts_settings.json` に Harvest 由来の赤ビーストが載っていることから、Bestiary 経由の出現と推定した。
- Exarch 系のモンスター（Molten・Void）が Exarch 影響の選択によって出ていることは【推定】。メタデータ名（Cleansing / BlackStar）と、公式の「Exarch の配下は Black Star」という説明が根拠。

### 2.7 防御層の稼働率（生存中のサンプル 1,295 件）【事実】

| バフ | 全体 | Scout | Fight | Loot | MapBoss | 死亡前 5 秒（13 件平均） |
|---|---|---|---|---|---|---|
| Coruscating Elixir（カオスが ES を貫通しない） | **74%** | 75% | 90% | **69%** | 100% | 76% |
| ミニオン由来の ES オーラ（+1,359 ES） | 76% | **50%** | 76% | 92% | 81% | **60%** |
| Fortification（10 スタック、Kingmaker の Guardian 由来） | 61% | **41%** | 66% | 72% | 62% | **45%** |
| Steelskin（`quick_guard`） | 43% | 30% | 57% | 47% | 54% | 39% |
| Elusive（Withering Step） | 38% | 17% | 29% | 49% | 40% | 33% |
| ES リーチ（Bismuth） | 14% | 22% | 50% | 4% | 20% | 33% |
| Arcane Surge | 47% | 61% | 72% | 38% | 58% | — |
| Sand Stance、Tukohama（静止時）、Onslaught、Frenzy | 100% | | | | | |
| MAP の呪い（Vulnerability 35%、Enfeeble 13%、Temporal Chains 20%）、Impale 11%、Poison 5% | | | | | | |

- 解釈【推定】: 防御の一部（ES +13%、Fortify）はミニオンに依存している。Bot が高速で先行する Scout ではそれが半分程度しか効いておらず、死亡直前はさらに低い。

### 2.8 放棄ルールの検証【事実＋推定】

- 3 回死亡に達した 11 マップのうち、**5 マップはその後に成功**していた（放棄ルール導入前）。
- 3 回目の死亡以降に積み上がった収益は +1,040c（当時価格）、追加稼働は 1,537 秒（物流を除く）。**限界収益は約 2,400c/稼働時** で、ベースライン（約 1,600c）を上回る。
- ただし n = 11 と小さく、Timeout の外れ値（653 秒）を含む。これを除くと約 3,900c/稼働時。
- 上限を変えた場合のモデル（λ = 1.48）:

| 上限 | 放棄率 | chaos/稼働時 | 変化 |
|---|---|---|---|
| 3 回 | 18.6% | 1,599c | 基準 |
| 4 回 | 6.3% | 1,903c | +19% |
| 5 回 | 1.8% | 2,015c | +26% |

### 2.9 MAP MOD と死亡率（81 マップ、基準 1.25 死/マップ）【事実】

「調整 RR」はボス群と期間を共変量にした Poisson 回帰（過分散補正）の率比。「ridge RR」は全 MOD を同時に入れて縮小推定したもので、偶然の過大評価を抑えた値である。

| MOD（代表テキスト） | n | 死/マップ | 95%CI | 無し | 粗 RR | 調整 RR [95%CI] | p（並べ替え） | q（BH 補正） | ridge RR |
|---|---|---|---|---|---|---|---|---|---|
| **MonsterFast**（28% increased Monster Movement Speed … Fleet） | 30 | 1.83 | 1.38–2.39 | 0.90 | 2.03 | **1.88 [1.21–2.92]** | 0.002 | 0.10 | **1.34** |
| **MonsterMultipleProjectiles**（2 additional Projectiles） | 24 | 1.88 | 1.37–2.51 | 0.98 | 1.91 | **1.77 [1.09–2.86]** | 0.007 | 0.16 | 1.20 |
| **PlayerCurseEnfeeblement**（Players are Cursed with Enfeeble） | 17 | 2.00 | 1.39–2.79 | 1.05 | 1.91 | **1.77 [1.10–2.86]** | 0.014 | 0.16 | 1.27 |
| PlayerElementalEquilibrium（Players cannot inflict Exposure） | 10 | 1.90 | 1.14–2.97 | 1.15 | 1.65 | 3.12 [1.40–6.96] | 0.116 | 0.49 | 1.27 |
| MonsterDamage（22% increased Monster Damage） | 27 | 1.67 | 1.22–2.23 | 1.04 | 1.61 | 1.56 [0.98–2.49] | 0.068 | 0.36 | 1.21 |
| MonsterFireDamage（extra Physical as Fire） | 11 | 1.73 | 1.04–2.70 | 1.17 | 1.47 | 1.96 [1.04–3.67] | 0.261 | 0.83 | 1.24 |
| MonsterCannotBeStunned（28% more Monster Life） | 12 | 1.50 | 0.89–2.37 | 1.20 | 1.25 | 1.92 [1.03–3.59] | 0.514 | 0.95 | 1.28 |
| PlayersReducedAuraEffect（60% reduced effect of Non-Curse Auras） | 18 | 1.56 | 1.03–2.25 | 1.16 | 1.34 | 1.39 [0.81–2.40] | 0.307 | 0.87 | 1.20 |
| MonsterAreaOfEffect（100% increased AoE） | 6 | 2.50 | 1.40–4.12 | 1.15 | 2.18 | 1.65 [0.83–3.26] | 0.033 | 0.24 | 1.13 |
| MonsterPacksRanged（ranged monsters） | 4 | 2.50 | 1.20–4.60 | 1.18 | 2.12 | 2.45 [1.13–5.30] | 0.069 | 0.36 | — |
| MonstersStealChargesOnHit | 4 | 2.25 | 1.03–4.27 | 1.19 | 1.88 | 1.66 [0.72–3.79] | 0.134 | 0.53 | — |
| PlayersBlockAndArmour（30% less Armour） | 57 | 1.28 | 1.00–1.61 | 1.17 | 1.10 | 0.91 [0.53–1.57] | 0.790 | 1.00 | 0.99 |
| MonstersImpaleOnHit | 19 | 1.00 | 0.60–1.56 | 1.32 | 0.76 | 0.75 [0.40–1.41] | 0.405 | 0.87 | 0.96 |
| MonsterCriticalStrikesAndDamage | 33 | 0.82 | 0.54–1.19 | 1.54 | 0.53 | 0.59 [0.35–0.99] | 0.024 | 0.21 | 0.78 |
| TwoBosses（Twinned） | 25 | 0.92 | 0.58–1.38 | 1.39 | 0.66 | 0.48 [0.29–0.82] | 0.172 | 0.63 | 0.78 |
| Hexproof | 17 | 0.71 | 0.36–1.23 | 1.39 | 0.51 | 0.57 [0.28–1.15] | 0.078 | 0.36 | 0.82 |
| Poisoning | 25 | 0.68 | 0.40–1.09 | 1.50 | 0.45 | 0.55 [0.28–1.09] | 0.015 | 0.16 | 0.86 |
| PlayerCurseTemporalChains | 11 | 0.55 | 0.20–1.19 | 1.36 | 0.40 | 0.46 [0.18–1.19] | 0.076 | 0.36 | 0.83 |
| BossPossessed | 15 | 0.47 | 0.19–0.96 | 1.42 | 0.33 | 0.46 [0.19–1.11] | 0.012 | 0.16 | 0.87 |

**統計的な確からしさの評価**

- 51 MOD を同時に比べると、BH 補正後に q < 0.10 となるものは **無い**。最小は Monster Fast の q = 0.10。
- 並べ替え検定で p < 0.05 になったのは 7 個で、偶然だけなら期待値は約 2.5 個。全体として何らかの信号はあるが、どの MOD が効いているかの特定は不確実である。
- 「危険側」で 3 手法すべてが一致するのは Fast・Multiple Projectiles・Enfeeble の 3 つ。いずれも仕組みで説明できる。
  - Fast: 攻撃・詠唱速度が約 40% 上がる。
  - Multiple Projectiles: Sirus・Veritania・Cardinal などボスの投射物にも効くと考えられる。
  - Enfeeble: プレイヤーの与ダメが下がり、戦闘が長引く。
- Elemental Equilibrium（Exposure 不可）は調整 RR が大きいが、仕組みが弱く n = 10 なので保留とする。
- 「安全側」の Crit・Twinned・Temporal Chains・Possessed には、プレイヤーを守る仕組みが見当たらない。偶然や期間の交絡の可能性が高いので、**NG 解除や優先購入の根拠にはしない**。
- 今回の効果量（RR 約 1.8）を 80% の検出力で確かめるには、MOD あり／なしで各 100 マップ程度が必要になる（過分散を考慮）。40 マップの比較で判別できるのは、半減程度の大きな差に限られる。

---

## 3. ビルドの現状（公式 API、2026-09-23 取得）

**取得方法**

- `character-window/get-items` と `get-passive-skills`（公開プロフィール）を WebFetch で要約取得した。そのため細かい MOD 値に抜けがある可能性がある。
- パッシブは API がハッシュ値のみを返すため、下表の主要ノードだけを poedb の PassiveSkillsHash と照合した。残り約 90 ノードの名前とマスタリー（5356 / 50906 / 31556）は未解決なので、**PoB へのインポートで確認**してほしい。
- poe.ninja の builds / character API は公式ドキュメントで「第三者の利用対象外」とされているため、使っていない。

### 3.1 装備

| 部位 | 現状 | 要点 |
|---|---|---|
| 武器 | Jiquani's Potential（6L、Q20） | スペル +1、Int 10 ごとに雷 1–36、**Blood Magic**、スキルコスト +31%、**スペルブロック +25%**（暗黙） |
| 胴 | Twilight Regalia（Crusader、ES 1,209） | ソケット中の Int ジェム +1、Int+54、ES +158%、クラフト ES +74 |
| 兜 | Lich's Circlet（Crusader / Hunter、ES 505、アビス 1） | Int+60、ES +98%、ライフ +32 |
| 手 | Arcanist Gloves（Shaper / Elder、Duplicated・Corrupted） | ソケット +1、Duration +2、Projectile +2、Lv25 Faster Projectiles / Faster Casting 付与、スペルのクリ倍 +90% |
| 足 | Warlock Boots（Hunter、ES 204） | Int+58、移動速度 27%、Tailwind |
| 首 | Simplex Amulet（Shaper / Crusader） | 属性値 19%、Int 26%、Int 15 ごとにダメージ 2%。塗油は **Tranquility**（最大 ES 5%、ES% 増加がスペルダメージに 30% 反映） |
| 指 | Pearl of Tsoatha | 全耐性 +42%、手袋のスキルに Elemental Focus と Increased Critical Damage を付与 |
| 指 | Helical Ring（Fractured・Duplicated） | Int+120、Str+120、クリ倍 +48%、最低 Frenzy +1 |
| 帯 | **Mageblood** | 左から 4 つの魔法ユーティリティフラスコを常時適用 |
| フラスコ | ① Quicksilver ② **Bismuth**（全属性耐性 +35%、スペルダメージの 0.7% を ES としてリーチ）③ **Coruscating Elixir**（エンチャント「効果終了時に再使用」）④ Sulphur（ダメージ 40%、クリ率 55%）⑤ Silver（Onslaught、ライフ再生 3%/秒） | 魔法フラスコには効果 +25%（Alchemist's）と +70%（エンチャント）が付いている |

- 持ち替え用: 部族の棍棒と Yew Spirit Shield。Shield Charge、GMP、Faster Projectiles、Bloodsoaked Banner、Enlighten。

### 3.2 ジェム

| 部位 | リンク | 備考 |
|---|---|---|
| 手（4L） | Spark of the Nova＋Pinpoint＋Greater Spell Echo＋Arcane Surge | 手袋と指から Faster Projectiles、Faster Casting、Elemental Focus、Increased Critical Damage が付く |
| 武器（6L） | **Automation＋Steelskin＋Withering Step＋Convocation＋More Duration＋Cooldown Recovery** | Steelskin は被弾の 70% を肩代わりし、最大 2,403 |
| 胴（6L） | Raise Spectre＋Animate Guardian＋Elemental Army＋Empower＋Minion Life＋Meat Shield | |
| 兜 | Zealotry＋Eternal Blessing（予約なし）、Frostblink of Wintry Blast | |
| 足 | Herald of Thunder＋Enlighten、Flesh and Stone、Purity of Elements | |

- 観測されたミニオン: Carnage Chieftain（Frenzy / Onslaught）、Perfect Spirit of Fortune（雷ダメージが Lucky）、Perfect Judgemental Spirit、Animated Guardian（Kingmaker 系のオーラで Fortify、クリ倍、Rarity、Culling）。
- **Spectral Tiger Companion** も観測された。API の `alternate_ascendancy = 11` と合わせて、Farrul Bloodline 由来と推定する。同型の Inquisitor（pobarchives、3.27）が Farrul Bloodline を採用している。

### 3.3 パッシブ（ハッシュ照合済みのもの）【事実】

- **取得済み**:
  - キーストーン: **Pain Attunement**（31703）、**Eternal Youth**（21650: ES リチャージがライフに適用され、ライフ再生 50% less）、**Iron Will**（50288）
  - Inquisitor: **Righteous Providence**（53884）、**Inevitable Judgement**（48214）、**Sanctuary**（39790）
- **未取得**: Pious Path（32816）、Augury of Penitence（40059）、Instruments of Zeal（13851）/ Justice（3154）/ Virtue（19417）、Elemental Overload（22088）、Whirling Barrier（42917）
- ジュエル:
  - Watcher's Eye: PoE 中の最大耐性 +1%、Zealotry 中の詠唱速度 15%、Zealotry 中のクリティカル時の属性耐性貫通 9%
  - Forbidden Flame / Flesh（Arcane Blessing: スペルのヒットで Arcane Surge を得て、スペルダメージ 20% more）
  - その他: Emperor's Mastery、Bound By Destiny（Crusader 2 部位で Int +12%）、Split Personality ×2、Large Cluster ×3、雷／クリ倍の Cobalt Jewel ×6
- Arcane Surge Support は「マナ消費」が発動条件なので、Blood Magic 下では自力では発動しない。実際の Arcane Surge（稼働率 47%）は Arcane Blessing 由来と推定され、サポートの「Arcane Surge 中 35% more」は有効に働いている。

### 3.4 防御の構造上の弱点（まとめ）

1. **物理の大技を受ける層が薄い**【推定】
   - アーマーは実質 0。物理軽減は Tukohama（静止 3 秒で最大 9%）、Fortify（ミニオンが近い時だけ）、Sand Stance のみ。
   - 死因上位の Baran のスラム、Kitava のクリーブ＋Impale、赤ビーストの GroundSlam、Bandit のモールはいずれも近接・物理主体と推定される。
2. **カオス貫通の穴**【事実】: Coruscating が 26% の時間止まっている。その間はライフ 432 がカオスを直接受ける。
3. **ミニオン依存の防御**【事実】: ES +13% と Fortify が、先行しがちな Scout で半分程度しか効いていない。
4. **スペルブロックは武器の 25% が主体**（ツリー分は不明）。最大耐性は Watcher's Eye で 76%、Coruscating 中は火 +5% と推定される。

---

## 4. 改善案の詳細

### 4.1 周回ルール（Bot 設定）

1. **放棄上限を 3 回から 4〜5 回に**（上位案 #1）
   - 条件付きにする。
     - 「ボスが 1 体以上生存」または「未回収の高額ドロップがある」: 5 回まで再入場
     - 「3 回死んでもボスを 1 体も見つけていない」: 現状どおり放棄
   - 09-23 の 1b143a は後者のケース（Scout で 3 回死亡）。
   - 経験値ロスは許容できる前提。Lv95 なので最優先ではない。
2. **MapBoss（Exarch 招待状）**
   - 死亡 7 件（7%）に対して、得た招待状は 2 枚（195c、約 4.8c/マップ）。
   - 死因は MapBoss 本体ではなく、Kitava パックと赤ビースト（Living Coral）だった。
   - 現行の「2 回死亡で中止」を「1 回死亡で中止」に強め、さらに Impale・Kitava パック・Fast の MOD があるマップでは挑まない。期待値への影響は小さい。
3. **Feared 用のプロトコル**【仮説】
   - 死亡の 36% が Feared に集中している。
   - Feared と判明したら、ミニオンの合流と ES 満タンを待ってから交戦する。
   - Sirus の `OrionMultiBeam` / `BeamBomb` 予兆で必ず退避する。
   - 最長射程を維持し、ボスとの距離を 45 以上に保つ既存ロジックを優先する。

### 4.2 生存行動（Bot。上位案 #2）

| 施策 | 根拠【事実】 | 仕様案 |
|---|---|---|
| **Coruscating の常時維持** | 稼働率 74%（Loot 69%）。ES 9,606 を残したカオス死が 1 件 | マップ内でバフが切れていて、チャージがあればフラスコキーを押す。Fight 中は既存の自動再使用に任せ、Scout・Loot・入場直後に限定する。`build.flasksEnabled = false` のため現状は押していない |
| **ミニオンを連れて進む** | ES オーラと Fortify の稼働率が、死亡前 5 秒で 60% / 45%（平常 76% / 61%）、Scout で 50% / 41% | パック接敵前とボス発見時に 1〜2 秒止まり、`player_aura_energy_shield` と `fortification` のバフを確認してから進む。Convocation（自動）の直後に交戦する |
| **連続バースト時の後退** | バースト 18 件中 7 件が 5 秒以内に死亡。うち 2 件は 2 回目のバーストで死亡。ES の自然回復は 1,540/秒 | 1 回目のバースト（1 秒以内にプールの 30% 以上を失う）の後は、ES 90% に戻るまで敵集団から離れて待つ。現行のバースト検知は、ES が既に 1,302〜6,487 まで減ってから記録されており遅い |
| **赤ビースト狩りの制限** | 24 件中 7 件の死亡でビースト由来の脅威が存在。Black Mórrigan の追跡（04:01:36）から 4 秒後に死亡し、そのマップは放棄（−108c） | 狩る対象を 350c 以上（Black Mórrigan、Fenumal Plagued Arachnid、Craicic Croaker）に絞る。ES 満タンかつミニオン合流時のみ接近し、1 回でもバーストを受けたら中止する。Fight・Loot・MapBoss 中は狩らない。**捕獲結果（種類・価格）を記録し、価値を実測する**（現状は収益に計上されていない） |
| **危険スキルの回避リスト** | 死亡直前 3〜6 秒に出ていたスキル ID | `AtlasExileOrionMultiBeam*`、`AtlasExileOrionBeamBomb`、`AtlasExileOrionDeathBarrage`、`AtlasExilesCrusaderSlam`、`CrusaderRuneDetonation`、`EyrieIceShardProjectileBarrage`、`KitavaDemonLeapSlam` / `Cleave`、`GAAzmeriGullGoliathGroundSlam`、`GADeepwaterCoralGolemGroundSlam`、`HarvestCrab*Slam`、`TentaclePortalSummonHumanoids`（Null Portal は最優先で撃破するか回避）、`ArchnemesisFireVolatileSuicideExplosion` |
| **Loot の安全確認** | Loot での死亡が 24%。カオス貫通の 1 件も Loot 中 | Loot 開始前に「半径 40 以内の敵が 0」「床ハザードから 22 以上離れている」「Coruscating が有効」を確認する |

- 効果の見積もり【仮説】: 死亡 −20〜35%。chaos/稼働時はモデルで +14〜24%。

### 4.3 マップ選択・NG MOD（上位案 #3）

**現状**

- 市場の T16（IIQ 115 以上、パック 40 以上）は 7〜11c で、10c に厚い板がある（`market.results`）。
- 1 回で 8 枚程度を購入している。NOT フィルタの入力は初回だけ時間がかかる（1 ルール約 15 秒）。

**NG 候補の組み合わせ評価**（λ は「その MOD を持たないマップ」の実測死亡率と ridge 推定。chaos/時はベースライン 1,599c 比）

| NG の組み合わせ | 除外率 | 残り n | λ 実測（95%CI） | λ ridge | chaos/時（+3c/枚） | chaos/時（+10c/枚） |
|---|---|---|---|---|---|---|
| Fast | 37% | 51 | 0.90（0.61–1.24） | 1.11 | +5〜16% | −1〜+10% |
| **Fast＋Multiple Projectiles** | 59% | 33 | **0.58（0.33–0.85）** | 1.05 | **+8〜31%** | **+2〜24%** |
| Fast＋MP＋Enfeeble | 63% | 30 | 0.57（0.33–0.83） | 0.99 | +12〜32% | +5〜25% |
| Fast＋MP＋Enfeeble＋Monster Damage | 77% | 19 | 0.37（0.16–0.58） | 0.92 | +15〜39% | +9〜32% |
| Enfeeble のみ | 21% | 64 | 1.05（0.77–1.38） | 1.18 | +1〜8% | −5〜+2% |

**推奨**

- まず **Fast と Multiple Projectiles の 2 つ** を NOT フィルタに加える。
  - 除外率は 59% になる。10c の板が厚いので、価格上昇は +3〜10c/枚程度と見込む【推定】。
  - 悲観側の縮小推定でも損益はほぼゼロ〜+8%、実測どおりなら +24〜31%。下振れが小さく、元に戻すのも容易。
- 評価は「NG あり 50 マップ」対「直前 50 マップ」の死亡率で行う。**同時に他の施策を入れると効果を分けられない**ので、順番に導入する。
- 監視を続けるもの（今は NG にしない）: Enfeeble、Elemental Equilibrium、Monster Damage、Reduced Aura Effect（Sand Stance や PoE の効果を下げる）。
  - Enfeeble は Watcher's Eye や Sanctuary の呪い軽減と重なるので、ビルド側の対策と比較して決める。
- **注意**:
  - Fast は IIQ +19% を持つ MOD なので、除外すると IIQ 115 以上の在庫が減る。
  - 購入条件（`mapMinQuantity 115`）を満たす件数と価格の分布を毎回ログに残し、価格影響を実測する。
  - Sacrifice at Midnight は IIQ +5%（poedb）。他の 3 種も IIQ を加えると推定されるので、IIQ を補う手段は残る。

### 4.4 装備（部位ごとの候補。数値はすべて PoB で確認すること）

| 部位 | 現状 | 候補 | 狙い | 優先度 |
|---|---|---|---|---|
| 胴 | Crusader の Twilight Regalia（ES 1,209） | **非インフルエンスの Twilight Regalia に Eldritch 暗黙を付ける**。Eater 側: スペルブロック +5〜10%（ユニーク戦時 8〜12%、ピナクル戦時 11〜14%）、最大 ES +6〜17%（最大 28〜29%）、Int 180〜230 ごとに被ダメ 1% less。Exarch 側: 全最大耐性 +1〜2%（ピナクル戦時 +4〜5%）。明示 MOD は ES% とフラット ES、Int、「ソケット中の Int ジェム +1」を維持 | スペルの大技（Sirus・Veritania・Cardinal）を確率で無効化し、属性の最大被ダメを底上げする | 高。Bound By Destiny の Crusader 2 部位条件は、兜と首で満たせる |
| 兜 | Lich's Circlet（ES 505） | ES% とフラット ES が上位の個体。Shaper 系の「全最大耐性 +1%」や ES% の上位 Tier | ES の総量を増やす | 中 |
| 足 | Warlock Boots（ES 204） | ES 250 以上で、移動速度・Tailwind・Onslaught を維持 | ES の総量を増やす | 中 |
| 首 | Simplex（塗油 Tranquility） | 塗油を **Whirling Barrier**（杖で攻撃・スペルブロック各 +8%）に替える案。ツリーで取る場合は不要 | ブロック。代わりに Tranquility の ES 5% とスペルダメージを失う | 中（PoB で比較） |
| 武器・手・指・帯 | 火力の中核 | **変更しない** | 火力の維持。Feared の戦闘時間を延ばさない | — |
| ジュエル | Cobalt Jewel ×6（雷／クリ倍） | 火力への寄与が最も低い 1〜2 枚を「最大 ES% と Int」、またはアビスのフラット ES に替える | ES の総量 | 低〜中 |
| Watcher's Eye | PoE の最大耐性 / Zealotry 2 種 | **PoE 中のカオス耐性 +30〜50%** を持つ個体は、Coruscating が切れた時の保険になる | カオス貫通対策 | 中（高価なら Bot の Coruscating 対応を優先） |

- 参考: 同系統の 3.27 Spark Inquisitor（pobarchives）は ES 19.0k、物理の最大被ダメ 36.5k、スペルブロック 25%。旧リーグの上位装備の例として示す。

### 4.5 ジェム

- **Steelskin を Immortal Call に替える**（上位案 #4 の中核）【仮説】
  - Immortal Call はガードスキルで Automation に入れられる。poedb（3.29）の Lv20 性能: クールダウン 3 秒、効果時間 1 秒、物理 35% less、属性 34% less。
  - Steelskin は 2,403 の吸収で頭打ちになる。10k の一撃なら 24% 相当で、連続被弾ではすぐ使い切る。
  - Immortal Call は効果中のすべての被弾を割合で減らすので、1〜3 秒の連続被弾で死ぬ現状の型に合う。
  - 稼働率は More Duration と Cooldown Recovery 込みで 40% 前後と推定され、Steelskin の実測 43% と同程度【推定】。
  - ガードスキル同士はクールダウンを共有するので、置き換えになる。Blood Magic 下でのライフコストは約 70/回と推定。
- Arcane Surge Support は維持する（§3.3）。Fight 中の Arcane Surge 稼働率は 72%。
- ミニオンの構成（Kingmaker Guardian、Spirit 系 Spectre、Carnage Chieftain）は **防御にも効いている**（ES +13%、Fortify）ので変えない。Meat Shield（Defensive）も維持する。

### 4.6 パッシブ・アセンダンシー・パンテオン

| 候補 | 内容（poedb） | 評価 |
|---|---|---|
| **Whirling Barrier**（42917） | 杖で攻撃ブロック +8%、スペルブロック +8%、ブロック時に 20% の確率で Power Charge | 取得候補。塗油でも取れる。経路の長さと失う火力を PoB で比較する |
| Pious Path（32816） | 自分の Consecrated Ground 上でライフ再生が ES も回復し、4 秒残留 | ES の回復は既に 1,540/秒あるため優先度は低い |
| Augury of Penitence（40059） | 近くの敵の属性ダメージ 8% less、敵の被属性ダメージ 16% increased | 属性攻撃への防御と火力を両立する。4 つ目のノートを取れるかどうか（ポイント配分）を PoB で確認 |
| パンテオン（メジャー） | Solaris: 敵 1 体の時に物理軽減 6%、20% の確率で範囲攻撃を 50% 軽減、強化でクリ追加ダメージ無効。Lunaris: 近くの敵 1 体ごとに物理軽減 1%（最大 8%）、強化で投射物回避 10% とチェインした投射物の回避 | 現在のメジャーは API で読めない。複数ボス＋雑魚が多い Awakening 戦では Lunaris、単体のスラム対策なら Solaris。**Multiple Projectiles の MAP では Lunaris の投射物回避が効く可能性あり**（PoB で比較） |
| パンテオン（マイナー） | Tukohama（静止で最大 9% 物理軽減）が稼働中（バフ 100%） | 維持 |
| カオス耐性・フラスコチャージ | ツリーのカオス耐性や Flask 系ノード・マスタリー | Coruscating の自動再使用を止めない（チャージ切れの防止）ための選択肢。現在のカオス耐性は API では不明 |
| 振り直しの原資 | 未解決の約 90 ノード | PoB でノードごとの DPS 寄与を並べ、寄与の最も低い Large Cluster の小ノードなどから 2〜6 点を回す |

### 4.7 フラスコ

- Mageblood の 4 本（Quicksilver、Bismuth、Sulphur、Silver）は **維持を推奨** する。
  - Bismuth は耐性と ES リーチを担うので必須。
  - Silver は Blood Magic のコストを支えるライフ再生 3%/秒と Onslaught を担う。
  - Sulphur を防御用に替える候補も検討したが、効果は薄い。
    - Basalt は 3.29 で「20% more Armour」なので、アーマー 0 のこのビルドには効かない（poedb）。
    - Granite（+1,500 アーマー、効果 +95% で約 2,900）でも、10k の一撃に対して約 5% しか減らない。
    - Jade（回避）は ES 型では回避率がほぼ 0% で効かない【推定】。
- Coruscating Elixir は **稼働率 74% を 100% に近づけるのが最優先**（Bot 側と §4.6 のチャージ対策）。
  - エンチャント「効果終了時に再使用」はチャージがないと止まる。Loot と Scout で切れやすい（69% / 75%）。

### 4.8 Atlas（スカラベ・ノード・マップ選択）

- **Atlas パッシブは公開 API から取得できなかった**。以下はデータと一般論に基づく確認項目である。
- **Eldritch 影響（Exarch）**【仮説・A/B 推奨】
  - Exarch 系と推定されるモンスター（Molten、Void）は、直近の死亡 13 件中 4 件の周囲にいた。MapBoss での死亡は 7 件。
  - 得られる価値は招待状 約 4.8c/マップと Eldritch 系通貨が少々（81 マップで Eldritch Chaos Orb 1 個、Grand Eldritch Ember 1 個）。
  - **影響を選ばない 40〜50 マップ** と比べて、死亡率・Scout 時間・収益を確認する。死亡率が 0.1/マップ下がるだけで、失う招待状の価値を上回る見込み【推定】。
- **Bestiary（Einhar）**
  - 赤ビーストは死亡 24 件中 7 件に関与していたが、捕獲の価値はまだ未計測。
  - 次の順で判断する。
    1. Bot 側で狩りを制限する（§4.2）
    2. 捕獲価値を記録する（`Beasts_settings.json` の価格表では 50c 以上が 11 種、上位は Black Mórrigan 580〜665c、Fenumal Plagued Arachnid 500〜548c、Craicic Croaker 367〜424c）
    3. 50 マップで「捕獲額 − 死亡の損失」がマイナスなら、Bestiary 系の Atlas ノードを外す
- **マップ**
  - Scout が 69 秒/マップで最大の時間枠になっている。特に Formed と Feared は平均 106〜112 秒。
  - 参考戦略は「全体を素早く回れるマップ」（Cemetery や、Atlas の左下・右上の地域）を推奨している（2026-08）。
  - Dunes 専用の地形・ボス位置の学習をやり直す必要があるので、上位 5 案の後に行う。Scout が半分になれば、モデルで +16% 程度と見込む。
- **スカラベ・フラグメント**
  - Horned Scarab of Awakening と Sacrifice 4 種（Midnight は IIQ +5% を確認、4 種合計 4.3c）の構成を **維持**。
  - 参考戦略が使う Trarthan Scarab of Infamy（傭兵が Infamous になり、野良傭兵が 2 体追加される）と Templar Astrolabe（Shaped Region、マップボス必須）は、Bot にとって危険と手間が増えるので非推奨。
  - Horned Scarab of Awakening と Influencing Scarab of Interference は同時に使えないというバグ報告がある（公式フォーラム、2026-03、未解決）。
- **Maven's Invitation**: 参考戦略は 4 マップごとに Invitation を実施している（36 マップで 9 回）。Bot は未対応。収益への寄与は未検証なので、上位案の後の検討事項とする。
- 3.29.0 で、マップに出る Remembered と Feared のドロップ遅延が削除された（公式パッチノート）。Loot の開始を早められる。

### 4.9 物流（上位案 #5）

- **一括購入を増やす**
  - `scarabTarget` 5 → 15〜20、`sacrificeTarget` 20 → 60、`mapBuyCount` 10 → 20。
  - Faustus（Restock）は 17〜28 秒/マップ、RestockMap は 13〜29 秒/マップかかっている。1 回の訪問で済むマップ数を 3〜4 倍にすれば、物流の固定費をその分薄められる【推定】。
- **運転資金の維持**
  - Chaos 残高が 0〜12c まで落ち、購入失敗（`purchase_not_confirmed(out_of_chaos…)`）が 04:04、04:14、04:50、04:52Z の 4 回あった。
  - 在庫（Avarice 18 個など）は売れるまで Chaos にならない。
  - **2,000〜3,000c** を常に確保する。Divine の両替と、一部を `SellAtBid` で即時売却する（現状は `PickHave` のタイムアウトで失敗している）。
- 効果: 物流が 48〜97 秒/マップから 25〜45 秒短くなれば、マップ/時と chaos/時がともに +11〜22%（モデル）。

---

## 5. 各ピナクルボス（Awakening で出る群）の危険技と効く防御層

- Horned Scarab of Awakening は「T16 以上のエリアに、ランダムな Maven's Invitation のボス群を追加する」（poedb）。
- Searing Exarch、Eater of Worlds、Maven 本人はこの周回には出ない。MapBoss フェーズは Dunes のマップボス（Exarch 招待状用）である。

| 群 | 観測したスキル ID（ログ）【事実】 | 主な被ダメの種類【推定】 | 効く層 |
|---|---|---|---|
| Feared | Sirus: `OrionMultiBeam2`（直後に死亡）、`OrionBeamBomb`、`OrionSpammableBeamBlastFinal`、`OrionDeathBarrage`。Shaper: `AtlasBossAcceleratingProjectiles`。Dread: `SSMBenevolenceBossChaosPortal`、近接。Nightmare: `CameraLensExpandingDonut` | Sirus のビームは物理 25% と属性 75%。嵐は物理・カオス・雷・火（旧リーグの攻略記事、2020〜21 年。マップ版は未確認） | 最大耐性、スペルブロック、Immortal Call、距離。カオスは Coruscating とカオス耐性 |
| Elderslayers | Baran: `CrusaderSlam`、`RuneDetonation`、`DashToTarget`。Veritania: `IceShardProjectileBarrage`、`EyrieCleave2`。Al-Hezmin: `BasiliskWyvern`。Drox: `WarlordSpawnSlamGlove` | 物理と雷のスラム、冷気の投射物 | 物理軽減（IC、Fortify、Tukohama）、スペルブロック、Multiple Projectiles の MOD 回避 |
| Twisted（Elder の守護者） | `ElderGuardianWhirlingBlades`、`CurveProjectile2`。メタデータ名は Fire（Enslaver）/ Lightning（Eradicator）/ Poison（Constrictor）/ Holy（Purifier） | 火・雷・毒（カオス）・物理 | 最大耐性、カオス（Coruscating）、IC |
| Remembered | Cardinal: `MPWFragmentOfAngerDeceleratingProjectile`、`ComboDash`。Neglected Flame: `RainOfStars`、`SpammableBeamBlast`。Deceitful God: `OffsetProjectile` | 投射物とビーム | スペルブロック、Lunaris の投射物回避、距離 |
| Formed / Forgotten | 死亡直前に特定のスキルは記録されていない | — | Formed の死亡は Scout 主体（9/18）。道中対策が効く |

- ボス以外の脅威も同じ扱いが必要である。赤ビーストのスラム、Kitava のクリーブ＋Impale、Bandit のモール、Molten Golem、Null Portal。
- 物理主体と推定されるので、有効なのは **IC、Fortify の維持（ミニオン随伴）、スラムの予兆での退避**。

---

## 6. Bot 側でできること／プレイヤーが手でやること

| 区分 | 項目 |
|---|---|
| **Bot 側（コード・設定）** | ① 放棄上限を条件付きで 5 回に。② Coruscating が切れたら使用（Scout・Loot・入場直後）。③ ミニオンの合流待ち（ES オーラと Fortify のバフを確認）。④ 1 回目のバーストで後退し、ES 90% まで待つ。⑤ 赤ビースト狩りを 350c 以上・安全条件つきに絞り、捕獲を記録。⑥ 危険スキル ID を回避リストに追加（§4.2）。⑦ Loot 前の安全確認。⑧ NOT フィルタに Fast と Multiple Projectiles を追加し、候補数と価格をログに残す。⑨ MapBoss は 1 回死亡で中止、危険 MOD では挑まない。⑩ 補充ロットの拡大、Chaos 残高の下限、即時売却の修正。⑪ 検証ログ: マップ開始時の最大 ES とバフ、捕獲したビースト、施策 ID（A/B 用） |
| **プレイヤー側** | ① PoB へのインポートと各候補の比較（§7）。② Steelskin を Immortal Call に替える（ジェムの購入と差し替え）。③ Whirling Barrier の取得、または塗油の変更、ツリーの振り直し。④ パンテオンの確認と変更。⑤ 胴の作り直し（Eldritch 化）と兜・足の更新。⑥ Watcher's Eye（カオス耐性つき）の検討。⑦ Atlas: 影響なしの試験、Bestiary ノードの見直し、将来のマップ変更。⑧ 運転資金の確保（Divine の両替）。⑨ NG MOD 追加と放棄上限の最終判断（ユーザー判断事項） |

---

## 7. PoB で確認が必要な点

1. 現在の最大被ダメ（物理・各属性・カオス）と EHP。最大 ES 10,131 / 11,490 との整合、最大ライフ（約 1,895 と推定）と予約率。
2. Steelskin と Immortal Call の比較（効果時間、クールダウン、稼働率、物理の最大被ダメ）。Automation と More Duration の効き方も含む。
3. Whirling Barrier の経路コストと、失う DPS。塗油（Tranquility → Whirling Barrier）の場合も比較する。
4. 現在のスペル／攻撃ブロック率と、ツリー上の追加候補。
5. 胴を Eldritch 化した場合の ES、Int、Tranquility（Transfiguration of Soul）経由の DPS 変化。
6. アセンダンシーの 4 つ目のノート（Augury / Pious Path）を取れるかどうか。
7. パンテオンのメジャー（Solaris / Lunaris）の比較。
8. カオス耐性の現在値と、Coruscating が切れた時にライフ 432 が耐えられるカオスの一撃。
9. 最大耐性（冷気・雷 76%、火は Coruscating 中 81%）と、Elemental Weakness を受けた時の余力。
10. Feared 戦の所要時間。Enfeeble 下での DPS 低下率。
11. 未解決の約 90 ノードとマスタリー 3 つ（5356 / 50906 / 31556）の中身。

---

## 8. 検証の進め方（施策を順番に入れる）

1. **1 つずつ導入し、各 50 マップ以上で比較する**。
   - 主指標: 死亡/マップ（Poisson 率比）、放棄率、純益/稼働時
   - 副指標: Scout / Loot / 物流の秒数/マップ
2. 推奨する順番: #1 放棄上限（確認は放棄率と純益）→ #2 生存行動（死亡率）→ #5 物流（秒/マップ）→ #3 NG MOD（死亡率と地図単価）→ #4 ビルド（死亡率と Fight 秒）。
3. 死亡率 1.48 から 30% 下がったかを 80% の検出力で判定するには、過分散込みで各群 100〜160 マップが必要になる。50 マップ時点では「半減程度の大きな効果」しか確定できない。
4. 小さな効果は、方向の一貫性（Fight / Scout 別、ボス群別）で判断する。

---

## 9. 出典一覧（取得日 2026-09-23）

**ローカルデータ**（ユーザー提供、2026-09-19〜09-23）

- `runs.jsonl`、`events.jsonl`、`checkpoint.json`、`economy.jsonl`
- `Logs/Verbose20260920.log`、`Verbose20260921.log`
- `config/global/Beasts_settings.json`（LastUpdate 2026-09-23 15:01 JST）

**公式**

- 公開キャラクター API: https://www.pathofexile.com/character-window/get-items?accountName=Dmarch%231202&character=DmarX&realm=pc と `get-passive-skills`（WebFetch で要約取得）
- 3.29.0 パッチノート（2026-07-17）: https://www.pathofexile.com/forum/view-thread/3985332
- バグ報告「Horned Scarab of Awakening と Influencing Scarab of Interference の併用不可」（2026-03-08、GGG 受領 03-13）: https://www.pathofexile.com/forum/view-thread/3916825

**価格**

- poe.ninja 公開経済 API（2026-09-23）: `/poe1/api/economy/exchange/current/overview?league=Allflame&type=Scarab|Fragment|Currency`、`/poe1/api/economy/stash/current/item/overview?league=Allflame&type=Invitation`
- 同 API ドキュメント（builds / character API は非公開・利用対象外との記載）: https://poe.ninja/docs/api

**戦略**

- wealthyexile「Awakening scarab boss rush for chisels」（ckaiba、2026-08-01、更新 08-05）: https://wealthyexile.com/strategies/7332/awakening_scarab_boss_rush_for_chisels
- wealthyexile「Awakened Boss Rush」（2026-04、**前リーグ**）: https://wealthyexile.com/strategies/6819/awakened_boss_rush
- farmofexile「High budget Strongbox + Maven chisel farm」（**3.28 Mirage**）: https://farmofexile.com/poe1/article/high-budget-strongbox-maven-chisel-farm
- poeplanner の Atlas リンク（JS 必須のため内容は未取得）: https://poeplanner.com/a/6g_p

**ゲームデータ**（poedb、現行版表示）

- Horned Scarab of Awakening: https://poedb.tw/us/Horned_Scarab_of_Awakening
- Sacrifice at Midnight（IIQ +5%）: https://poedb.tw/us/Sacrifice_at_Midnight
- Trarthan Scarab of Infamy: https://poedb.tw/us/Trarthan_Scarab_of_Infamy
- Templar Astrolabe: https://poedb.tw/us/Templar_Astrolabe
- キーストーン: Pain Attunement https://poedb.tw/us/Pain_Attunement / Eternal Youth https://poedb.tw/us/Eternal_Youth / Iron Will https://poedb.tw/us/Iron_Will / Elemental Overload https://poedb.tw/us/Elemental_Overload
- Inquisitor: https://poedb.tw/us/Inquisitor / Pious Path https://poedb.tw/us/Pious_Path / Sanctuary https://poedb.tw/us/Sanctuary / Augury of Penitence https://poedb.tw/us/Augury_of_Penitence / Inevitable Judgement https://poedb.tw/us/Inevitable_Judgement / Righteous Providence https://poedb.tw/us/Righteous_Providence / Instruments of Zeal https://poedb.tw/us/Instruments_of_Zeal / Instruments of Justice https://poedb.tw/us/Instruments_of_Justice / Instruments of Virtue https://poedb.tw/us/Instruments_of_Virtue
- ノート・ジュエル: Arcane Blessing https://poedb.tw/us/Arcane_Blessing / Whirling Barrier https://poedb.tw/us/Whirling_Barrier / Counterweight https://poedb.tw/us/Counterweight / Wind Dancer https://poedb.tw/us/Wind_Dancer / Tranquility https://poedb.tw/us/Tranquility / Watcher's Eye https://poedb.tw/us/Watchers_Eye
- ジェム: Immortal Call https://poedb.tw/us/Immortal_Call / Arcane Surge Support https://poedb.tw/us/Arcane_Surge_Support
- フラスコ: Basalt Flask https://poedb.tw/us/Basalt_Flask / Granite Flask https://poedb.tw/us/Granite_Flask
- パンテオン: Soul of Solaris https://poedb.tw/us/Soul_of_Solaris / Soul of Lunaris https://poedb.tw/us/Soul_of_Lunaris / Soul of Tukohama https://poedb.tw/us/Soul_of_Tukohama

**Eldritch 暗黙 MOD**（日付不明。数値は現行と要照合）

- Exarch: https://www.vhpg.com/poe-eldritch-mods/eldritch-mods-body_armour.html
- Eater: https://www.vhpg.com/poe-eldritch-mods/body_armour.html

**ビルド参考**（**旧リーグ。数値は参考**）

- 3.27 Inquisitor Spark / Jiquani's（Farrul Bloodline、ES 19.02k、物理の最大被ダメ 36.51k）: https://pobarchives.com/build/K8QKAiG3
- 3.27 Jiquani's Arc Inquisitor（2025-11-22）: https://odealo.com/articles/jiquanis-arc-inquisitor
- Phrecia「MasterT's Aristocrat's Potential」（3.27、Eternal Youth と Coruscating の併用）: https://mobalytics.gg/poe/builds/mastert-s-aristocrat-s-potential
- 3.29 の変更まとめ（2026-07-17。Jiquani's の雷 1–24〜35、Spark of the Nova の投射物数 6〜10）: https://www.poebuilds.net/post/curse-of-the-allflame-changes
- 3.29 の Spark ビルド一覧: https://www.poebuilds.cc/poe1?skill=Spark

**ボスの技**（**旧版の Sirus 本戦。マップ版とは異なる可能性あり**）

- http://www.vhpg.com/sirus-awakener-of-worlds/（2020-08-23）
- https://playerassist.com/best-poe-sirus-tutorial/（2021-12-24）

**Exarch の配下（Black Star）の説明**: Path of Exile 公式 X（2022-01）https://x.com/pathofexile/status/1486778200222928897

**取得できなかったもの**

- poewiki: Anubis 保護で取得不可
- maxroll: robots.txt で不可
- raw.githubusercontent.com / cdn.jsdelivr.net / poe.ninja への curl: 組織ポリシーで不可
- Atlas パッシブの公開 API: 見当たらない
