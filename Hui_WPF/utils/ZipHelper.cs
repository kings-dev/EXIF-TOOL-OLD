using System;
using System.Collections.Generic; // 需要添加这个 using 语句
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Hui_WPF // 确保命名空间正确
{
    public static class ZipsHelper
    {
        // --- 添加这些字段来存储翻译和语言 ---
        private static Dictionary<string, Dictionary<string, string>> _translations = new Dictionary<string, Dictionary<string, string>>();
        private static string _currentLanguage = "zh"; // 默认语言
        // ------------------------------------

        // --- 添加这个 Initialize 方法 ---
        /// <summary>
        /// 初始化 ZipsHelper，传递翻译字典和当前语言。
        /// 应该在 MainWindow 构造函数中调用。
        /// </summary>
        public static void Initialize(Dictionary<string, Dictionary<string, string>> translations, string currentLanguage)
        {
            _translations = translations;
            _currentLanguage = currentLanguage;
        }
        // -----------------------------

        // --- 添加这个 SetLanguage 方法 ---
        /// <summary>
        /// 更新 ZipsHelper 使用的当前语言。
        /// 应该在 MainWindow 语言更改时调用。
        /// </summary>
        public static void SetLanguage(string language)
        {
            _currentLanguage = language;
        }
        // ----------------------------

        // --- 添加这个 GetHelperLocalizedString 方法 ---
        /// <summary>
        /// ZipsHelper 内部获取本地化字符串的方法。
        /// </summary>
        private static string GetHelperLocalizedString(string key, string fallback)
        {
            if (_translations.TryGetValue(key, out var langDict))
            {
                if (langDict.TryGetValue(_currentLanguage, out var translation) && !string.IsNullOrEmpty(translation)) { return translation; }
                if (langDict.TryGetValue("en", out var enTranslation) && !string.IsNullOrEmpty(enTranslation)) { return enTranslation; } // Fallback to English
                if (langDict.TryGetValue("zh", out var zhTranslation) && !string.IsNullOrEmpty(zhTranslation)) { return zhTranslation; } // Fallback to Chinese
            }
            // 如果需要，可以在这里添加日志记录缺失的键
            // Debug.WriteLine($"Helper Missing Key '{key}' in lang '{_currentLanguage}'");
            return fallback; // 返回提供的备用文本
        }
        // -----------------------------------------


        /// <summary>
        /// 初始化所有工具（ExifTool、ImageMagick、ffmpeg）
        /// </summary>
        public static void EnsureAllToolsReady(
            IProgress<int>? progress, // 确保接受可空的 IProgress<int>
            IProgress<string> status,
            CancellationToken token)
        {
            var tasks = new[]
            {
                new { Keyword = "exiftool", Folder = "exiftool",   CheckPath = "exiftool.exe",    ZipKey = "exiftool",   RenameTarget = "exiftool(-k).exe", RenameFlag = true  },
                new { Keyword = "ImageMagick", Folder = "ImageMagick", CheckPath = "magick.exe",    ZipKey = "ImageMagick", RenameTarget = string.Empty, RenameFlag = false },
                new { Keyword = "ffmpeg",     Folder = "ffmpeg",      CheckPath = "bin/ffmpeg.exe", ZipKey = "ffmpeg",     RenameTarget = string.Empty, RenameFlag = false }
            };

            int total = tasks.Length;
            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                var t = tasks[i];

                // 使用 GetHelperLocalizedString 来获取状态文本（如果需要的话）
                status.Report($"[{DateTime.Now:HH:mm:ss}] " + GetHelperLocalizedString($"PreparingTool_{t.Keyword}", $"正在准备 {t.Keyword} …")); // 示例：使用本地化

                EnsureToolReady(
                    zipFolder: "Zips",
                    zipKeyword: t.ZipKey,
                    finalFolder: t.Folder,
                    checkRelativePath: t.CheckPath,
                    renameTarget: t.RenameTarget,
                    shouldRename: t.RenameFlag,
                    status: status); // 传递 status

                int percentage = (i + 1) * 100 / total;
                progress?.Report(percentage); // 安全调用

                Thread.Sleep(200); // 保留延迟
            }

            status.Report($"[{DateTime.Now:HH:mm:ss}] " + GetHelperLocalizedString("AllToolsReadyComplete", "全部Zips已准备就绪。")); // 使用本地化
        }

        // EnsureToolReady 方法保持不变 (内部逻辑可以按需调整)
        private static void EnsureToolReady(
            string zipFolder,
            string zipKeyword,
            string finalFolder,
            string checkRelativePath,
            string renameTarget,
            bool shouldRename,
            IProgress<string> status) // 确保 status 参数被接收
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var zipDir = Path.Combine(basePath, zipFolder);
            var toolDir = Path.Combine(basePath, finalFolder); // Final destination
            var checkFullPath = Path.Combine(toolDir, checkRelativePath.Replace('/', Path.DirectorySeparatorChar));

            // 查找匹配 ZIP
            if (!Directory.Exists(zipDir))
            {
                status.Report($"[{DateTime.Now:HH:mm:ss}] ❌ 错误: ZIP 文件夹 '{zipDir}' 不存在。");
                return;
            }
            var zips = Directory.GetFiles(zipDir, "*.zip")
                .Where(f => Path.GetFileName(f).IndexOf(zipKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (!zips.Any())
            {
                // 使用本地化字符串
                status.Report($"[{DateTime.Now:HH:mm:ss}] ❌ " + GetHelperLocalizedString($"ZipNotFound_{zipKeyword}", $"未找到 {zipKeyword} ZIP 包。"));
                return;
            }

            // 选最新版本
            var selectedZip = zips.OrderByDescending(f => ParseVersion(Path.GetFileNameWithoutExtension(f))).FirstOrDefault();
            if (selectedZip == null)
            {
                status.Report($"[{DateTime.Now:HH:mm:ss}] ❌ 无法确定最新的 {zipKeyword} ZIP 包。");
                return;
            }
            var newVer = ParseVersion(Path.GetFileNameWithoutExtension(selectedZip));
            var versionFile = Path.Combine(toolDir, "version.txt");

            // 版本检测跳过
            bool skipUpdate = false;
            if (Directory.Exists(toolDir) && File.Exists(versionFile))
            {
                try
                {
                    var oldVerStr = File.ReadAllText(versionFile).Trim();
                    if (Version.TryParse(oldVerStr, out var oldVer) && oldVer >= newVer)
                    {
                        skipUpdate = true;
                        status.Report($"[{DateTime.Now:HH:mm:ss}] ✅ {zipKeyword} 版本 {oldVer} ≥ 最新 {newVer}，跳过更新。\n"); // <--- 使用 ✅ 图标
                    }
                }
                catch (Exception ex)
                {
                    status.Report($"[{DateTime.Now:HH:mm:ss}] ⚠️ 读取版本文件 '{versionFile}' 出错: {ex.Message}。将尝试更新。");
                }
            }

            if (skipUpdate) return;

            // 解压到临时目录 (使用 Path.GetTempPath() 更可靠)
            var tempDir = Path.Combine(Path.GetTempPath(), zipKeyword + "_Temp_" + Guid.NewGuid().ToString("N"));
            try
            {
                status.Report($"[{DateTime.Now:HH:mm:ss}] ⏳ 解压 {Path.GetFileName(selectedZip)} 到 {tempDir}..."); // <--- 使用 ⏳ 图标
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); // Ensure clean temp dir
                ZipFile.ExtractToDirectory(selectedZip, tempDir);
                status.Report($"[{DateTime.Now:HH:mm:ss}] ℹ️ 解压完成."); // <--- 添加解压完成日志

                // 查找解压后的源目录
                var extractedDirs = Directory.GetDirectories(tempDir);
                var extractedFiles = Directory.GetFiles(tempDir);
                string sourceDirToProcess = tempDir;
                if (extractedDirs.Length == 1 && extractedFiles.Length == 0)
                {
                    sourceDirToProcess = extractedDirs[0];
                    status.Report($"[{DateTime.Now:HH:mm:ss}] ℹ️ 解压后找到单根目录: {sourceDirToProcess}");
                }
                else
                {
                    status.Report($"[{DateTime.Now:HH:mm:ss}] ℹ️ 解压后直接处理临时目录内容: {sourceDirToProcess}");
                }

                // ExifTool 重命名 (在临时目录中进行)
                if (shouldRename && !string.IsNullOrEmpty(renameTarget))
                {
                    var oldExe = Directory.GetFiles(sourceDirToProcess, renameTarget, SearchOption.AllDirectories).FirstOrDefault();
                    if (oldExe != null)
                    {
                        string targetFileName = Path.GetFileName(checkRelativePath.Replace('/', Path.DirectorySeparatorChar));
                        var newExePath = Path.Combine(Path.GetDirectoryName(oldExe)!, targetFileName);
                        try
                        {
                            File.Move(oldExe, newExePath, true);
                            status.Report($"[{DateTime.Now:HH:mm:ss}] ℹ️ 已在临时目录重命名 {renameTarget} -> {targetFileName}");
                        }
                        catch (Exception rnEx)
                        {
                            status.Report($"[{DateTime.Now:HH:mm:ss}] ❌ 在临时目录重命名时出错: {rnEx.Message}");
                            // Decide if fatal
                        }
                    }
                    else
                    {
                        status.Report($"[{DateTime.Now:HH:mm:ss}] ⚠️ 未在解压文件中找到需要重命名的 '{renameTarget}'。");
                    }
                }

                // --- 使用复制代替移动 ---
                status.Report($"[{DateTime.Now:HH:mm:ss}] ⏳ 正在复制新版本 {zipKeyword} 到 {toolDir}..."); // <--- 使用 ⏳ 图标
                // 1. 清理旧的目标文件夹 (如果存在)
                if (Directory.Exists(toolDir))
                {
                    status.Report($"[{DateTime.Now:HH:mm:ss}] ℹ️ 清理旧目录: {toolDir}");
                    try { Directory.Delete(toolDir, true); }
                    catch (IOException ioEx) { status.Report($"[{DateTime.Now:HH:mm:ss}] ⚠️ 清理旧目录 '{toolDir}' 时出错 (可能被占用): {ioEx.Message}"); Thread.Sleep(500); try { Directory.Delete(toolDir, true); } catch (Exception finalEx) { status.Report($"[{DateTime.Now:HH:mm:ss}] ❌ 无法删除旧目录 '{toolDir}': {finalEx.Message}"); throw; } }
                    catch (Exception ex) { status.Report($"[{DateTime.Now:HH:mm:ss}] ❌ 清理旧目录 '{toolDir}' 时发生未知错误: {ex.Message}"); throw; }
                }

                // 2. 创建目标文件夹 (CopyDirectory 内部会创建)
                // Directory.CreateDirectory(toolDir); // 不需要这行了

                // 3. 递归复制文件夹内容
                CopyDirectory(sourceDirToProcess, toolDir, status); // <--- 调用复制方法
                // --- 修改结束 ---

                // 写入新版本文件
                File.WriteAllText(versionFile, newVer.ToString());

                status.Report($"[{DateTime.Now:HH:mm:ss}] ✅ {zipKeyword} 更新完成 (版本 {newVer})。\n"); // <--- 使用 ✅ 图标
            }
            catch (Exception ex)
            {
                status.Report($"[{DateTime.Now:HH:mm:ss}] ❌ 处理 {zipKeyword} 时发生错误: {ex.Message}");
                // Consider re-throwing if needed: throw;
            }
            finally
            {
                // 清理临时目录
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); }
                    catch (Exception ex) { status.Report($"[{DateTime.Now:HH:mm:ss}] ⚠️ 清理临时目录 '{tempDir}' 时出错: {ex.Message}"); }
                }
            }
        }

        private static void CopyDirectory(string sourceDir, string destinationDir, IProgress<string> status)
        {
            DirectoryInfo dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists) { throw new DirectoryNotFoundException($"Source directory does not exist: {sourceDir}"); }
            DirectoryInfo[] dirs = dir.GetDirectories();
            Directory.CreateDirectory(destinationDir);
            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                try { string tempPath = Path.Combine(destinationDir, file.Name); file.CopyTo(tempPath, true); }
                catch (IOException ioEx) { status.Report($"[{DateTime.Now:HH:mm:ss}] ⚠️ 复制文件 '{file.Name}' 时出错 (IO): {ioEx.Message}"); }
                catch (UnauthorizedAccessException uaEx) { status.Report($"[{DateTime.Now:HH:mm:ss}] ⚠️ 复制文件 '{file.Name}' 时权限不足: {uaEx.Message}"); }
                catch (Exception ex) { status.Report($"[{DateTime.Now:HH:mm:ss}] ❌ 复制文件 '{file.Name}' 时发生未知错误: {ex.Message}"); }
            }
            foreach (DirectoryInfo subdir in dirs) { string tempPath = Path.Combine(destinationDir, subdir.Name); CopyDirectory(subdir.FullName, tempPath, status); }
        }

        // ParseVersion 方法保持不变
        private static Version ParseVersion(string name)
        {
            var m = Regex.Match(name, "(\\d+(?:\\.\\d+)+)");
            // 增加一个默认版本，避免 Version.TryParse 失败时返回 null
            return m.Success && Version.TryParse(m.Value, out var v) ? v : new Version(0, 0);
        }


    }
}