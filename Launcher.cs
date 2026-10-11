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
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Launcher
{
    private const string AppName = "FuShengYi";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args != null && args.Length > 0 && args[0] == "--apply-update")
                return ApplyStagedUpdate(args);

            bool manualCheck = HasArgument(args, "--check-update");
            string sourceFolder = AppDomain.CurrentDomain.BaseDirectory;
            string appFolder;
            if (!EnsureCanonicalInstall(sourceFolder, manualCheck, out appFolder)) return 0;

            RegisterUpdateProtocol(Path.Combine(appFolder, "浮生弈.exe"));
            if (manualCheck)
            {
                TryLaunchUpdate(appFolder, true, true);
                return 0;
            }
            if (TryLaunchUpdate(appFolder)) return 0;

            string imageFolder = Path.Combine(appFolder, "images");
            Directory.CreateDirectory(imageFolder);
            List<string> imagePaths = new List<string>();
            HashSet<string> imageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddImageFiles(imageFolder, imagePaths, imageNames);
            AddImageFiles(GetUserImagesFolder(), imagePaths, imageNames);
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

    private static void RegisterUpdateProtocol(string executable)
    {
        try
        {
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

        string localData = GetLocalDataFolder();
        string installFolder = GetInstallFolder();
        if (!PathsEqual(appFolder, installFolder))
        {
            if (showLatest) MessageBox.Show("程序尚未迁移到固定安装目录，请重新打开软件后再检查更新。", "浮生弈更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
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

        if (!AskToInstallUpdate(localVersion, remoteVersion)) return false;

        string versionName = remoteVersion.ToString();
        string updateFolder = Path.Combine(localData, "updates");
        string updateId = Guid.NewGuid().ToString("N");
        string stagingFolder = Path.Combine(updateFolder, "stage-v" + versionName + "-" + updateId);
        string zipPath = Path.Combine(updateFolder, "fushengyi-update-" + updateId + ".zip");
        string helperPath = Path.Combine(updateFolder, "apply-update-" + updateId + ".exe");
        try
        {
            Directory.CreateDirectory(updateFolder);
            DownloadFile(downloadUrl, zipPath, 30000);
            VerifySha256(zipPath, digest);

            Directory.CreateDirectory(stagingFolder);
            ExtractPackage(zipPath, stagingFolder);
            if (!IsValidInstalledVersion(stagingFolder, remoteVersion))
                throw new InvalidDataException("更新包中的版本号或程序文件不正确。");
            try { File.Delete(zipPath); }
            catch { }

            PreserveCustomImages(installFolder, stagingFolder);
            File.Copy(Assembly.GetExecutingAssembly().Location, helperPath, true);
            ProcessStartInfo helper = new ProcessStartInfo();
            helper.FileName = helperPath;
            helper.Arguments = "--apply-update " + QuoteArgument(stagingFolder) + " " + QuoteArgument(installFolder) + " " + versionName + " " + Process.GetCurrentProcess().Id.ToString();
            helper.UseShellExecute = false;
            if (Process.Start(helper) == null) throw new InvalidOperationException("无法启动更新辅助程序。");
            return true;
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true); }
            catch { }
            try { if (File.Exists(zipPath)) File.Delete(zipPath); }
            catch { }
            try { if (File.Exists(helperPath)) File.Delete(helperPath); }
            catch { }
            MessageBox.Show("更新没有完成，继续打开当前版本。\r\n\r\n" + ex.Message, "浮生弈更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private static bool EnsureCanonicalInstall(string sourceFolder, bool manualCheck, out string appFolder)
    {
        string localData = GetLocalDataFolder();
        string installFolder = GetInstallFolder();
        sourceFolder = Path.GetFullPath(sourceFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        installFolder = Path.GetFullPath(installFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (PathsEqual(sourceFolder, installFolder))
        {
            appFolder = installFolder;
            return true;
        }

        Version sourceVersion = ParseVersion(ReadVersionText(sourceFolder));
        Version installedVersion;
        string installedVersionText = ReadVersionText(installFolder);
        bool installedIsValid = Version.TryParse(installedVersionText, out installedVersion) &&
            IsValidInstalledVersion(installFolder, installedVersion);
        if (installedIsValid && installedVersion >= sourceVersion)
        {
            appFolder = installFolder;
            StartExecutable(Path.Combine(installFolder, "浮生弈.exe"), manualCheck ? "--check-update" : null);
            return false;
        }

        Directory.CreateDirectory(localData);
        string stagingFolder = Path.Combine(localData, "migration-stage-" + Guid.NewGuid().ToString("N"));
        string backupFolder = Path.Combine(localData, "app-backup-" + DateTime.UtcNow.Ticks.ToString());
        bool movedOldInstall = false;
        bool movedNewInstall = false;
        try
        {
            CopyDirectory(sourceFolder, stagingFolder);
            if (!IsValidInstalledVersion(stagingFolder, sourceVersion))
                throw new InvalidDataException("当前程序文件不完整，无法迁移到固定安装目录。");

            PreserveCustomImages(sourceFolder, stagingFolder);
            string previousVersionFolder = FindPreviousVersionFolder(sourceFolder, sourceVersion);
            PreserveCustomImages(previousVersionFolder, stagingFolder);
            if (Directory.Exists(installFolder)) PreserveCustomImages(installFolder, stagingFolder);

            if (Directory.Exists(installFolder))
            {
                Directory.Move(installFolder, backupFolder);
                movedOldInstall = true;
            }
            Directory.Move(stagingFolder, installFolder);
            movedNewInstall = true;
            if (!IsValidInstalledVersion(installFolder, sourceVersion))
                throw new InvalidDataException("迁移后的程序文件校验失败。");

            appFolder = installFolder;
            return true;
        }
        catch
        {
            try
            {
                if (movedNewInstall && Directory.Exists(installFolder))
                    Directory.Move(installFolder, stagingFolder + ".failed");
            }
            catch { }
            try
            {
                if (movedOldInstall && Directory.Exists(backupFolder) && !Directory.Exists(installFolder))
                    Directory.Move(backupFolder, installFolder);
            }
            catch { }
            try { if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true); }
            catch { }
            throw;
        }
    }

    private static int ApplyStagedUpdate(string[] args)
    {
        string stagingFolder = null;
        try
        {
            if (args.Length < 5) throw new ArgumentException("更新参数不完整。");
            stagingFolder = Path.GetFullPath(args[1]);
            string installFolder = Path.GetFullPath(args[2]);
            Version expectedVersion;
            int parentProcessId;
            if (!Version.TryParse(args[3], out expectedVersion) || !Int32.TryParse(args[4], out parentProcessId))
                throw new ArgumentException("更新版本或进程参数无效。");

            string localData = GetLocalDataFolder();
            string updatesFolder = Path.Combine(localData, "updates");
            if (!PathsEqual(installFolder, GetInstallFolder()) || !IsChildPath(updatesFolder, stagingFolder))
                throw new InvalidDataException("更新目录不在允许的位置。");
            if (!IsValidInstalledVersion(stagingFolder, expectedVersion))
                throw new InvalidDataException("临时更新包中的程序文件不完整。");

            try
            {
                using (Process parent = Process.GetProcessById(parentProcessId))
                {
                    if (!parent.WaitForExit(30000))
                        throw new TimeoutException("更新程序未能正常退出。");
                }
            }
            catch (ArgumentException) { }

            using (Mutex updateMutex = new Mutex(false, @"Local\FuShengYiUpdate"))
            {
                bool ownsMutex = false;
                try
                {
                    try { ownsMutex = updateMutex.WaitOne(0); }
                    catch (AbandonedMutexException) { ownsMutex = true; }
                    if (!ownsMutex)
                    {
                        try { if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true); }
                        catch { }
                        MessageBox.Show("另一个更新程序正在处理，请等待当前更新完成。", "浮生弈更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 0;
                    }

                    return ApplyStagedUpdateLocked(stagingFolder, installFolder, expectedVersion);
                }
                finally
                {
                    if (ownsMutex) updateMutex.ReleaseMutex();
                }
            }
        }
        catch (Exception ex)
        {
            try { if (stagingFolder != null && Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true); }
            catch { }
            MessageBox.Show("本地替换没有完成，旧版本已保留或恢复。\r\n\r\n" + ex.Message, "浮生弈更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    private static int ApplyStagedUpdateLocked(string stagingFolder, string installFolder, Version expectedVersion)
    {
        Version installedVersion;
        if (Version.TryParse(ReadVersionText(installFolder), out installedVersion) && installedVersion >= expectedVersion)
        {
            try { if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true); }
            catch { }
            return 0;
        }

        DialogResult closeResult = MessageBox.Show(
            "新版已经下载并验证。请先关闭所有打开的浮生弈窗口，再点击“确定”替换本地程序并重启。",
            "浮生弈更新", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (closeResult != DialogResult.OK)
        {
            if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true);
            return 0;
        }

        string updatesFolder = Path.Combine(GetLocalDataFolder(), "updates");
        string backupFolder = Path.Combine(updatesFolder, "backup-v" + expectedVersion + "-" + Guid.NewGuid().ToString("N"));
        bool movedOldInstall = false;
        bool movedNewInstall = false;
        try
        {
            if (Directory.Exists(installFolder))
            {
                Directory.Move(installFolder, backupFolder);
                movedOldInstall = true;
            }
            Directory.Move(stagingFolder, installFolder);
            movedNewInstall = true;
            if (!IsValidInstalledVersion(installFolder, expectedVersion))
                throw new InvalidDataException("替换后的程序文件校验失败。");

            StartExecutable(Path.Combine(installFolder, "浮生弈.exe"));
            if (movedOldInstall && Directory.Exists(backupFolder))
            {
                try { Directory.Delete(backupFolder, true); }
                catch { }
            }
            CleanupLegacyVersions();
            return 0;
        }
        catch
        {
            try
            {
                if (movedNewInstall && Directory.Exists(installFolder))
                    Directory.Move(installFolder, Path.Combine(updatesFolder, "failed-" + Guid.NewGuid().ToString("N")));
            }
            catch { }
            try
            {
                if (movedOldInstall && Directory.Exists(backupFolder) && !Directory.Exists(installFolder))
                    Directory.Move(backupFolder, installFolder);
            }
            catch { }
            if (Directory.Exists(installFolder))
            {
                try { StartExecutable(Path.Combine(installFolder, "浮生弈.exe")); }
                catch { }
            }
            throw;
        }
    }

    private static bool HasArgument(string[] args, string argument)
    {
        return args != null && Array.IndexOf(args, argument) >= 0;
    }

    private static string GetLocalDataFolder()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    }

    private static string GetInstallFolder()
    {
        return Path.Combine(GetLocalDataFolder(), "app");
    }

    private static string GetUserImagesFolder()
    {
        return Path.Combine(GetLocalDataFolder(), "data", "images");
    }

    private static void CleanupLegacyVersions()
    {
        string localData = GetLocalDataFolder();
        string versionsFolder = Path.Combine(localData, "versions");
        if (!Directory.Exists(versionsFolder) || !IsChildPath(localData, versionsFolder)) return;
        try { Directory.Delete(versionsFolder, true); }
        catch { }
    }

    private static Version ParseVersion(string value)
    {
        Version version;
        return Version.TryParse(value, out version) ? version : new Version(1, 0, 0);
    }

    private static bool PathsEqual(string first, string second)
    {
        if (String.IsNullOrWhiteSpace(first) || String.IsNullOrWhiteSpace(second)) return false;
        string a = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string b = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return String.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChildPath(string parent, string candidate)
    {
        string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(candidate);
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value + "\"";
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string FindPreviousVersionFolder(string sourceFolder, Version currentVersion)
    {
        DirectoryInfo parent = Directory.GetParent(Path.GetFullPath(sourceFolder.TrimEnd(Path.DirectorySeparatorChar)));
        if (parent == null || !String.Equals(parent.Name, "versions", StringComparison.OrdinalIgnoreCase)) return null;
        string bestFolder = null;
        Version bestVersion = new Version(0, 0, 0);
        foreach (string folder in Directory.GetDirectories(parent.FullName))
        {
            Version candidate;
            if (!Version.TryParse(ReadVersionText(folder), out candidate) || candidate >= currentVersion || candidate <= bestVersion) continue;
            if (Directory.Exists(Path.Combine(folder, "images")))
            {
                bestFolder = folder;
                bestVersion = candidate;
            }
        }
        return bestFolder;
    }

    private static HashSet<string> ReadManagedImageNames(string packageFolder)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string manifest = Path.Combine(packageFolder, "managed-images.txt");
        if (File.Exists(manifest))
            foreach (string line in File.ReadAllLines(manifest, Encoding.UTF8))
                if (!String.IsNullOrWhiteSpace(line)) names.Add(Path.GetFileName(line.Trim()));
        string imageFolder = Path.Combine(packageFolder, "images");
        if (Directory.Exists(imageFolder))
            foreach (string file in Directory.GetFiles(imageFolder)) names.Add(Path.GetFileName(file));
        return names;
    }

    private static void PreserveCustomImages(string oldPackageFolder, string newPackageFolder)
    {
        if (String.IsNullOrWhiteSpace(oldPackageFolder) || !Directory.Exists(oldPackageFolder)) return;
        string oldImages = Path.Combine(oldPackageFolder, "images");
        if (!Directory.Exists(oldImages)) return;
        HashSet<string> managedNames = ReadManagedImageNames(newPackageFolder);
        string userImages = GetUserImagesFolder();
        foreach (string file in Directory.GetFiles(oldImages))
        {
            if (!IsSupportedImage(file)) continue;
            string name = Path.GetFileName(file);
            if (managedNames.Contains(name) || File.Exists(Path.Combine(newPackageFolder, "images", name))) continue;
            Directory.CreateDirectory(userImages);
            string destination = Path.Combine(userImages, name);
            File.Copy(file, destination, true);
        }
    }

    private static void AddImageFiles(string folder, List<string> output, HashSet<string> names)
    {
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.GetFiles(folder))
        {
            if (!IsSupportedImage(file)) continue;
            if (names.Add(Path.GetFileName(file))) output.Add(file);
        }
    }

    private static bool IsSupportedImage(string file)
    {
        string extension = Path.GetExtension(file).ToLowerInvariant();
        return extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
            extension == ".gif" || extension == ".webp" || extension == ".bmp";
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

    private static bool IsValidInstalledVersion(string folder, Version expected)
    {
        Version installed;
        return File.Exists(Path.Combine(folder, "浮生弈.exe")) &&
            Version.TryParse(ReadVersionText(folder), out installed) && installed == expected;
    }

    private static bool AskToInstallUpdate(Version current, Version latest)
    {
        string message = "发现新版本 " + latest + "（当前版本 " + current + "）。\r\n\r\n现在下载并替换本地程序吗？替换前会提示你关闭软件窗口。";
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
        using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
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
        StartExecutable(path, null);
    }

    private static void StartExecutable(string path, string arguments)
    {
        ProcessStartInfo start = new ProcessStartInfo(path);
        start.UseShellExecute = true;
        if (!String.IsNullOrWhiteSpace(arguments)) start.Arguments = arguments;
        Process.Start(start);
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
