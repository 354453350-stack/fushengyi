using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Launcher
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string appFolder = AppDomain.CurrentDomain.BaseDirectory;
            RegisterUpdateProtocol();
            if (args != null && Array.IndexOf(args, "--check-update") >= 0)
            {
                TryLaunchUpdate(appFolder, true, true);
                return 0;
            }
            if (TryLaunchUpdate(appFolder)) return 0;

            string imageFolder = Path.Combine(appFolder, "images");
            Directory.CreateDirectory(imageFolder);

            string[] imagePaths = Directory.GetFiles(imageFolder);
            StringBuilder json = new StringBuilder("[");
            bool first = true;
            foreach (string file in imagePaths)
            {
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" &&
                    extension != ".gif" && extension != ".webp" && extension != ".bmp")
                    continue;

                if (!first) json.Append(',');
                first = false;
                json.Append('"').Append(EscapeJson(new Uri(file).AbsoluteUri)).Append('"');
            }
            json.Append(']');

            string page;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Page.html"))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                page = reader.ReadToEnd();
            page = page.Replace("__IMAGE_FILES_JSON__", json.ToString());

            string dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FuShengYi");
            Directory.CreateDirectory(dataFolder);
            string pagePath = Path.Combine(dataFolder, "index.html");
            File.WriteAllText(pagePath, page, new UTF8Encoding(false));
            string pageUri = new Uri(pagePath).AbsoluteUri;

            string edge = FindEdge();
            if (edge != null)
            {
                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = edge;
                start.Arguments = "--app=\"" + pageUri + "\" --no-first-run";
                start.UseShellExecute = false;
                Process.Start(start);
            }
            else
            {
                Process.Start(new ProcessStartInfo(pageUri) { UseShellExecute = true });
            }
            return 0;
        }
        catch (Exception ex)
        {
            string logPath = Path.Combine(Path.GetTempPath(), "FuShengYi-startup-error.txt");
            try { File.WriteAllText(logPath, ex.ToString()); }
            catch { logPath = "无法写入错误日志"; }
            try { MessageBox.Show("程序启动失败。请确认程序目录可写。\r\n\r\n错误日志：" + logPath, "浮生弈", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch { }
            return 1;
        }
    }

    private static string FindEdge()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] candidates = {
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe")
        };
        foreach (string candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static void RegisterUpdateProtocol()
    {
        try
        {
            string executable = Assembly.GetExecutingAssembly().Location;
            using (RegistryKey protocol = Registry.CurrentUser.CreateSubKey(@"Software\Classes\fushengyicheck"))
            {
                if (protocol == null) return;
                protocol.SetValue("", "URL:浮生弈检查更新");
                protocol.SetValue("URL Protocol", "", RegistryValueKind.String);
            }
            using (RegistryKey command = Registry.CurrentUser.CreateSubKey(@"Software\Classes\fushengyicheck\shell\open\command"))
            {
                if (command == null) return;
                command.SetValue("", "\"" + executable + "\" --check-update \"%1\"", RegistryValueKind.String);
            }
        }
        catch { }
    }

    private static bool TryLaunchUpdate(string appFolder, bool forceCheck = false, bool showLatest = false)
    {
        string configPath = Path.Combine(appFolder, "update-config.json");
        if (!File.Exists(configPath))
        {
            if (showLatest) MessageBox.Show("更新配置不存在，请重新下载完整版本。", "浮生弈更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        string owner;
        string repository;
        string assetName;
        try
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> config = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath, Encoding.UTF8));
            owner = ReadJsonString(config, "owner");
            repository = ReadJsonString(config, "repository");
            assetName = ReadJsonString(config, "assetName");
        }
        catch { return false; }

        if (!IsGitHubName(owner) || !IsGitHubName(repository))
        {
            if (showLatest) MessageBox.Show("更新源配置无效，请重新下载完整版本。", "浮生弈更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (String.IsNullOrWhiteSpace(assetName)) assetName = "fushengyi-win-x64.zip";

        string localVersionText = ReadVersionText(appFolder);
        Version localVersion;
        if (!Version.TryParse(localVersionText, out localVersion)) localVersion = new Version(1, 0, 0);

        string localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FuShengYi");
        string versionsFolder = Path.Combine(localData, "versions");
        string cachedExecutable = FindCachedVersion(versionsFolder, localVersion);
        // A manual check must still contact GitHub. Otherwise an older launcher
        // with a cached version would keep opening that cache and never discover
        // a newer release.
        if (cachedExecutable != null && !forceCheck)
        {
            StartExecutable(cachedExecutable);
            return true;
        }

        Directory.CreateDirectory(localData);
        string checkPath = Path.Combine(localData, "last-update-check.txt");
        if (!forceCheck)
        {
            try
            {
                DateTime previousCheck;
                if (DateTime.TryParse(File.ReadAllText(checkPath), out previousCheck) &&
                    DateTime.UtcNow - previousCheck.ToUniversalTime() < TimeSpan.FromHours(8))
                    return false;
            }
            catch { }
        }
        try { File.WriteAllText(checkPath, DateTime.UtcNow.ToString("o")); }
        catch { }

        string tag;
        try
        {
            tag = RequestLatestReleaseTag(owner, repository, 10000);
        }
        catch (Exception ex)
        {
            if (showLatest)
                MessageBox.Show("暂时无法连接更新服务器，请检查网络后重试。\r\n\r\n" + ex.Message, "浮生弈更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        Version remoteVersion;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out remoteVersion))
        {
            if (showLatest) MessageBox.Show("暂时无法识别服务器上的版本号。", "浮生弈更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (remoteVersion <= localVersion)
        {
            if (showLatest) MessageBox.Show("当前已经是最新版本（v" + localVersion + "）。", "浮生弈更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        string releaseBase = "https://github.com/" + owner + "/" + repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/";
        string downloadUrl = releaseBase + Uri.EscapeDataString(assetName);
        string digest;
        try
        {
            digest = RequestReleaseDigest(downloadUrl + ".sha256", assetName, 10000);
        }
        catch (Exception ex)
        {
            if (showLatest)
                MessageBox.Show("发现新版本 v" + remoteVersion + "，但暂时无法读取安装包校验文件。\r\n\r\n" + ex.Message, "浮生弈更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        string versionName = remoteVersion.ToString();
        string finalFolder = Path.Combine(versionsFolder, "v" + versionName);
        if (IsValidInstalledVersion(finalFolder, remoteVersion))
        {
            if (AskToInstallUpdate(localVersion, remoteVersion))
            {
                StartExecutable(Path.Combine(finalFolder, "浮生弈.exe"));
                return true;
            }
            return false;
        }

        if (!AskToInstallUpdate(localVersion, remoteVersion)) return false;

        try
        {
            string updateFolder = Path.Combine(localData, "updates");
            Directory.CreateDirectory(updateFolder);
            string zipPath = Path.Combine(updateFolder, "fushengyi-v" + versionName + ".zip");
            DownloadFile(downloadUrl, zipPath, 30000);
            VerifySha256(zipPath, digest);

            Directory.CreateDirectory(versionsFolder);
            string stagingFolder = Path.Combine(versionsFolder, "stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingFolder);
            ExtractPackage(zipPath, stagingFolder);
            if (!IsValidInstalledVersion(stagingFolder, remoteVersion))
                throw new InvalidDataException("更新包中的版本号或程序文件不正确。");

            if (Directory.Exists(finalFolder))
                finalFolder = Path.Combine(versionsFolder, "v" + versionName + "-" + DateTime.UtcNow.Ticks.ToString());
            Directory.Move(stagingFolder, finalFolder);
            StartExecutable(Path.Combine(finalFolder, "浮生弈.exe"));
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("更新没有完成，继续打开当前版本。\r\n\r\n" + ex.Message, "浮生弈更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private static string ReadJsonString(Dictionary<string, object> values, string key)
    {
        object value;
        return values != null && values.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : String.Empty;
    }

    private static bool IsGitHubName(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || value == "OWNER" || value == "REPOSITORY") return false;
        foreach (char ch in value)
            if (!Char.IsLetterOrDigit(ch) && ch != '-' && ch != '_' && ch != '.') return false;
        return true;
    }

    private static string ReadVersionText(string folder)
    {
        string path = Path.Combine(folder, "version.txt");
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : "1.0.0";
    }

    private static string FindCachedVersion(string versionsFolder, Version currentVersion)
    {
        if (!Directory.Exists(versionsFolder)) return null;
        string bestPath = null;
        Version bestVersion = currentVersion;
        foreach (string folder in Directory.GetDirectories(versionsFolder))
        {
            if (!Path.GetFileName(folder).StartsWith("v", StringComparison.OrdinalIgnoreCase)) continue;
            Version candidate;
            if (!Version.TryParse(ReadVersionText(folder), out candidate) || candidate <= bestVersion) continue;
            if (File.Exists(Path.Combine(folder, "浮生弈.exe")))
            {
                bestVersion = candidate;
                bestPath = Path.Combine(folder, "浮生弈.exe");
            }
        }
        return bestPath;
    }

    private static bool IsValidInstalledVersion(string folder, Version expected)
    {
        Version installed;
        return File.Exists(Path.Combine(folder, "浮生弈.exe")) &&
            Version.TryParse(ReadVersionText(folder), out installed) && installed == expected;
    }

    private static bool AskToInstallUpdate(Version current, Version latest)
    {
        string message = "发现新版本 " + latest + "（当前版本 " + current + "）。\r\n\r\n现在下载并启动更新吗？";
        return MessageBox.Show(message, "浮生弈更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes;
    }

    private static string RequestText(string url, int timeout)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Accept = "text/plain, text/html, */*";
        request.UserAgent = "FuShengYi-Updater/1.0";
        request.Timeout = timeout;
        request.ReadWriteTimeout = timeout;
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            return reader.ReadToEnd();
    }

    private static string RequestLatestReleaseTag(string owner, string repository, int timeout)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        string url = "https://github.com/" + owner + "/" + repository + "/releases/latest";
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Accept = "text/html";
        request.UserAgent = "FuShengYi-Updater/1.0";
        request.Timeout = timeout;
        request.ReadWriteTimeout = timeout;
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        {
            Uri finalUri = response.ResponseUri;
            if (!String.Equals(finalUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub 返回的更新页面地址无效。");

            string[] segments = finalUri.AbsolutePath.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 5 ||
                !String.Equals(segments[0], owner, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(segments[1], repository, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(segments[2], "releases", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(segments[3], "tag", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub 没有返回有效的最新 Release 页面。");

            return Uri.UnescapeDataString(segments[4]);
        }
    }

    private static string RequestReleaseDigest(string url, string assetName, int timeout)
    {
        if (String.IsNullOrWhiteSpace(url) || !url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("新版本校验文件地址无效。");

        string[] lines = RequestText(url, timeout).Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1)
            throw new InvalidDataException("新版本校验文件格式无效。");

        string line = lines[0].Trim();
        if (line.Length < 64)
            throw new InvalidDataException("新版本校验文件内容不完整。");

        string hash = line.Substring(0, 64);
        for (int i = 0; i < hash.Length; i++)
        {
            char ch = hash[i];
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')))
                throw new InvalidDataException("新版本校验文件中的 SHA-256 格式无效。");
        }

        string declaredName = line.Substring(64).Trim().TrimStart('*').Trim();
        if (declaredName.Length > 0 && !String.Equals(declaredName, assetName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("新版本校验文件对应的文件名不一致。");

        return "sha256:" + hash;
    }

    private static void DownloadFile(string url, string destination, int timeout)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.UserAgent = "FuShengYi-Updater/1.0";
        request.Timeout = timeout;
        request.ReadWriteTimeout = timeout;
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (Stream input = response.GetResponseStream())
        using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            input.CopyTo(output);
    }

    private static void VerifySha256(string filePath, string digest)
    {
        if (String.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub Release 没有提供 SHA-256 校验值，已取消安装。");
        string expected = digest.Substring("sha256:".Length).Trim();
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(filePath))
        {
            string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty);
            if (!String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包校验失败，文件可能已损坏。");
        }
    }

    private static void ExtractPackage(string zipPath, string destination)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using (ZipArchive archive = ZipFile.OpenRead(zipPath))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(Path.Combine(destination, relative));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包包含无效路径。");
                if (String.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }
    }

    private static void StartExecutable(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
