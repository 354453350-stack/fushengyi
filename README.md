# 浮生弈 · 朽碑撰新篇

## 使用

双击 `浮生弈.exe` 即可打开桌面窗口。首次运行会在程序旁创建 `images` 文件夹。

把要抽取的 PNG、JPG、JPEG、GIF、WebP 或 BMP 图片放进 `images` 文件夹，然后重新打开程序。程序会在每次启动时重新读取图片，不会上传图片或依赖在线字体、图标和 CSS 库。

正式发行包还带 GitHub Release 更新检查：联网时每 8 小时自动检查一次，也可以点击“检查更新”立即检查；发现新版本会询问是否下载。首次运行会迁移到固定目录 `%LOCALAPPDATA%\FuShengYi\app`，原目录中的 EXE 会继续作为启动入口。下载包会校验 GitHub 提供的 SHA-256，再由独立更新程序替换固定目录中的程序文件。替换前请关闭所有浮生弈窗口；更新成功后程序会重新启动。用户自加图片会保存在 `%LOCALAPPDATA%\FuShengYi\data\images`，不会被程序更新覆盖。首次点击时，Windows 可能询问是否允许打开“浮生弈更新程序”，请选择允许。请通过仓库中的发布工作流生成发行包，它会自动写入当前 GitHub 仓库地址。

## 文件

- `浮生弈.exe`：Windows 启动程序，页面已内嵌在 EXE 中。
- `images/`：程序自带图片目录；更新后用户自加图片保存在用户数据目录。
- `index.html`、`Launcher.cs`：可修改的页面与启动器源码。
- `.github/workflows/release.yml`、`package-release.ps1`：打包并发布 GitHub Release。

适用于 Windows 10/11。程序优先用 Microsoft Edge 的应用窗口显示页面；如果没有找到 Edge，则改用系统默认浏览器。

完整的首次发布步骤见 [发布指南.md](发布指南.md)。
