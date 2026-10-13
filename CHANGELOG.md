# Changelog

形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/)、版の付け方は [Semantic Versioning](https://semver.org/lang/ja/) に従います。

## [0.1.0] - 2026-10-14

最初の公開版です。

### Added

- 登録したアプリ(exe 名)ごとのミュート/解除の切り替えと、まとめての切り替え
- 全部の再生デバイス・全部のプロセスのセッションをまとめて切り替える
- ミュート予約(音を出していないアプリが鳴り始めたらミュートする。1 秒ごとの確認による)
- Windows 側(音量ミキサー等)での変更を表示に反映する
- 設定の保存(`%APPDATA%\HushSwitch\settings.json`)、二重起動の防止
