using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Launcher
{
    [STAThread]
    private static int Main()
    {
        try
        {
            string appFolder = AppDomain.CurrentDomain.BaseDirectory;
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

    private static bool TryLaunchUpdate(string appFolder)
    {
        string configPath = Path.Combine(appFolder, "update-config.json");
        if (!File.Exists(configPath)) return false;

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

        if (!IsGitHubName(owner) || !IsGitHubName(repository)) return false;
        if (String.IsNullOrWhiteSpace(assetName)) assetName = "fushengyi-win-x64.zip";

        string localVersionText = ReadVersionText(appFolder);
        Version localVersion;
        if (!Version.TryParse(localVersionText, out localVersion)) localVersion = new Version(1, 0, 0);

        string localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FuShengYi");
        string versionsFolder = Path.Combine(localData, "versions");
        string cachedExecutable = FindCachedVersion(versionsFolder, localVersion);
        if (cachedExecutable != null)
        {
            StartExecutable(cachedExecutable);
            return true;
        }

        string checkPath = Path.Combine(localData, "last-update-check.txt");
        try
        {
            DateTime previousCheck;
            if (DateTime.TryParse(File.ReadAllText(checkPath), out previousCheck) &&
                DateTime.UtcNow - previousCheck.ToUniversalTime() < TimeSpan.FromHours(8))
                return false;
        }
        catch { }

        Directory.CreateDirectory(localData);
        try { File.WriteAllText(checkPath, DateTime.UtcNow.ToString("o")); }
        catch { }

        Dictionary<string, object> release;
        try
        {
            string apiUrl = "https://api.github.com/repos/" + owner + "/" + repository + "/releases/latest";
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            release = serializer.Deserialize<Dictionary<string, object>>(RequestText(apiUrl, 5000));
        }
        catch { return false; }

        string tag = ReadJsonString(release, "tag_name");
        Version remoteVersion;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out remoteVersion) || remoteVersion <= localVersion) return false;

        Dictionary<string, object> matchingAsset = null;
        object assetsValue;
        if (release.TryGetValue("assets", out assetsValue))
        {
            IEnumerable assets = assetsValue as IEnumerable;
            if (assets != null)
            {
                foreach (object item in assets)
                {
                    Dictionary<string, object> asset = item as Dictionary<string, object>;
                    if (asset != null && String.Equals(ReadJsonString(asset, "name"), assetName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingAsset = asset;
                        break;
                    }
                }
            }
        }
        if (matchingAsset == null) return false;

        string downloadUrl = ReadJsonString(matchingAsset, "browser_download_url");
        string digest = ReadJsonString(matchingAsset, "digest");
        if (String.IsNullOrWhiteSpace(downloadUrl) || !downloadUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return false;

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
        request.Accept = "application/vnd.github+json";
        request.UserAgent = "FuShengYi-Updater/1.0";
        request.Timeout = timeout;
        request.ReadWriteTimeout = timeout;
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            return reader.ReadToEnd();
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
