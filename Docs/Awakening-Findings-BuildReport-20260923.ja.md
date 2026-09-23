# DmarX（Inquisitor Lv95）ビルド／Atlas 改善レポート — Awakening Boss Rush の時給最大化

作成: 2026-09-23 ／ 根拠: 公式 API の装備・ジェム・ジュエル（公開プロフィール）、AutoExile の events.jsonl（直近 1 時間）、Web の既存戦略。
方針: ビルドは「推奨の叩き台」。数値効果は必ず Path of Building（PoB）で確認してから導入すること。

## 1. 現状の数字（Bot ログ、03:36〜04:41）

| 指標 | 値 | コメント |
|---|---|---|
| マップ起動 | 6 回／時 | 参考戦略は 36 マップ／時。桁が違う |
| 成功時の収益 | 平均 約 290c／マップ（最大 622c） | Avarice / Scarabs / Proliferation の Chisel が主 |
| 死亡 | 14 回（6 マップ中） | ほぼ全て **ES 満タン（約 6〜7k）から 1 秒以内に 0** の一撃死 |
| 時間配分 | 物流 49%（MarketBuy 19.5%、RestockMap 15.7%、Faustus 13.4%）、マップ内 28% | 時給のボトルネックはまず物流、次に死亡 |

→ 時給を決めているのは「1 時間に何マップ回せるか」と「死なずにボスを倒し切れるか」。装備の火力より、**被弾耐性（最大被ダメ）と物流の短縮**の優先度が高い。

## 2. ビルドの現状（公式 API から）

- **メインスキル**: Spark of the Nova（`spark_alt_x`）＋ Pinpoint ＋ Greater Spell Echo ＋ Arcane Surge。グローブの暗黙 +1/+2（投射物）と Faster Projectiles／Faster Casting の付与、Pearl of Tsoatha による Elemental Focus／Increased Critical Damage の付与。
- **武器**: Jiquani's Potential（+1 スペル、Int 10 あたり雷追加、**Blood Magic**、スキルコスト +31%、スペルブロック +25%）
- **防御**: ES 型（Lich's Circlet／Twilight Regalia／Warlock Boots）、ライフ 432、Coruscating Elixir（ライフを 1 まで減らして ES 化、カオスが ES を貫通しない）、Mageblood で Quicksilver／Bismuth（スペルダメージの ES リーチ）／Silver／Sulphur を常時維持
- **オーラ・バフ**: Zealotry（Eternal Blessing）、Purity of Elements、Herald of Thunder、Flesh and Stone、Bloodsoaked Banner、Automation による Steelskin／Withering Step／Convocation
- **ミニオン**: Raise Spectre、Animate Guardian、Elemental Army、Minion Life、Meat Shield
- **ジュエル**: Watcher's Eye（Zealotry 中の詠唱速度 他）、Forbidden Flame／Flesh（Arcane Blessing）、Emperor's Mastery、Bound By Destiny、Split Personality ×2、雷／クリ倍のレア、Large Cluster ×2

## 3. 改善案（時給インパクト順）

### A. 一撃死対策（最優先）
ログ上の死因は、Shaper のビーム、Sirus、Elderslayers（Baran のスラム／ルーン、Veritania）、The Remembered の三体、Volatile・Spirit 系の爆発。いずれも「ES 満タンからの単発」。対策は**最大被ダメ（max hit）の底上げ**が本筋。
1. **ES の総量**：現在約 6〜7k。ES% とフラット ES を両方持つ部位（兜・胴・靴）の上振れ、アビス／通常ジュエルの ES% 追加を優先。1 k 増えるだけで、ギリギリの一撃を耐えるケースが出る。
2. **スペルブロック**：スタッフに +25% がある。Templar／Inquisitor 近辺のスペルブロック系ノードで 40〜50% 台に乗るなら、一撃死の確率を直接下げられる（ツリー上の具体的な取得ルートは PoB で要確認）。
3. **最大耐性**：ピナクルボスは属性の大技が多い。最大耐性 +1〜2% は被ダメ 4〜8% 減に相当するので、取れる手段（ツリー、装備）があれば優先度が高い。
4. **フラスコ**：Mageblood の 4 枠のうち Sulphur（火力）を防御系（例：物理軽減や被ダメ軽減系）に替えられないか検討。ES リーチの Bismuth は維持。
5. **ガード**：Automation の Steelskin は吸収量が小さい。より吸収の大きいガードスキルへの置換を PoB で比較。

### B. ボス戦の短縮（死ぬ前に倒す）
- ボス戦 1 回あたりの時間が短いほど被弾回数が減る。Spark of the Nova は射程が長いので、**距離 45 以上で撃ち続けられる形**（Bot 側で実装済み）を活かせる投射物速度・持続を維持。
- 火力の伸びしろ候補：Watcher's Eye の追加 mod、Large Cluster の notable 構成、ジェムの Awakened 化（該当するものがあれば）。数値比較は PoB で。

### C. Atlas・周回戦略
参考：wealthyexile の Awakening scarab boss rush（約 36 マップ／時、純益 約 25 div／時）
- **マップ**：高速クリアの地域（例：Cemetery）を使う。現在は Bot が Dunes 専用に調整済みのため、変更は Bot 側の対応（地形／ボス出現位置の学習）とセット。
- **スカラベ／アストロラーベ**：Horned Scarab of Awakening に加えて Templar Astrolabe、Trarthan Scarab of Infamy を併用している。Bot の NG／レシピ判定に追加する余地あり。
- **Maven's Invitation**：4 マップごとに実行して利益に加えている。
- **Atlas ツリー**：スカラベ効果・Maven 系に寄せたツリー構成の確認を推奨（ノード名は現行リーグのツリーで要確認）。

### D. 物流（Bot 側で継続対応中）
- マップの Tmp 取り出し失敗 → 毎回マーケットで買い直し、という無駄が大きかった（原因：インベントリが Chaos で満杯）。Chaos 格納と Faustus 回収を修正済み。
- スカラベを 1 回の Faustus 訪問で 5 個ではなく 10〜20 個まとめ買いすれば、Faustus の時間はさらに減る（Chaos 残高次第）。

## 4. すぐ試せる順番（提案）
1. Bot の物流修正の効果を確認（マップ／時が 6 → 10 以上に上がるか）
2. PoB でスペルブロック／最大耐性／ES 増の候補を比較し、一番安いものから導入
3. Templar Astrolabe の併用と Maven's Invitation の定期実行を Bot に組み込むか判断
