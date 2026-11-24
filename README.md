# SMC-AD Tank Rigging for Unity

> [!WARNING]
> **α版・試作品です。中身は実験的なものです。**
> - [SMC-AD](https://github.com/KULALENUMIO/SMC-AD) と組み合わせて使うことだけを前提に作っています。それ以外の使い方（他のツールで作ったモデル、別のレンダーパイプライン、他の操作系との併用など）は今のところ考慮していません
> - 作者の気まぐれで作っています。まともな更新やサポートは期待しないでください
> - 不具合があっても直すとは限りません。使い方や中身・データの形式は予告なく変わります（バージョン間の互換性もありません）
> - 正式版にするときは、このリポジトリを削除するか、別のリポジトリに置き換えるかもしれません
>
> **Alpha / prototype.** Built only to be used together with [SMC-AD](https://github.com/KULALENUMIO/SMC-AD); other workflows are
> not considered for now. Updates happen on a whim — please don't expect regular updates, fixes or support. Things may change
> without notice, and this repository may be deleted or replaced by another one if a proper release is ever made.

**SMC-AD Tank Rigging for Unity** は、SMC-AD で Unity に取り込んだ戦車を、**WASD とマウスで操縦できる戦車**にする Unity エディタ拡張です。
組み立てウィンドウで車体・砲塔・砲座・砲身を選び、履帯（6 輪 / 7 輪、リンク 5 種）を左右対称に置いて、ワンクリックでリギングします。

- 車体だけ Rigidbody の物理演算 + 転輪ごとのトーションバー。履帯は描画だけで地形に沿います
- War Thunder 風の三人称カメラ、主砲（反動・砲身の後退）、排気煙、ライト
- 機銃（同軸・砲塔・車体をいくつでも。発射レートや弾数などの性能を調整可）と副砲塔（砲塔・砲架・砲身はどれもなくて可）
- 三面図（正面・側面・上面）と、Scrap Mechanic のブロック単位のスナップで、履帯や機銃の位置を合わせられます

> 非公式のツールです。Scrap Mechanic は Axolot Games の商標で、このプロジェクトは Axolot Games とは関係ありません。
> このリポジトリにゲームのデータは入っていません。

## SMC-AD との関係

```
Scrap Mechanic のブループリント
  → SMC-AD（変換ツール + Blender 拡張）で Blender に取り込み・整理 → Unity 向けに書き出し
  → SMC-AD の Unity パッケージ（com.smcad.unity）で Unity に取り込み
  → このプラグインで 履帯の組み合わせ・リギング・操縦
```

- 部位の自動検出は、SMC-AD の Unity パッケージが付ける `SMBody` / `SMJoint`（回転軸の情報）を読みます。SMC-AD のパッケージがなくてもコンパイルはできますが（リフレクションで読むだけで、コードには依存しません）、その場合は部位と回転軸を手で指定・推定することになります
- 確認しているのは、SMC-AD v0.6.0-alpha + `com.smcad.unity` 0.3.0 で取り込んだ戦車だけです

## 構成

| フォルダ / ファイル | 内容 | ライセンス |
|---|---|---|
| `com.smcad.tankrig/` | Unity パッケージ本体（使い方は [com.smcad.tankrig/README.md](com.smcad.tankrig/README.md)） | MIT |
| `com.smcad.tankrig/Tracks~/` | 同梱の転輪・履帯 FBX（6 輪 / 7 輪、リンク 5 種）。Unity が自動でプロジェクトにコピーします | MIT |
| `Blender/TankTrack_Models.blend` | 同梱モデルの元データ（Blender 5.3） | MIT |

- 著作権: Copyright (c) 2026 SMC-AD Project（[LICENSE](LICENSE)）。改変・再配布・商用利用も、MIT の条件（著作権表示とライセンス文を残す）の範囲で自由です
- 同梱の転輪は、Scrap Mechanic の部品の形を参考にこのプロジェクトで作り直したものです（ゲームのメッシュ・テクスチャは含みません）

## 必要なもの

- Unity 6（`6000.0` 以降。確認は **6000.3 / URP**）
- Input System パッケージ（依存として自動で入ります）。Player Settings の **Active Input Handling** が「Input System Package (New)」か「Both」になっていること
- 戦車モデル: [SMC-AD](https://github.com/KULALENUMIO/SMC-AD) で取り込んだもの（`com.smcad.unity` 導入済みのプロジェクト）

## 入れ方

Unity の Package Manager > + > **Add package from git URL…** に次を入力します。

```
https://github.com/KULALENUMIO/SMC-AD-TankRigging.git?path=/com.smcad.tankrig
```

その後 `Tools > SMC-AD > 戦車（Tank Rigging） > 戦車組み立てウィンドウ` を開きます（使い方はパッケージの README）。

## 導入するときの注意

- **先にプロジェクトのバックアップか、バージョン管理（Git など）でのコミットを取ってください。** α版なので、シーンやアセットを壊す不具合が残っている可能性があります
- Input System を初めて有効にすると、Unity の再起動を求められます。旧 Input Manager だけを使っているプロジェクトでは、操作の一部が効かないことがあります
- プラグインはプロジェクトに次のものを作ります
  - `Assets/TankRig/Tracks/` … 同梱の転輪・履帯 FBX のコピーと、そのテクスチャ（パッケージを更新しても自動では入れ替わりません。組み立てウィンドウの「同梱の履帯を取り込む」で入れ替え）
  - `Assets/TankRig/Generated/` … エフェクトのマテリアル、当たり判定のメッシュ、物理マテリアル
  - シーン内 … 戦車のルート `Tank_<名前>`（元のモデルはその子に移動）、エフェクト・ライト・当たり判定、テスト用の地面 `TankRig_TestGround`（作る設定のとき）、メインカメラへの `TankCamera`
- 「戦車を完成させる」は、戦車の子にある `FX_` / `COL_` で始まるオブジェクトを作り直します。同じ名前で自分のオブジェクトを置かないでください
- 1 つのシーンでプレイヤーが操縦する戦車は 1 台を想定しています（2 台目にカメラを付けると、前の戦車は `externalInput` での操縦に切り替わります）
- 物理の調整は Unity の既定（Fixed Timestep 0.02 秒、重力 -9.81）で行っています
- SMC-AD で取り込んだモデルやテクスチャは、利用者の PC にあるゲームのデータから作られたものです。それを含むシーンやビルドを配布するときは、Scrap Mechanic の利用規約に従ってください
- 外すとき: Package Manager でパッケージを削除し、`Assets/TankRig/` を消します。組み立て済みの戦車のコンポーネントは「Missing Script」になるので、戦車のオブジェクトごと消すか作り直してください

## 謝辞

- [SMC-AD](https://github.com/KULALENUMIO/SMC-AD)（Scrap Mechanic → Blender → Unity の変換）
