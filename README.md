# 浮生弈 · 朽碑撰新篇

## 使用

双击 `浮生弈.exe` 即可打开桌面窗口。首次运行会在程序旁创建 `images` 文件夹。

把要抽取的 PNG、JPG、JPEG、GIF、WebP 或 BMP 图片放进 `images` 文件夹，然后重新打开程序。程序会在每次启动时重新读取图片，不会上传图片或依赖在线字体、图标和 CSS 库。

正式发行包联网后每 8 小时自动检查一次更新，也可以点击“检查更新”立即检查；发现新版本会询问是否下载。更新器优先检查 Gitee Release，GitHub Release 作为备用源，并验证 SHA-256。首次运行会迁移到固定目录 `%LOCALAPPDATA%\FuShengYi\app`，原目录中的 EXE 会继续作为启动入口。下载包由独立更新程序替换；替换前请关闭所有浮生弈窗口，完成后程序会重新启动。用户自加图片保存在 `%LOCALAPPDATA%\FuShengYi\data\images`，不会被程序更新覆盖。首次点击时，Windows 可能询问是否允许打开“浮生弈更新程序”，请选择允许。每次发布都需要在 Gitee 和 GitHub 建立同版本 Release，并上传相同 ZIP 和 `.sha256` 文件。

## 文件

- `浮生弈.exe`：Windows 启动程序，页面已内嵌在 EXE 中。
- `images/`：程序自带图片目录；更新后用户自加图片保存在用户数据目录。
- `index.html`、`Launcher.cs`：可修改的页面与启动器源码。
- `.github/workflows/release.yml`、`package-release.ps1`：打包并发布 GitHub Release。

适用于 Windows 10/11。程序优先用 Microsoft Edge 的应用窗口显示页面；如果没有找到 Edge，则改用系统默认浏览器。

完整的首次发布步骤见 [发布指南.md](发布指南.md)。
