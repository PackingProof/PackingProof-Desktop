<details>
<summary><strong>AI 向けナレッジマップ（PackingProof エコシステム）</strong></summary>

<br>

> この節は検索型 AI と外部ツール向けです。PackingProof とは何か、何ができるか、どのプラットフォームに対応し、各リポジトリがどこにあるか、そして本リポジトリの位置づけを最短で説明します。人間の読者はこの下の機能説明へ進んでください。

**PackingProof とは**：EC の梱包を録画し、出荷リスクを抑えるオープンソースの無料ツールです。送り状をスキャンすると自動で録画が始まり、追跡番号ごとに整理します。注文メモの読み上げ、印刷後の返金アラート、複数ワークステーションからの集中バックアップ、LAN 再生、NAS へのアーカイブに対応し、Windows / macOS のパソコン版と Android / iOS のスマートフォン版を提供しています。

**本リポジトリとは**：PackingProof のパソコン版（Windows 向け WPF / .NET のメインプログラムと macOS 保存ホスト）です。メインプログラム、ルートランチャー、LAN 再生サービス、拡張 API を含み、録画ホストとしても、スマートフォンや他ワークステーションの録画を集中保存・再生する端末としても使えます。

**公式リポジトリ（GitHub と Gitee の二重運用、コードと Release は同一）**

