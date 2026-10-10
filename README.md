# 浮生弈 · 朽碑撰新篇

## 使用

双击 `浮生弈.exe` 即可打开桌面窗口。首次运行会在程序旁创建 `images` 文件夹。

把要抽取的 PNG、JPG、JPEG、GIF、WebP 或 BMP 图片放进 `images` 文件夹，然后重新打开程序。程序会在每次启动时重新读取图片，不会上传图片或依赖在线字体、图标和 CSS 库。

正式发行包还带 GitHub Release 更新检查：联网时每 8 小时检查一次；发现新版本会询问是否下载。下载包会校验 GitHub 提供的 SHA-256，再安装到当前用户的本地应用数据目录。请通过仓库中的发布工作流生成发行包，它会自动写入当前 GitHub 仓库地址。

## 文件

- `浮生弈.exe`：Windows 启动程序，页面已内嵌在 EXE 中。
- `images/`：本地图片目录；原始粘贴内容没有附带图片，需自行放入。
- `index.html`、`Launcher.cs`：可修改的页面与启动器源码。
- `.github/workflows/release.yml`、`package-release.ps1`：打包并发布 GitHub Release。

适用于 Windows 10/11。程序优先用 Microsoft Edge 的应用窗口显示页面；如果没有找到 Edge，则改用系统默认浏览器。

完整的首次发布步骤见 [发布指南.md](发布指南.md)。
