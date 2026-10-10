# MotionProbe

AIエージェントがVRChatアバターの「動き」を把握するためのUnityエディタ拡張です。
髪・スカート・尻尾・耳などの揺れもの（PhysBone）や、ダンス・エモートなどのアニメーションを決まった動きで再生・記録し、
AIが読める数値レポートと画像（コマ割り・軌跡・グラフ）にまとめます。

## 導入（VCC）

1. [VCCに追加](https://ocha98.github.io/MotionProbe/) を開き、「VCCに追加」を押します。
   うまくいかないときは、VCCの Settings → Packages → Add Repository に次のURLを貼り付けます。
   ```
   https://ocha98.github.io/MotionProbe/index.json
   ```
2. プロジェクトの「Manage Project」で **MotionProbe** を追加します。

必要なもの:

- VRChat Avatars SDK 3.5以降（Unity 2022.3）
- Gesture Manager（パラメータのON/OFFに使います。なくても移動系のテストはできます）
- AIとUnityをつなぐ [MCP for Unity](https://github.com/CoplayDev/unity-mcp)（あれば `motion_probe` ツールとして登録されます）

## 使い方

AIエージェントに「髪の揺れが自然か確認して」「このダンスがどんな動きか教えて」のように頼みます。
AIは `MotionProbe.Run("standard")` などを実行し、結果を「どの部分が、何秒後に、何cm、どちらへ動いたか」で説明します。
結果はプロジェクト直下の `MotionProbeRuns/<実行日時>/` に保存されるので、自分の目でも確認できます。

詳しくはパッケージ内の資料を見てください。

- [パッケージのREADME](Packages/com.ocha98.motionprobe/README.md)
- [シナリオ（動きの指定方法）](Packages/com.ocha98.motionprobe/Documentation~/scenarios.md)
- [レポートの読み方](Packages/com.ocha98.motionprobe/Documentation~/report-guide.md)

## リリース手順（開発者向け）

1. `Packages/com.ocha98.motionprobe/package.json` の `version` と `CHANGELOG.md` を更新してコミットします。
2. 同じ番号のタグ（例: `v1.0.1`）をpushします。
3. GitHub Actions がzipを作ってリリースに添付し、GitHub PagesのVCCリスティング（`index.json`）を更新します。

## ライセンス

MIT