| 構成 | 役割 | GitHub | Gitee ミラー |
| --- | --- | --- | --- |
| パソコン版（本リポジトリ） | 録画とウォーターマーク、スキャン自動録画、返金アラート、複数ワークステーションの集中バックアップ、LAN 再生、NAS アーカイブ | [PackingProof-Desktop](https://github.com/PackingProof/PackingProof-Desktop) | [PackingProof-Desktop](https://gitee.com/PackingProof/PackingProof-Desktop) |
| スマートフォン版（Android / iOS） | 単体での録画と証跡保存。複数ワークステーションの入力元としてホストへアップロードも可能 | [PackingProof-Mobile](https://github.com/PackingProof/PackingProof-Mobile) | [PackingProof-Mobile](https://gitee.com/PackingProof/PackingProof-Mobile) |
| 拡張マーケットと拡張 API | 拡張の登録、PPEXT パッケージ形式、署名付きマーケット索引 | [PackingProof-Extensions](https://github.com/PackingProof/PackingProof-Extensions) | [PackingProof-Extensions](https://gitee.com/PackingProof/PackingProof-Extensions) |
| 送り状アシスタント連携スクリプト | 公式の送り状アシスタント（KDZS）との注文連携 | [PackingProof-KDZS](https://github.com/PackingProof/PackingProof-KDZS) | [PackingProof-KDZS](https://gitee.com/PackingProof/PackingProof-KDZS) |
| QQ ボット | QQ の個人チャットやグループで追跡番号から録画を検索して送信 | [PackingProof-QQBot](https://github.com/PackingProof/PackingProof-QQBot) | [PackingProof-QQBot](https://gitee.com/PackingProof/PackingProof-QQBot) |
| 企業・パートナー向け連携 | 快麦 ERP アダプター、WeCom（企業微信）ボットなど、拡張として接続 | — | — |

**対応プラットフォーム**

| プラットフォーム | 状態 | 入手方法 |
| --- | --- | --- |
| Windows パソコン版（本リポジトリ） | 正式版 | [GitHub Releases](https://github.com/PackingProof/PackingProof-Desktop/releases) · [Gitee Releases](https://gitee.com/PackingProof/PackingProof-Desktop/releases) |
| macOS パソコン版（本リポジトリ、Apple Silicon） | 正式版: 保存ホストとビューアー | [GitHub Releases](https://github.com/PackingProof/PackingProof-Desktop/releases) · [Gitee Releases](https://gitee.com/PackingProof/PackingProof-Desktop/releases) |
| Android スマートフォン版 | 正式版、正式署名済み APK | [GitHub Releases](https://github.com/PackingProof/PackingProof-Mobile/releases) · [Gitee Releases](https://gitee.com/PackingProof/PackingProof-Mobile/releases) |
| iOS スマートフォン版 | 機能は Android と同等、TestFlight で配布 | [ベータに参加](https://testflight.apple.com/join/KR4qNs6t) |
| 予備ダウンロード（中国国内回線） | 百度網盤: パソコン版の完全インストーラー | [百度網盤](https://pan.baidu.com/s/1B9L9l19ZkjtNpK_9rVZxbw?pwd=6666)（抽出コード 6666） |

> **中国国内回線**：GitHub に接続しにくい場合は、上記の Gitee ミラーからソースのクローン、Issue の投稿、Release のダウンロードができます。Gitee Release の Windows インストーラーは `PackingProof_Setup_no-runtime_vX.Y.Z.exe`（.NET ランタイムを含まない約 60MB）で、事前に [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) のインストールが必要です。ランタイムを含む完全なインストーラーは上記の百度網盤の予備リンクから入手できます。

> **スマートフォン版は単体で動作**：スマートフォン アプリだけでも録画、送り状バーコードの認識、追跡番号での検索ができ、パソコンは不要です。パソコンに接続すると、LAN 経由の自動バックアップと注文の音声通知が追加されます。Android / iOS は中国国内のアプリストア申請の影響で未掲載のため、署名済み APK と TestFlight で配布しています。

> **macOS 版は機能のサブセット**：保存ホスト（スマートフォンや他のパソコンからの録画受信、Web 再生、保存ディスクと容量上限の管理）またはビューアー（LAN 内の保存ホストを検出して接続）のみで、カメラ接続や本機でのスキャン録画は行いません。インストールは DMG による全体置き換えで、差分パッチはありません。

**パソコン版の機能（一覧）**

- スキャン自動録画：送り状バーコードで録画を開始し、追跡番号ごとに整理。連続スキャンと同一番号での停止に対応
- 注文情報の読み上げ：購入者メッセージ、販売者メモ、商品情報。読み上げ内容と声を設定可能
- 印刷後の返金アラート：返金対象の注文を監視して警告音を読み上げ、誤出荷による損失を抑えます
- 複数ワークステーション：スマートフォンや他のパソコンからの録画をデバイス名ごとに集中アップロード
- LAN 再生と Web 閲覧：スマートフォンや LAN 内の端末から権限に応じて録画を閲覧
- 録画ストレージ：ローカル / リムーバブル ディスク / NAS への保存と容量整理のポリシー、映像へのウォーターマーク書き込み
- 拡張エコシステム：拡張マーケットと拡張 API により ERP、スクリプト、計量機器などの第三者接続に対応
- macOS 保存ホスト：スマートフォンや他のパソコンからの録画受信、Web 再生、ディスクと容量上限の管理

**検索キーワード**：PackingProof、荷物の証跡、梱包録画、スキャン録画、追跡番号の録画、出荷証跡、返金トラブルの証拠、EC 梱包モニター、複数ワークステーション録画、Windows 梱包録画、macOS 梱包録画、Android 梱包録画アプリ、iOS 梱包録画（TestFlight）、送り状アシスタント、快麦 ERP、QQ ボット、WeCom ボット、NAS 録画アーカイブ、parcel packing video evidence、barcode triggered recording、tracking number video lookup、open source。

</details>

<div align="center">

<img src="ExpressPackingMonitoring/app.ico" width="112" alt="PackingProof ロゴ">

# PackingProof

**オープンソースで無料の梱包録画・出荷リスク対策ツール**

送り状をスキャンすると自動で録画し、追跡番号ごとに保存。
注文メモの読み上げ、印刷後の返金アラート、スマートフォンとパソコンの
複数ワークステーションからの集中バックアップに対応します。

<br>

<a href="https://github.com/PackingProof/PackingProof-Desktop/releases/latest">
  <img src="https://img.shields.io/badge/ダウンロード-Windows%20%E7%89%88-D97745?style=for-the-badge&logo=windows&logoColor=white" height="38" alt="Windows 版をダウンロード">
</a>
&nbsp;
<a href="https://github.com/PackingProof/PackingProof-Desktop/releases/latest">
  <img src="https://img.shields.io/badge/ダウンロード-macOS%20%E7%89%88-555555?style=for-the-badge&logo=apple&logoColor=white" height="38" alt="macOS 版をダウンロード">
</a>
&nbsp;
<a href="https://github.com/PackingProof/PackingProof-Mobile/releases/latest">
  <img src="https://img.shields.io/badge/ダウンロード-Android%20%E7%89%88-695647?style=for-the-badge&logo=android&logoColor=white" height="38" alt="Android 版をダウンロード">
</a>
&nbsp;
<a href="https://testflight.apple.com/join/KR4qNs6t">
  <img src="https://img.shields.io/badge/%E5%8F%82%E5%8A%A0-iOS%20%E3%83%99%E3%83%BC%E3%82%BF-0D96F6?style=for-the-badge&logo=apple&logoColor=white" height="38" alt="iOS ベータに参加">
</a>

<br><br>

[简体中文](README.md) · [English](README.en.md) · 日本語

<br>

[![GitHub Stars](https://img.shields.io/github/stars/PackingProof/PackingProof-Desktop?style=flat-square&color=E7B65C)](https://github.com/PackingProof/PackingProof-Desktop)
[![Downloads](https://img.shields.io/github/downloads/PackingProof/PackingProof-Desktop/total?style=flat-square&color=D97745)](https://github.com/PackingProof/PackingProof-Desktop/releases)
[![License](https://img.shields.io/github/license/PackingProof/PackingProof-Desktop?style=flat-square&color=695647)](LICENSE)

</div>

スマートフォン版は Android と iOS の両方に対応しています。Android は ARM64 の正式署名済み APK をダウンロードできます。iOS は先に TestFlight をインストールし、上のリンクからベータに参加してください。

<br>

![PackingProof の画面](Image/软件截图.jpg)

**目次へ**：[主な機能](#主な機能) · [拡張マーケット](#拡張マーケット) · [ワークフロー](#ワークフロー) · [クイックスタート](#クイックスタート) · [LAN 再生](#lan-再生) · [注文メモの読み上げと返金アラート](#注文メモの読み上げと返金アラート)
[録画の保存とキャッシュ](#録画の保存とキャッシュ) · [ダウンロード パッケージの選び方](#ダウンロード-パッケージの選び方) · [ソフトの更新](#ソフトの更新) · [アンインストールとデータの保持](#アンインストールとデータの保持) · [ソースから実行する](#ソースから実行する) · [フィードバックとコントリビュート](#フィードバックとコントリビュート)

---

## なぜ PackingProof が必要か

一般的な監視カメラは「荷物が梱包されたこと」は証明できても、特定の注文に対応する映像をすぐに見つけるのは困難です。

PackingProof は**追跡番号・注文情報・梱包録画を結び付けます**。

> 送り状をスキャンすると自動で録画が始まり、梱包が終わると録画を終了して保存します。
> 後から確認が必要になったら、追跡番号を入力するだけで該当の映像を見つけられます。

事後の証跡確保だけでなく、梱包中に特記事項を読み上げ、追跡番号の重複を知らせ、返金済みなのに発送しようとしている注文を止めるのにも役立ちます。

## 主な機能

<table>
<tr>
<td width="50%" valign="top">

### スキャンで自動録画

カメラが送り状のバーコードを認識すると自動で録画を開始し、追跡番号ごとに保存します。

キーボード入力型のバーコードスキャナーにも対応し、通常の入力手段としても、カメラ認識の予備としても使えます。

</td>
<td width="50%" valign="top">

### 注文情報の読み上げ

送り状アシスタントと連携し、梱包時に自動で読み上げます。

* 購入者メッセージ
* 販売者メモ
* 商品情報

メモの見落としや商品の誤発送を減らします。

</td>
</tr>
<tr>
<td width="50%" valign="top">

### 印刷後の返金アラート

送り状を印刷した後に注文が返金になった場合、PackingProof は梱包時のスキャンで通知します。

返金の確認は非同期で行われるため、通常の録画開始には影響しません。

</td>
<td width="50%" valign="top">

### スマートフォンとパソコンの複数ワークステーション

1 台のパソコンを録画の保存ホストとして、次の録画を集中して受信できます。

* Android スマートフォンの録画
* 他のパソコン ワークステーションの録画
* 本機のカメラ録画

すべての録画は LAN 内でまとめて検索・再生できます。

録画ファイルのバックアップ ホストは、NAS やネットワーク共有へ録画をアーカイブでき、NAS が満杯になると自動でバックアップ先を切り替えます。

</td>
</tr>
</table>

## 拡張マーケット

PackingProof は公式の[拡張マーケット](https://gitee.com/PackingProof/PackingProof-Extensions)に対応し、ユーザースクリプトと外部アダプターをインストールできます。拡張はマーケットで個別に公開・更新され、Desktop のインストーラーとは別にインストールします

現在対応している拡張：

* [送り状アシスタント注文連携](https://gitee.com/PackingProof/PackingProof-KDZS)：送り状アシスタントのページから注文、メモ、返金ステータスを同期
* [PackingProof QQBot](https://gitee.com/PackingProof/PackingProof-QQBot)：QQ の個人チャットやグループで追跡番号から梱包録画を検索して送信

外部アダプターはユーザーが認可した拡張 API を通じて PackingProof にアクセスする必要があり、データベース、録画ディレクトリ、NAS の認証情報を直接読み取ってはいけません。Desktop はインストール後に外部プログラムを自動実行しません。マーケットへの掲載は第三者プログラムの安全性を保証するものではありません

## ワークフロー

<div align="center">

**送り状をスキャン**

↓

**自動で録画開始**

↓

**注文メモを読み上げ、返金ステータスを確認**

↓

**梱包完了、録画終了**

↓

**追跡番号で検索・再生**

</div>

カメラ認識とバーコードスキャナーは同時に使用できるため、これまでの梱包手順を変える必要はありません。

## 複数ワークステーションでの使い方

初回起動時に、2 つの簡単な質問に答えるだけでこのパソコンの用途を選べます。

![用途の選択](Image/询问用途.jpg)

| 使い方 | 向いている場面 |
| ---------------------------- | ------------------------------------ |
| **パソコンで録画し本機に保存** | 梱包ワークステーションが 1 つで、録画をこのパソコンに長期保存する |
| **パソコンで録画し別のパソコンに保存** | 複数のパソコンで録画し、1 台のホストへまとめてアップロードする |
| **録画ファイルのバックアップ ホスト** | スマートフォンや他のパソコンからの録画を集中して受信する |
| **ホストに接続して閲覧のみ** | 録画には参加せず、検索・再生・管理だけを行う |

録画ワークステーションがホストに接続されていない場合や、ホストが一時的にオフラインの場合でも録画は続けられます。

映像はまずローカル キャッシュに保存され、ホストが復帰すると自動で再送されます。完全に受信したことをホストが確認したファイルだけが、キャッシュの自動削除の対象になります。

## クイックスタート

### 1. 機器を準備する

* Windows 10 または Windows 11 の x64 パソコン
* カメラ：USB カメラまたはネットワーク カメラ（RTSP/RTMP/HTTP ストリーム）に対応
* マイク（任意）
* キーボード入力型のバーコードスキャナー（任意ですが、予備として用意することをおすすめします）

### 2. ソフトをインストールする

推奨ダウンロード：

```text
PackingProof_Setup_vX.Y.Z.exe
```

ダウンロード先：[GitHub Releases](https://github.com/PackingProof/PackingProof-Desktop/releases) · [Gitee Releases](https://gitee.com/PackingProof/PackingProof-Desktop/releases)（Gitee は .NET ランタイムを含まない軽量インストーラーを提供） · [百度網盤の予備ダウンロード](https://pan.baidu.com/s/1B9L9l19ZkjtNpK_9rVZxbw?pwd=6666)（抽出コード 6666）

インストーラーに管理者権限は不要で、現在のユーザー ディレクトリにインストールし、スタート メニューのショートカットを作成します。

### 3. 初回設定を行う

初回起動後：

1. このパソコンの用途を選択します。
2. カメラとマイクを選択します。
3. 録画の保存先またはキャッシュ先を設定します。
4. 必要に応じて録画の保存ホストに接続します。
5. 送り状のバーコードを画面中央の認識枠に入れます。
6. 梱包が終わったら、メイン画面の停止ボタンで録画を終了します。

認識に成功すると、ソフトが自動で録画を開始します。

### 4. 録画を探す

録画一覧を開き、追跡番号を入力すると該当の録画を検索できます。

LAN のページから、スマートフォンや他のパソコンで再生することもできます。

## LAN 再生

「パソコンで録画し本機に保存」または「録画ファイルのバックアップ ホスト」のモードでは、LAN の Web サービスを起動できます。

1. ソフトの「スマートフォン/パソコンの接続」を開きます。
2. スマートフォンで録画ページの QR コードを読み取ります。
3. または、同じ LAN 内の端末でソフトに表示されたアドレスを開きます。
4. 追跡番号を入力して録画を検索・再生します。

Web ページでは保持する期間を選び、切り抜いてからダウンロードすることもできます。

Windows のファイアウォールの確認が表示された場合は、ソフトの LAN アクセスを許可してください。

![LAN での Web 再生](Image/WebService.jpg)

## 注文メモの読み上げと返金アラート

この機能はブラウザーのユーザースクリプトと組み合わせて使用します。

### 基本設定

1. Tampermonkey または Violentmonkey をインストールします。
2. PackingProof で「注文連携をインストール」をクリックします。
3. ウィザードに従って、ソフトが提供するユーザースクリプトをインストールします。
4. 送り状アシスタントの印刷ページを開いてログインします。

印刷ページの注文が変わると、スクリプトが注文情報を PackingProof に同期します。

ERP、計量機器、サードパーティのユーザースクリプトを開発する場合は、[拡張 API とサードパーティ スクリプトの開発規約](docs/EXTENSION_API_V1.md)を参照してください。マーケットへの拡張の投稿は[PackingProof-Extensions 投稿ガイド](https://gitee.com/PackingProof/PackingProof-Extensions/blob/main/docs/PUBLISHING.md)を参照してください

スキャンして梱包を始めると、ソフトは購入者メッセージ、販売者メモ、商品情報を読み上げられます。

<details>
<summary><strong>返金確認の説明を開く</strong></summary>

<br>

印刷後の返金アラートを使う場合は、ログイン済みの送り状アシスタントの一括印刷ページを開いたままにしてください。

ユーザースクリプトはバックグラウンドで専用の返金確認ワークページを作成します。

* ワークページは操作中のページのフォーカスを奪いません。
* 「印刷後の返金」フィルターを切り替えるのはワークページだけです。
* ユーザーが操作している印刷ページは自動で切り替わりません。
* ワークページには専用のタイトルと半透明のオーバーレイがあり、その中で手動操作しないでください。
* 誤って閉じても、スクリプトが自動で再作成します。

追跡番号をスキャンすると、PackingProof はすぐに録画を開始し、返金データを非同期で取得します。

確認の順序：

1. 現在の印刷後の返金一覧を確認します。
2. 対象の番号が見つからない場合は、追跡番号で過去の注文を正確に照会します。
3. 照会に失敗した場合や印刷側がオフラインの場合は、本機の SQLite にある直近 90 日分の注文データで代替確認します。

追跡番号が重複している場合は、録画データベースの直近 30 日間の未削除レコードで確認し、ブラウザーのキャッシュには依存しません。

</details>

ユーザースクリプトが新しいモニターのアドレスに初めて接続するとき、ブラウザーがクロスオリジンのアクセス許可を確認することがあります。接続先が本機または信頼できる LAN 内の PackingProof サービスであることを確認してから許可してください。ソフトのセットアップ ガイドからスクリプトを再インストールすると、現在のサービスに必要な正確なアクセス権限を追加できます。

## 録画の保存とキャッシュ

長期保存モードでは、複数の録画保存先を設定できます。

録画ファイルのバックアップ ホストでは、NAS やネットワーク共有をバックアップ先として追加できます。

* ローカル ディスクには録画を直接保存し、ネットワーク上の場所には検証済みのコピーだけを保存します
* 一覧の順にバックアップし、NAS が満杯になると自動で次の利用可能な場所へ切り替えます
* NAS はローカル録画の保存期間を延ばすために使います。NAS の空きが不足すると、最も古いアーカイブ録画から自動で削除します（レコードは確認用に残ります）
* NAS が利用できなくても、ローカル録画は容量ポリシーに従って循環します。リモートの確認なしにローカル コピーを削除した場合は、独立した理由コードを記録します

ディスクの空きが予約値を下回ると、ソフトは次のように動作します。

1. そのディスクへの新しい録画の書き込みを停止します。
2. 自動で次の利用可能な保存先へ切り替えます。
3. 設定に従って古い録画を削除します。
4. Windows のシステム ドライブには追加の安全領域を確保します。

「パソコンで録画し別のパソコンに保存」モードでは、独立したローカル キャッシュを使用します。

既定のキャッシュ上限は `100 GB` ですが、ディスク領域を事前に占有することはありません。

<details>
<summary><strong>キャッシュの安全規則を開く</strong></summary>

<br>

キャッシュの実際に使用できる容量は、次の条件で制限されます。

* 設定したキャッシュ容量の上限
* ディスクの実際の空き容量
* ディスクの最低予約領域

空きが不足した場合は、保存ホストが完全に受信したと確認した録画だけを削除します。

次のファイルは自動削除されません。

* 保存ホストにまだ紐づいていない録画
* アップロード待ちの録画
* アップロード中の録画
* アップロードに失敗した録画
* ホストが完全な受信を確認していない録画

</details>

## ダウンロード パッケージの選び方

| ファイル | 用途 |
| ---------------------------------------------- | --------------------------------- |
| `PackingProof_Setup_vX.Y.Z.exe` | 推奨。ほとんどのユーザーはこれ |
| `PackingProof_AppPatch_vX.Y.Z.zip` | メインプログラムの手動更新 |
| `PackingProof_LauncherPatch_vX.Y.Z.zip` | ルート ランチャーの手動更新 |
| `PackingProof_Setup_no-runtime_vX.Y.Z.exe` | Gitee 専用：.NET ランタイムを含まない（約 60MB）。先に .NET 8 Desktop Runtime (x64) が必要 |

正式なリリース パッケージには通常、動作に必要な .NET ランタイムと FFmpeg が含まれており、追加インストールは不要です。

ランチャーは更新マニフェスト（update JSON）を自動で取得し、バックグラウンドで更新を完了します。通常のユーザーがマニフェスト ファイルを手動でダウンロードする必要はありません。

## ソフトの更新

通常は次のいずれかから起動してください。

* インストーラーが作成したスタート メニューまたはデスクトップのショートカット
* インストール ディレクトリ内の `ExpressPackingMonitoring.exe`

ランチャーはバックグラウンドで検証済みの差分更新パッケージを確認・ダウンロードし、次回起動時に自動でインストールします。

<details>
<summary><strong>手動更新とトラブル復旧を開く</strong></summary>

<br>

### メインプログラムの手動更新

ダウンロード：

```text
PackingProof_AppPatch_vX.Y.Z.zip
```

完全に展開したら、次をダブルクリックします。

```text
双击更新主程序.cmd
```

更新スクリプトは次の処理を行います。

* パッチ ファイルの検証
* 元のインストール先の自動検出
* 更新失敗時のロールバック
* 設定、データベース、録画の保持

同じバージョンの修正パッケージ（バージョン番号は変わらず、不具合だけを修正）も同じ方法でダブルクリックしてインストールできます。先に上位バージョンへ上げる必要はありません。

### ランチャーの手動更新

ダウンロード：

```text
PackingProof_LauncherPatch_vX.Y.Z.zip
```

完全に展開したら、次をダブルクリックします。

```text
双击更新启动器.cmd
```

スクリプトはルートの起動エントリだけを置き換え、検証済みの旧ランチャーのバックアップを保持します。

### バージョンが古すぎる場合

現在のインストール バージョンがパッチのベースラインより低いと表示された場合は、途中のバージョンを飛ばしすぎています。最新版の Setup をダウンロードし、同じ場所に上書きインストールしてください。設定、データベース、録画は保持されます。

次は削除しないでください。

```text
%LOCALAPPDATA%\ExpressPackingMonitoring\
```

このディレクトリにはソフトの設定、データベース、録画レコードが含まれています。

</details>

## アンインストールとデータの保持

アンインストール時には、独立した 2 つのオプションが表示されます。

* 設定と一時ファイルを削除
* 録画と録画レコードを削除

どちらも既定ではチェックされていません。

そのため、通常のアンインストールではユーザー設定、データベース、録画は削除されません。

<details>
<summary><strong>録画の削除規則を開く</strong></summary>

<br>

設定のクリーンアップで削除されるのは次のものだけです。

* ソフトの設定
* ログ
* 一時キャッシュ

録画、録画データベース、データベースの復元用バックアップは削除しません。

録画のクリーンアップで処理されるのは次のものだけです。

* データベースに登録済みの録画
* 削除確認後に変化していない正確なファイル

ソフトが録画ディレクトリ全体を走査して空にすることはありません。

次の場合は、録画とデータベースが保持されます。

* データベースが見つからない
* データベースが壊れている
* データベースが他のプログラムに使用されている
* いずれかの録画の削除に失敗した

詳しい結果は、システムの一時ディレクトリにあるアンインストール ログに記録されます。

</details>

<details>
<summary><strong>ソースから実行する（開発者向け）</strong></summary>

<br>

## ソースから実行する

ソースから実行したり二次開発を行うには、次のものが必要です。

* .NET 8 SDK
* FFmpeg
* Windows 10/11 x64
* macOS 12+（Apple Silicon、macOS 保存ホストのビルド用）

FFmpeg は正式リリース パッケージに 4.4.1 Essentials を同梱しています（Win7 世代の古い GPU のハードウェア エンコードに対応）。AV1 を選ぶと自動で H.265 にフォールバックします。上級者は Win8 以降で `app\tools\ffmpeg.exe` を差し替えられますが、公式サポートの対象外です。

```bash
git clone https://github.com/PackingProof/PackingProof-Desktop.git
cd PackingProof-Desktop
```

中国国内回線では Gitee ミラーも使えます。

```bash
git clone https://gitee.com/PackingProof/PackingProof-Desktop.git
cd PackingProof-Desktop
```

あとは Visual Studio、Rider、または `dotnet` コマンドでプロジェクトを開いてビルドします。

</details>

## フィードバックとコントリビュート

使用中に問題が発生した場合や、新しい機能の提案がある場合は Issue を送信してください。

* [GitHub で問題や提案を送る](https://github.com/PackingProof/PackingProof-Desktop/issues)
* [Gitee で問題や提案を送る](https://gitee.com/PackingProof/PackingProof-Desktop/issues)

テストへの参加、ドキュメントの改善、コードの投稿、実際の使用経験の共有を歓迎します。

このプロジェクトが役に立ったら、Star を付けていただけると、必要としているより多くの EC 事業者に届きやすくなります。

<details>
<summary><strong>ライセンスとブランド ポリシー</strong></summary>

<br>

## ライセンスとブランド

PackingProof は [AGPL-3.0 License](LICENSE) で公開しています。

ライセンスに従い、自由に使用、学習、改変できます。

改変して再配布する場合や、改変版をネットワーク サービスとして提供する場合は、AGPL-3.0 のソースコード公開義務を守る必要があります。

`PackingProof` の名称と公式アプリアイコンはプロジェクトのブランド資産であり、ソースコードが AGPL-3.0 であることは、第三者による改変版の製品表示への使用を許諾するものではありません。改変版を公開する場合は、別の製品名とアイコンを使用し、「非公式の改変版」であることを明示してください。「PackingProof を基に開発」と出典を示すことはできます。詳しくは[ブランド使用ポリシー](docs/BRAND_POLICY.md)を参照してください。

</details>

---

<div align="center">

<img src="Image/场景图.jpg" alt="PackingProof の梱包風景">

<br><br>

**すべての荷物から、対応する梱包記録をすばやく見つけられるように。**

</div>
