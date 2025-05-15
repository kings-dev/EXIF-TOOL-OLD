using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hui_WPF.Properties; // Ensure this namespace exists and is correct
using Microsoft.Win32;
using Ookii.Dialogs.Wpf; // Ensure NuGet package Ookii.Dialogs.Wpf is installed
using System.Collections.Generic;
using Hui_WPF.utils;     // ← 引入你的工具类命名空间


#nullable enable // Enable nullable reference type checking


namespace Hui_WPF
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    /// 
    //public class DirectoryRule
    //{
    //    public string Prefix { get; set; }
    //    public int Count { get; set; }
    //    public bool Recursive { get; set; }
    //    public List<DirectoryRule> SubRules { get; set; } = new();

    //    public override string ToString()
    //    {
    //        return $"{Prefix} ×{Count} {(Recursive ? "[递归]" : "")}";
    //    }
    //}

    public partial class MainWindow : Window
    {
        // --- Fields ---

    private static readonly int MaxConcurrentProcesses = Math.Max(1, Environment.ProcessorCount / 2);//多线程 
        private readonly SemaphoreSlim processSemaphore = new SemaphoreSlim(MaxConcurrentProcesses, MaxConcurrentProcesses); //多线程



        // Parallel Processing Control
        //private static readonly int LogicalCores = Environment.ProcessorCount;
        //// 2) 按 80% 计算目标线程数，向下取整，至少 1
        //private static readonly int MaxConcurrentProcesses =
        //    Math.Max(1, (int)Math.Floor(LogicalCores * 0.8));
        //// 3) 用 SemaphoreSlim 控制并发
        //private readonly SemaphoreSlim processSemaphore =
        //    new SemaphoreSlim(MaxConcurrentProcesses, MaxConcurrentProcesses);

        //private static readonly int MaxConcurrentProcesses = Math.Max(1, Environment.ProcessorCount); // Ensure at least 1
        //private readonly SemaphoreSlim processSemaphore = new SemaphoreSlim(MaxConcurrentProcesses, MaxConcurrentProcesses);
        private volatile int _activeTasks = 0; // Counter for active parallel tasks

        // Time Tracking
        private DateTime startTime;
        private readonly Stopwatch processStopwatch = new Stopwatch();
        private DispatcherTimer? realTimeTimer;

        // File Processing Settings & State
        private readonly string[] supportedExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".heic", ".webp" };
        private int totalImages = 0;    // Total items for the current operation (files or folders)
        private int processedImages = 0;// Items successfully processed
        private int failedImages = 0;   // Items failed during processing
        private List<string> inputPaths = new List<string>(); // User selected input files/folders
        private readonly Regex invalidFileNameCharsRegex = new Regex($"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]");
        private readonly Regex invalidPathCharsRegex = new Regex($"[{Regex.Escape(new string(Path.GetInvalidPathChars()))}]");

        // --- Naming Formats/Values Defaults ---
        private const string DefaultTimestampFormat = "yyyyMMdd_HHmmss_"; // Keep trailing underscore from scenarios
        private const string DefaultCounterFormat = "D2";
        private const int DefaultCounterStartValue = 1;
        private const bool DefaultEnableTimestamp = true;
        private const bool DefaultEnableCounter = true;

        // Backup/Rename State
        // Maps Original Source Path -> New Path (after move/rename)
        private Dictionary<string, string> sourceToBackupPathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Only used for Direct Rename mode folder tracking
        private Dictionary<string, string> folderRenameMap_DirectModeOnly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // UI State
        private Brush defaultBackColor;
        private Brush dragEnterBackColor = Brushes.LightGreen;
        private bool isUpdatingCheckboxes = false;
        private bool isTextBoxFocused = false;
        private Brush defaultTextBoxForeground;
        private Brush placeholderForeground = Brushes.Gray;

        // Cancellation & Language & Settings
        private CancellationTokenSource? cancellationTokenSource;
        private string currentLanguage = "zh";
        private readonly Dictionary<string, Dictionary<string, string>> translations = new Dictionary<string, Dictionary<string, string>>();
        private ZoompanSettings currentZoompanSettings = new ZoompanSettings();
        private Random random = new Random();

        // Property to check processing state
        private bool isProcessing => cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested;


        // --- Constructor ---
        public MainWindow()
        {

            InitializeComponent();
            InitializeTranslations();
            //Loaded += MainWindow_Loaded;
            //UpdatePreview(); // 启动时预览
            //RefreshPreview(); // 初始化预览
            InitializeRealTimeTimer();
            defaultBackColor = pnlDragDrop.Background;
            defaultTextBoxForeground = txtFolderPath.Foreground;
            placeholderForeground = Brushes.Gray;
            LoadSettings();
            ZipsHelper.Initialize(translations, currentLanguage);
            try { Uri iconUri = new Uri("pack://application:,,,/assets/ExifDog.ico", UriKind.RelativeOrAbsolute); this.Icon = BitmapFrame.Create(iconUri); } catch (Exception ex) { Debug.WriteLine($"Warning: Icon load failed: {ex.Message}"); }
            SetupLanguageComboBox();
            // Initial CheckBox states
            chkDisableBackup.IsChecked = false; chkEnableBackup.IsChecked = true; // Backup enabled by default
            chkUseCustomBackupPath2.IsChecked = Settings.Default.UserBackupOutputPath?.Length > 0; // Check based on loaded setting
            chkEnableZoompan.IsChecked = false; chkTimestampSubfolder.IsChecked = true;
            chkBurstMode.IsChecked = false; rbFormatMov.IsChecked = true;
            // Naming checkboxes (chkEnableTimestamp, chkEnableCounter) are set by LoadSettings
            // Custom Output checkboxes are set by LoadSettings
            this.Dispatcher.InvokeAsync(() =>
            {
                UpdatePreview(); // 这个 UpdatePreview 是 MainWindow.DirectoryPreview.cs 中的
            }, DispatcherPriority.ContextIdle); // ContextIdle 通常是一个不错的选择，表示在当前主要工作完成后
            // Apply language and initial UI state
            ApplyLanguage();
            UpdateBackupControlsState();
            UpdateVideoControlsState();
            UpdateOutputControlsState();
            UpdateUIState();
            // Trigger initial UI linkage
            ChkEnableTimestamp_CheckedChanged(null, null);
            ChkEnableCounter_CheckedChanged(null, null);
            ChkUseCustomImageOutputPath_CheckedChanged(null, null);
            ChkUseCustomVideoOutputPath_CheckedChanged(null, null);
            chkUseCustomBackupPath2_CheckedChanged(null, null); // Link backup path enabled state
        }




        // --- Real Time Clock ---
        private void InitializeRealTimeTimer() { realTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; realTimeTimer.Tick += RealTimeTimer_Tick; realTimeTimer.Start(); RealTimeTimer_Tick(null, EventArgs.Empty); }
        private void RealTimeTimer_Tick(object? sender, EventArgs e) { if (lblRealTimeClock != null) { lblRealTimeClock.Text = DateTime.Now.ToString("HH:mm:ss"); } }

        // --- Translations (Paste ALL key/value pairs here) ---
        private void InitializeTranslations()
        {
            void AddTrans(string key, string en, string zh) { if (!translations.TryAdd(key, new Dictionary<string, string> { { "en", en }, { "zh", zh } })) { Debug.WriteLine($"Warn: Duplicate translation key '{key}'."); } }
            // --- PASTE ALL PREVIOUSLY DEFINED TRANSLATIONS HERE ---
            AddTrans("WindowTitle", "ExifDog - EXIF And Video Tool", "ExifDog - EXIF 和 视频工具");
            AddTrans("SelectFolderLabel", "Select Folder", "📂 选择文件夹");
            AddTrans("SelectImagesLabel", "Select Images", "📂 选择源图片");
            AddTrans("BrowseFolderLabel", "Browse...", "浏览...");
            AddTrans("StartProcessingLabel", "Start Processing", "EXIF 清除/重命名");
            AddTrans("GenerateZoompanLabel", "Generate Video/Animation", "生成视频/动画");
            AddTrans("RenameFoldersOnlyLabel", "Rename Folders Only (_O)", "仅重命名文件夹 (_O)");
            AddTrans("RenameFilesOnlyLabel", "Rename Files Only (_N)", "仅重命名源文件 (_N)");
            AddTrans("EnableBackupLabel", "Enable Backup (_B)", "启用备份 (_B)");
            AddTrans("DisableBackupLabel", "Disable Backup (_D)", "禁用备份 (_D)");
            AddTrans("UseCustomBackupPathLabel", "Custom Backup Path", "自定义备份输出路径");
            AddTrans("SelectCustomBackupPathLabel", "Browse Backup Output Folder", "浏览备份输出文件夹");
            AddTrans("RenamePrefixLabel", "Rename Prefix:", "重命名源文件前缀:");
            AddTrans("BackupPrefixLabel", "Backup or Folder Prefix:", "备份或文件夹前缀:");
            AddTrans("ProcessingToolLabel", "Processing Tool:", "处理工具:");
            AddTrans("EnableZoompanLabel", "Enable ZoomPan Effect", "启用 ZoomPan 效果");
            AddTrans("ZoompanSettingsButtonLabel", "Effect Settings...", "效果设置...");
            AddTrans("TimestampSubfolderLabel", "Output to Timestamp Subfolder", "输出到时间戳子文件夹");
            AddTrans("OutputResolutionLabel", "Output Resolution (Non-Burst):", "输出分辨率 (非连拍模式):");
            AddTrans("ProgressHintLabel", "Progress", "处理进度");
            AddTrans("LogLabel", "Processing Log:", "处理日志:");
            AddTrans("ClearLog", "Clear Log", "清除日志");
            AddTrans("SaveLog", "Save Log", "保存日志");
            AddTrans("ProcessStatusLabelInitial", "Ready", "就绪");
            AddTrans("UnselectedState", "No folder/source images selected", "未选择文件夹/源图片");
            AddTrans("Selected", "Selected: {0}", "已选择: {0}");
            AddTrans("Folders", "Folders", "文件夹");
            AddTrans("Files", "Files", "文件");
            AddTrans("And", " and ", " 和 ");
            AddTrans("InvalidItemsSelected", "Invalid items selected", "选择了无效的项目");
            AddTrans("ProgressReady", "Ready to process", "准备处理");
            AddTrans("SupportedImageFiles", "Supported Image Files", "支持的图片文件");
            AddTrans("AllFiles", "All Files", "所有文件");
            AddTrans("SelectFolder", "Select Folder", "选择文件夹");
            AddTrans("SelectOneOrMoreImages", "Select One or More Images", "选择一个或多个图片");
            AddTrans("SelectCustomBackupFolderTitle", "Select Custom Backup Folder", "选择自定义备份文件夹");
            AddTrans("SelectFilesAndFoldersTitle", "Select Files and/or Folders", "选择文件和/或文件夹");
            AddTrans("SelectItemsPrompt", "Select Items", "选择项目");
            AddTrans("AllFilesFilter", "All Files (*.*)|*.*", "所有文件 (*.*)|*.*");
            AddTrans("Tip", "Tip", "提示");
            AddTrans("SaveErrorTitle", "Save Error", "保存错误");
            AddTrans("DirectRename_ErrorTitle", "Direct Rename Error", "直接重命名错误");
            AddTrans("ProcessingErrorTitle", "Processing Error", "处理错误");
            AddTrans("RetryTitle", "Rename Access Denied", "重命名访问被拒绝");
            AddTrans("TextFile", "Text File", "文本文件");
            AddTrans("ZoompanSettingsTitle", "Effect Settings", "效果参数设置");
            AddTrans("ZoompanGenerationErrorTitle", "Video/Animation Generation Error", "视频/动画生成错误");
            AddTrans("Tooltip_EnableBackup", "Enable Backup: Moves original items to a backup location before processing.", "启用备份：处理前将原始项目移动到备份位置。");
            AddTrans("Tooltip_DisableBackup", "Disable Backup: Skips moving originals. Processes files in-place (if no custom output) and deletes original after successful processing.", "禁用备份：跳过移动原始文件。原地处理文件（若无自定义输出）并在成功处理后删除原始文件。");
            AddTrans("Tooltip_StartProcessing", "Start the selected EXIF cleaning or direct rename operation.", "开始所选的 EXIF 清理或直接重命名操作。");
            AddTrans("Tooltip_SelectFolder", "Select a single folder to process (replaces current selection).", "选择单个文件夹进行处理（替换当前选择）。");
            AddTrans("Tooltip_SelectImages", "Select one or more image files to process (replaces current selection).", "选择一个或多个图片文件进行处理（替换当前选择）。");
            AddTrans("Tooltip_BrowseFolder", "Browse for files and/or folders to process (replaces current selection).", "浏览要处理的文件和/或文件夹（替换当前选择）。");
            AddTrans("Tooltip_Log", "Log of operations and errors. Right-click to Clear or Save.", "操作和错误日志。右键单击可清除或保存。");
            AddTrans("Tooltip_DragDropPanel", "Drag and drop folders or image files here, or paste a folder path into the text box.", "在此处拖放文件夹或图片文件，或将文件夹路径粘贴到文本框中。");
            AddTrans("Tooltip_FolderPath", "Shows the selected folder or a summary. Paste a folder path here or drag items.", "显示选定的文件夹或摘要。在此粘贴文件夹路径或拖放项目。");
            AddTrans("Tooltip_ImageCount", "Shows the number of selected items.", "显示所选项目的数量。");
            AddTrans("Tooltip_ProgressHint", "Shows current processing progress.", "显示当前处理进度。");
            AddTrans("Tooltip_RenamePrefix", "Prefix used for processed files (EXIF or Video modes) or directly renamed files.", "用于已处理文件（EXIF 或视频模式）或直接重命名文件的前缀。");
            AddTrans("Tooltip_RenamePrefixText", "Enter the desired prefix for processed/renamed files. Invalid characters removed.", "输入处理/重命名文件的所需前缀。无效字符将被删除。");
            AddTrans("Tooltip_BackupPrefix", "Prefix for backup folders (EXIF/Video Modes) or directly renamed folders.", "用于备份文件夹（EXIF/视频模式）或直接重命名文件夹的前缀。");
            AddTrans("Tooltip_BackupPrefixText", "Enter the desired prefix for backup/renamed folders. Invalid characters removed.", "输入备份/重命名文件夹的所需前缀。无效字符将被删除。");
            AddTrans("Tooltip_RenameFoldersOnly", "Direct Rename Mode: Only rename folders using prefix/timestamp/counter. Skips files/EXIF/Video. Backup disabled.", "直接重命名模式：仅使用前缀/时间戳/计数器重命名文件夹。跳过文件/EXIF/视频。备份被禁用。");
            AddTrans("Tooltip_RenameFilesOnly", "Direct Rename Mode: Only rename files using prefix/timestamp/counter. Skips folders/EXIF/Video. Backup disabled.", "直接重命名模式：仅使用前缀/时间戳/计数器重命名文件。跳过文件夹/EXIF/视频。备份被禁用。");
            AddTrans("Tooltip_UseCustomBackupPath", "Use a specific folder for backups (if Backup Enabled). Overrides default backup location rules.", "使用特定文件夹进行备份（如果启用了备份）。覆盖默认备份位置规则。");
            AddTrans("Tooltip_CustomBackupPathText", "Enter or browse to the custom backup location.", "输入或浏览到自定义备份位置。");
            AddTrans("Tooltip_SelectCustomBackupPath", "Browse for a custom backup folder.", "浏览自定义备份文件夹。");
            AddTrans("Tooltip_LanguageSelector", "Select the display language.", "选择显示语言。");
            AddTrans("Tooltip_EnableZoompan", "Enable ZoomPan effects (unless Burst Mode is checked).", "启用 ZoomPan 效果（除非勾选了连拍模式）。");
            AddTrans("Tooltip_ZoompanSettingsButton", "Open detailed settings for effects (presets, custom zoom/pan, duration, FPS).", "打开效果的详细设置（预设、自定义缩放/平移、时长、帧率）。");
            AddTrans("Tooltip_TimestampSubfolder", "Output generated videos/animations into a subfolder named with the current timestamp (requires 'Enable Timestamp' also). Not used if Custom Video Output is set.", "将生成的视频/动画输出到以当前日期和时间命名的子文件夹中 (也需要勾选“启用时间戳”)。如果设置了自定义视频输出路径则不使用。");
            AddTrans("Tooltip_OutputResolution", "Select the resolution for the output video (ignored in Burst Mode).", "选择输出视频的分辨率（连拍模式下忽略）。");
            AddTrans("Tooltip_ProcessingToolSelector", "Select the tool to use for metadata cleaning (EXIF Mode).", "选择用于清理元数据的工具（EXIF模式）。");
            AddTrans("Tooltip_GenerateZoompan", "Start generating video or animation based on selected options.", "根据所选选项开始生成视频或动画。");
            AddTrans("ProcessingCompleted", "Processing Completed. Processed: {0}, Failed: {1}.", "处理完成。已处理: {0}, 失败: {1}。");
            AddTrans("DirectRename_FinishedLog", "Direct Rename Finished. Processed: {0}, Failed: {1}.", "直接重命名完成。已处理: {0}, 失败: {1}。");
            AddTrans("AllToolsReadyComplete", "[Complete] All tools are ready.", "【完成】所有工具已就绪。");
            AddTrans("CheckingTools", "Checking tools...", "正在检查工具...");
            AddTrans("ZipsHelperNotFoundWarn", "[Warning] ZipsHelper or EnsureAllToolsReady not found. Skipping tool check.", "【警告】ZipsHelper 或 EnsureAllToolsReady 未找到。跳过工具检查。");
            AddTrans("ToolCheckCancelled", "[Cancelled] Tool check operation cancelled.", "【取消】工具检查操作已取消。");
            AddTrans("ToolCheckError", "[Tool Check Error] {0}", "【工具检查错误】{0}");
            AddTrans("FormatErrorMarker", "(Format Error)", "(格式错误)");
            AddTrans("RenamedDefaultPrefix", "Renamed", "已重命名");
            AddTrans("BackupDefaultPrefix", "Backup", "备份");
            AddTrans("NoStackTrace", "No stack trace available", "无可用堆栈跟踪");
            AddTrans("NoSpecificError", "(No specific error message)", "(无具体错误信息)");
            AddTrans("CustomPathReverting", "Reverting to default backup logic.", "恢复为默认备份逻辑。");
            AddTrans("RetryAttempt", "Retry", "重试");
            AddTrans("DropProcessingStart", "--- Processing dropped items (appending) ---", "--- 处理拖放的项目 (追加) ---");
            AddTrans("DropAddedItems", "Added {0} new valid items from drop.", "从拖放中添加了 {0} 个新的有效项目。");
            AddTrans("DropNoNewItems", "No new valid items added from drop (duplicates or invalid).", "未从拖放中添加新的有效项目 (重复或无效)。");
            AddTrans("IgnoringInvalidPath", "Ignoring invalid or non-existent path: {0}", "忽略无效或不存在的路径: {0}");
            AddTrans("IgnoringDuplicateItem", "Ignoring duplicate item: {0}", "忽略重复项目: {0}");
            AddTrans("AddedFolder", "Added Folder: {0}", "已添加文件夹: {0}");
            AddTrans("AddedImageFile", "Added Image: {0}", "已添加图片: {0}");
            AddTrans("AddedNonImageFile", "Added File: {0}", "已添加文件: {0}");
            AddTrans("ErrorAddingPath", "Error: Failed to add path '{0}': {1}", "错误: 添加路径 '{0}' 失败: {1}");
            AddTrans("TextBoxPathSelected", "Path selected via text box: {0}", "通过文本框选择路径: {0}");
            AddTrans("TextBoxCleared", "Input cleared.", "输入已清除。");
            AddTrans("ErrorProcessingTextBoxPath", "Error: Processing path from text box '{0}' failed: {1}", "错误: 处理文本框路径 '{0}' 失败: {1}");
            AddTrans("FolderSelectionComplete", "Added {0} folder(s).", "添加了 {0} 个文件夹。");
            AddTrans("FolderSelectionCancelled", "Folder selection cancelled.", "文件夹选择已取消。");
            AddTrans("ImageSelectionStart", "--- Image Selection ---", "--- 图片选择 ---");
            AddTrans("ImageSelectionComplete", "Added {0} file(s).", "添加了 {0} 个文件。");
            AddTrans("ImageSelectionCancelled", "Image selection cancelled.", "图片选择已取消。");
            AddTrans("SetInitialPathError", "Error setting initial path: {0}", "设置初始路径时出错: {0}");
            AddTrans("NoFilesSelected", "No folder/images selected.", "未选择文件夹/图片。");
            AddTrans("CustomPathInvalid", "Custom backup path invalid: {0}.", "自定义备份路径无效: {0}。");
            AddTrans("CustomPathValid", "Using custom backup path: {0}", "使用自定义备份: {0}");
            AddTrans("CustomPathVerifyError", "Error verifying custom path: {0}", "验证自定义路径出错: {0}");
            AddTrans("CustomPathEmptyWarning", "Warn: Custom path empty. Using default.", "警告:自定义路径为空，使用默认。");
            AddTrans("DirectRename_StartLog", "--- Direct Rename Start ---", "--- 开始直接重命名 ---");
            AddTrans("DirectRename_OptionFolder", "Mode: Folders Only", "模式: 仅文件夹");
            AddTrans("DirectRename_OptionFolderWithPrefix", "Mode: Folders Only (Prefix: '{0}')", "模式: 仅文件夹(前缀:'{0}')");
            AddTrans("DirectRename_OptionFile", "Mode: Files Only (Prefix: '{0}')", "模式: 仅文件(前缀:'{0}')");
            AddTrans("DirectRename_DefaultFilePrefixInfo", "Info: Using default file prefix '{0}'.", "提示:使用默认文件前缀'{0}'。");
            AddTrans("DirectRename_FolderPrefixEmptyInfo", "Info: Folder prefix empty.", "提示:文件夹前缀为空。");
            AddTrans("ExifMode_StartLog", "--- EXIF Clean/Rename Start ---", "--- 开始EXIF清理/重命名 ---");
            AddTrans("ExifMode_BackupEnabled", "Backup: Enabled (Default Path Logic)", "备份:启用(默认路径逻辑)");
            AddTrans("ExifMode_BackupEnabledCustom", "Backup: Enabled (Custom Path: '{0}')", "备份:启用(自定义路径:'{0}')");
            AddTrans("ExifMode_BackupDisabled", "Backup: Disabled", "备份:禁用");
            AddTrans("ProcessingReady", "Processing ready...", "准备处理...");
            AddTrans("ProcessingCancelled", "Processing cancelled.", "处理已取消。");
            AddTrans("DirectRename_FatalError", "FATAL RENAME ERROR: {0}\n{1}", "严重重命名错误:{0}\n{1}");
            AddTrans("FatalProcessingError", "FATAL PROCESSING ERROR: {0}\n{1}", "严重处理错误:{0}\n{1}");
            AddTrans("NoImagesFound", "No supported images found.", "未找到支持的图片。");
            AddTrans("OpenFolderComplete", "Opening folder: {0}", "正在打开: {0}");
            AddTrans("OpenFolderFailed", "Could not open folder '{0}': {1}", "无法打开'{0}': {1}");
            AddTrans("OpenFolderFallback", "Target invalid, opening fallback: {1}", "目标无效,打开备用:{1}");
            AddTrans("OpenFolderFallbackFailed", "Target & fallback invalid: {0}", "目标和备用均无效:{0}");
            AddTrans("CollectingFiles", "Collecting files...", "收集文件...");
            AddTrans("ScanningFolder", "Scanning: {0}", "扫描中: {0}");
            AddTrans("WarningScanningFolder", "Warn scan '{0}': {1}-{2}", "警告扫描'{0}': {1}-{2}");
            AddTrans("CollectionComplete", "Found {0} files.", "找到 {0} 文件。");
            AddTrans("StartingProcessing", "Processing {0} images...", "处理 {0} 图片...");
            AddTrans("ExifToolNotFound", "ExifTool not found: {0}. Check 'exiftool'.", "找不到ExifTool:{0}。检查'exiftool'。");
            AddTrans("ImageMagickNotFound", "ImageMagick not found: {0}. Check 'ImageMagick'.", "找不到 ImageMagick: {0}。检查 'ImageMagick' 子目录。");
            AddTrans("FFprobeNotFound", "Error: ffprobe.exe not found. Ensure ffmpeg tools are in 'ffmpeg/bin' subdirectory.", "错误：找不到 ffprobe.exe。请确保 ffmpeg 工具位于 'ffmpeg/bin' 子目录中。");
            AddTrans("FFmpegNotFound", "Error: ffmpeg.exe not found. Ensure ffmpeg tools are in 'ffmpeg/bin' subdirectory.", "错误：找不到 ffmpeg.exe。请确保 ffmpeg 工具位于 'ffmpeg/bin' 子目录中。");
            AddTrans("CustomPathCreateAttempt", "Creating custom backup dir: {0}", "创建自定义备份:{0}");
            AddTrans("ProcessingFile", "Processing(B): {0}", "处理中(备):{0}");
            AddTrans("ProcessingFileNoBackup", "Processing: {0}", "处理中:{0}");
            AddTrans("ErrorDeterminingDirectory", "Cannot get directory for: {0}", "无法获取目录:{0}");
            AddTrans("BackupFolderExists", "Warn: Backup target path exists: {0}. Cannot move source here.", "警告:备份目标路径已存在:{0}。无法移动源到此处。"); // Updated message
            AddTrans("MovingFolderToBackup", "Moving folder to backup: '{0}' -> '{1}'", "移动文件夹到备份:'{0}'->'{1}'");
            AddTrans("ErrorMovingFolder", "ERROR moving folder '{0}'->'{1}': {2}", "错误移动文件夹'{0}'->'{1}':{2}");
            AddTrans("BackupFolderExpectedNotFound", "ERR: Expected backup folder not found: {0}. Skip '{1}'.", "错误:预期备份文件夹未找到:{0}.跳过'{1}'.");
            AddTrans("BackupFileNotFound", "ERR: File not found in backup: {0}. Skip.", "错误:备份中无文件:{0}.跳过.");
            AddTrans("CreatingBackupDirectory", "Creating file backup dir: {0}", "创建文件备份目录:{0}");
            AddTrans("FileNotFoundBackup", "ERR: Original file not found for backup: {0}. Skip.", "错误:找不到原文件备份:{0}.跳过.");
            AddTrans("MovingFileToBackup", "Moving file to backup: '{0}' -> '{1}'", "移动文件到备份:'{0}'->'{1}'");
            AddTrans("ErrorMovingFile", "ERROR moving file '{0}'->'{1}': {2}. Skip.", "错误移动文件'{0}'->'{1}':{2}.跳过.");
            AddTrans("ErrorCreatingOutputFolder", "ERROR creating output folder '{0}': {1}. Skip.", "错误创建输出'{0}':{1}.跳过.");
            AddTrans("ExifToolSourceNotFound", "ERR: Source not found for tool: {0}.", "错误:工具源未找到:{0}。"); // Removed Skip.
            AddTrans("SuccessRename", "OK: Cleaned '{0}'->'{1}'", "成功:已清理'{0}'->'{1}'");
            AddTrans("SuccessProcessed", "OK: Processed '{0}' -> '{1}'", "成功: 已处理 '{0}' -> '{1}'");
            AddTrans("DeletingOriginalAfterSuccess", "Deleting original (no backup/in-place): {0}", "删除原文件(无备份/原地):{0}");
            AddTrans("ErrorDeletingOriginal", "ERR delete original '{0}': {1}", "错误删除原文件'{0}':{1}");
            AddTrans("ExifToolFailed", "FAIL ExifTool(Code {1}) for '{0}'. Err: {2}.", "失败 ExifTool(代码 {1})于'{0}'.错:{2}.");
            AddTrans("ImageMagickFailed", "FAIL ImageMagick(Code {1}) for '{0}'. Err: {2}.", "失败 ImageMagick(代码 {1})于'{0}'.错:{2}.");
            AddTrans("UnexpectedErrorProcessingFile", "UNEXPECTED ERR process '{0}': {1}-{2}.", "意外错误处理'{0}':{1}-{2}.");
            AddTrans("ProcessedCounts", "Done: {0}, Fail: {1}, Total: {2}", "完成:{0},失败:{1},总计:{2}");
            AddTrans("ProgressCounts", "Prog: {0}/{1}", "进度:{0}/{1}");
            AddTrans("ErrorMatchingInputPath", "Warn match path '{0}' vs '{1}': {2}", "警告匹配'{0}'和'{1}':{2}");
            AddTrans("ErrorNoInputContext", "ERR no input context for '{0}'.", "错误:无'{0}'上下文");
            AddTrans("ErrorCheckingFolderPath", "ERR check path '{0}': {1}", "错误检查路径'{0}':{1}");
            AddTrans("ClearLogMessage", "Log Cleared.", "日志已清除。");
            AddTrans("LogSaved", "Log saved: {0}", "日志已保存:{0}");
            AddTrans("ErrorSavingLog", "ERR save log: {0}", "错误保存日志:{0}");
            AddTrans("CancelRequested", "Cancel requested...", "请求取消...");
            AddTrans("DirectRename_Preparing", "Direct Rename: Prepare...", "直接重命名:准备...");
            AddTrans("DirectRename_FoundFolders", "Direct Rename: Found {0} folders.", "直接重命名:找到{0}文件夹。");
            AddTrans("DirectRename_FoundFiles", "Direct Rename: Found {0} files.", "直接重命名:找到{0}文件。");
            AddTrans("DirectRename_StartFolders", "Direct Rename: Renaming folders...", "直接重命名:重命名文件夹...");
            AddTrans("DirectRename_FolderStatus", "Folder: {0} ({1}/{2})", "文件夹:{0}({1}/{2})");
            AddTrans("DirectRename_FolderNotFound", "ERR Rename: Folder not found: {0} (At {1})", "错误重命名:文件夹未找到:{0}(在{1})");
            AddTrans("DirectRename_ParentError", "ERR Rename: Cannot get parent dir for '{0}'.", "错误重命名:无法获取'{0}'父目录");
            AddTrans("DirectRename_AttemptFolder", "Rename Folder: '{0}' -> '{1}'", "重命名文件夹:'{0}'->'{1}'");
            AddTrans("DirectRename_FolderSuccess", "OK Rename Folder: '{0}' -> '{1}'", "成功重命名文件夹:'{0}'->'{1}'");
            AddTrans("DirectRename_SubDirFindError", "Warn find subdirs in '{0}': {1}", "警告查找子目录于'{0}':{1}");
            AddTrans("DirectRename_AccessDeniedWarning", "Warn Rename Folder: Access denied '{0}'.", "警告重命名文件夹:访问被拒'{0}'.");
            AddTrans("DirectRename_RetryPromptMessage", "Cannot rename:\n'{0}'\n\nIn use? Close Explorer/Program and Retry.", "无法重命名:\n'{0}'\n\n可能被占用?请关闭资源管理器/程序后重试。");
            AddTrans("DirectRename_RetryPromptButton", "Retry", "重试");
            AddTrans("DirectRename_RetryLog", "User Retry. Retry in {0}ms...", "用户重试。{0}ms后重试...");
            AddTrans("DirectRename_UserCancelledRetry", "User cancelled rename for '{0}'.", "用户取消重命名'{0}'.");
            AddTrans("DirectRename_MaxRetriesReached", "FAIL Rename Folder: Access denied '{0}' (max retries).", "失败重命名文件夹:访问被拒'{0}'(已达上限).");
            AddTrans("DirectRename_ErrorFolder", "ERR Rename Folder '{0}' to '{1}': {2}", "错误重命名文件夹'{0}'到'{1}':{2}");
            AddTrans("DirectRename_FolderComplete", "Direct Rename: Folders done.", "直接重命名:文件夹完成.");
            AddTrans("DirectRename_StartFiles", "Direct Rename: Renaming files...", "直接重命名:重命名文件...");
            AddTrans("DirectRename_FileStatus", "File: {0} ({1}/{2})", "文件:{0}({1}/{2})");
            AddTrans("DirectRename_FileNotFound", "ERR Rename: File not found: {0}", "错误重命名:文件未找到:{0}");
            AddTrans("DirectRename_FileDirError", "ERR Rename: Dir '{1}' for file '{0}' not exist. Skip.", "错误重命名:文件'{0}'目录'{1}'不存在.跳过.");
            AddTrans("DirectRename_AttemptFile", "Rename File: '{0}' -> '{1}'", "重命名文件:'{0}'->'{1}'");
            AddTrans("DirectRename_FileSuccess", "OK Rename File: '{0}' -> '{1}'", "成功重命名文件:'{0}'->'{1}'");
            AddTrans("DirectRename_FileAccessDenied", "FAIL Rename File: Access denied '{0}'.", "失败重命名文件:访问被拒'{0}'.");
            AddTrans("DirectRename_ErrorFile", "ERR Rename File '{0}' to '{1}': {2}", "错误重命名文件'{0}'到'{1}':{2}");
            AddTrans("DirectRename_FileComplete", "Direct Rename: Files done.", "直接重命名:文件完成.");
            AddTrans("DirectRename_NothingSelected", "Direct Rename: No items selected for mode.", "直接重命名:无项目被选中.");
            AddTrans("RelativePathError", "Warn calc relative path '{0}' (parent '{1}'->'{2}'): {3}", "警告计算相对路径'{0}'(父'{1}'->'{2}'):{3}");
            AddTrans("FormatErrorLog", "ERR Fmt Key '{0}': {1}. Base='{2}' Args='{3}'", "错误格式化键'{0}':{1}.基础='{2}'参数='{3}'");
            AddTrans("MissingKeyLog", "Missing Key '{0}' in lang '{1}'", "语言'{1}'缺失键'{0}'");
            AddTrans("SaveLogDialogError", "Log unavailable.", "日志不可用.");
            AddTrans("ExifModeRestoredMsg", "Switched back to default EXIF processing mode.", "已恢复为默认 EXIF 处理模式。");
            AddTrans("ZoompanSettingsUpdatedMsg", "Effect settings updated.", "效果设置已更新。");
            AddTrans("ZoompanSettingsCancelledMsg", "Effect settings cancelled.", "效果设置已取消。");
            AddTrans("ZoompanGenerationComplete", "Video/Animation generation complete. Success: {0}, Fail: {1}.", "视频/动画生成完成。成功: {0}, 失败: {1}.");
            AddTrans("SelectionStartedLog", "Starting to add selection...", "开始添加选择...");
            AddTrans("SelectionCompleteLog", "Selection complete. Added {0}.", "选择完成。已添加 {0}。");
            AddTrans("NoValidItemsSelectedLog", "No valid items were selected or added.", "未选择或添加任何有效项目。");
            AddTrans("NoValidFoldersAdded", "No valid folders were added (duplicates, permissions?).", "未添加任何有效的文件夹（可能由于重复或权限问题）。");
            AddTrans("NoValidFoldersSelected", "No valid folders were selected.", "未选择任何有效文件夹。");
            AddTrans("NoValidItemsAddedLog", "No valid items were added (invalid paths, permissions?).", "未添加任何有效的文件或文件夹（可能由于无效路径或权限问题）。");
            AddTrans("SelectionCancelled", "Selection cancelled.", "选择已取消。");
            AddTrans("StartingZoompanGeneration", "Starting Video/Animation generation for {0} items...", "开始为 {0} 个项目生成 视频/动画 ...");
            AddTrans("ErrorGeneratingZoompan", "Error during Video/Animation generation: {0}\n{1}", "生成 视频/动画 时发生错误: {0}\n{1}");
            AddTrans("ZoompanStatusProcessing", "Processing (Video/Anim): {0} ({1}/{2})", "处理中 (视频/动画): {0} ({1}/{2})");
            AddTrans("ErrorGettingResolution", "Failed to get resolution for '{0}', skipping. [{1}/{2}]", "无法获取 '{0}' 的分辨率，跳过。 [{1}/{2}]");
            AddTrans("SuccessZoompan", "OK (Video/Anim): '{0}' -> '{1}' (Took: {2:F2}s)", "成功 (视频/动画): '{0}' -> '{1}' (耗时: {2:F2}s)"); // Simplified log
            AddTrans("FailedZoompan", "FAIL (Video/Anim): '{0}' (Code {1}). Err: {2} (Took: {3:F2}s)", "失败 (视频/动画): '{0}' (Code {1}). Err: {2} (耗时: {3:F2}s)"); // Simplified log
            AddTrans("BurstModeLabel", "Burst Mode (Images to Video/GIF)", "连拍模式 (多图转单视频/GIF)");
            AddTrans("OutputFormatLabel", "Output Format:", "输出格式:");
            AddTrans("OutputFormatMOV", "MOV (H.265)", "MOV (H.265)");
            AddTrans("OutputFormatMP4", "MP4 (H.264)", "MP4 (H.264)");
            AddTrans("OutputFormatGIF", "GIF", "GIF");
            AddTrans("BurstModeWarning", "Burst Mode requires selecting a single folder containing only images.", "连拍模式需要选择一个仅包含图片的文件夹。");
            AddTrans("BurstModeNoImages", "No images found in the selected folder for Burst Mode.", "在所选文件夹中未找到用于连拍模式的图片。");
            AddTrans("BurstModeSingleFile", "Error: Burst Mode cannot process single file selections.", "错误：连拍模式无法处理单个文件选择。");
            AddTrans("StartingBurstGeneration", "Starting Burst Mode generation for folder '{0}'...", "开始为文件夹 '{0}' 生成连拍模式文件...");
            AddTrans("SuccessBurst", "OK (Burst): Folder '{0}' -> '{1}' (Took: {2:F2}s)", "成功 (连拍): 文件夹 '{0}' -> '{1}' (耗时: {2:F2}s)");
            AddTrans("FailedBurst", "FAIL (Burst): Folder '{0}' (Code {1}). Err: {2} (Took: {3:F2}s)", "失败 (连拍): 文件夹 '{0}' (Code {1}). Err: {2} (耗时: {3:F2}s)");
            AddTrans("Tooltip_BurstMode", "Combine all images in the selected folder into a single video/GIF file. Disables ZoomPan effects.", "将选定文件夹中的所有图片按顺序合成为一个视频/GIF文件。禁用 ZoomPan 效果。");
            AddTrans("Tooltip_OutputFormat", "Select the output container format (MOV, MP4, or GIF).", "选择输出容器格式 (MOV, MP4, 或 GIF)。");
            AddTrans("GeneratingPalette", "Generating optimal GIF palette...", "正在生成最佳 GIF 调色板...");
            AddTrans("EncodingGIF", "Encoding GIF using palette...", "正在使用调色板编码 GIF...");
            AddTrans("PaletteGenFailed", "FAIL (Burst/GIF): Palette generation failed. Code {0}. Err: {1}", "失败 (连拍/GIF): 调色板生成失败。代码 {0}。错误: {1}");
            AddTrans("GIFEncodingFailed", "FAIL (Burst/GIF): Final GIF encoding failed. Code {0}. Err: {1}", "失败 (连拍/GIF): 最终 GIF 编码失败。代码 {0}。错误: {1}");
            AddTrans("BurstOutputLabel", "Output Filename (Burst Mode):", "输出文件名 (连拍模式):");
            AddTrans("BurstOutputTooltip", "Specify the base name for the output file in Burst Mode (extension added automatically).", "指定连拍模式输出文件的基本名称（扩展名将自动添加）。");
            AddTrans("StatusBar_Start", "Start:", "开始:");
            AddTrans("StatusBar_End", "End:", "结束:");
            AddTrans("StatusBar_Elapsed", "Elapsed:", "耗时:");
            AddTrans("StatusBar_Total", "Total:", "总计:");
            AddTrans("StatusBar_Concurrent", "Concurrent:", "并发:");
            AddTrans("Debug_TaskStarted", "DEBUG: Task IN for {0}. Active: {1}", "调试：任务进入 {0}。活动: {1}");
            AddTrans("Debug_TaskEnded", "DEBUG: Task OUT for {0}. BeforeDec: {1}, AfterDec: {2}", "调试：任务退出 {0}。减少前: {1}, 减少后: {2}");
            AddTrans("FailedProcessGeneric", "FAIL Process: Input='{0}', ExitCode={1}, Error='{2}', Time={3:F2}s", "处理失败: 输入='{0}', 代码={1}, 错误='{2}', 耗时={3:F2}s");
            AddTrans("Debug_TaskCancelledAfterSemaphore", "DEBUG: Task for {0} cancelled immediately after acquiring semaphore.", "调试：文件 {0} 的任务在获取信号量后立即取消。");
            AddTrans("Debug_FolderAlreadyBackedUp", "DEBUG: Folder '{0}' already backed up to '{1}'. Using this path.", "调试：文件夹 '{0}' 已备份到 '{1}'。使用此路径。");
            AddTrans("Debug_FolderPreviouslyFailed", "DEBUG: Folder '{0}' previously failed backup move. Skipping file '{1}'.", "调试：文件夹 '{0}' 先前备份移动失败。跳过文件 '{1}'。");
            AddTrans("Debug_AttemptingDirectoryMoveWithCount", "DEBUG: Attempting Directory.Move (Attempt {0}) from '{1}' to '{2}'", "调试：尝试移动文件夹 (第 {0} 次) 从 '{1}' 到 '{2}'");
            AddTrans("Debug_DirectoryMoveSuccessful", "DEBUG: Directory.Move successful for '{0}'.", "调试：文件夹移动成功: '{0}'.");
            AddTrans("Debug_WarnBackupExists", "WARN: Backup destination '{0}' already exists.", "警告：备份目标 '{0}' 已存在。");
            AddTrans("Debug_ErrorMappedBackupNotFound", "ERROR: Mapped backup folder '{0}' not found! Skipping file.", "错误：映射的备份文件夹 '{0}' 未找到！跳过文件。");
            AddTrans("Debug_ErrorBackupPathInvalid", "ERROR: Backup path '{0}' for folder '{1}' is invalid or missing after checks. Skipping file.", "错误：检查后发现文件夹 '{1}' 的备份路径 '{0}' 无效或丢失。跳过文件。");
            AddTrans("Debug_SetToolSourcePath", "DEBUG: Set toolSourcePath to: {0}", "调试：设置工具源路径为: {0}");
            AddTrans("Debug_ErrorFileMoveIO", "ERROR during File.Move/Backup: {0} - {1}", "错误：移动/备份文件时出错: {0} - {1}");
            AddTrans("Debug_ErrorFileMoveUnexpected", "ERROR during File.Move/Backup (Unexpected): {0} - {1}", "错误：移动/备份文件时发生意外错误: {0} - {1}");
            AddTrans("Debug_WarnOutputPathExists", "WARN: Output file path already exists (collision?): {0}", "警告：输出文件路径已存在（冲突？）：{0}");
            AddTrans("Debug_ToolArgs", "DEBUG: Tool Arguments: {0}", "调试：工具参数: {0}");
            AddTrans("Debug_BackupDisabledDelete", "DEBUG: Backup disabled & in-place. Attempting delete of original: {0}", "调试：备份已禁用且为原地处理。尝试删除原始文件: {0}");
            AddTrans("Debug_OriginalFileGone", "DEBUG: Original file already gone? {0}", "调试：原始文件已不存在？ {0}");
            AddTrans("Debug_BackupFileExists", "DEBUG: Backup file still exists after process: {0}", "调试：处理后备份文件仍然存在: {0}");
            AddTrans("Debug_BackupFileMissing", "ERROR: Backup file MISSING after process: {0}", "错误：处理后备份文件丢失: {0}");
            AddTrans("Debug_IncrementingFailCount", "DEBUG: Incrementing failed count for {0}. Current failed: {1}", "调试：增加文件 {0} 的失败计数。当前失败: {1}");
            AddTrans("Debug_TaskCancelledGeneric", "DEBUG: Task cancelled for {0}.", "调试：文件 {0} 的任务已取消。");
            AddTrans("Debug_SemaphoreReleased", "DEBUG: Released Semaphore for '{0}'. New count: {1}", "调试：已释放文件 '{0}' 的信号量。新计数: {1}");
            AddTrans("Debug_ParallelProcessingFinished", "DEBUG: Parallel processing finished. Processed: {0}, Failed: {1}", "调试：并行处理完成。成功: {0}, 失败: {1}");
            AddTrans("Debug_WhenAllCaughtCancellation", "DEBUG: Task.WhenAll caught OperationCanceledException (processing cancelled).", "调试：Task.WhenAll 捕获到 OperationCanceledException (处理已取消)。");
            AddTrans("Debug_WhenAllCaughtError", "ERROR: Unexpected error during Task.WhenAll: {0} - {1}", "错误：Task.WhenAll 期间发生意外错误: {0} - {1}");
            AddTrans("Debug_WhenAllInnerError", "-- Inner Exception: {0} - {1}", "-- 内部异常: {0} - {1}");
            AddTrans("EnableTimestampLabel", "Enable Timestamp", "启用时间戳");
            AddTrans("EnableCounterLabel", "Enable Counter", "启用计数器");
            AddTrans("TimestampFormatLabel", "Timestamp Format:", "时间戳格式:");
            AddTrans("CounterStartValueLabel", "Counter Start Value:", "计数器起始值:");
            AddTrans("CounterFormatLabel", "Counter Format:", "计数器格式:");
            AddTrans("Tooltip_EnableTimestamp", "Include the timestamp in renamed file/backup names.", "在重命名文件/备份名称中包含时间戳。");
            AddTrans("Tooltip_EnableCounter", "Include an incrementing counter in renamed file/backup names.", "在重命名文件/备份名称中包含递增计数器。");
            AddTrans("Tooltip_TimestampFormat", "Enter custom timestamp format (e.g., yyyy-MM-dd_HH.mm.ss). Used if 'Enable Timestamp' is checked.", "输入自定义时间戳格式 (例如 yyyy-MM-dd_HH.mm.ss)。如果勾选了“启用时间戳”则使用。");
            AddTrans("Tooltip_CounterStartValue", "Enter the starting number for the counter (e.g., 1, 5). Applies per output directory.", "输入计数器的起始数字（例如 1, 5）。应用于每个输出目录。");
            AddTrans("Tooltip_CounterFormat", "Enter counter format (e.g., D3 for 001, 0000 for 0001). Used if 'Enable Counter' is checked.", "输入计数器格式 (例如 D3 代表 001, 0000 代表 0001)。如果勾选了“启用计数器”则使用。");
            AddTrans("WarnInvalidTimestampFormat", "Warning: Invalid timestamp format '{0}'. Reverting to default '{1}'.", "警告：无效的时间戳格式 '{0}'。将恢复为默认格式 '{1}'。");
            AddTrans("WarnProblematicTimestampFormat", "Warning: Problematic timestamp format '{0}': {1}. Reverting to default '{2}'.", "警告：时间戳格式 '{0}' 可能存在问题：{1}。将恢复为默认格式 '{2}'。");
            AddTrans("WarnTimestampFormatProducesEmpty", "Warning: Timestamp format '{0}' resulted in an empty string. Reverting to default.", "警告：时间戳格式 '{0}' 产生了空字符串。将恢复为默认格式。");
            AddTrans("WarnTimestampFormatInvalidChars", "Warning: Timestamp format '{0}' contains potentially invalid characters for filenames/folders or ends with dot/space. Reverting to default.", "警告：时间戳格式 '{0}' 包含对文件名/文件夹无效的字符，或以点/空格结尾。将恢复为默认格式。");
            AddTrans("WarnInvalidCounterFormat", "Warning: Invalid counter format '{0}'. Reverting to default '{1}'.", "警告：无效的计数器格式 '{0}'。将恢复为默认格式 '{1}'。");
            AddTrans("WarnInvalidCounterStartValue", "Warning: Invalid counter start value '{0}'. Using default '{1}'.", "警告：无效的计数器起始值 '{0}'。将使用默认值 '{1}'。");
            AddTrans("ErrorGeneratingTimestamp", "Error generating timestamp with format '{0}': {1}. Using default.", "使用格式 '{0}' 生成时间戳时出错：{1}。将使用默认格式。");
            AddTrans("ErrorGeneratingTimestampFolder", "Error generating timestamp folder name with format '{0}': {1}. Using default.", "使用格式 '{0}' 生成时间戳文件夹名称时出错：{1}。将使用默认格式。");
            AddTrans("WarnTimestampFormatInvalidFolder", "Warning: Timestamp format '{0}' produced invalid folder name '{1}'. Using default.", "警告：时间戳格式 '{0}' 生成了无效的文件夹名称 '{1}'。将使用默认格式。");
            AddTrans("ErrorFormattingCounter", "Error formatting counter '{0}' with format '{1}'. Using default.", "使用格式 '{1}' 格式化计数器 '{0}' 时出错。将使用默认格式。");
            AddTrans("UseCustomImageOutputPathLabel", "Custom Image Output Path", "自定义图像输出路径");
            AddTrans("UseCustomVideoOutputPathLabel", "Custom Video Output Path", "自定义视频输出路径");
            AddTrans("SelectCustomImageOutputPathLabel", "Browse Image Output Folder", "浏览图像输出文件夹");
            AddTrans("SelectCustomVideoOutputPathLabel", "Browse Video Output Folder", "浏览视频输出文件夹");
            AddTrans("Tooltip_UseCustomImageOutputPath", "Output processed images (EXIF mode) to this specific folder instead of the original location.", "将处理后的图像（EXIF 模式）输出到此指定文件夹，而不是原始位置。");
            AddTrans("Tooltip_UseCustomVideoOutputPath", "Output generated videos/animations to this specific folder instead of the default 'Output' subfolder.", "将生成的视频/动画输出到此指定文件夹，而不是默认的 'Output' 子文件夹。");
            AddTrans("Tooltip_CustomImageOutputPathText", "Enter or browse to the custom output location for images.", "输入或浏览到自定义图像输出位置。");
            AddTrans("Tooltip_CustomVideoOutputPathText", "Enter or browse to the custom output location for videos.", "输入或浏览到自定义视频输出位置。");
            AddTrans("CustomOutputPathInvalid", "Custom output path invalid: {0}.", "自定义输出路径无效: {0}。");
            AddTrans("CustomOutputPathValid", "Using custom output path: {0}", "使用自定义输出路径: {0}");
            AddTrans("CustomOutputPathVerifyError", "Error verifying custom path '{0}': {1}", "验证自定义路径“{0}”时出错：{1}"); // Updated format
            AddTrans("CustomOutputPathEmptyWarning", "Warning: Custom output path is empty. Using default location.", "警告：自定义输出路径为空。将使用默认位置。");
            AddTrans("CustomOutputPathCreateAttempt", "Attempting to create custom output directory: {0}", "尝试创建自定义输出目录: {0}");
            AddTrans("ErrorCreatingCustomOutputDir", "ERROR creating custom output directory '{0}': {1}", "错误：创建自定义输出目录 '{0}' 时出错：{1}");
            AddTrans("ZoompanPreset_Custom", "Custom (Use Sliders/Radios)", "自定义 (使用下方设置)");
            AddTrans("ZoompanPreset_ZoomInCenterSlow", "Zoom In Center (Slow)", "中心放大 (慢速)");
            AddTrans("ZoompanPreset_ZoomInCenterFast", "Zoom In Center (Fast)", "中心放大 (快速)");
            AddTrans("ZoompanPreset_ZoomOutCenter", "Zoom Out Center", "中心缩小");
            AddTrans("ZoompanPreset_PanRight", "Pan Right (No Zoom)", "向右平移 (无缩放)");
            AddTrans("ZoompanPreset_PanLeft", "Pan Left (No Zoom)", "向左平移 (无缩放)");
            AddTrans("ZoompanPreset_PanUp", "Pan Up (No Zoom)", "向上平移 (无缩放)");
            AddTrans("ZoompanPreset_PanDown", "Pan Down (No Zoom)", "向下平移 (无缩放)");
            AddTrans("ZoompanPreset_ZoomInPanTopRight", "Zoom In + Pan Top-Right", "放大 + 右上平移");
            AddTrans("ZoompanPreset_ZoomInPanBottomLeft", "Zoom In + Pan Bottom-Left", "放大 + 左下平移");
            AddTrans("ZoompanPreset_IphoneStyle", "iPhone Ken Burns Style", "iPhone Ken Burns 风格");
            AddTrans("ZoompanPreset_RandomPreset", "Random (Preset per Image)", "随机 (每图随机预设)");
            AddTrans("<<< EXIF/重命名选项 >>>", "<<< EXIF/Rename Options >>>", "<<< EXIF/重命名选项 >>>");
            AddTrans("<<< 视频/动画选项 >>>", "<<< Video/Animation Options >>>", "<<< 视频/动画选项 >>>");
            AddTrans("EffectPresetLabel", "Effect Preset:", "效果预设:");
            AddTrans("CustomSettingsInfoLabel", "Custom Effect Settings (Active only when preset is 'Custom'):", "自定义效果设置 (仅当预设为'Custom'时生效):");
            AddTrans("TargetZoomLabel", "Target Zoom:", "缩放目标:");
            AddTrans("PanDirectionLabel", "Pan Direction:", "平移方向:");
            AddTrans("DurationLabel", "Animation Duration (sec):", "动画时长(秒):");
            AddTrans("BurstFpsLabel", "Burst Mode FPS:", "连拍模式帧率:");
            AddTrans("OkButtonLabel", "OK", "确定");
            AddTrans("CancelButtonLabel", "Cancel", "取消");
            AddTrans("PanDirection_None", "No Pan", "无平移");
            AddTrans("PanDirection_Up", "Pan Up", "向上平移");
            AddTrans("PanDirection_Down", "Pan Down", "向下平移");
            AddTrans("PanDirection_Left", "Pan Left", "向左平移");
            AddTrans("PanDirection_Right", "Pan Right", "向右平移");
            AddTrans("OutputFpsLabel", "Output Framerate (FPS):", "输出帧率 (FPS):");
            AddTrans("FpsOption24", "24 FPS (Film)", "24 FPS (电影)");
            AddTrans("FpsOption30", "30 FPS (Standard)", "30 FPS (标准)");
            AddTrans("FpsOption60", "60 FPS (Smooth)", "60 FPS (流畅)");
            AddTrans("FpsOption120", "120 FPS (Ultra Smooth)", "120 FPS (超流畅)");
            AddTrans("StartingBackup", "Starting backup pre-processing...", "开始备份预处理...");
            AddTrans("PerformingBackup", "Performing backup...", "正在执行备份...");
            AddTrans("BackupFailedAbort", "ERROR: Backup pre-processing failed. Aborting operation.", "错误：备份预处理失败。中止操作。");
            AddTrans("BackupComplete", "Backup pre-processing completed.", "备份预处理完成。");
            AddTrans("NoSourcesForBackup", "No valid sources found for backup.", "未找到用于备份的有效源。");
            AddTrans("BackupErrorCreateBase", "ERROR: Failed to create custom backup base directory '{0}': {1}", "错误：创建自定义备份基础目录“{0}”失败：{1}");
            AddTrans("BackupErrorCreateRoot", "ERROR: Failed to create backup root directory '{0}': {1}", "错误：创建备份根目录“{0}”失败：{1}");
            AddTrans("BackupRootExists", "Warn: Backup target path exists: {0}. Cannot move source here.", "警告：备份目标路径已存在：{0}。无法移动源到此处。");
            AddTrans("BackupErrorParentRoot", "Warning: Cannot determine default backup parent for root path '{0}'. Skipping backup for this item.", "警告：无法确定根路径“{0}”的默认备份父目录。跳过此项目的备份。");
            AddTrans("BackupErrorRootMapping", "Warning: Could not find backup root mapping for '{0}'. Skipping backup.", "警告：找不到“{0}”的备份根目录映射。跳过备份。");
            AddTrans("BackupErrorLogicFailed", "ERROR: Backup path logic failed for '{0}'. Skipping backup.", "错误：“{0}”的备份路径逻辑失败。跳过备份。");
            AddTrans("BackupErrorCreateSubdir", "ERROR: Failed to create source sub-directory in backup '{0}': {1}", "错误：在备份“{0}”中创建源子目录失败：{1}");
            AddTrans("BackupCopying", "Backing up '{0}' to '{1}'...", "正在备份“{0}”到“{1}”...");
            AddTrans("BackupErrorCopyDir", "ERROR: Failed to recursively copy directory '{0}' to backup.", "错误：递归复制目录“{0}”到备份失败。");
            AddTrans("BackupErrorCopyFile", "ERROR: Failed to copy file '{0}' to backup location '{1}': {2}", "错误：复制文件“{0}”到备份位置“{1}”失败：{2}");
            AddTrans("BackupCancelled", "Backup operation cancelled.", "备份操作已取消。");
            AddTrans("BackupFatalError", "FATAL ERROR during backup pre-processing: {0}", "备份预处理期间发生严重错误：{0}");
            AddTrans("CreatedOutputDir", "Created output directory: {0}", "已创建输出目录：{0}");
            AddTrans("WarnRelativePath", "Warning: Could not calculate relative path for '{0}' relative to '{1}': {2}.", "警告：无法计算“{0}”相对于“{1}”的相对路径：{2}。"); // Simplified
            AddTrans("WarnRelativePathVideo", "Warning: Could not calculate relative path for video '{0}': {1}", "警告：无法计算视频“{0}”的相对路径：{1}");
            AddTrans("ErrorUniqueFile", "Error: Could not generate unique filename for {0} in {1}. Skipping.", "错误：无法在 {1} 中为 {0} 生成唯一文件名。跳过。");
            AddTrans("ErrorUniqueFolder", "Error: Could not find unique folder name for {0}. Skipping.", "错误：找不到 {0} 的唯一文件夹名称。跳过。");
            AddTrans("ErrorUniqueVideo", "Error: Could not generate unique video filename for {0}. Skipping.", "错误：无法为 {0} 生成唯一的视频文件名。跳过。");
            AddTrans("ErrorUniqueBurst", "Error: Could not generate unique burst filename for {0}. Skipping.", "错误：无法为 {0} 生成唯一的连拍文件名。跳过。");
            AddTrans("CopyDirPlaceholderLog", "Placeholder: Recursively copying '{0}' to '{1}'", "占位符：递归复制“{0}”到“{1}”");
            AddTrans("CopyDirError", "ERROR during recursive copy '{0}' -> '{1}': {2}", "递归复制“{0}”->“{1}”时出错：{2}");
            AddTrans("BackupStrategySingleRename", "Identified single folder input. Will rename '{0}' to '{1}'.", "检测到单个文件夹输入。将重命名“{0}”为“{1}”。");
            AddTrans("BackupStrategyMultiFolderContainer", "Identified multiple folders from same parent '{0}'. Creating container '{1}' and moving folders into it.", "检测到来自同一父目录“{0}”的多个文件夹。正在创建容器“{1}”并将文件夹移入其中。");
            AddTrans("BackupStrategyFallback", "Using standard recursive move backup logic for current input.", "对当前输入使用标准递归移动备份逻辑。");
            AddTrans("BackupCreateContainer", "Creating container backup folder: {0}", "正在创建容器备份文件夹：{0}");
            AddTrans("BackupMoveIntoContainer", "Moving '{0}' into '{1}'...", "正在移动“{0}”到“{1}”中...");
            AddTrans("BackupItemExistsInContainer", "Warning: Item '{0}' already exists in backup container '{1}'. Skipping move for this item.", "警告：项目“{0}”已存在于备份容器“{1}”中。跳过此项目的移动。");
            AddTrans("BackupMultiMoveComplete", "Multiple folders moved into backup container successfully.", "已成功将多个文件夹移至备份容器。");
            AddTrans("BackupMultiMoveError", "ERROR: Failed during multi-folder backup to container '{0}': {1}", "错误：在多文件夹备份到容器“{0}”期间失败：{1}");
            AddTrans("BackupAttemptRename", "Attempting to rename '{0}' to '{1}'...", "尝试重命名“{0}”为“{1}”...");
            AddTrans("BackupRenameError", "ERROR: Failed to rename folder '{0}' to '{1}': {2}", "错误：重命名文件夹“{0}”为“{1}”失败：{2}");
            AddTrans("BackupRenameSuccess", "Folder renamed successfully.", "文件夹重命名成功。");
            AddTrans("BackupCannotGetParent", "ERROR: Cannot get parent directory for single folder '{0}'.", "错误：无法获取单个文件夹“{0}”的父目录。");
            AddTrans("BackupMoveToCustom", "Moving items into custom backup folder: {0}", "正在将项目移动到自定义备份文件夹：{0}");
            AddTrans("BackupMoveItemToCustom", "Moving '{0}' to '{1}'...", "正在移动“{0}”到“{1}”...");
            AddTrans("BackupItemExistsInCustom", "Warning: Item '{0}' already exists in custom backup target '{1}'. Skipping move.", "警告：项目“{0}”已存在于自定义备份目标“{1}”中。跳过移动。");
            AddTrans("BackupMoveCustomComplete", "Items moved to custom backup location.", "项目已移动到自定义备份位置。");
            AddTrans("BackupMoveCustomError", "ERROR: Failed moving items to custom backup location '{0}': {1}", "错误：将项目移动到自定义备份位置“{0}”失败：{1}");
            AddTrans("BackupErrorNoStrategy", "ERROR: No backup strategy executed. This indicates a logic error.", "错误：未执行备份策略。这表示存在逻辑错误。");
            AddTrans("BackupFileInBackupNotFound", "Warning: File '{0}' expected in backup location '{1}' but not found. Skipping.", "警告：预期文件“{0}”位于备份位置“{1}”，但未找到。跳过。");
            AddTrans("BackupMapEntryNotFound", "Warning: Original input '{0}' not found in backup map. Skipping collection for this item.", "警告：在备份映射中找不到原始输入“{0}”。跳过此项目的收集。"); // Modified message
            AddTrans("BackupMapEntrySourceNotFound", "Warning: Could not find backup map entry for source of '{0}'.", "警告：找不到“{0}”源的备份映射条目。"); // Simplified
            AddTrans("BackupProcessingFromBackup", "Collecting files from backup location(s).", "正在从备份位置收集文件。"); // Simplified
            AddTrans("BackupProcessingOriginals", "Collecting files from original location(s).", "正在从原始位置收集文件。"); // Simplified
            AddTrans("SourceNotFoundForMove", "ERROR: Source {0} '{1}' not found for move.", "错误：用于移动的源 {0} “{1}”未找到。");
            AddTrans("AccessDeniedMoveBackup", "ERROR: Access denied moving '{0}' to backup: {1}", "错误：移动“{0}”到备份时访问被拒绝：{1}");
            AddTrans("ErrorMoveBackupGeneral", "ERROR: Failed to move '{0}' to backup location '{1}': {2}", "错误：将“{0}”移动到备份位置“{1}”失败：{2}");
            AddTrans("ErrorCreatingBackupParent", "ERROR: Failed creating parent of backup target '{0}': {1}", "错误：创建备份目标“{0}”的父目录失败：{1}");
            AddTrans("ErrorCreatingNestedBackupRoot", "ERROR: Failed creating nested backup root '{0}': {1}", "错误：创建嵌套备份根“{0}”失败：{1}");
            // --- End of Translations ---
        }



        private void BtnResetParameters_Click(object sender, RoutedEventArgs e)
        {
            // 在这里实现重置所有参数的逻辑
            // 例如：
            txtFolderPath.Text = "未选择文件夹/源图片";
            BasePathBox.Text = "请选择文件夹或粘贴路径到这";
            PrefixBox.Text = "IMG";
            CountBox.Text = "2";
            RecursiveCheck.IsChecked = true;

            chkRenameFoldersOnly.IsChecked = false;
            txtBackupPrefix.Text = "Backup_"; // 根据您的默认值调整
            chkRenameFilesOnly.IsChecked = false;
            txtRenamePrefix.Text = "JYH_"; // 根据您的默认值调整
            chkEnableTimestamp.IsChecked = true;
            txtTimestampFormat.Text = "yyyyMMdd_HHmm_"; // 根据您的默认值调整
            chkEnableCounter.IsChecked = true;
            txtCounterStartValue.Text = "1"; // 根据您的默认值调整
            txtCounterFormat.Text = "D2"; // 根据您的默认值调整
            chkEnableBackup.IsChecked = true;
            chkDisableBackup.IsChecked = false;
            cmbProcessingTool.SelectedIndex = 0; // ExifTool (推荐)

            chkEnableZoompan.IsChecked = false;
            // btnZoompanSettings.IsEnabled = false; // 会自动根据 chkEnableZoompan.IsChecked 绑定
            rbFormatMov.IsChecked = true;
            chkBurstMode.IsChecked = false;
            chkTimestampSubfolder.IsChecked = true;
            cmbOutputResolution.SelectedIndex = 0; // 源分辨率 (Default)
            cmbOutputFps.SelectedIndex = 1; // 15 FPS

            chkUseCustomBackupPath2.IsChecked = false;
            txtCustomBackupPath2.Text = string.Empty;
            chkUseCustomImageOutputPath.IsChecked = false;
            txtCustomImageOutputPath.Text = string.Empty;
            chkUseCustomVideoOutputPath.IsChecked = false;
            txtCustomVideoOutputPath.Text = string.Empty;

            // 重置进度条和日志
            progressBar.Value = 0;
            TbLog.Clear();
            lblProcessStatus.Text = "Ready";
            lblImageCount.Text = "Selected Items: 0";
            lblProgressHint.Text = "Progress:";
            lblStartTime.Text = "Start: -";
            lblEndTime.Text = "End: -";
            lblElapsedTime.Text = "Elapsed: -";
            lblTotalTime.Text = "Total: -";
            lblConcurrentTasks.Text = "Concurrent: 0";

            // 可能会需要其他重置逻辑，取决于您的应用程序状态
        }
        // --- Window Event Handlers ---
        private async void Window_Loaded(object sender, RoutedEventArgs e) { await Task.Delay(100); CancellationTokenSource ctsForTools = new CancellationTokenSource(); IProgress<string> status = new Progress<string>(msg => Dispatcher.Invoke(() => LogMessage(msg))); IProgress<int>? progress = null; if (progressBar != null) { progress = new Progress<int>(p => Dispatcher.Invoke(() => { if (progressBar != null) { progressBar.Value = Math.Max(progressBar.Minimum, Math.Min((double)p, progressBar.Maximum)); progressBar.IsIndeterminate = false; } })); progressBar.Value = progressBar.Minimum; progressBar.IsIndeterminate = true; progressBar.Visibility = Visibility.Visible; } bool operationCompletedSuccessfully = false; try { LogMessage(GetLocalizedString("CheckingTools")); await Task.Run(() => ZipsHelper.EnsureAllToolsReady(progress, status, ctsForTools.Token), ctsForTools.Token); operationCompletedSuccessfully = true; } catch (OperationCanceledException) { status?.Report(GetLocalizedString("ToolCheckCancelled")); } catch (Exception ex) { string errorMsg = GetLocalizedString("ToolCheckError", ex.Message); status?.Report(errorMsg); LogMessage(errorMsg); if (progressBar != null) { await Dispatcher.InvokeAsync(() => progressBar.Foreground = Brushes.Red); } } finally { bool wasCancelled = ctsForTools.IsCancellationRequested; if (progress != null && operationCompletedSuccessfully && !wasCancelled) { await Dispatcher.InvokeAsync(() => progress?.Report(100)); } if (progressBar != null) { await Dispatcher.InvokeAsync(() => { progressBar.IsIndeterminate = false; if (!operationCompletedSuccessfully || wasCancelled) { progressBar.Value = progressBar.Minimum; } }); } ctsForTools.Dispose(); } }
        private void Window_Closing(object sender, CancelEventArgs e) { if (isProcessing) { LogMessage(GetLocalizedString("CancelRequested")); cancellationTokenSource?.Cancel(); e.Cancel = true; MessageBox.Show(this, GetLocalizedString("CancelRequested") + " Please wait...", "Processing", MessageBoxButton.OK, MessageBoxImage.Information); } else { SaveSettings(); } }
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape && isProcessing) { LogMessage(GetLocalizedString("CancelRequested")); cancellationTokenSource?.Cancel(); e.Handled = true; } else if (Keyboard.Modifiers == ModifierKeys.Control && !(Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is RichTextBox || Keyboard.FocusedElement is PasswordBox)) { Action? action = null; switch (e.Key) { case Key.O: if (btnBrowseFolder?.IsEnabled == true) action = () => BtnBrowseFolder_Click(btnBrowseFolder, new RoutedEventArgs()); break; case Key.F: if (btnSelectFolder?.IsEnabled == true) action = () => BtnSelectFolder_Click(btnSelectFolder, new RoutedEventArgs()); break; case Key.I: if (btnSelectImages?.IsEnabled == true) action = () => BtnSelectImages_Click(btnSelectImages, new RoutedEventArgs()); break; case Key.S: if (btnStartProcessing?.IsEnabled == true) action = () => BtnStartProcessing_Click(btnStartProcessing, new RoutedEventArgs()); break; case Key.G: if (btnGenerateZoompan?.IsEnabled == true) action = () => BtnGenerateZoompan_Click(btnGenerateZoompan, new RoutedEventArgs()); break; } action?.Invoke(); if (action != null) e.Handled = true; } }

        // --- Drag and Drop Handlers ---
        private void Window_DragEnter(object sender, DragEventArgs e) => UpdateDragEffect(e);
        private void Window_Drop(object sender, DragEventArgs e) => HandleItemDrop(e);
        private void Panel_DragEnter(object sender, DragEventArgs e) { UpdateDragEffect(e); if (e.Effects == DragDropEffects.Copy && sender is Border b) b.Background = dragEnterBackColor; e.Handled = true; }
        private void Panel_DragLeave(object sender, DragEventArgs e) { if (sender is Border b) b.Background = defaultBackColor; e.Handled = true; }
        private void Panel_DragDrop(object sender, DragEventArgs e) { if (sender is Border b) b.Background = defaultBackColor; HandleItemDrop(e); e.Handled = true; }
        private void TextBox_DragEnter(object sender, DragEventArgs e) { UpdateDragEffectForTextBox(e); e.Handled = true; }
        private void TextBox_DragDrop(object sender, DragEventArgs e) { HandleTextBoxDrop(e); e.Handled = true; }
        private void UpdateDragEffect(DragEventArgs e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !isProcessing ? DragDropEffects.Copy : DragDropEffects.None;
        private void UpdateDragEffectForTextBox(DragEventArgs e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !isProcessing ? DragDropEffects.Copy : DragDropEffects.None;
        private void HandleItemDrop(DragEventArgs e) { if (isProcessing) return; if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) { LogMessage(GetLocalizedString("DropProcessingStart")); int added = paths.Count(p => AddInputPath(p?.Trim())); if (added > 0) LogMessage(GetLocalizedString("DropAddedItems", added)); else LogMessage(GetLocalizedString("DropNoNewItems")); UpdateUIState(); } }
        private void HandleTextBoxDrop(DragEventArgs e) { if (isProcessing) return; if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) { if (paths.Length == 1 && Directory.Exists(paths[0])) { UpdateSelectionFromPath(paths[0]); } else { LogMessage(GetLocalizedString("DropProcessingStart")); int added = paths.Count(p => AddInputPath(p?.Trim())); if (added > 0) LogMessage(GetLocalizedString("DropAddedItems", added)); else LogMessage(GetLocalizedString("DropNoNewItems")); UpdateUIState(); } } }

        // --- Input Path Selection & Text Box Logic ---
        private void TxtFolderPath_TextChanged(object sender, TextChangedEventArgs e) { if (!isTextBoxFocused || isProcessing) return; string path = txtFolderPath.Text.Trim(); if (Directory.Exists(path)) { try { UpdateSelectionFromPath(path); } catch (Exception ex) { LogMessage(GetLocalizedString("ErrorProcessingTextBoxPath", path, ex.Message)); } } else if (!string.IsNullOrEmpty(path) && !IsPlaceholderText(path)) { txtFolderPath.Foreground = defaultTextBoxForeground; } }
        private void TxtFolderPath_GotFocus(object sender, RoutedEventArgs e) { isTextBoxFocused = true; if (IsPlaceholderText(txtFolderPath.Text)) { txtFolderPath.Text = ""; txtFolderPath.Foreground = defaultTextBoxForeground; } }
        private void TxtFolderPath_LostFocus(object sender, RoutedEventArgs e) { isTextBoxFocused = false; if (string.IsNullOrWhiteSpace(txtFolderPath.Text)) { UpdateUIState(); } else { string path = txtFolderPath.Text.Trim(); if (!Directory.Exists(path) && !File.Exists(path) && !IsSummaryText(path)) { LogMessage(GetLocalizedString("IgnoringInvalidPath", path)); } } }
        private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e) { if (isProcessing) return; var ofd = new VistaOpenFileDialog { Multiselect = true, Title = GetLocalizedString("SelectFilesAndFoldersTitle"), CheckPathExists = true, ValidateNames = false, CheckFileExists = false, FileName = GetLocalizedString("SelectItemsPrompt"), Filter = GetLocalizedString("AllFilesFilter") }; SetInitialDialogPath(ofd, txtFolderPath.Text); if (ofd.ShowDialog(this) == true) { inputPaths.Clear(); LogMessage(GetLocalizedString("SelectionStartedLog")); int foldersAdded = 0; int filesAdded = 0; string dummyFileName = GetLocalizedString("SelectItemsPrompt"); if (ofd.FileNames != null && ofd.FileNames.Any()) { foreach (string selectedPath in ofd.FileNames) { if (string.IsNullOrWhiteSpace(selectedPath)) continue; string? directoryName = Path.GetDirectoryName(selectedPath); string fileNameOnly = Path.GetFileName(selectedPath); if (fileNameOnly.Equals(dummyFileName, StringComparison.OrdinalIgnoreCase) && ofd.FileNames.Length == 1 && directoryName != null && Directory.Exists(directoryName)) { if (AddInputPath(directoryName)) { if (Directory.Exists(directoryName)) foldersAdded++; } } else if (!fileNameOnly.Equals(dummyFileName, StringComparison.OrdinalIgnoreCase)) { if (AddInputPath(selectedPath)) { try { if (Directory.Exists(selectedPath)) foldersAdded++; else if (File.Exists(selectedPath)) filesAdded++; } catch { /* Ignore validation error here */ } } } } } var summaryParts = new List<string>(); if (foldersAdded > 0) summaryParts.Add($"{foldersAdded} {GetLocalizedString("Folders")}"); if (filesAdded > 0) summaryParts.Add($"{(foldersAdded > 0 ? GetLocalizedString("And") : "")}{filesAdded} {GetLocalizedString("Files")}"); if (summaryParts.Count > 0) { LogMessage(GetLocalizedString("SelectionCompleteLog", string.Join("", summaryParts))); } else { if (ofd.FileNames != null && ofd.FileNames.Any(p => !string.IsNullOrWhiteSpace(p) && !Path.GetFileName(p).Equals(dummyFileName, StringComparison.OrdinalIgnoreCase))) { LogMessage(GetLocalizedString("NoValidItemsAddedLog")); } else { LogMessage(GetLocalizedString("NoValidItemsSelectedLog")); } } UpdateUIState(); } else { LogMessage(GetLocalizedString("SelectionCancelled")); } }
        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e) { if (isProcessing) return; var dialog = new VistaFolderBrowserDialog { Description = GetLocalizedString("SelectFolder"), UseDescriptionForTitle = true, ShowNewFolderButton = true }; SetInitialDialogPath(dialog, txtFolderPath.Text); if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.SelectedPath)) { UpdateSelectionFromPath(dialog.SelectedPath); LogMessage(GetLocalizedString("FolderSelectionComplete", 1)); } else { LogMessage(GetLocalizedString("FolderSelectionCancelled")); } }
        private void BtnSelectImages_Click(object sender, RoutedEventArgs e) { if (isProcessing) return; var ofd = new OpenFileDialog { Multiselect = true, Filter = BuildFilterString(), Title = GetLocalizedString("SelectOneOrMoreImages") }; SetInitialDialogPath(ofd, txtFolderPath.Text); if (ofd.ShowDialog(this) == true && ofd.FileNames != null && ofd.FileNames.Length > 0) { UpdateSelectionFromFiles(ofd.FileNames); } else { LogMessage(GetLocalizedString("ImageSelectionCancelled")); } }

        // --- Custom Path Options ---
        private void chkUseCustomBackupPath2_CheckedChanged(object? sender, RoutedEventArgs? e) => UpdateBackupControlsState();
        private void btnSelectCustomBackupPath_Click(object sender, RoutedEventArgs e) { if (!isProcessing) SelectFolderDialog(txtCustomBackupPath2, GetLocalizedString("SelectCustomBackupFolderTitle")); }
        private void ChkUseCustomImageOutputPath_CheckedChanged(object? sender, RoutedEventArgs? e) => UpdateOutputControlsState();
        private void btnSelectCustomImageOutputPath_Click(object sender, RoutedEventArgs e) { if (!isProcessing) SelectFolderDialog(txtCustomImageOutputPath, GetLocalizedString("SelectCustomImageOutputPathLabel")); }
        private void ChkUseCustomVideoOutputPath_CheckedChanged(object? sender, RoutedEventArgs? e) => UpdateOutputControlsState();
        private void btnSelectCustomVideoOutputPath_Click(object sender, RoutedEventArgs e) { if (!isProcessing) SelectFolderDialog(txtCustomVideoOutputPath, GetLocalizedString("SelectCustomVideoOutputPathLabel")); }
        private void SelectFolderDialog(TextBox targetTextBox, string title) { if (targetTextBox == null) return; var dialog = new VistaFolderBrowserDialog { Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = true }; SetInitialDialogPath(dialog, targetTextBox.Text); if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.SelectedPath)) { targetTextBox.Text = dialog.SelectedPath; } }

        // --- Checkbox Logic ---
        private void ChkEnableBackup_CheckedChanged(object sender, RoutedEventArgs e) { if (isUpdatingCheckboxes || !this.IsInitialized) return; bool enableChecked = chkEnableBackup.IsChecked ?? false; bool renameMode = (chkRenameFoldersOnly?.IsChecked ?? false) || (chkRenameFilesOnly?.IsChecked ?? false); isUpdatingCheckboxes = true; try { if (enableChecked && renameMode) { SetControlChecked(chkRenameFoldersOnly, false); SetControlChecked(chkRenameFilesOnly, false); SetControlChecked(chkDisableBackup, false); LogMessage(GetLocalizedString("ExifModeRestoredMsg")); renameMode = false; } if (!renameMode) { SetControlChecked(chkDisableBackup, !enableChecked); } } finally { isUpdatingCheckboxes = false; } UpdateBackupControlsState(); UpdateOutputControlsState(); }
        private void chkDisableBackup_CheckedChanged(object sender, RoutedEventArgs e) { if (isUpdatingCheckboxes || !this.IsInitialized) return; bool disableChecked = chkDisableBackup.IsChecked ?? false; bool renameMode = (chkRenameFoldersOnly?.IsChecked ?? false) || (chkRenameFilesOnly?.IsChecked ?? false); isUpdatingCheckboxes = true; try { if (!renameMode) { SetControlChecked(chkEnableBackup, !disableChecked); } else if (!disableChecked) { SetControlChecked(chkDisableBackup, true); } } finally { isUpdatingCheckboxes = false; } UpdateBackupControlsState(); UpdateOutputControlsState(); }
        private void RenamingOnly_CheckedChanged(object sender, RoutedEventArgs e) { if (isUpdatingCheckboxes || !this.IsInitialized) return; UpdateBackupControlsState(); UpdateOutputControlsState(); }
        private void ChkEnableZoompan_CheckedChanged(object sender, RoutedEventArgs e) => UpdateVideoControlsState();
        private void ChkBurstMode_CheckedChanged(object sender, RoutedEventArgs e) => UpdateVideoControlsState();
        private void ChkEnableTimestamp_CheckedChanged(object? sender, RoutedEventArgs? e) { if (txtTimestampFormat != null && chkEnableTimestamp != null && this.IsInitialized) { txtTimestampFormat.IsEnabled = chkEnableTimestamp.IsChecked ?? false; } UpdateVideoControlsState(); UpdateOutputControlsState(); }
        private void ChkEnableCounter_CheckedChanged(object? sender, RoutedEventArgs? e) { if (this.IsInitialized && chkEnableCounter != null) { bool isEnabled = chkEnableCounter.IsChecked ?? false; SetControlEnabled(txtCounterStartValue, isEnabled); SetControlEnabled(txtCounterFormat, isEnabled); } }

        // --- ZoomPan Settings Button ---
        private void BtnZoompanSettings_Click(object sender, RoutedEventArgs e) { if (isProcessing) return; var settingsWindow = new ZoompanSettingsWindow(currentZoompanSettings) { Owner = this, Title = GetLocalizedString("ZoompanSettingsTitle") }; if (settingsWindow.ShowDialog() == true) { currentZoompanSettings = settingsWindow.WindowSettings; LogMessage(GetLocalizedString("ZoompanSettingsUpdatedMsg")); } else { LogMessage(GetLocalizedString("ZoompanSettingsCancelledMsg")); } }

        // --- Settings Load/Save ---
        private void LoadSettings() { try { string savedLang = Settings.Default.UserLanguage ?? "zh"; if (translations.ContainsKey("WindowTitle") && translations["WindowTitle"].ContainsKey(savedLang)) { currentLanguage = savedLang; } else { currentLanguage = "zh"; } SetCurrentCulture(); ZipsHelper.SetLanguage(currentLanguage); bool savedEnableTimestamp = Settings.Default.UserEnableTimestamp; if (chkEnableTimestamp != null) chkEnableTimestamp.IsChecked = savedEnableTimestamp; bool savedEnableCounter = Settings.Default.UserEnableCounter; if (chkEnableCounter != null) chkEnableCounter.IsChecked = savedEnableCounter; string savedFormat = Settings.Default.UserTimestampFormat ?? DefaultTimestampFormat; if (string.IsNullOrWhiteSpace(savedFormat)) savedFormat = DefaultTimestampFormat; if (txtTimestampFormat != null) txtTimestampFormat.Text = savedFormat; int savedCounterStart = Settings.Default.UserCounterStartValue; if (savedCounterStart < 1) savedCounterStart = DefaultCounterStartValue; if (txtCounterStartValue != null) txtCounterStartValue.Text = savedCounterStart.ToString(CultureInfo.InvariantCulture); string savedCounterFormat = Settings.Default.UserCounterFormat ?? DefaultCounterFormat; if (string.IsNullOrWhiteSpace(savedCounterFormat)) savedCounterFormat = DefaultCounterFormat; if (txtCounterFormat != null) txtCounterFormat.Text = savedCounterFormat; string savedImageOut = Settings.Default.UserImageOutputPath ?? ""; if (txtCustomImageOutputPath != null) txtCustomImageOutputPath.Text = savedImageOut; if (chkUseCustomImageOutputPath != null) chkUseCustomImageOutputPath.IsChecked = !string.IsNullOrWhiteSpace(savedImageOut); string savedVideoOut = Settings.Default.UserVideoOutputPath ?? ""; if (txtCustomVideoOutputPath != null) txtCustomVideoOutputPath.Text = savedVideoOut; if (chkUseCustomVideoOutputPath != null) chkUseCustomVideoOutputPath.IsChecked = !string.IsNullOrWhiteSpace(savedVideoOut); string savedBackupOut = Settings.Default.UserBackupOutputPath ?? ""; if (txtCustomBackupPath2 != null) txtCustomBackupPath2.Text = savedBackupOut; if (chkUseCustomBackupPath2 != null) chkUseCustomBackupPath2.IsChecked = !string.IsNullOrWhiteSpace(savedBackupOut); } catch (Exception ex) { Debug.WriteLine($"Error loading settings: {ex.Message}"); } }
        private void SaveSettings() { try { Settings.Default.UserLanguage = currentLanguage; Settings.Default.UserEnableTimestamp = chkEnableTimestamp?.IsChecked ?? DefaultEnableTimestamp; Settings.Default.UserEnableCounter = chkEnableCounter?.IsChecked ?? DefaultEnableCounter; string currentFormat = txtTimestampFormat?.Text ?? DefaultTimestampFormat; Settings.Default.UserTimestampFormat = string.IsNullOrWhiteSpace(currentFormat) ? DefaultTimestampFormat : currentFormat; int currentCounterStart = DefaultCounterStartValue; if (int.TryParse(txtCounterStartValue?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedStart) && parsedStart >= 1) currentCounterStart = parsedStart; Settings.Default.UserCounterStartValue = currentCounterStart; string currentCounterFormat = txtCounterFormat?.Text ?? DefaultCounterFormat; try { _ = 1.ToString(currentCounterFormat); } catch { currentCounterFormat = DefaultCounterFormat; } Settings.Default.UserCounterFormat = string.IsNullOrWhiteSpace(currentCounterFormat) ? DefaultCounterFormat : currentCounterFormat; Settings.Default.UserImageOutputPath = (chkUseCustomImageOutputPath?.IsChecked ?? false) ? (txtCustomImageOutputPath?.Text ?? "") : ""; Settings.Default.UserVideoOutputPath = (chkUseCustomVideoOutputPath?.IsChecked ?? false) ? (txtCustomVideoOutputPath?.Text ?? "") : ""; Settings.Default.UserBackupOutputPath = (chkUseCustomBackupPath2?.IsChecked ?? false) ? (txtCustomBackupPath2?.Text ?? "") : ""; Settings.Default.Save(); } catch (Exception ex) { LogMessage($"Error saving settings: {ex.Message}"); Debug.WriteLine($"Error saving settings: {ex.Message}"); } }

        // --- Language & Localization Methods ---
        private void SetCurrentCulture() { try { CultureInfo c = new CultureInfo(currentLanguage); Thread.CurrentThread.CurrentUICulture = c; CultureInfo.DefaultThreadCurrentUICulture = c; } catch (Exception ex) { Debug.WriteLine($"Culture err '{currentLanguage}': {ex.Message}. Default zh."); CultureInfo c = new CultureInfo("zh"); Thread.CurrentThread.CurrentUICulture = c; CultureInfo.DefaultThreadCurrentUICulture = c; } }
        private void SetupLanguageComboBox() { if (cmbLanguage == null) return; cmbLanguage.Items.Clear(); cmbLanguage.Items.Add(new LanguageItem("en", "English")); cmbLanguage.Items.Add(new LanguageItem("zh", "中文")); cmbLanguage.DisplayMemberPath = "DisplayName"; cmbLanguage.SelectedValuePath = "Code"; LanguageItem? itemToSelect = cmbLanguage.Items.OfType<LanguageItem>().FirstOrDefault(item => item.Code.Equals(currentLanguage, StringComparison.OrdinalIgnoreCase)); if (itemToSelect != null) { cmbLanguage.SelectedItem = itemToSelect; } else if (cmbLanguage.Items.Count > 0) { LanguageItem? defaultItem = cmbLanguage.Items.OfType<LanguageItem>().FirstOrDefault(item => item.Code == "zh"); if (defaultItem != null) { cmbLanguage.SelectedItem = defaultItem; if (currentLanguage != defaultItem.Code) { currentLanguage = defaultItem.Code; SetCurrentCulture(); ZipsHelper.SetLanguage(currentLanguage); } } else { cmbLanguage.SelectedIndex = 0; if (cmbLanguage.SelectedItem is LanguageItem firstItem && currentLanguage != firstItem.Code) { currentLanguage = firstItem.Code; SetCurrentCulture(); ZipsHelper.SetLanguage(currentLanguage); } } } }
        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (cmbLanguage.SelectedItem is LanguageItem selectedLang && currentLanguage != selectedLang.Code) { currentLanguage = selectedLang.Code; SetCurrentCulture(); ZipsHelper.SetLanguage(currentLanguage); ApplyLanguage(); } }
        private void cmbProcessingTool_SelectionChanged(object sender, SelectionChangedEventArgs e) { /* No action needed */ }
        internal string GetLocalizedString(string key) { if (translations.TryGetValue(key, out var langDict)) { if (langDict.TryGetValue(currentLanguage, out var translation) && !string.IsNullOrEmpty(translation)) { return translation; } if (langDict.TryGetValue("en", out var enTranslation) && !string.IsNullOrEmpty(enTranslation)) { return enTranslation; } if (langDict.TryGetValue("zh", out var zhTranslation) && !string.IsNullOrEmpty(zhTranslation)) { return zhTranslation; } } if (!translations.ContainsKey($"<{key}>_Logged")) { Debug.WriteLine($"Missing Key '{key}' in lang '{currentLanguage}'"); translations.TryAdd($"<{key}>_Logged", new Dictionary<string, string>()); } return $"<{key}>"; }
        internal string GetLocalizedString(string key, params object?[]? args) { string baseStr = GetLocalizedString(key); if (args is { Length: > 0 } && baseStr.Contains('{') && !baseStr.StartsWith("<")) { try { return string.Format(CultureInfo.CurrentUICulture, baseStr, args.Select(a => a ?? string.Empty).ToArray()); } catch (FormatException ex) { string argStr = string.Join(",", args.Select(a => a?.ToString() ?? "null")); string fmt = GetLocalizedString("FormatErrorLog"); if (fmt.StartsWith("<")) fmt = "Err fmt key '{0}'"; string logMsg = string.Format(CultureInfo.InvariantCulture, fmt, key, ex.Message, baseStr, argStr); LogMessage(logMsg); return $"{baseStr}({GetLocalizedString("FormatErrorMarker")})"; } } return baseStr; }
        private void ApplyLanguage() { if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(ApplyLanguage); return; } if (!this.IsInitialized) return; Action<ContentControl?, string> setC = (c, k) => { if (c != null) c.Content = GetLocalizedString(k); }; Action<TextBlock?, string> setT = (c, k) => { if (c != null) c.Text = GetLocalizedString(k); }; Action<MenuItem?, string> setM = (c, k) => { if (c != null) c.Header = GetLocalizedString(k); }; Action<Label?, string> setL = (c, k) => { if (c != null) c.Content = GetLocalizedString(k); }; Action<GroupBox?, string> setH = (gb, k) => { if (gb != null) gb.Header = GetLocalizedString(k); }; Action<CheckBox?, string> setChk = (c, k) => { if (c != null) c.Content = GetLocalizedString(k); }; this.Title = GetLocalizedString("WindowTitle"); setC(btnSelectFolder, "SelectFolderLabel"); setC(btnSelectImages, "SelectImagesLabel"); setC(btnBrowseFolder, "BrowseFolderLabel"); setH(gbRenameBackup, GetLocalizedString("<<< EXIF/重命名选项 >>>")); setChk(chkRenameFoldersOnly, "RenameFoldersOnlyLabel"); setL(lblBackupPrefix, "BackupPrefixLabel"); setChk(chkRenameFilesOnly, "RenameFilesOnlyLabel"); setL(lblRenamePrefix, "RenamePrefixLabel"); setChk(chkEnableTimestamp, "EnableTimestampLabel"); setL(lblTimestampFormat, "TimestampFormatLabel"); setChk(chkEnableCounter, "EnableCounterLabel"); setL(lblCounterStartValue, "CounterStartValueLabel"); setL(lblCounterFormat, "CounterFormatLabel"); setChk(chkEnableBackup, "EnableBackupLabel"); setChk(chkDisableBackup, "DisableBackupLabel"); setL(lblProcessingTool, "ProcessingToolLabel"); setH(gbVideo, GetLocalizedString("<<< 视频/动画选项 >>>")); setChk(chkEnableZoompan, "EnableZoompanLabel"); setC(btnZoompanSettings, "ZoompanSettingsButtonLabel"); setL(lblOutputFormat, "OutputFormatLabel"); setC(rbFormatMov, "OutputFormatMOV"); setC(rbFormatMp4, "OutputFormatMP4"); setC(rbFormatGif, "OutputFormatGIF"); setChk(chkBurstMode, "BurstModeLabel"); setChk(chkTimestampSubfolder, "TimestampSubfolderLabel"); setL(lblOutputResolution, "OutputResolutionLabel"); setC(btnStartProcessing, "StartProcessingLabel"); setC(btnGenerateZoompan, "GenerateZoompanLabel"); setChk(chkUseCustomBackupPath2, "UseCustomBackupPathLabel"); setC(btnSelectCustomBackupPath, "SelectCustomBackupPathLabel"); setChk(chkUseCustomImageOutputPath, "UseCustomImageOutputPathLabel"); setC(btnSelectCustomImageOutputPath, "SelectCustomImageOutputPathLabel"); setChk(chkUseCustomVideoOutputPath, "UseCustomVideoOutputPathLabel"); setC(btnSelectCustomVideoOutputPath, "SelectCustomVideoOutputPathLabel"); setC(LogLabelElement, "LogLabel"); setT(lblProgressHint, "ProgressHintLabel"); ApplyToolTips(); UpdateUIState(); }
        private void ApplyToolTips() { if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(ApplyToolTips); return; } Action<DependencyObject?, string> setTip = (e, k) => { if (e != null) try { ToolTipService.SetToolTip(e, GetLocalizedString(k)); } catch { /* Ignore tooltip errors */ } }; setTip(chkEnableBackup, "Tooltip_EnableBackup"); setTip(chkDisableBackup, "Tooltip_DisableBackup"); setTip(btnStartProcessing, "Tooltip_StartProcessing"); setTip(btnSelectFolder, "Tooltip_SelectFolder"); setTip(btnSelectImages, "Tooltip_SelectImages"); setTip(btnBrowseFolder, "Tooltip_BrowseFolder"); setTip(TbLog, "Tooltip_Log"); setTip(pnlDragDrop, "Tooltip_DragDropPanel"); setTip(txtFolderPath, "Tooltip_FolderPath"); setTip(lblImageCount, "Tooltip_ImageCount"); setTip(lblProgressHint, "Tooltip_ProgressHint"); setTip(lblRenamePrefix, "Tooltip_RenamePrefix"); setTip(txtRenamePrefix, "Tooltip_RenamePrefixText"); setTip(lblBackupPrefix, "Tooltip_BackupPrefix"); setTip(txtBackupPrefix, "Tooltip_BackupPrefixText"); setTip(chkRenameFoldersOnly, "Tooltip_RenameFoldersOnly"); setTip(chkRenameFilesOnly, "Tooltip_RenameFilesOnly"); setTip(chkEnableTimestamp, "Tooltip_EnableTimestamp"); setTip(txtTimestampFormat, "Tooltip_TimestampFormat"); setTip(chkEnableCounter, "Tooltip_EnableCounter"); setTip(txtCounterStartValue, "Tooltip_CounterStartValue"); setTip(txtCounterFormat, "Tooltip_CounterFormat"); setTip(chkUseCustomBackupPath2, "Tooltip_UseCustomBackupPath"); setTip(txtCustomBackupPath2, "Tooltip_CustomBackupPathText"); setTip(btnSelectCustomBackupPath, "Tooltip_SelectCustomBackupPath"); setTip(chkUseCustomImageOutputPath, "Tooltip_UseCustomImageOutputPath"); setTip(txtCustomImageOutputPath, "Tooltip_CustomImageOutputPathText"); setTip(btnSelectCustomImageOutputPath, "SelectCustomImageOutputPathLabel"); setTip(chkUseCustomVideoOutputPath, "Tooltip_UseCustomVideoOutputPath"); setTip(txtCustomVideoOutputPath, "Tooltip_CustomVideoOutputPathText"); setTip(btnSelectCustomVideoOutputPath, "SelectCustomVideoOutputPathLabel"); setTip(cmbLanguage, "Tooltip_LanguageSelector"); if (lblProcessStatus != null) setTip(lblProcessStatus, "Tooltip_ProgressHint"); setTip(cmbProcessingTool, "Tooltip_ProcessingToolSelector"); setTip(chkEnableZoompan, "Tooltip_EnableZoompan"); setTip(btnZoompanSettings, "Tooltip_ZoompanSettingsButton"); setTip(chkTimestampSubfolder, "Tooltip_TimestampSubfolder"); setTip(cmbOutputResolution, "Tooltip_OutputResolution"); setTip(btnGenerateZoompan, "Tooltip_GenerateZoompan"); setTip(chkBurstMode, "Tooltip_BurstMode"); setTip(rbFormatMov, GetLocalizedString("Tooltip_OutputFormat") + " (H.265, High Quality/Compression)"); setTip(rbFormatMp4, GetLocalizedString("Tooltip_OutputFormat") + " (H.264, Good Compatibility)"); setTip(rbFormatGif, GetLocalizedString("Tooltip_OutputFormat") + " (Animation, Large File, Fewer Colors)"); }

        // --- UI State Updates ---
        private void UpdateBackupControlsState() { if (isUpdatingCheckboxes || !this.IsInitialized) return; if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(UpdateBackupControlsState); return; } if (isProcessing) return; isUpdatingCheckboxes = true; try { bool fo = chkRenameFoldersOnly?.IsChecked ?? false; bool fi = chkRenameFilesOnly?.IsChecked ?? false; bool enB = chkEnableBackup?.IsChecked ?? false; bool useC = chkUseCustomBackupPath2?.IsChecked ?? false; bool tEnB_En = true, tDisB_En = true, tRP_En, tBP_En, tUseC_En, tCustP_En; if (fo || fi) { tRP_En = fi; tBP_En = fo; tUseC_En = false; tCustP_En = false; if (chkEnableBackup?.IsChecked == true) SetControlChecked(chkEnableBackup, false); if (chkDisableBackup?.IsChecked == false) SetControlChecked(chkDisableBackup, true); enB = false; } else { tRP_En = true; tBP_En = true; tUseC_En = enB; tCustP_En = enB && useC; SetControlChecked(chkDisableBackup, !enB); } SetControlEnabled(chkEnableBackup, tEnB_En && !isProcessing && !(fo || fi)); SetControlEnabled(chkDisableBackup, tDisB_En && !isProcessing); SetControlEnabled(chkRenameFoldersOnly, !isProcessing); SetControlEnabled(chkRenameFilesOnly, !isProcessing); SetControlEnabled(txtRenamePrefix, tRP_En && !isProcessing); SetControlEnabled(txtBackupPrefix, tBP_En && !isProcessing); SetControlEnabled(chkUseCustomBackupPath2, tUseC_En && !isProcessing); SetControlEnabled(txtCustomBackupPath2, tCustP_En && !isProcessing); SetControlEnabled(btnSelectCustomBackupPath, tCustP_En && !isProcessing); } finally { isUpdatingCheckboxes = false; } }
        private void UpdateVideoControlsState() { if (isProcessing || !this.IsInitialized) return; if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(UpdateVideoControlsState); return; } bool burstChecked = chkBurstMode?.IsChecked ?? false; bool enableZoompanChecked = chkEnableZoompan?.IsChecked ?? false; bool timestampEnabled = chkEnableTimestamp?.IsChecked ?? false; bool useCustomVideoOut = chkUseCustomVideoOutputPath?.IsChecked ?? false; SetControlEnabled(btnZoompanSettings, enableZoompanChecked && !burstChecked && !isProcessing); SetControlEnabled(cmbOutputResolution, !burstChecked && !isProcessing); SetControlEnabled(lblOutputResolution, !burstChecked && !isProcessing); SetControlEnabled(rbFormatMov, !isProcessing); SetControlEnabled(rbFormatMp4, !isProcessing); SetControlEnabled(rbFormatGif, !isProcessing); SetControlEnabled(chkBurstMode, !isProcessing); SetControlEnabled(chkTimestampSubfolder, !isProcessing && timestampEnabled && !useCustomVideoOut); if (chkEnableZoompan != null) { chkEnableZoompan.Opacity = burstChecked ? 0.5 : 1.0; } if (chkTimestampSubfolder != null) { chkTimestampSubfolder.Opacity = (timestampEnabled && !useCustomVideoOut) ? 1.0 : 0.5; } }
        private void UpdateOutputControlsState() { if (!this.IsInitialized || isProcessing) return; if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(UpdateOutputControlsState); return; } bool useCustomImage = chkUseCustomImageOutputPath?.IsChecked ?? false; SetControlEnabled(txtCustomImageOutputPath, useCustomImage && !isProcessing); SetControlEnabled(btnSelectCustomImageOutputPath, useCustomImage && !isProcessing); bool useCustomVideo = chkUseCustomVideoOutputPath?.IsChecked ?? false; SetControlEnabled(txtCustomVideoOutputPath, useCustomVideo && !isProcessing); SetControlEnabled(btnSelectCustomVideoOutputPath, useCustomVideo && !isProcessing); bool enableTimestamp = chkEnableTimestamp?.IsChecked ?? false; SetControlEnabled(chkTimestampSubfolder, enableTimestamp && !useCustomVideo && !isProcessing); if (chkTimestampSubfolder != null) chkTimestampSubfolder.Opacity = (enableTimestamp && !useCustomVideo) ? 1.0 : 0.5; UpdateVideoControlsState(); }
        private void UpdateUIState()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(UpdateUIState); return; }
            if (lblImageCount == null || txtFolderPath == null || !this.IsInitialized) { return; }
            bool proc = isProcessing; bool hasSel = inputPaths.Count > 0; SetControlEnabled(btnStartProcessing, hasSel && !proc); SetControlEnabled(btnGenerateZoompan, hasSel && !proc); string statTxt = ""; string tbTxt = ""; if (!hasSel) { statTxt = GetLocalizedString("UnselectedState"); tbTxt = GetLocalizedString("UnselectedState"); } else { int fCnt = 0, dCnt = 0; foreach (var p in inputPaths) { try { if (File.Exists(p)) fCnt++; else if (Directory.Exists(p)) dCnt++; } catch (Exception ex) { LogMessage($"Warn: Error checking path type for '{p}': {ex.Message}"); } } if (dCnt == 1 && fCnt == 0) { try { tbTxt = Path.GetFullPath(inputPaths[0]); } catch { tbTxt = inputPaths[0]; } statTxt = GetLocalizedString("Selected", $"1 {GetLocalizedString("Folders")}"); } else if (fCnt == 1 && dCnt == 0) { try { tbTxt = Path.GetFullPath(inputPaths[0]); } catch { tbTxt = inputPaths[0]; } statTxt = GetLocalizedString("Selected", $"1 {GetLocalizedString("Files")}"); } else { var parts = new List<string>(); if (dCnt > 0) parts.Add($"{dCnt} {GetLocalizedString("Folders")}"); if (fCnt > 0) parts.Add($"{(dCnt > 0 ? GetLocalizedString("And") : "")}{fCnt} {GetLocalizedString("Files")}"); if (parts.Count > 0) { statTxt = GetLocalizedString("Selected", string.Join("", parts)); tbTxt = statTxt; } else { statTxt = GetLocalizedString("InvalidItemsSelected"); tbTxt = GetLocalizedString("UnselectedState"); LogMessage(GetLocalizedString("WarningScanningFolder", "Input List", "Invalid Items", "Contains non-file/folder paths after selection")); } } }
            if (lblImageCount != null) lblImageCount.Text = statTxt; if (!isTextBoxFocused && !proc) { bool isPh = IsPlaceholderText(tbTxt) || IsSummaryText(tbTxt); if (txtFolderPath.Text != tbTxt) { txtFolderPath.Text = tbTxt; } txtFolderPath.Foreground = isPh ? placeholderForeground : defaultTextBoxForeground; } else if (isTextBoxFocused) { txtFolderPath.Foreground = defaultTextBoxForeground; }
            if (!proc) { UpdateStatusLabel(GetLocalizedString("ProcessStatusLabelInitial")); UpdateProgressBar(0); if (progressBar != null) progressBar.IsIndeterminate = false; if (lblStartTime != null) lblStartTime.Text = $"{GetLocalizedString("StatusBar_Start", "Start:")} -"; if (lblEndTime != null) lblEndTime.Text = $"{GetLocalizedString("StatusBar_End", "End:")} -"; if (lblElapsedTime != null) lblElapsedTime.Text = $"{GetLocalizedString("StatusBar_Elapsed", "Elapsed:")} -"; if (lblTotalTime != null) lblTotalTime.Text = $"{GetLocalizedString("StatusBar_Total", "Total:")} -"; if (lblConcurrentTasks != null) lblConcurrentTasks.Text = $"{GetLocalizedString("StatusBar_Concurrent", "Concurrent:")} 0"; UpdateBackupControlsState(); UpdateVideoControlsState(); UpdateOutputControlsState(); }
        }
        private void SetUIEnabled(bool enabled) { if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => SetUIEnabled(enabled)); return; } Action<UIElement?, bool> setE = (e, s) => { if (e != null) e.IsEnabled = s; }; setE(cmbLanguage, true); setE(btnSelectFolder, enabled); setE(btnSelectImages, enabled); setE(btnBrowseFolder, enabled); setE(pnlDragDrop, enabled); setE(txtFolderPath, enabled); setE(chkRenameFoldersOnly, enabled); setE(chkRenameFilesOnly, enabled); setE(chkEnableBackup, enabled); setE(chkDisableBackup, enabled); setE(chkUseCustomBackupPath2, enabled); setE(txtRenamePrefix, enabled); setE(txtBackupPrefix, enabled); setE(txtCustomBackupPath2, enabled); setE(btnSelectCustomBackupPath, enabled); setE(cmbProcessingTool, enabled); setE(chkEnableTimestamp, enabled); setE(txtTimestampFormat, enabled && (chkEnableTimestamp?.IsChecked ?? false)); setE(chkEnableCounter, enabled); setE(txtCounterStartValue, enabled && (chkEnableCounter?.IsChecked ?? false)); setE(txtCounterFormat, enabled && (chkEnableCounter?.IsChecked ?? false)); setE(chkEnableZoompan, enabled); setE(btnZoompanSettings, enabled); setE(chkBurstMode, enabled); setE(chkTimestampSubfolder, enabled && (chkEnableTimestamp?.IsChecked ?? false) && !(chkUseCustomVideoOutputPath?.IsChecked ?? false)); setE(cmbOutputResolution, enabled); setE(lblOutputResolution, enabled); setE(rbFormatMov, enabled); setE(rbFormatMp4, enabled); setE(rbFormatGif, enabled); setE(chkUseCustomImageOutputPath, enabled); setE(txtCustomImageOutputPath, enabled && (chkUseCustomImageOutputPath?.IsChecked ?? false)); setE(btnSelectCustomImageOutputPath, enabled && (chkUseCustomImageOutputPath?.IsChecked ?? false)); setE(chkUseCustomVideoOutputPath, enabled); setE(txtCustomVideoOutputPath, enabled && (chkUseCustomVideoOutputPath?.IsChecked ?? false)); setE(btnSelectCustomVideoOutputPath, enabled && (chkUseCustomVideoOutputPath?.IsChecked ?? false)); bool allowDrop = enabled; if (pnlDragDrop != null) pnlDragDrop.AllowDrop = allowDrop; if (txtFolderPath != null) txtFolderPath.AllowDrop = allowDrop; this.AllowDrop = allowDrop; if (enabled) { UpdateBackupControlsState(); UpdateVideoControlsState(); UpdateOutputControlsState(); } bool hasSelection = inputPaths.Count > 0; setE(btnStartProcessing, enabled && hasSelection); setE(btnGenerateZoompan, enabled && hasSelection); }


        // --- Naming & Output Options Helpers ---
        private string GetValidatedTimestampFormat(bool forFolder = false) { string format = DefaultTimestampFormat; string? potentialFormat = null; Dispatcher.Invoke(() => potentialFormat = txtTimestampFormat?.Text); if (!string.IsNullOrWhiteSpace(potentialFormat)) { potentialFormat = potentialFormat.Trim(); try { string testOutput = DateTime.Now.ToString(potentialFormat); bool invalidChars = false; char[] checkedChars = forFolder ? Path.GetInvalidPathChars() : Path.GetInvalidFileNameChars(); if (potentialFormat.IndexOfAny(checkedChars) >= 0) invalidChars = true; else if (potentialFormat.EndsWith(".") || potentialFormat.EndsWith(" ")) invalidChars = true; else if (string.IsNullOrWhiteSpace(testOutput)) invalidChars = true; if (invalidChars) { LogMessage(GetLocalizedString(forFolder ? "WarnTimestampFormatInvalidFolder" : "WarnTimestampFormatInvalidChars", potentialFormat)); } else { format = potentialFormat; } } catch (FormatException) { LogMessage(GetLocalizedString("WarnInvalidTimestampFormat", potentialFormat, DefaultTimestampFormat)); } catch (ArgumentException argEx) { LogMessage(GetLocalizedString("WarnProblematicTimestampFormat", potentialFormat, argEx.Message, DefaultTimestampFormat)); } } return string.IsNullOrWhiteSpace(format) ? DefaultTimestampFormat : format; }
        private string GetValidatedCounterFormat() { string format = DefaultCounterFormat; string? potentialFormat = null; Dispatcher.Invoke(() => potentialFormat = txtCounterFormat?.Text); if (!string.IsNullOrWhiteSpace(potentialFormat)) { potentialFormat = potentialFormat.Trim(); try { _ = 1.ToString(potentialFormat); format = potentialFormat; } catch (FormatException) { LogMessage(GetLocalizedString("WarnInvalidCounterFormat", potentialFormat, DefaultCounterFormat)); } } return string.IsNullOrWhiteSpace(format) ? DefaultCounterFormat : format; }
        private int GetValidatedStartValue() { int startValue = DefaultCounterStartValue; string? potentialValue = null; Dispatcher.Invoke(() => potentialValue = txtCounterStartValue?.Text); if (!string.IsNullOrWhiteSpace(potentialValue)) { if (int.TryParse(potentialValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue) && parsedValue >= 1) { startValue = parsedValue; } else { LogMessage(GetLocalizedString("WarnInvalidCounterStartValue", potentialValue ?? "", DefaultCounterStartValue)); } } return startValue; }
        private async Task<string?> GetValidatedCustomPath(CheckBox? chkEnabled, TextBox? txtPath, bool createDirectoryIfNeeded = false, string pathType = "Output")
        {
            string? customPath = null; bool isEnabled = false; await Dispatcher.InvokeAsync(() => { isEnabled = chkEnabled?.IsChecked ?? false; if (isEnabled) customPath = txtPath?.Text?.Trim(); }); if (!isEnabled || string.IsNullOrWhiteSpace(customPath)) { if (isEnabled && chkEnabled != null) { string emptyWarnKey = (pathType == "Backup") ? "CustomPathEmptyWarning" : "CustomOutputPathEmptyWarning"; LogMessage(GetLocalizedString(emptyWarnKey)); } return null; }
            try { if (!Path.IsPathRooted(customPath)) { string invalidKey = (pathType == "Backup") ? "CustomPathInvalid" : "CustomOutputPathInvalid"; LogMessage(GetLocalizedString(invalidKey, customPath) + " Reason: Path is not absolute."); return null; } if (customPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0) { string invalidKey = (pathType == "Backup") ? "CustomPathInvalid" : "CustomOutputPathInvalid"; LogMessage(GetLocalizedString(invalidKey, customPath) + " Reason: Contains invalid characters."); return null; } customPath = Path.GetFullPath(customPath); if (createDirectoryIfNeeded && !Directory.Exists(customPath)) { try { string createAttemptKey = (pathType == "Backup") ? "CustomPathCreateAttempt" : "CustomOutputPathCreateAttempt"; LogMessage(GetLocalizedString(createAttemptKey, customPath)); Directory.CreateDirectory(customPath); string validKey = (pathType == "Backup") ? "CustomPathValid" : "CustomOutputPathValid"; LogMessage(GetLocalizedString(validKey, customPath) + " (Created)"); } catch (Exception createEx) { string createErrorKey = (pathType == "Backup") ? "ErrorCreatingCustomBackupDir" : "ErrorCreatingCustomOutputDir"; LogMessage(GetLocalizedString(createErrorKey, customPath, createEx.Message)); return null; } } else if (!Directory.Exists(customPath)) { string invalidKey = (pathType == "Backup") ? "CustomPathInvalid" : "CustomOutputPathInvalid"; LogMessage(GetLocalizedString(invalidKey, customPath) + " Reason: Directory does not exist."); return null; } return customPath; } catch (Exception ex) { string verifyErrorKey = (pathType == "Backup") ? "CustomPathVerifyError" : "CustomOutputPathVerifyError"; LogMessage(GetLocalizedString(verifyErrorKey, customPath ?? "null path", ex.Message)); return null; }
        }

        // --- Core Processing Methods ---
        private string FormatTimeSpan(TimeSpan ts) { return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100}"; }

        // --- EXIF Processing Entry Point ---
        private async void BtnStartProcessing_Click(object sender, RoutedEventArgs e)
        {
            if (isProcessing || inputPaths.Count == 0) { if (inputPaths.Count == 0) MessageBox.Show(this, GetLocalizedString("NoFilesSelected"), GetLocalizedString("Tip"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            bool enableTimestampNaming = false, enableCounterNaming = false;
            string renamePrefixText = "", backupPrefixText = "";
            bool enableBackupState = false, useCustomBackupCheckedState = false;
            string selectedToolTag = "ExifTool";
            await Dispatcher.InvokeAsync(() => {
                enableTimestampNaming = chkEnableTimestamp?.IsChecked ?? DefaultEnableTimestamp;
                enableCounterNaming = chkEnableCounter?.IsChecked ?? DefaultEnableCounter;
                renamePrefixText = txtRenamePrefix?.Text ?? "";
                backupPrefixText = txtBackupPrefix?.Text ?? "";
                enableBackupState = chkEnableBackup?.IsChecked ?? false;
                useCustomBackupCheckedState = chkUseCustomBackupPath2?.IsChecked ?? false;
                selectedToolTag = (cmbProcessingTool?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ExifTool";
            });
            string currentTimestampFormat = GetValidatedTimestampFormat();
            string currentCounterFormat = GetValidatedCounterFormat();
            int currentCounterStartValue = GetValidatedStartValue();
            string? customImageOutputPath = await GetValidatedCustomPath(chkUseCustomImageOutputPath, txtCustomImageOutputPath, true, "Output");

            bool renameFoldersDirectly = chkRenameFoldersOnly?.IsChecked ?? false;
            bool renameFilesDirectly = chkRenameFilesOnly?.IsChecked ?? false;
            bool directRenameMode = renameFoldersDirectly || renameFilesDirectly;
            bool useImageMagick = selectedToolTag == "ImageMagick";
            string? firstSuccessfullyProcessedTopLevelPath = null;
            List<Tuple<string, string>> collectedFileDetails = new List<Tuple<string, string>>(); // Holds (OriginalPath, CurrentPath)
            bool wasCancelled = false;
            int finalProcessed = 0, finalFailed = 0, finalTotal = 0;
            bool wasDirectMode = directRenameMode;
            List<string> originalInputsCopy = new List<string>(inputPaths);

            cancellationTokenSource?.Cancel(); cancellationTokenSource?.Dispose();
            cancellationTokenSource = new CancellationTokenSource();
            var token = cancellationTokenSource.Token;

            SetUIEnabled(false);
            processedImages = 0; failedImages = 0; totalImages = 0;
            startTime = DateTime.Now; processStopwatch.Restart();
            sourceToBackupPathMap.Clear();
            folderRenameMap_DirectModeOnly.Clear();

            await Dispatcher.InvokeAsync(() => {
                lblStartTime.Text = $"{GetLocalizedString("StatusBar_Start", "Start:")} {startTime:HH:mm:ss}"; string processingPlaceholder = "..."; lblEndTime.Text = $"{GetLocalizedString("StatusBar_End", "End:")} {processingPlaceholder}"; lblElapsedTime.Text = $"{GetLocalizedString("StatusBar_Elapsed", "Elapsed:")} 00:00:00.0"; lblTotalTime.Text = $"{GetLocalizedString("StatusBar_Total", "Total:")} {processingPlaceholder}";
                UpdateProgressBar(0); if (progressBar != null) progressBar.IsIndeterminate = true; UpdateStatusLabel(GetLocalizedString("ProgressReady"));
            });

            try
            {
                if (progressBar != null) await Dispatcher.InvokeAsync(() => progressBar.IsIndeterminate = false);
                string timestamp = ""; if (enableTimestampNaming) { try { timestamp = DateTime.Now.ToString(currentTimestampFormat); if (string.IsNullOrWhiteSpace(timestamp)) { LogMessage(GetLocalizedString("WarnTimestampFormatProducesEmpty", currentTimestampFormat)); timestamp = DateTime.Now.ToString(DefaultTimestampFormat); } } catch (Exception ex) { LogMessage(GetLocalizedString("ErrorGeneratingTimestamp", currentTimestampFormat, ex.Message)); timestamp = DateTime.Now.ToString(DefaultTimestampFormat); } }

                if (directRenameMode)
                {
                    string filePrefix = CleanPrefix(renamePrefixText.Trim()); string folderPrefix = CleanPrefix(backupPrefixText.Trim()); if (renameFilesDirectly && string.IsNullOrWhiteSpace(filePrefix)) { filePrefix = GetLocalizedString("RenamedDefaultPrefix"); LogMessage(GetLocalizedString("DirectRename_DefaultFilePrefixInfo", filePrefix)); }
                    if (renameFoldersDirectly && string.IsNullOrWhiteSpace(folderPrefix)) { LogMessage(GetLocalizedString("DirectRename_FolderPrefixEmptyInfo")); }
                    LogMessage(GetLocalizedString("DirectRename_StartLog")); if (renameFoldersDirectly) LogMessage(string.IsNullOrEmpty(folderPrefix) ? GetLocalizedString("DirectRename_OptionFolder") : GetLocalizedString("DirectRename_OptionFolderWithPrefix", folderPrefix)); if (renameFilesDirectly) LogMessage(GetLocalizedString("DirectRename_OptionFile", filePrefix));
                    await PerformDirectRenamingAsync(originalInputsCopy, renameFoldersDirectly, renameFilesDirectly, filePrefix, folderPrefix, enableTimestampNaming, timestamp, enableCounterNaming, currentCounterFormat, currentCounterStartValue, token);
                    finalTotal = this.totalImages; if (processedImages > 0) { string? firstOrig = GetUniqueTopLevelFolders(originalInputsCopy).FirstOrDefault(); if (firstOrig != null && folderRenameMap_DirectModeOnly.TryGetValue(firstOrig, out var firstNew)) firstSuccessfullyProcessedTopLevelPath = firstNew; else if (renameFilesDirectly) firstSuccessfullyProcessedTopLevelPath = originalInputsCopy.FirstOrDefault(p => File.Exists(folderRenameMap_DirectModeOnly.GetValueOrDefault(p, p)))?.Let(p => Path.GetDirectoryName(folderRenameMap_DirectModeOnly.GetValueOrDefault(p, p))); else if (firstOrig != null && Directory.Exists(folderRenameMap_DirectModeOnly.GetValueOrDefault(firstOrig, firstOrig))) firstSuccessfullyProcessedTopLevelPath = folderRenameMap_DirectModeOnly.GetValueOrDefault(firstOrig, firstOrig); } // Use renamed path if exists
                }
                else
                { // EXIF Mode
                    string renamePrefix = CleanPrefix(renamePrefixText.Trim()); string backupPrefix = CleanPrefix(backupPrefixText.Trim()); if (string.IsNullOrWhiteSpace(renamePrefix)) renamePrefix = GetLocalizedString("RenamedDefaultPrefix"); if (string.IsNullOrWhiteSpace(backupPrefix)) backupPrefix = GetLocalizedString("BackupDefaultPrefix");
                    bool backupEnabled = enableBackupState; LogMessage(GetLocalizedString("ExifMode_StartLog"));

                    if (backupEnabled)
                    {
                        bool useCustomBackup = useCustomBackupCheckedState; UpdateStatusLabel(GetLocalizedString("PerformingBackup")); await Dispatcher.InvokeAsync(() => { if (progressBar != null) progressBar.IsIndeterminate = true; });
                        bool backupSuccess = await PerformPreProcessingBackupAsync(originalInputsCopy, backupPrefix, useCustomBackup, chkUseCustomBackupPath2, txtCustomBackupPath2, enableTimestampNaming, timestamp, token, sourceToBackupPathMap);
                        await Dispatcher.InvokeAsync(() => { if (progressBar != null) progressBar.IsIndeterminate = false; });
                        if (!backupSuccess) { LogMessage(GetLocalizedString("BackupFailedAbort")); UpdateStatusLabel(GetLocalizedString("BackupFailedAbort")); throw new Exception("Backup pre-processing failed."); }
                        LogMessage(GetLocalizedString("BackupComplete"));
                    }
                    else { LogMessage(GetLocalizedString("ExifMode_BackupDisabled")); }

                    collectedFileDetails = await CollectAllUniqueFilesAndTheirOriginalPathsAsync(originalInputsCopy, backupEnabled, sourceToBackupPathMap, token);
                    totalImages = collectedFileDetails.Count; finalTotal = totalImages;
                    await Dispatcher.InvokeAsync(() => { if (progressBar != null) progressBar.Maximum = Math.Max(1, totalImages); UpdateProgressBar(0); });

                    if (totalImages == 0) { LogMessage(GetLocalizedString("NoImagesFound")); if (progressBar != null) await Dispatcher.InvokeAsync(() => progressBar.IsIndeterminate = false); }
                    else
                    {
                        bool customOutputRequiresSubdirs = customImageOutputPath != null && (originalInputsCopy.Count(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) > 1 || (originalInputsCopy.Count > 1 && originalInputsCopy.Any(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))));
                        await ProcessImagesAsync(collectedFileDetails, originalInputsCopy, renamePrefix, backupEnabled, useImageMagick, token, enableTimestampNaming, enableCounterNaming, timestamp, currentCounterFormat, currentCounterStartValue, customImageOutputPath, customOutputRequiresSubdirs, sourceToBackupPathMap);

                        if (processedImages > 0)
                        { // Determine path to open
                            if (customImageOutputPath != null) { if (customOutputRequiresSubdirs) { string? firstSourceSubDir = GetUniqueValidSources(originalInputsCopy).Keys.Select(s => Path.Combine(customImageOutputPath, Path.GetFileName(s))).FirstOrDefault(Directory.Exists); firstSuccessfullyProcessedTopLevelPath = firstSourceSubDir ?? customImageOutputPath; } else { firstSuccessfullyProcessedTopLevelPath = customImageOutputPath; } }
                            else if (backupEnabled)
                            { // Backup occurred, open the *backup* location
                                firstSuccessfullyProcessedTopLevelPath = sourceToBackupPathMap.Values.FirstOrDefault()?.Let(p => File.Exists(p) ? Path.GetDirectoryName(p) : p); // Find first backup path (dir or file's parent)
                                                                                                                                                                                 // Fallback: Find the backup path of the first *processed* original file if the general map lookup failed
                                if (string.IsNullOrEmpty(firstSuccessfullyProcessedTopLevelPath))
                                {
                                    string? firstOriginalProcessed = collectedFileDetails.Select(t => t.Item1).FirstOrDefault(); // Get the original path of the first item processed
                                    if (firstOriginalProcessed != null)
                                    {
                                        string? backupPathForFirst = sourceToBackupPathMap.GetValueOrDefault(firstOriginalProcessed); // Get its backup path
                                        if (string.IsNullOrEmpty(backupPathForFirst))
                                        { // Maybe its parent folder was mapped?
                                            string? originalParent = Path.GetDirectoryName(firstOriginalProcessed);
                                            if (originalParent != null) backupPathForFirst = sourceToBackupPathMap.GetValueOrDefault(originalParent);
                                        }
                                        firstSuccessfullyProcessedTopLevelPath = backupPathForFirst?.Let(p => File.Exists(p) ? Path.GetDirectoryName(p) : p);
                                    }
                                }
                            }
                            else
                            { // In-place, no backup, open the *original* location
                                firstSuccessfullyProcessedTopLevelPath = collectedFileDetails.FirstOrDefault()?.Item1.Let(Path.GetDirectoryName); // Use original parent of first processed file
                            }
                            if (string.IsNullOrEmpty(firstSuccessfullyProcessedTopLevelPath)) { string? firstInput = originalInputsCopy.FirstOrDefault(p => Directory.Exists(p) || File.Exists(p)); if (firstInput != null) firstSuccessfullyProcessedTopLevelPath = Directory.Exists(firstInput) ? firstInput : Path.GetDirectoryName(firstInput); } // Final fallback to original input
                        }
                    }
                }
            }
            catch (OperationCanceledException) { wasCancelled = true; LogMessage(GetLocalizedString("ProcessingCancelled")); }
            catch (Exception ex) { string errMode = directRenameMode ? "DirectRename_ErrorTitle" : "ProcessingErrorTitle"; string fatalKey = directRenameMode ? "DirectRename_FatalError" : "FatalProcessingError"; string trace = ex.StackTrace ?? GetLocalizedString("NoStackTrace"); LogMessage(GetLocalizedString(fatalKey, ex.Message, trace)); MessageBox.Show(this, GetLocalizedString(fatalKey, ex.Message, ""), GetLocalizedString(errMode), MessageBoxButton.OK, MessageBoxImage.Error); failedImages = totalImages - processedImages; }
            finally
            {
                processStopwatch.Stop(); DateTime endTime = DateTime.Now; TimeSpan totalDuration = processStopwatch.Elapsed; string totalFormatted = FormatTimeSpan(totalDuration);
                _activeTasks = 0; finalProcessed = processedImages; finalFailed = failedImages;
                string? finalPath = firstSuccessfullyProcessedTopLevelPath; bool cancellationFlag = cancellationTokenSource?.IsCancellationRequested ?? wasCancelled;
                cancellationTokenSource?.Dispose(); cancellationTokenSource = null;
                await Dispatcher.InvokeAsync(() => {
                    if (lblConcurrentTasks != null) { lblConcurrentTasks.Text = $"{GetLocalizedString("StatusBar_Concurrent", "Concurrent:")} 0"; }
                    SetUIEnabled(true); if (progressBar != null) progressBar.IsIndeterminate = false; lblStartTime.Text = $"{GetLocalizedString("StatusBar_Start", "Start:")} {startTime:HH:mm:ss}"; lblEndTime.Text = $"{GetLocalizedString("StatusBar_End", "End:")} {endTime:HH:mm:ss}"; lblTotalTime.Text = $"{GetLocalizedString("StatusBar_Total", "Total:")} {totalFormatted}"; lblElapsedTime.Text = $"{GetLocalizedString("StatusBar_Elapsed", "Elapsed:")} {totalFormatted}"; string finalStatus; if (cancellationFlag) finalStatus = GetLocalizedString("ProcessingCancelled"); else if (wasDirectMode) finalStatus = (finalTotal == 0) ? GetLocalizedString("DirectRename_NothingSelected") : GetLocalizedString("DirectRename_FinishedLog", finalProcessed, finalFailed); else finalStatus = (finalTotal == 0) ? GetLocalizedString("NoImagesFound") : GetLocalizedString("ProcessingCompleted", finalProcessed, finalFailed); UpdateStatusLabel(finalStatus); UpdateProgressBar(finalProcessed + finalFailed); if (lblImageCount != null) lblImageCount.Text = GetLocalizedString("ProcessedCounts", finalProcessed, finalFailed, finalTotal); if (lblProgressHint != null) lblProgressHint.Text = GetLocalizedString("ProgressCounts", finalProcessed + finalFailed, finalTotal); LogMessage(finalStatus);
                    folderRenameMap_DirectModeOnly.Clear(); sourceToBackupPathMap.Clear();
                    if (!cancellationFlag && finalProcessed > 0 && !string.IsNullOrEmpty(finalPath)) { try { string full = Path.GetFullPath(finalPath); if (Directory.Exists(full)) { LogMessage(GetLocalizedString("OpenFolderComplete", full)); Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{full}\"", UseShellExecute = true }); } else { string? fbPath = originalInputsCopy.FirstOrDefault(p => Directory.Exists(p) || File.Exists(p))?.Let(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p))?.Let(Path.GetFullPath); if (fbPath != null && Directory.Exists(fbPath)) { LogMessage(GetLocalizedString("OpenFolderFallback", full, fbPath)); Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{fbPath}\"", UseShellExecute = true }); } else { LogMessage(GetLocalizedString("OpenFolderFallbackFailed", full)); } } } catch (Exception ex) { LogMessage(GetLocalizedString("OpenFolderFailed", finalPath, ex.Message)); } }
                    RealTimeTimer_Tick(null, EventArgs.Empty);
                });
            }
        }

        // --- Video Generation Entry Point ---
        private async void BtnGenerateZoompan_Click(object sender, RoutedEventArgs e)
        {
            if (isProcessing || inputPaths.Count == 0) { if (inputPaths.Count == 0) MessageBox.Show(this, GetLocalizedString("NoFilesSelected"), GetLocalizedString("Tip"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            bool enableTimestampNaming = false, enableCounterNaming = false;
            string renamePrefixText = "", backupPrefixText = "";
            bool enableBackupState = false, useCustomBackupCheckedState = false;
            bool useTimestampSubfolder = false;
            await Dispatcher.InvokeAsync(() => {
                enableTimestampNaming = chkEnableTimestamp?.IsChecked ?? DefaultEnableTimestamp;
                enableCounterNaming = chkEnableCounter?.IsChecked ?? DefaultEnableCounter;
                renamePrefixText = txtRenamePrefix?.Text ?? "";
                backupPrefixText = txtBackupPrefix?.Text ?? "";
                enableBackupState = chkEnableBackup?.IsChecked ?? false;
                useCustomBackupCheckedState = chkUseCustomBackupPath2?.IsChecked ?? false;
                useTimestampSubfolder = chkTimestampSubfolder?.IsChecked ?? true;
            });
            string currentTimestampFormat = GetValidatedTimestampFormat();
            string currentCounterFormat = GetValidatedCounterFormat();
            int currentCounterStartValue = GetValidatedStartValue();
            string? customVideoOutputPath = await GetValidatedCustomPath(chkUseCustomVideoOutputPath, txtCustomVideoOutputPath, true, "Output");

            bool isBurst = chkBurstMode?.IsChecked ?? false;
            OutputFormat selectedFormat = GetSelectedOutputFormat();

            List<Tuple<string, string>> collectedFileDetails = new List<Tuple<string, string>>();
            bool wasCancelled = false;
            int finalProcessed = 0, finalFailed = 0, finalTotal = 0;
            List<string> originalInputsCopy = new List<string>(inputPaths);
            string? finalPathToOpen = null;

            cancellationTokenSource?.Cancel(); cancellationTokenSource?.Dispose();
            cancellationTokenSource = new CancellationTokenSource();
            var token = cancellationTokenSource.Token;

            SetUIEnabled(false);
            processedImages = 0; failedImages = 0; totalImages = 0;
            startTime = DateTime.Now; processStopwatch.Restart();
            sourceToBackupPathMap.Clear();

            await Dispatcher.InvokeAsync(() => {
                lblStartTime.Text = $"{GetLocalizedString("StatusBar_Start", "Start:")} {startTime:HH:mm:ss}"; string processingPlaceholder = "..."; lblEndTime.Text = $"{GetLocalizedString("StatusBar_End", "End:")} {processingPlaceholder}"; lblElapsedTime.Text = $"{GetLocalizedString("StatusBar_Elapsed", "Elapsed:")} 00:00:00.0"; lblTotalTime.Text = $"{GetLocalizedString("StatusBar_Total", "Total:")} {processingPlaceholder}";
                UpdateProgressBar(0); if (progressBar != null) progressBar.IsIndeterminate = true; UpdateStatusLabel(GetLocalizedString("ProgressReady"));
            });

            try
            {
                if (progressBar != null) await Dispatcher.InvokeAsync(() => progressBar.IsIndeterminate = false);
                string timestamp = ""; if (enableTimestampNaming) { try { timestamp = DateTime.Now.ToString(currentTimestampFormat); if (string.IsNullOrWhiteSpace(timestamp)) { LogMessage(GetLocalizedString("WarnTimestampFormatProducesEmpty", currentTimestampFormat)); timestamp = DateTime.Now.ToString(DefaultTimestampFormat); } } catch (Exception ex) { LogMessage(GetLocalizedString("ErrorGeneratingTimestamp", currentTimestampFormat, ex.Message)); timestamp = DateTime.Now.ToString(DefaultTimestampFormat); } }

                if (enableBackupState)
                {
                    bool useCustomBackup = useCustomBackupCheckedState; string backupPrefix = CleanPrefix(backupPrefixText.Trim()); if (string.IsNullOrWhiteSpace(backupPrefix)) backupPrefix = GetLocalizedString("BackupDefaultPrefix"); UpdateStatusLabel(GetLocalizedString("PerformingBackup")); await Dispatcher.InvokeAsync(() => { if (progressBar != null) progressBar.IsIndeterminate = true; });
                    bool backupSuccess = await PerformPreProcessingBackupAsync(originalInputsCopy, backupPrefix, useCustomBackup, chkUseCustomBackupPath2, txtCustomBackupPath2, enableTimestampNaming, timestamp, token, sourceToBackupPathMap);
                    await Dispatcher.InvokeAsync(() => { if (progressBar != null) progressBar.IsIndeterminate = false; });
                    if (!backupSuccess) { LogMessage(GetLocalizedString("BackupFailedAbort")); UpdateStatusLabel(GetLocalizedString("BackupFailedAbort")); throw new Exception("Backup pre-processing failed."); }
                    LogMessage(GetLocalizedString("BackupComplete"));
                }
                else { LogMessage(GetLocalizedString("ExifMode_BackupDisabled")); } // Log backup disabled status

                string baseOutputDirectory; string timestampStringForDir = "";
                if (customVideoOutputPath != null) { baseOutputDirectory = customVideoOutputPath; if (useTimestampSubfolder && enableTimestampNaming) { LogMessage($"Info: Using custom video output path '{customVideoOutputPath}'. Timestamp subfolder setting ignored."); } }
                else { baseOutputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Output"); if (useTimestampSubfolder && enableTimestampNaming) { string folderTimestampFormat = GetValidatedTimestampFormat(forFolder: true); try { timestampStringForDir = DateTime.Now.ToString(folderTimestampFormat); if (string.IsNullOrWhiteSpace(timestampStringForDir) || timestampStringForDir.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || timestampStringForDir.EndsWith(".") || timestampStringForDir.EndsWith(" ")) { string originalBadName = timestampStringForDir; LogMessage(GetLocalizedString("WarnTimestampFormatInvalidFolder", folderTimestampFormat, originalBadName)); timestampStringForDir = DateTime.Now.ToString(DefaultTimestampFormat); } baseOutputDirectory = Path.Combine(baseOutputDirectory, timestampStringForDir); } catch (Exception ex) { LogMessage(GetLocalizedString("ErrorGeneratingTimestampFolder", folderTimestampFormat, ex.Message)); timestampStringForDir = DateTime.Now.ToString(DefaultTimestampFormat); baseOutputDirectory = Path.Combine(baseOutputDirectory, timestampStringForDir); } } else if (useTimestampSubfolder && !enableTimestampNaming) { LogMessage("Info: Timestamp subfolder requested but timestamp naming is disabled. Outputting to base folder."); } }
                if (!Directory.Exists(baseOutputDirectory)) { try { Directory.CreateDirectory(baseOutputDirectory); LogMessage(GetLocalizedString("CreatedOutputDir", baseOutputDirectory)); } catch (Exception dirEx) { LogMessage(GetLocalizedString("ErrorCreatingCustomOutputDir", baseOutputDirectory, dirEx.Message)); throw; } }

                bool customOutputRequiresSubdirs = customVideoOutputPath != null && (originalInputsCopy.Count(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) > 1 || (originalInputsCopy.Count > 1 && originalInputsCopy.Any(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))));
                string renamePrefix = CleanPrefix(renamePrefixText.Trim());

                if (isBurst)
                {
                    if (originalInputsCopy.Count != 1 || !Directory.Exists(originalInputsCopy[0])) { MessageBox.Show(this, GetLocalizedString("BurstModeWarning"), GetLocalizedString("Tip"), MessageBoxButton.OK, MessageBoxImage.Warning); throw new InvalidOperationException("Burst mode requires a single folder input."); }
                    string inputFolderOriginal = originalInputsCopy[0]; string currentInputFolder = enableBackupState ? sourceToBackupPathMap.GetValueOrDefault(inputFolderOriginal, inputFolderOriginal) : inputFolderOriginal; if (!Directory.Exists(currentInputFolder)) { LogMessage($"ERROR: Source folder for burst mode not found at '{currentInputFolder}'. Original: '{inputFolderOriginal}'."); throw new DirectoryNotFoundException($"Burst source folder not found: {currentInputFolder}"); }
                    LogMessage(GetLocalizedString("StartingBurstGeneration", Path.GetFileName(inputFolderOriginal))); totalImages = 1; await Dispatcher.InvokeAsync(() => { if (progressBar != null) { progressBar.Maximum = totalImages; progressBar.IsIndeterminate = false; } UpdateProgressBar(0); });
                    await GenerateBurstVideoAsync(currentInputFolder, inputFolderOriginal, baseOutputDirectory, selectedFormat, currentZoompanSettings.BurstFramerate, token, renamePrefix, enableTimestampNaming, timestamp, enableCounterNaming, currentCounterFormat, currentCounterStartValue); if (processedImages > 0) { finalPathToOpen = baseOutputDirectory; }
                }
                else
                {
                    collectedFileDetails = await CollectAllUniqueFilesAndTheirOriginalPathsAsync(originalInputsCopy, enableBackupState, sourceToBackupPathMap, token);
                    totalImages = collectedFileDetails.Count; finalTotal = totalImages; await Dispatcher.InvokeAsync(() => { if (progressBar != null) { progressBar.Maximum = Math.Max(1, totalImages); progressBar.IsIndeterminate = false; } UpdateProgressBar(0); });
                    if (totalImages == 0) { LogMessage(GetLocalizedString("NoImagesFound")); UpdateStatusLabel(GetLocalizedString("NoImagesFound")); }
                    else
                    {
                        LogMessage(GetLocalizedString("StartingZoompanGeneration", totalImages)); UpdateStatusLabel(GetLocalizedString("StartingZoompanGeneration", totalImages));
                        string selectedResolutionTag = (cmbOutputResolution?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "source"; bool applyZoompanEffect = chkEnableZoompan?.IsChecked ?? false;
                        await GenerateZoompanVideosAsync(collectedFileDetails, originalInputsCopy, baseOutputDirectory, applyZoompanEffect, currentZoompanSettings, selectedResolutionTag, selectedFormat, token, renamePrefix, enableTimestampNaming, timestamp, enableCounterNaming, currentCounterFormat, currentCounterStartValue, customVideoOutputPath, customOutputRequiresSubdirs, sourceToBackupPathMap, enableBackupState);
                        if (processedImages > 0) { finalPathToOpen = baseOutputDirectory; }
                    }
                }
            }
            catch (OperationCanceledException) { wasCancelled = true; LogMessage(GetLocalizedString("ProcessingCancelled")); }
            catch (FileNotFoundException fnfEx) { LogMessage(fnfEx.Message); MessageBox.Show(this, fnfEx.Message, GetLocalizedString("ZoompanGenerationErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error); if (!isBurst) failedImages = totalImages - processedImages; else failedImages = 1; }
            catch (InvalidOperationException ioEx) { LogMessage(ioEx.Message); if (!isBurst) failedImages = totalImages - processedImages; else failedImages = 1; }
            catch (Exception ex) { string errorMsg = GetLocalizedString("ErrorGeneratingZoompan", ex.Message, ex.StackTrace ?? GetLocalizedString("NoStackTrace")); LogMessage(errorMsg); MessageBox.Show(this, $"Error during processing: {ex.Message}", GetLocalizedString("ZoompanGenerationErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error); if (!isBurst) failedImages = totalImages - processedImages; else failedImages = 1; }
            finally
            {
                processStopwatch.Stop(); DateTime endTime = DateTime.Now; TimeSpan totalDuration = processStopwatch.Elapsed; string totalFormatted = FormatTimeSpan(totalDuration);
                _activeTasks = 0; finalProcessed = processedImages; finalFailed = failedImages; finalTotal = totalImages; bool cancellationFlag = cancellationTokenSource?.IsCancellationRequested ?? wasCancelled;
                cancellationTokenSource?.Dispose(); cancellationTokenSource = null;
                await Dispatcher.InvokeAsync(() => {
                    if (lblConcurrentTasks != null) { lblConcurrentTasks.Text = $"{GetLocalizedString("StatusBar_Concurrent", "Concurrent:")} 0"; }
                    SetUIEnabled(true); if (progressBar != null) progressBar.IsIndeterminate = false; lblStartTime.Text = $"{GetLocalizedString("StatusBar_Start", "Start:")} {startTime:HH:mm:ss}"; lblEndTime.Text = $"{GetLocalizedString("StatusBar_End", "End:")} {endTime:HH:mm:ss}"; lblTotalTime.Text = $"{GetLocalizedString("StatusBar_Total", "Total:")} {totalFormatted}"; lblElapsedTime.Text = $"{GetLocalizedString("StatusBar_Elapsed", "Elapsed:")} {totalFormatted}"; string finalStatusKey = cancellationFlag ? "ProcessingCancelled" : "ZoompanGenerationComplete"; string finalStatus = GetLocalizedString(finalStatusKey, finalProcessed, finalFailed); UpdateStatusLabel(finalStatus); LogMessage(finalStatus); UpdateProgressBar(finalProcessed + finalFailed); if (lblImageCount != null) lblImageCount.Text = GetLocalizedString("ProcessedCounts", finalProcessed, finalFailed, isBurst ? 1 : finalTotal); if (lblProgressHint != null) lblProgressHint.Text = GetLocalizedString("ProgressCounts", finalProcessed + finalFailed, isBurst ? 1 : finalTotal);
                    sourceToBackupPathMap.Clear();
                    if (!cancellationFlag && finalProcessed > 0 && !string.IsNullOrEmpty(finalPathToOpen)) { try { if (Directory.Exists(finalPathToOpen)) { LogMessage(GetLocalizedString("OpenFolderComplete", finalPathToOpen)); Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{finalPathToOpen}\"", UseShellExecute = true }); } else { LogMessage(GetLocalizedString("OpenFolderFailed", finalPathToOpen, "Directory not found after processing.")); } } catch (Exception ex) { LogMessage(GetLocalizedString("OpenFolderFailed", finalPathToOpen, ex.Message)); } }
                    RealTimeTimer_Tick(null, EventArgs.Empty);
                });
            }
        }

        // --- File Collection (Returns Tuples) ---
        private async Task<List<Tuple<string, string>>> CollectAllUniqueFilesAndTheirOriginalPathsAsync(List<string> originalInputPaths, bool backupWasEnabled, Dictionary<string, string> sourceToBackupPathMap, CancellationToken token)
        {
            LogMessage(GetLocalizedString("CollectingFiles")); UpdateStatusLabel(GetLocalizedString("CollectingFiles")); await Dispatcher.InvokeAsync(() => { if (progressBar != null) progressBar.IsIndeterminate = true; });
            var uniqueFilesBag = new ConcurrentBag<Tuple<string, string>>();
            var pathsToScanDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (backupWasEnabled) { LogMessage(GetLocalizedString("BackupProcessingFromBackup")); foreach (string originalInput in originalInputPaths) { if (sourceToBackupPathMap.TryGetValue(originalInput, out string? backupPath)) { pathsToScanDetails[originalInput] = backupPath; } else { LogMessage(GetLocalizedString("BackupMapEntryNotFound", originalInput)); } } }
            else { LogMessage(GetLocalizedString("BackupProcessingOriginals")); foreach (string originalInput in originalInputPaths) { pathsToScanDetails[originalInput] = originalInput; } }
            try { ParallelOptions parallelOptions = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = MaxConcurrentProcesses }; LogMessage($"DEBUG: Starting parallel file collection with MaxDegreeOfParallelism={parallelOptions.MaxDegreeOfParallelism}"); await Task.Run(() => { try { Parallel.ForEach(pathsToScanDetails, parallelOptions, (kvp, loopState) => { if (token.IsCancellationRequested) { loopState.Stop(); return; } string originalInputRoot = kvp.Key; string actualScanRoot = kvp.Value; try { if (Directory.Exists(actualScanRoot)) { try { var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }; foreach (var currentFilePath in Directory.EnumerateFiles(actualScanRoot, "*.*", opts)) { if (token.IsCancellationRequested) { loopState.Stop(); return; } string fileExtension = Path.GetExtension(currentFilePath).ToLowerInvariant(); if (supportedExtensions.Contains(fileExtension)) { string relativePath = Path.GetRelativePath(actualScanRoot, currentFilePath); string originalFilePath = Path.Combine(originalInputRoot, relativePath); uniqueFilesBag.Add(Tuple.Create(originalFilePath, currentFilePath)); } } } catch (OperationCanceledException) { loopState.Stop(); return; } catch (Exception ex) when (ex is UnauthorizedAccessException || ex is PathTooLongException || ex is IOException || ex is DirectoryNotFoundException) { LogMessage(GetLocalizedString("WarningScanningFolder", actualScanRoot, ex.GetType().Name, ex.Message)); } } else if (File.Exists(actualScanRoot)) { string fileExtension = Path.GetExtension(actualScanRoot).ToLowerInvariant(); if (supportedExtensions.Contains(fileExtension)) { uniqueFilesBag.Add(Tuple.Create(originalInputRoot, actualScanRoot)); } } } catch (OperationCanceledException) { loopState.Stop(); return; } catch (Exception pathEx) { LogMessage(GetLocalizedString("ErrorCheckingFolderPath", actualScanRoot, pathEx.Message)); } }); } catch (OperationCanceledException) { LogMessage("DEBUG: Parallel.ForEach File Collection Cancelled."); throw; } catch (AggregateException aggEx) when (aggEx.InnerExceptions.Any(e => e is OperationCanceledException)) { LogMessage("DEBUG: Parallel.ForEach File Collection Cancelled Inner."); throw new OperationCanceledException(token); } catch (Exception taskRunEx) { LogMessage($"ERROR in Task.Run for file collection: {taskRunEx.Message}"); } }, token); } catch (OperationCanceledException) { LogMessage(GetLocalizedString("ProcessingCancelled")); } catch (Exception ex) { LogMessage($"Error during parallel file collection setup: {ex.Message}"); }
            await Dispatcher.InvokeAsync(() => { if (progressBar != null) progressBar.IsIndeterminate = false; }); var finalUniqueFiles = uniqueFilesBag.GroupBy(t => t.Item1, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(t => t.Item1).ToList(); LogMessage(GetLocalizedString("CollectionComplete", finalUniqueFiles.Count)); UpdateStatusLabel(GetLocalizedString("CollectionComplete", finalUniqueFiles.Count)); return finalUniqueFiles;
        }

        // --- EXIF Processing Logic ---
        private async Task ProcessImagesAsync(List<Tuple<string, string>> filesToProcessDetails, List<string> originalInputPaths, string renamePrefix, bool enableBackup, bool useImageMagick, CancellationToken token, bool enableTimestampNaming, bool enableCounterNaming, string timestamp, string counterFormat, int counterStartValue, string? customImageOutputPath, bool customOutputRequiresSubdirs, Dictionary<string, string> sourceToBackupPathMap)
        {
            string? toolPath = null; string notFoundKey = "", failedKey = "", successKey = ""; if (useImageMagick) { toolPath = FindToolPath("magick.exe"); notFoundKey = "ImageMagickNotFound"; failedKey = "ImageMagickFailed"; successKey = "SuccessProcessed"; } else { toolPath = FindToolPath("exiftool.exe"); notFoundKey = "ExifToolNotFound"; failedKey = "ExifToolFailed"; successKey = "SuccessRename"; }
            if (string.IsNullOrEmpty(toolPath)) { string toolExe = useImageMagick ? "magick.exe" : "exiftool.exe"; string errorMsg = GetLocalizedString(notFoundKey, toolExe); LogMessage(errorMsg); await Dispatcher.InvokeAsync(() => MessageBox.Show(this, errorMsg, GetLocalizedString("ProcessingErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error)); throw new FileNotFoundException(errorMsg, toolExe); }
            string nonNullToolPath = toolPath!;
            var renamingCounters = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase); List<Task> processingTasks = new List<Task>();
            for (int i = 0; i < filesToProcessDetails.Count; i++) { if (token.IsCancellationRequested) break; string originalFilePath = filesToProcessDetails[i].Item1; string currentProcessingFile = filesToProcessDetails[i].Item2; int currentFileIndex = i; processingTasks.Add(ProcessSingleImageAsync(currentProcessingFile, originalFilePath, originalInputPaths, renamePrefix, enableBackup, useImageMagick, nonNullToolPath, successKey, failedKey, enableTimestampNaming, enableCounterNaming, timestamp, counterFormat, counterStartValue, customImageOutputPath, customOutputRequiresSubdirs, renamingCounters, token, currentFileIndex)); }
            try { await Task.WhenAll(processingTasks); LogMessage(GetLocalizedString("Debug_ParallelProcessingFinished", processedImages, failedImages)); } catch (OperationCanceledException) { LogMessage(GetLocalizedString("Debug_WhenAllCaughtCancellation")); throw; } catch (Exception ex) { LogMessage(GetLocalizedString("Debug_WhenAllCaughtError", ex.GetType().Name, ex.Message)); if (ex is AggregateException aggEx) { foreach (var innerEx in aggEx.Flatten().InnerExceptions) { LogMessage(GetLocalizedString("Debug_WhenAllInnerError", innerEx.GetType().Name, innerEx.Message)); } } } finally { LogMessage($"DEBUG: Parallel EXIF processing finished check. Processed: {processedImages}, Failed: {failedImages}"); }
        }

        private async Task ProcessSingleImageAsync(
    string currentProcessingFile,     // Path to file NOW (might be in backup)
    string originalFilePathForContext,// Original path of the file
    List<string> originalInputPaths, // List of USER selections
    string renamePrefix,
    bool enableBackup, // If true, means original was moved
    bool useImageMagick,
    string nonNullToolPath,
    string successKey, string failedKey,
    bool useTimestamp, bool useCounter, string timestamp,
    string counterFormat, int counterStartValue,
    string? customImageOutputPath, bool customOutputRequiresSubdirs,
    ConcurrentDictionary<string, int> renamingCounters,
    CancellationToken token, int fileIndex)
        {
            await processSemaphore.WaitAsync(token);
            int countAfterInc = Interlocked.Increment(ref _activeTasks);
            LogMessage(GetLocalizedString("Debug_TaskStarted", Path.GetFileName(originalFilePathForContext), countAfterInc));
            await UpdateCountsAndUIAsync(token);

            bool skipFile = false;
            string toolSourcePath = currentProcessingFile;
            string finalOutputDirectory = "";
            string finalOutputFilePath = "";

            try
            {
                if (token.IsCancellationRequested) return;

                string originalInputItemForFile = FindBestInputPathForFile(originalFilePathForContext, originalInputPaths);
                string currentFileDisplayName = Path.GetFileName(originalFilePathForContext);
                await Dispatcher.InvokeAsync(() => UpdateStatusLabel($"{GetLocalizedString(enableBackup ? "ProcessingFile" : "ProcessingFileNoBackup", currentFileDisplayName)} ({fileIndex + 1}/{totalImages})"));

                // --- Determine Output Directory BASED ON ORIGINAL STRUCTURE ---
                string relativePathDir = ""; // Still calculate for custom path case
                try
                {
                    string relativePath = Path.GetRelativePath(originalInputItemForFile, originalFilePathForContext);
                    relativePathDir = Path.GetDirectoryName(relativePath) ?? "";
                    if (string.IsNullOrEmpty(relativePathDir) || relativePathDir == ".") relativePathDir = "";
                }
                catch (Exception ex) { LogMessage(GetLocalizedString("WarnRelativePath", originalFilePathForContext, originalInputItemForFile, ex.Message)); relativePathDir = ""; }


                if (customImageOutputPath != null)
                {
                    // Custom output path logic (remains the same)
                    if (customOutputRequiresSubdirs)
                    {
                        string sourceName = Path.GetFileName(originalInputItemForFile);
                        finalOutputDirectory = Path.Combine(customImageOutputPath, sourceName, relativePathDir);
                    }
                    else
                    {
                        finalOutputDirectory = Path.Combine(customImageOutputPath, relativePathDir);
                    }
                }
                else
                {
                    // --- *** CORRECTED In-Place Logic *** ---
                    // "In-place" output uses the ORIGINAL file's parent directory directly.
                    string? originalFileParentDir = Path.GetDirectoryName(originalFilePathForContext);
                    if (string.IsNullOrEmpty(originalFileParentDir))
                    {
                        LogMessage(GetLocalizedString("ErrorDeterminingDirectory", originalFilePathForContext));
                        skipFile = true;
                    }
                    else
                    {
                        finalOutputDirectory = originalFileParentDir; // Use the original parent directly
                    }
                    // --- *** End Correction *** ---
                }

                // Ensure output directory exists
                if (!skipFile)
                {
                    if (string.IsNullOrEmpty(finalOutputDirectory)) { LogMessage(GetLocalizedString("ErrorDeterminingDirectory", "final output")); skipFile = true; }
                    else if (!Directory.Exists(finalOutputDirectory)) { try { Directory.CreateDirectory(finalOutputDirectory); LogMessage(GetLocalizedString("CreatedOutputDir", finalOutputDirectory)); } catch (Exception crEx) { LogMessage(GetLocalizedString("ErrorCreatingOutputFolder", finalOutputDirectory, crEx.Message)); skipFile = true; } }
                }

                // --- Generate Final Filename (using finalOutputDirectory) ---
                if (!skipFile)
                {
                    // Counter key should be the final output directory
                    int uniqueCounter = renamingCounters.AddOrUpdate(finalOutputDirectory, counterStartValue, (key, existingValue) => existingValue + 1);
                    string counterString = ""; if (useCounter) { try { counterString = uniqueCounter.ToString(counterFormat); } catch { counterString = uniqueCounter.ToString(DefaultCounterFormat); } }
                    var nameParts = new List<string>(); nameParts.Add(renamePrefix); if (useTimestamp && !string.IsNullOrEmpty(timestamp)) { nameParts.Add(timestamp); }
                    if (useCounter && !string.IsNullOrEmpty(counterString)) { nameParts.Add(counterString); }
                    string baseFileName = string.Join("_", nameParts.Where(p => !string.IsNullOrEmpty(p))); if (string.IsNullOrEmpty(baseFileName)) { baseFileName = Path.GetFileNameWithoutExtension(originalFilePathForContext) + "_processed"; }
                    string extension = Path.GetExtension(originalFilePathForContext).ToLowerInvariant(); string potentialFilePath = Path.Combine(finalOutputDirectory, baseFileName + extension); int collisionCounter = 1; string originalBaseForCollision = baseFileName;
                    while (File.Exists(potentialFilePath) || Directory.Exists(potentialFilePath)) { baseFileName = $"{originalBaseForCollision}({collisionCounter++})"; potentialFilePath = Path.Combine(finalOutputDirectory, baseFileName + extension); if (collisionCounter > 100) { LogMessage(GetLocalizedString("ErrorUniqueFile", originalBaseForCollision, finalOutputDirectory)); skipFile = true; break; } }
                    if (!skipFile) finalOutputFilePath = potentialFilePath;
                }

                // --- Tool Execution ---
                if (!skipFile)
                {
                    if (!File.Exists(toolSourcePath)) { LogMessage(GetLocalizedString("ExifToolSourceNotFound", toolSourcePath) + (enableBackup ? " (Backup location?)" : "")); skipFile = true; }
                    else
                    {
                        string args; if (useImageMagick) { args = $"\"{toolSourcePath}\" -strip \"{finalOutputFilePath}\""; } else { args = $"-all= -TagsFromFile \"{toolSourcePath}\" -Orientation -ColorSpace -ExifByteOrder -Software= -ModifyDate= -o \"{finalOutputFilePath}\" \"{toolSourcePath}\""; }
                        LogMessage(GetLocalizedString("Debug_ToolArgs", args));
                        var psi = new ProcessStartInfo { FileName = nonNullToolPath, Arguments = args, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(nonNullToolPath), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                        using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
                        {
                            var errorLines = new List<string>(); proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) errorLines.Add(e.Data); }; var outputLines = new List<string>(); proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) outputLines.Add(e.Data); }; proc.Start(); proc.BeginErrorReadLine(); proc.BeginOutputReadLine(); await proc.WaitForExitAsync(token); bool toolSuccess = false; try { toolSuccess = proc.ExitCode == 0 && File.Exists(finalOutputFilePath) && new FileInfo(finalOutputFilePath).Length > 0; } catch { toolSuccess = false; }
                            if (toolSuccess) { Interlocked.Increment(ref processedImages); LogMessage(GetLocalizedString(successKey, Path.GetFileName(originalFilePathForContext), finalOutputFilePath)); if (!enableBackup && customImageOutputPath == null && toolSourcePath != finalOutputFilePath) { LogMessage(GetLocalizedString("Debug_BackupDisabledDelete", toolSourcePath)); try { if (File.Exists(toolSourcePath)) File.Delete(toolSourcePath); else LogMessage(GetLocalizedString("Debug_OriginalFileGone", toolSourcePath)); } catch (Exception delEx) { LogMessage(GetLocalizedString("ErrorDeletingOriginal", toolSourcePath, delEx.Message)); } } } else { string errSummary = errorLines.Any() ? string.Join("; ", errorLines) : (outputLines.Any() ? string.Join("; ", outputLines) : GetLocalizedString("NoSpecificError")); LogMessage(GetLocalizedString(failedKey, Path.GetFileName(originalFilePathForContext), proc.ExitCode, errSummary)); if (File.Exists(finalOutputFilePath)) { try { File.Delete(finalOutputFilePath); } catch { /* Ignore delete error */ } } skipFile = true; }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { LogMessage(GetLocalizedString("Debug_TaskCancelledGeneric", Path.GetFileName(originalFilePathForContext))); return; }
            catch (Exception ex) { LogMessage(GetLocalizedString("UnexpectedErrorProcessingFile", originalFilePathForContext, ex.GetType().Name, ex.Message)); skipFile = true; }
            finally { if (skipFile) { Interlocked.Increment(ref failedImages); } int tasksBeforeDecrement = _activeTasks; Interlocked.Decrement(ref _activeTasks); LogMessage(GetLocalizedString("Debug_TaskEnded", Path.GetFileName(originalFilePathForContext), tasksBeforeDecrement, _activeTasks)); await UpdateCountsAndUIAsync(token); processSemaphore.Release(); LogMessage(GetLocalizedString("Debug_SemaphoreReleased", Path.GetFileName(originalFilePathForContext), processSemaphore.CurrentCount)); }
        }

        // --- Direct Renaming Logic ---
        private async Task PerformDirectRenamingAsync(List<string> originalInputPaths, bool renameFolders, bool renameFiles, string filePrefix, string folderPrefix, bool enableTimestampNaming, string timestamp, bool enableCounterNaming, string counterFormat, int counterStartValue, CancellationToken token)
        {
            var folderCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); var fileCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); folderRenameMap_DirectModeOnly.Clear(); LogMessage(GetLocalizedString("DirectRename_Preparing")); UpdateStatusLabel(GetLocalizedString("DirectRename_Preparing")); await Dispatcher.InvokeAsync(() => progressBar.IsIndeterminate = true); var topLevelFoldersToProcess = new List<string>(); var filesToRename = new List<string>(); var processedOriginalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int collectedFileCount = 0, collectedFolderCount = 0;
            if (renameFolders || renameFiles) { try { await Task.Run(async () => { var collectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var collectedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (string path in originalInputPaths) { token.ThrowIfCancellationRequested(); if (string.IsNullOrWhiteSpace(path)) continue; try { string fullPath = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); if (renameFolders && Directory.Exists(fullPath)) { collectedFolders.Add(fullPath); if (renameFiles) { try { var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }; foreach (var file in Directory.EnumerateFiles(fullPath, "*.*", opts)) { token.ThrowIfCancellationRequested(); collectedFiles.Add(file); } } catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is DirectoryNotFoundException) { await Dispatcher.InvokeAsync(() => LogMessage(GetLocalizedString("WarningScanningFolder", path, ex.GetType().Name, ex.Message))); } } } else if (renameFiles && File.Exists(fullPath)) { collectedFiles.Add(fullPath); } } catch (Exception ex) { await Dispatcher.InvokeAsync(() => LogMessage(GetLocalizedString("ErrorCheckingFolderPath", path, ex.Message))); } } topLevelFoldersToProcess.AddRange(collectedFolders.OrderBy(f => f)); filesToRename.AddRange(collectedFiles.OrderBy(f => f)); collectedFileCount = filesToRename.Count; collectedFolderCount = topLevelFoldersToProcess.Count; }, token); } catch (OperationCanceledException) { LogMessage(GetLocalizedString("ProcessingCancelled")); await Dispatcher.InvokeAsync(() => { if (progressBar != null) progressBar.IsIndeterminate = false; }); return; } }
            if (renameFolders) LogMessage(GetLocalizedString("DirectRename_FoundFolders", collectedFolderCount)); if (renameFiles) LogMessage(GetLocalizedString("DirectRename_FoundFiles", collectedFileCount)); totalImages = filesToRename.Count + (renameFolders ? collectedFolderCount : 0); await Dispatcher.InvokeAsync(() => { if (progressBar != null) { progressBar.Maximum = Math.Max(1, totalImages); progressBar.IsIndeterminate = false; UpdateProgressBar(0); } }); int itemsProcessedSoFar = 0; processedImages = 0; failedImages = 0; await UpdateCountsAndUIAsync(token);
            if (renameFolders && topLevelFoldersToProcess.Count > 0) { LogMessage(GetLocalizedString("DirectRename_StartFolders")); var processingStack = new Stack<string>(topLevelFoldersToProcess.OrderByDescending(f => f.Length)); var stopwatch = new Stopwatch(); while (processingStack.Count > 0) { token.ThrowIfCancellationRequested(); string currentOriginalPath = processingStack.Pop(); if (processedOriginalPaths.Contains(currentOriginalPath)) continue; itemsProcessedSoFar++; string displayFolderName = Path.GetFileName(currentOriginalPath.TrimEnd(Path.DirectorySeparatorChar)); await Dispatcher.InvokeAsync(() => UpdateStatusLabel(GetLocalizedString("DirectRename_FolderStatus", displayFolderName, itemsProcessedSoFar, totalImages))); UpdateProgressBar(itemsProcessedSoFar); string currentActualPath = CheckIfPathWasRenamed(currentOriginalPath, folderRenameMap_DirectModeOnly) ?? currentOriginalPath; stopwatch.Restart(); bool moveSuccess = false; if (!Directory.Exists(currentActualPath)) { LogMessage(GetLocalizedString("DirectRename_FolderNotFound", currentOriginalPath, currentActualPath)); failedImages++; processedOriginalPaths.Add(currentOriginalPath); goto SkipFolderProcessing_DR; } string? parentDir = null; try { parentDir = Path.GetDirectoryName(currentActualPath.TrimEnd(Path.DirectorySeparatorChar)); } catch { } if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir)) { LogMessage(GetLocalizedString("DirectRename_ParentError", currentActualPath)); failedImages++; processedOriginalPaths.Add(currentOriginalPath); goto SkipFolderProcessing_DR; } if (!folderCounters.ContainsKey(parentDir)) folderCounters[parentDir] = counterStartValue; else folderCounters[parentDir]++; int index = folderCounters[parentDir]; string counterString = ""; if (enableCounterNaming) { try { counterString = index.ToString(counterFormat); } catch { counterString = index.ToString(DefaultCounterFormat); } } string newFolderNameBase; var nameParts = new List<string>(); if (!string.IsNullOrEmpty(folderPrefix)) nameParts.Add(folderPrefix); if (enableTimestampNaming && !string.IsNullOrEmpty(timestamp)) nameParts.Add(timestamp); if (enableCounterNaming && !string.IsNullOrEmpty(counterString)) nameParts.Add(counterString); if (nameParts.Count > 0) { newFolderNameBase = string.Join("_", nameParts); } else { newFolderNameBase = Path.GetFileName(currentActualPath.TrimEnd(Path.DirectorySeparatorChar)) + "_renamed"; } string newFolderName = CleanPathSegment(newFolderNameBase); string newFolderPath = Path.Combine(parentDir, newFolderName); int collisionCounter = 1; while (Directory.Exists(newFolderPath) || File.Exists(newFolderPath)) { token.ThrowIfCancellationRequested(); newFolderName = CleanPathSegment($"{newFolderNameBase}({collisionCounter++})"); newFolderPath = Path.Combine(parentDir, newFolderName); if (collisionCounter > 100) { LogMessage(GetLocalizedString("ErrorUniqueFolder", newFolderNameBase)); moveSuccess = false; goto SkipFolderProcessing_DR; } } int retryCount = 0; const int maxRetries = 1; const int retryDelayMs = 300; while (!moveSuccess && retryCount <= maxRetries) { token.ThrowIfCancellationRequested(); try { string retryMsg = retryCount > 0 ? $" ({GetLocalizedString("RetryAttempt")}{retryCount})" : ""; LogMessage(GetLocalizedString("DirectRename_AttemptFolder", currentActualPath, newFolderPath) + retryMsg); await Task.Run(() => Directory.Move(currentActualPath, newFolderPath), token); moveSuccess = true; processedImages++; processedOriginalPaths.Add(currentOriginalPath); folderRenameMap_DirectModeOnly[currentOriginalPath] = newFolderPath; stopwatch.Stop(); double elapsed = stopwatch.Elapsed.TotalSeconds; LogMessage(GetLocalizedString("DirectRename_FolderSuccess", currentOriginalPath, newFolderPath) + $" (Took:{elapsed:F2}s) [{processedImages + failedImages}/{totalImages}]"); try { var subDirs = Directory.EnumerateDirectories(newFolderPath); foreach (var subDirFullPath in subDirs.OrderByDescending(d => d.Length)) { token.ThrowIfCancellationRequested(); string subDirName = Path.GetFileName(subDirFullPath); string originalSubDirPath = Path.Combine(currentOriginalPath, subDirName); if (!processedOriginalPaths.Contains(originalSubDirPath)) { processingStack.Push(originalSubDirPath); } } } catch (Exception findEx) { LogMessage(GetLocalizedString("DirectRename_SubDirFindError", newFolderPath, findEx.Message)); } } catch (IOException ioEx) when (IsAccessException(ioEx)) { retryCount++; LogMessage(GetLocalizedString("DirectRename_AccessDeniedWarning", displayFolderName)); if (retryCount <= maxRetries) { MessageBoxResult userChoice = await Dispatcher.InvokeAsync(() => MessageBox.Show(this, GetLocalizedString("DirectRename_RetryPromptMessage", currentActualPath), GetLocalizedString("RetryTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning)); if (userChoice == MessageBoxResult.Yes) { LogMessage(GetLocalizedString("DirectRename_RetryLog", retryDelayMs)); await Task.Delay(retryDelayMs, token); } else { LogMessage(GetLocalizedString("DirectRename_UserCancelledRetry", displayFolderName)); break; } } else { LogMessage(GetLocalizedString("DirectRename_MaxRetriesReached", displayFolderName)); } } catch (OperationCanceledException) { stopwatch.Stop(); throw; } catch (Exception ex) { LogMessage(GetLocalizedString("DirectRename_ErrorFolder", displayFolderName, newFolderName, ex.Message)); break; } } SkipFolderProcessing_DR:; stopwatch.Stop(); if (!moveSuccess) { if (!processedOriginalPaths.Contains(currentOriginalPath)) { failedImages++; processedOriginalPaths.Add(currentOriginalPath); } } await UpdateCountsAndUIAsync(token); } LogMessage(GetLocalizedString("DirectRename_FolderComplete")); }
            if (renameFiles && filesToRename.Count > 0) { LogMessage(GetLocalizedString("DirectRename_StartFiles")); var stopwatch = new Stopwatch(); foreach (string originalFilePath in filesToRename) { token.ThrowIfCancellationRequested(); if (!renameFolders) itemsProcessedSoFar++; string currentFilePath = CheckIfPathWasRenamed(originalFilePath, folderRenameMap_DirectModeOnly) ?? originalFilePath; string currentFileName = Path.GetFileName(currentFilePath); string? currentFileDir = Path.GetDirectoryName(currentFilePath); await Dispatcher.InvokeAsync(() => UpdateStatusLabel(GetLocalizedString("DirectRename_FileStatus", currentFileName, itemsProcessedSoFar, totalImages))); if (!renameFolders) UpdateProgressBar(itemsProcessedSoFar); stopwatch.Restart(); if (string.IsNullOrEmpty(currentFileDir)) { LogMessage(GetLocalizedString("DirectRename_ParentError", currentFileName)); failedImages++; stopwatch.Stop(); await UpdateCountsAndUIAsync(token); continue; } string? originalDir = Path.GetDirectoryName(originalFilePath); if (renameFolders && originalDir != null && processedOriginalPaths.Contains(originalDir) && !folderRenameMap_DirectModeOnly.ContainsKey(originalDir)) { LogMessage($"Skipping file '{currentFileName}' because its parent folder '{originalDir}' failed renaming."); failedImages++; stopwatch.Stop(); await UpdateCountsAndUIAsync(token); continue; } if (!File.Exists(currentFilePath)) { if (!renameFolders || originalDir == null || !processedOriginalPaths.Contains(originalDir)) { LogMessage(GetLocalizedString("DirectRename_FileNotFound", currentFilePath)); } failedImages++; stopwatch.Stop(); await UpdateCountsAndUIAsync(token); continue; } if (!Directory.Exists(currentFileDir)) { LogMessage(GetLocalizedString("DirectRename_FileDirError", currentFileName, currentFileDir)); failedImages++; stopwatch.Stop(); await UpdateCountsAndUIAsync(token); continue; } if (!fileCounters.ContainsKey(currentFileDir)) fileCounters[currentFileDir] = counterStartValue; else fileCounters[currentFileDir]++; int fileIndex = fileCounters[currentFileDir]; string counterString = ""; if (enableCounterNaming) { try { counterString = fileIndex.ToString(counterFormat); } catch { counterString = fileIndex.ToString(DefaultCounterFormat); } } string newFileNameBase; var nameParts = new List<string>(); if (!string.IsNullOrEmpty(filePrefix)) nameParts.Add(filePrefix); if (enableTimestampNaming && !string.IsNullOrEmpty(timestamp)) nameParts.Add(timestamp); if (enableCounterNaming && !string.IsNullOrEmpty(counterString)) nameParts.Add(counterString); if (nameParts.Count == 0) { newFileNameBase = Path.GetFileNameWithoutExtension(currentFileName) + "_renamed"; } else { newFileNameBase = string.Join("_", nameParts); } string fileExt = Path.GetExtension(currentFilePath); string newFileName = CleanPrefix(newFileNameBase) + fileExt; string newFilePath = Path.Combine(currentFileDir, newFileName); int collisionCounter = 1; while (File.Exists(newFilePath) || Directory.Exists(newFilePath)) { token.ThrowIfCancellationRequested(); newFileName = CleanPrefix($"{newFileNameBase}({collisionCounter++})") + fileExt; newFilePath = Path.Combine(currentFileDir, newFileName); if (collisionCounter > 100) { LogMessage(GetLocalizedString("ErrorUniqueFile", newFileNameBase, currentFileDir)); goto SkipFileProcessing_DR; } } try { LogMessage(GetLocalizedString("DirectRename_AttemptFile", currentFilePath, newFilePath)); await Task.Run(() => File.Move(currentFilePath, newFilePath), token); stopwatch.Stop(); double elapsed = stopwatch.Elapsed.TotalSeconds; processedImages++; LogMessage(GetLocalizedString("DirectRename_FileSuccess", currentFilePath, newFilePath) + $" (Took:{elapsed:F2}s) [{processedImages + failedImages}/{totalImages}]"); } catch (OperationCanceledException) { stopwatch.Stop(); throw; } catch (IOException ioEx) when (IsAccessException(ioEx)) { stopwatch.Stop(); failedImages++; LogMessage(GetLocalizedString("DirectRename_FileAccessDenied", currentFileName) + $" [{processedImages + failedImages}/{totalImages}]"); } catch (Exception ex) { stopwatch.Stop(); failedImages++; LogMessage(GetLocalizedString("DirectRename_ErrorFile", currentFileName, newFileName, ex.Message) + $" [{processedImages + failedImages}/{totalImages}]"); } SkipFileProcessing_DR:; if (!stopwatch.IsRunning) stopwatch.Stop(); await UpdateCountsAndUIAsync(token); } LogMessage(GetLocalizedString("DirectRename_FileComplete")); }
            if (totalImages == 0 && (renameFolders || renameFiles)) { LogMessage(GetLocalizedString("DirectRename_NothingSelected")); }
        }

        // --- Video Generation Logic ---
        private async Task GenerateZoompanVideosAsync(List<Tuple<string, string>> filesToProcessDetails, List<string> originalInputPaths, string baseOutputDirectory, bool applyZoompan, ZoompanSettings settings, string resolutionTag, OutputFormat outputFormat, CancellationToken token, string renamePrefix, bool useTimestamp, string timestamp, bool useCounter, string counterFormat, int counterStartValue, string? customVideoOutputPath, bool customOutputRequiresSubdirs, Dictionary<string, string> sourceToBackupPathMap, bool enableBackup)
        {
            List<Task> processingTasks = new List<Task>();
            var renamingCounters = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < filesToProcessDetails.Count; i++) { if (token.IsCancellationRequested) break; string originalFilePath = filesToProcessDetails[i].Item1; string currentProcessingFile = filesToProcessDetails[i].Item2; int currentFileIndex = i; processingTasks.Add(GenerateSingleVideoAsync(currentProcessingFile, originalFilePath, originalInputPaths, baseOutputDirectory, applyZoompan, settings, resolutionTag, outputFormat, token, currentFileIndex, renamePrefix, useTimestamp, timestamp, useCounter, counterFormat, counterStartValue, customVideoOutputPath, customOutputRequiresSubdirs, renamingCounters)); }
            try { await Task.WhenAll(processingTasks); LogMessage(GetLocalizedString("Debug_ParallelProcessingFinished", processedImages, failedImages)); }
            catch (OperationCanceledException) { LogMessage(GetLocalizedString("Debug_WhenAllCaughtCancellation")); throw; }
            catch (Exception ex) { LogMessage(GetLocalizedString("Debug_WhenAllCaughtError", ex.GetType().Name, ex.Message)); if (ex is AggregateException aggEx) { foreach (var innerEx in aggEx.Flatten().InnerExceptions) { LogMessage(GetLocalizedString("Debug_WhenAllInnerError", innerEx.GetType().Name, innerEx.Message)); } } }
            finally { LogMessage($"DEBUG: Parallel video generation finished check. Processed: {processedImages}, Failed: {failedImages}"); }
        }

        private async Task GenerateSingleVideoAsync(string currentProcessingFile, string originalFilePathForContext, List<string> originalInputPaths, string baseOutputDirectory, bool applyZoompan, ZoompanSettings settings, string resolutionTag, OutputFormat outputFormat, CancellationToken token, int fileIndex, string renamePrefix, bool useTimestamp, string timestamp, bool useCounter, string counterFormat, int counterStartValue, string? customVideoOutputPath, bool customOutputRequiresSubdirs, ConcurrentDictionary<string, int> renamingCounters)
        {
            await processSemaphore.WaitAsync(token); Interlocked.Increment(ref _activeTasks); await UpdateCountsAndUIAsync(token); bool skipFile = false; string finalOutputDirectory = ""; string finalOutputFile = ""; string tempPalettePath = "";
            try
            {
                if (token.IsCancellationRequested) return;
                string originalInputItemForFile = FindBestInputPathForFile(originalFilePathForContext, originalInputPaths); string relativePathDir = ""; try { string relativePath = Path.GetRelativePath(originalInputItemForFile, originalFilePathForContext); relativePathDir = Path.GetDirectoryName(relativePath) ?? ""; if (string.IsNullOrEmpty(relativePathDir) || relativePathDir == ".") relativePathDir = ""; } catch (Exception ex) { LogMessage(GetLocalizedString("WarnRelativePathVideo", originalFilePathForContext, ex.Message)); relativePathDir = ""; }
                if (customVideoOutputPath != null) { if (customOutputRequiresSubdirs) { string sourceName = Path.GetFileName(originalInputItemForFile); finalOutputDirectory = Path.Combine(customVideoOutputPath, sourceName, relativePathDir); } else { finalOutputDirectory = Path.Combine(customVideoOutputPath, relativePathDir); } } else { finalOutputDirectory = Path.Combine(baseOutputDirectory, relativePathDir); }
                if (string.IsNullOrEmpty(finalOutputDirectory)) { LogMessage("Could not determine final output directory for video."); skipFile = true; } else if (!Directory.Exists(finalOutputDirectory)) { try { Directory.CreateDirectory(finalOutputDirectory); LogMessage(GetLocalizedString("CreatedOutputDir", finalOutputDirectory)); } catch (Exception crEx) { LogMessage(GetLocalizedString("ErrorCreatingOutputFolder", finalOutputDirectory, crEx.Message)); skipFile = true; } }
                if (skipFile) return;

                string originalBaseFileName = Path.GetFileNameWithoutExtension(originalFilePathForContext); string outputExtension = outputFormat switch { OutputFormat.MP4 => ".mp4", OutputFormat.GIF => ".gif", _ => ".mov" }; int uniqueCounter = renamingCounters.AddOrUpdate(finalOutputDirectory, counterStartValue, (key, existingValue) => existingValue + 1); string counterString = ""; if (useCounter) { try { counterString = uniqueCounter.ToString(counterFormat); } catch { counterString = uniqueCounter.ToString(DefaultCounterFormat); } }
                var nameParts = new List<string>(); if (!string.IsNullOrEmpty(renamePrefix)) { nameParts.Add(renamePrefix); } else { nameParts.Add(CleanPrefix(originalBaseFileName)); }
                if (useTimestamp && !string.IsNullOrEmpty(timestamp)) { nameParts.Add(timestamp); }
                if (useCounter && !string.IsNullOrEmpty(counterString)) { nameParts.Add(counterString); }
                string baseFileName = string.Join("_", nameParts.Where(p => !string.IsNullOrEmpty(p))); if (string.IsNullOrWhiteSpace(baseFileName)) baseFileName = $"video_{fileIndex + 1}";
                string potentialFilePath = Path.Combine(finalOutputDirectory, $"{baseFileName}{outputExtension}"); int collisionCounter = 1; finalOutputFile = potentialFilePath;
                while (File.Exists(finalOutputFile)) { finalOutputFile = Path.Combine(finalOutputDirectory, $"{baseFileName}({collisionCounter++}){outputExtension}"); if (collisionCounter > 100) { LogMessage(GetLocalizedString("ErrorUniqueVideo", baseFileName)); skipFile = true; break; } }
                if (skipFile) return;

                string? ffprobePath = FindToolPath("ffprobe.exe"); string? ffmpegPath = FindToolPath("ffmpeg.exe"); 
                if (string.IsNullOrEmpty(ffprobePath) || string.IsNullOrEmpty(ffmpegPath)) { LogMessage("Error: FFmpeg or FFprobe tool path not found within task."); skipFile = true; return; }
                await Dispatcher.InvokeAsync(() => UpdateStatusLabel(GetLocalizedString("ZoompanStatusProcessing", Path.GetFileName(originalFilePathForContext), fileIndex + 1, totalImages))); Stopwatch stopwatch = Stopwatch.StartNew(); 
                string? sourceResolution = await GetImageResolutionAsync(ffprobePath, currentProcessingFile, token); 
                if (string.IsNullOrEmpty(sourceResolution)) { LogMessage(GetLocalizedString("ErrorGettingResolution", Path.GetFileName(originalFilePathForContext), fileIndex + 1, totalImages)); skipFile = true; return; }
                string[] sourceDims = sourceResolution.Split('x'); 
                if (sourceDims.Length != 2 || !int.TryParse(sourceDims[0], out int srcWidth) || !int.TryParse(sourceDims[1], out int srcHeight) || srcWidth <= 0 || srcHeight <= 0) { LogMessage($"Error parsing source resolution '{sourceResolution}' for {Path.GetFileName(originalFilePathForContext)}. Skipping."); skipFile = true; return; }
                string outputResolution = resolutionTag == "source" ? sourceResolution : resolutionTag; ZoompanEffectType effectToApply = settings.EffectType; 
                if (effectToApply == ZoompanEffectType.RandomPreset) { var availablePresets = Enum.GetValues(typeof(ZoompanEffectType)).Cast<ZoompanEffectType>().Where(et => et != ZoompanEffectType.Custom && et != ZoompanEffectType.RandomPreset).ToList(); effectToApply = availablePresets[random.Next(availablePresets.Count)]; }
                string vf_filter_parts = ""; double outputDuration = settings.DurationSeconds; int fps = settings.Fps; 
                if (applyZoompan) { vf_filter_parts = BuildZoompanFilter(effectToApply, settings, outputResolution, srcWidth, srcHeight); } else { outputDuration = 1.5; fps = 30; 
                    if (outputResolution != sourceResolution) vf_filter_parts = $"scale={outputResolution}:force_original_aspect_ratio=decrease,pad={outputResolution}:(ow-iw)/2:(oh-ih)/2,format=pix_fmts=yuv420p"; else vf_filter_parts = "format=pix_fmts=yuv420p"; }
                string vfArg = string.IsNullOrEmpty(vf_filter_parts) ? "" : $"-vf \"{vf_filter_parts}\" "; string durationArg = outputDuration.ToString(CultureInfo.InvariantCulture); string args; bool requiresTwoPass = false; switch (outputFormat) { case OutputFormat.MP4: args = $"-y -loop 1 -i \"{currentProcessingFile}\" {vfArg}-c:v libx264 -preset slow -crf 22 -pix_fmt yuv420p -r {fps} -t {durationArg} \"{finalOutputFile}\""; break; 
                    case OutputFormat.GIF: requiresTwoPass = true; tempPalettePath = Path.Combine(Path.GetTempPath(), $"palette_{Guid.NewGuid()}.png"); string paletteVf = vf_filter_parts.Length > 0 ? vf_filter_parts.Replace(",format=pix_fmts=yuv420p", "") + $",fps={fps},scale=640:-1:flags=lanczos,palettegen=stats_mode=diff" : $"fps={fps},scale=640:-1:flags=lanczos,palettegen=stats_mode=diff"; 
                        string paletteArgs = $"-y -loop 1 -i \"{currentProcessingFile}\" -vf \"{paletteVf}\" -t {durationArg} \"{tempPalettePath}\""; LogMessage(GetLocalizedString("GeneratingPalette")); if (!await RunFFmpegProcessAsync(ffmpegPath, paletteArgs, token, "PaletteGenFailed", Path.GetFileName(originalFilePathForContext), tempPalettePath)) { skipFile = true; return; } LogMessage(GetLocalizedString("EncodingGIF")); string encodeVf = vf_filter_parts.Length > 0 ? vf_filter_parts.Replace(",format=pix_fmts=yuv420p", "") + $",fps={fps},scale=640:-1:flags=lanczos [x]; [x][1:v] paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" : $"fps={fps},scale=640:-1:flags=lanczos [x]; [x][1:v] paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle"; args = $"-y -loop 1 -i \"{currentProcessingFile}\" -i \"{tempPalettePath}\" -lavfi \"{encodeVf}\" -t {durationArg} -f gif \"{finalOutputFile}\""; break; case OutputFormat.MOV: default: args = $"-y -loop 1 -i \"{currentProcessingFile}\" {vfArg}-c:v libx265 -preset slow -crf 20 -tag:v hvc1 -pix_fmt yuv420p -r {fps} -t {durationArg} \"{finalOutputFile}\""; break; }
                if (!await RunFFmpegProcessAsync(ffmpegPath, args, token, requiresTwoPass ? "GIFEncodingFailed" : "FailedZoompan", Path.GetFileName(originalFilePathForContext), finalOutputFile, stopwatch)) { skipFile = true; }

            }
            catch (OperationCanceledException) { LogMessage($"DEBUG: Task cancelled for {originalFilePathForContext}."); skipFile = true; }
            catch (Exception ex) { LogMessage(GetLocalizedString("UnexpectedErrorProcessingFile", originalFilePathForContext, ex.GetType().Name, ex.Message)); skipFile = true; }
            finally { if (!skipFile) { Interlocked.Increment(ref processedImages); } else { Interlocked.Increment(ref failedImages); } int tasksBeforeDecrement = _activeTasks; Interlocked.Decrement(ref _activeTasks); LogMessage(GetLocalizedString("Debug_TaskEnded", Path.GetFileName(originalFilePathForContext), tasksBeforeDecrement, _activeTasks)); await UpdateCountsAndUIAsync(token); if (File.Exists(tempPalettePath)) try { File.Delete(tempPalettePath); } catch { /* Ignore delete error */ } processSemaphore.Release(); LogMessage(GetLocalizedString("Debug_SemaphoreReleased", Path.GetFileName(originalFilePathForContext), processSemaphore.CurrentCount)); }
        }

        private async Task GenerateBurstVideoAsync(string currentInputFolder, string originalInputFolder, string outputDirectory, OutputFormat outputFormat, int framerate, CancellationToken token, string renamePrefix, bool useTimestamp, string timestamp, bool useCounter, string counterFormat, int counterStartValue)
        {
            string? ffmpegPath = FindToolPath("ffmpeg.exe"); if (string.IsNullOrEmpty(ffmpegPath)) throw new FileNotFoundException(GetLocalizedString("FFmpegNotFound"), "ffmpeg.exe");
            var stopwatch = new Stopwatch(); stopwatch.Start(); await Dispatcher.InvokeAsync(() => UpdateStatusLabel($"Processing (Burst): {Path.GetFileName(originalInputFolder)}")); string outputExtension = outputFormat switch { OutputFormat.MP4 => ".mp4", OutputFormat.GIF => ".gif", _ => ".mov" };
            string originalFolderName = Path.GetFileName(originalInputFolder); var nameParts = new List<string>(); if (!string.IsNullOrEmpty(renamePrefix)) { nameParts.Add(renamePrefix); } else { nameParts.Add(CleanPrefix(originalFolderName)); }
            if (useTimestamp && !string.IsNullOrEmpty(timestamp)) { nameParts.Add(timestamp); }
            if (useCounter) { string counterString = ""; try { counterString = counterStartValue.ToString(counterFormat); } catch { counterString = counterStartValue.ToString(DefaultCounterFormat); } if (!string.IsNullOrEmpty(counterString)) nameParts.Add(counterString); }
            nameParts.Add("burst");
            string baseFileName = string.Join("_", nameParts.Where(p => !string.IsNullOrEmpty(p))); if (string.IsNullOrWhiteSpace(baseFileName)) baseFileName = "burst_output";
            string potentialOutputFile = Path.Combine(outputDirectory, $"{baseFileName}{outputExtension}"); int collisionCounter = 1; string finalOutputFile = potentialOutputFile;
            while (File.Exists(finalOutputFile)) { finalOutputFile = Path.Combine(outputDirectory, $"{baseFileName}({collisionCounter++}){outputExtension}"); if (collisionCounter > 100) { LogMessage(GetLocalizedString("ErrorUniqueBurst", baseFileName)); Interlocked.Increment(ref failedImages); await UpdateCountsAndUIAsync(token); return; } }

            string fileListPath = Path.Combine(Path.GetTempPath(), $"burst_list_{Guid.NewGuid()}.txt"); List<string> imageFiles;
            try { imageFiles = Directory.EnumerateFiles(currentInputFolder).Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f).ToList(); if (imageFiles.Count == 0) { LogMessage(GetLocalizedString("BurstModeNoImages")); Interlocked.Increment(ref failedImages); await UpdateCountsAndUIAsync(token); return; } await File.WriteAllLinesAsync(fileListPath, imageFiles.Select(f => $"file '{f.Replace("'", "'\\''")}'"), Encoding.UTF8, token); } catch (Exception ex) { LogMessage($"Error enumerating/writing file list for '{currentInputFolder}': {ex.Message}"); Interlocked.Increment(ref failedImages); await UpdateCountsAndUIAsync(token); if (File.Exists(fileListPath)) try { File.Delete(fileListPath); } catch { /* Ignore delete error */ } return; }

            string args = ""; bool success = false; string tempPalettePath = "";
            try
            {
                if (outputFormat == OutputFormat.GIF) { tempPalettePath = Path.Combine(Path.GetTempPath(), $"palette_{Guid.NewGuid()}.png"); string paletteArgs = $"-y -f concat -safe 0 -i \"{fileListPath}\" -vf \"fps={framerate},scale=640:-1:flags=lanczos,palettegen=stats_mode=diff\" \"{tempPalettePath}\""; LogMessage(GetLocalizedString("GeneratingPalette")); if (!await RunFFmpegProcessAsync(ffmpegPath, paletteArgs, token, "PaletteGenFailed", Path.GetFileName(originalInputFolder), tempPalettePath)) { throw new Exception("Palette generation failed."); } LogMessage(GetLocalizedString("EncodingGIF")); args = $"-y -f concat -safe 0 -i \"{fileListPath}\" -i \"{tempPalettePath}\" -lavfi \"fps={framerate},scale=640:-1:flags=lanczos [x]; [x][1:v] paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle\" -f gif \"{finalOutputFile}\""; success = await RunFFmpegProcessAsync(ffmpegPath, args, token, "GIFEncodingFailed", Path.GetFileName(originalInputFolder), finalOutputFile, stopwatch); } else { string codec, preset, crf, pix_fmt, tag = ""; if (outputFormat == OutputFormat.MP4) { codec = "libx264"; preset = "medium"; crf = "23"; pix_fmt = "yuv420p"; } else { codec = "libx265"; preset = "medium"; crf = "25"; pix_fmt = "yuv420p"; tag = "-tag:v hvc1 "; } args = $"-y -f concat -safe 0 -i \"{fileListPath}\" -r {framerate} -c:v {codec} -preset {preset} -crf {crf} {tag}-pix_fmt {pix_fmt} \"{finalOutputFile}\""; success = await RunFFmpegProcessAsync(ffmpegPath, args, token, "FailedBurst", Path.GetFileName(originalInputFolder), finalOutputFile, stopwatch); }
            }
            catch (OperationCanceledException) { success = false; throw; }
            catch (Exception) { success = false; /* Error logged in RunFFmpegProcessAsync */ }
            finally { if (File.Exists(tempPalettePath)) try { File.Delete(tempPalettePath); } catch { /* Ignore delete error */ } if (File.Exists(fileListPath)) try { File.Delete(fileListPath); } catch { /* Ignore delete error */ } if (success) { Interlocked.Increment(ref processedImages); } else { Interlocked.Increment(ref failedImages); } await UpdateCountsAndUIAsync(token); }
        }

        private async Task<bool> RunFFmpegProcessAsync(string ffmpegPath, string args, CancellationToken token, string failMessageKey, string inputDesc, string outputPath, Stopwatch? outerStopwatch = null) { var psi = new ProcessStartInfo { FileName = ffmpegPath, Arguments = args, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = false, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(ffmpegPath), StandardErrorEncoding = Encoding.UTF8 }; Stopwatch stopwatch = outerStopwatch ?? new Stopwatch(); if (outerStopwatch == null) stopwatch.Start(); bool success = false; int exitCode = -1; using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true }) { var errorLines = new List<string>(); proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) errorLines.Add(e.Data); }; try { proc.Start(); proc.BeginErrorReadLine(); await proc.WaitForExitAsync(token); if (outerStopwatch == null || !outerStopwatch.IsRunning) stopwatch.Stop(); exitCode = proc.ExitCode; try { success = proc.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0; } catch { success = false; } string errSummary = errorLines.Any() ? string.Join("; ", errorLines) : GetLocalizedString("NoSpecificError"); if (success) { string successMsgKey = (failMessageKey == "FailedBurst" || failMessageKey == "GIFEncodingFailed" || failMessageKey == "PaletteGenFailed") ? "SuccessBurst" : "SuccessZoompan"; string logMsg; if (successMsgKey == "SuccessZoompan") { logMsg = GetLocalizedString("SuccessZoompan", inputDesc, Path.GetFileName(outputPath), stopwatch.Elapsed.TotalSeconds); } else { logMsg = GetLocalizedString(successMsgKey, inputDesc, Path.GetFileName(outputPath), stopwatch.Elapsed.TotalSeconds); } LogMessage(logMsg); } else { string logMsg = GetLocalizedString(failMessageKey, inputDesc, exitCode, errSummary, stopwatch.Elapsed.TotalSeconds); LogMessage(logMsg); if (File.Exists(outputPath) && failMessageKey != "PaletteGenFailed") { try { File.Delete(outputPath); } catch { /* Ignore delete error */ } } } } catch (OperationCanceledException) { if (outerStopwatch == null || !outerStopwatch.IsRunning) stopwatch.Stop(); throw; } catch (Win32Exception winEx) when (winEx.NativeErrorCode == 2) { if (outerStopwatch == null || !outerStopwatch.IsRunning) stopwatch.Stop(); string errorMsg = GetLocalizedString("FFmpegNotFound"); LogMessage(errorMsg); throw new FileNotFoundException(errorMsg, "ffmpeg.exe", winEx); } catch (Exception ex) { if (outerStopwatch == null || !outerStopwatch.IsRunning) stopwatch.Stop(); string errSummary = errorLines.Any() ? string.Join("; ", errorLines) : ex.Message; try { if (proc != null && proc.HasExited) exitCode = proc.ExitCode; } catch { /* Ignore */ } string logMsg = GetLocalizedString(failMessageKey, inputDesc, exitCode, errSummary, stopwatch.Elapsed.TotalSeconds); LogMessage(logMsg); if (File.Exists(outputPath) && failMessageKey != "PaletteGenFailed") { try { File.Delete(outputPath); } catch { /* Ignore delete error */ } } success = false; } } return success; }
        private string BuildZoompanFilter(ZoompanEffectType effectType, ZoompanSettings settings, string outputResolution, int sourceWidth, int sourceHeight) { string widthStr = sourceWidth.ToString(CultureInfo.InvariantCulture); string heightStr = sourceHeight.ToString(CultureInfo.InvariantCulture); string outWidthStr = "iw"; string outHeightStr = "ih"; string[] outDims = outputResolution.Split('x'); if (outDims.Length == 2 && int.TryParse(outDims[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ow) && int.TryParse(outDims[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int oh) && ow > 0 && oh > 0) { outWidthStr = ow.ToString(CultureInfo.InvariantCulture); outHeightStr = oh.ToString(CultureInfo.InvariantCulture); } else { outWidthStr = "iw"; outHeightStr = "ih"; } int fps = settings.Fps; double duration = settings.DurationSeconds; int totalFrames = Math.Max(1, (int)Math.Ceiling(duration * fps)); string dStr = totalFrames.ToString(CultureInfo.InvariantCulture); string sStr = $"{outWidthStr}x{outHeightStr}"; string fpsStr = fps.ToString(CultureInfo.InvariantCulture); string zoomExpr = "'zoom'"; string xExpr = $"'iw/2-(iw/zoom/2)'"; string yExpr = $"'ih/2-(ih/zoom/2)'"; double zoomStart = 1.0; double zoomEnd = settings.TargetZoom; double panPixelsPerSecond = 30; double panPixelsPerFrame = (fps > 0) ? panPixelsPerSecond / fps : 0; string panStepStr = panPixelsPerFrame.ToString("G6", CultureInfo.InvariantCulture); switch (effectType) { case ZoompanEffectType.Custom: if (Math.Abs(zoomEnd - zoomStart) < 0.001) zoomEnd = zoomStart + 0.01 * Math.Sign(zoomEnd - zoomStart); double zoomRate = (totalFrames > 1) ? (zoomEnd - zoomStart) / (totalFrames - 1) : 0; string zoomRateStr = zoomRate.ToString("F10", CultureInfo.InvariantCulture); string zoomEndStr = zoomEnd.ToString(CultureInfo.InvariantCulture); string zoomStartStr = zoomStart.ToString(CultureInfo.InvariantCulture); if (zoomRate > 0) zoomExpr = $"'min(max({zoomStartStr},zoom)+{zoomRateStr}*(on-1),{zoomEndStr})'"; else if (zoomRate < 0) zoomExpr = $"'max(min(zoom,{zoomStartStr})+{zoomRateStr}*(on-1),{zoomEndStr})'"; else zoomExpr = $"'{zoomStartStr}'"; switch (settings.PanDirection) { case PanDirection.Up: yExpr = $"'ih/2-(ih/zoom/2)-on*{panStepStr}'"; break; case PanDirection.Down: yExpr = $"'ih/2-(ih/zoom/2)+on*{panStepStr}'"; break; case PanDirection.Left: xExpr = $"'iw/2-(iw/zoom/2)-on*{panStepStr}'"; break; case PanDirection.Right: xExpr = $"'iw/2-(iw/zoom/2)+on*{panStepStr}'"; break; default: break; } break; case ZoompanEffectType.ZoomInCenterSlow: zoomExpr = "'min(zoom+0.0010, 1.5)'"; break; case ZoompanEffectType.ZoomInCenterFast: zoomExpr = "'min(zoom+0.0020, 1.8)'"; break; case ZoompanEffectType.ZoomOutCenter: zoomExpr = $"'max(1.5 - on/(({dStr})-1) * 0.5, 1.0)'"; break; case ZoompanEffectType.PanRight: zoomExpr = "'1.01'"; xExpr = $"'on*{panStepStr}'"; yExpr = $"(ih-({outHeightStr}))/2"; break; case ZoompanEffectType.PanLeft: zoomExpr = "'1.01'"; xExpr = $"'iw - on*{panStepStr} - ({outWidthStr})'"; yExpr = $"(ih-({outHeightStr}))/2"; break; case ZoompanEffectType.PanUp: zoomExpr = "'1.001'"; xExpr = $"(iw-({outWidthStr}))/2"; yExpr = $"'ih - on*{panStepStr} - ({outHeightStr})'"; break; case ZoompanEffectType.PanDown: zoomExpr = "'1.01'"; xExpr = $"(iw-({outWidthStr}))/2"; yExpr = $"'on*{panStepStr}'"; break; case ZoompanEffectType.ZoomInPanTopRight: zoomExpr = "'min(zoom+0.0015, 1.6)'"; xExpr = $"'iw/2-(iw/zoom/2)+(on*{panStepStr}*0.7)'"; yExpr = $"'ih/2-(ih/zoom/2)-(on*{panStepStr}*0.7)'"; break; case ZoompanEffectType.ZoomInPanBottomLeft: zoomExpr = "'min(zoom+0.0015, 1.6)'"; xExpr = $"'iw/2-(iw/zoom/2)-(on*{panStepStr}*0.7)'"; yExpr = $"'ih/2-(ih/zoom/2)+(on*{panStepStr}*0.7)'"; break; case ZoompanEffectType.IphoneStyle: zoomExpr = "'min(zoom+0.0010, 1.25)'"; xExpr = $"'iw/2-(iw/zoom/2)+(on*{panStepStr}*0.3)'"; yExpr = $"'ih/2-(ih/zoom/2)+(on*{panStepStr}*0.2)'"; break; default: break; } string baseFilter = $"zoompan=z={zoomExpr}:x={xExpr}:y={yExpr}:d={dStr}:s={sStr}:fps={fpsStr}"; if (!baseFilter.Contains("format=pix_fmts")) { baseFilter += ",format=pix_fmts=yuv420p"; } return baseFilter; }
        private async Task<string?> GetImageResolutionAsync(string ffprobePath, string imagePath, CancellationToken token) { string args = $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 \"{imagePath}\""; var psi = new ProcessStartInfo { FileName = ffprobePath, Arguments = args, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 }; try { using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true }) { var outputBuilder = new StringBuilder(); var errorBuilder = new StringBuilder(); proc.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); }; proc.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); }; proc.Start(); proc.BeginOutputReadLine(); proc.BeginErrorReadLine(); await proc.WaitForExitAsync(token); string errorOutput = errorBuilder.ToString().Trim(); if (proc.ExitCode == 0) { string resolution = outputBuilder.ToString().Trim(); if (!string.IsNullOrWhiteSpace(resolution) && resolution.Contains('x')) { string[] parts = resolution.Split('x'); if (parts.Length == 2 && int.TryParse(parts[0], out int width) && width > 0 && int.TryParse(parts[1], out int height) && height > 0) { if (!string.IsNullOrEmpty(errorOutput)) { LogMessage($"ffprobe warning for '{Path.GetFileName(imagePath)}': {errorOutput}"); } return resolution; } } LogMessage($"ffprobe returned invalid resolution format: '{resolution}' for '{Path.GetFileName(imagePath)}'. Stderr: {errorOutput}"); } else { LogMessage($"ffprobe failed (Code:{proc.ExitCode}) getting resolution for '{Path.GetFileName(imagePath)}': {errorOutput}"); } } } catch (OperationCanceledException) { throw; } catch (Exception ex) { LogMessage($"Error executing ffprobe for resolution of '{Path.GetFileName(imagePath)}': {ex.Message}"); } return null; }

        // --- Backup Logic (Move-Based with Strategies) ---
        private async Task<bool> PerformPreProcessingBackupAsync(List<string> originalInputPaths, string backupPrefix, bool useCustomBackupPath, CheckBox? chkUseCustomBackupPath, TextBox? txtCustomBackupPath, bool useTimestamp, string timestamp, CancellationToken token, Dictionary<string, string> outSourceToBackupPathMap)
        {
            LogMessage(GetLocalizedString("StartingBackup")); outSourceToBackupPathMap.Clear();
            try
            {
                var uniqueSources = GetUniqueValidSources(originalInputPaths); if (!uniqueSources.Any()) { LogMessage(GetLocalizedString("NoSourcesForBackup")); return true; }
                string backupRootCounterStr = "001"; string backupRootNameBase = CleanPathSegment(backupPrefix); if (useTimestamp && !string.IsNullOrEmpty(timestamp)) { backupRootNameBase = $"{backupRootNameBase}_{timestamp}"; }
                string finalBackupFolderName = $"{backupRootNameBase}_{backupRootCounterStr}";
                bool useSingleFolderRenameLogic = false; bool useMultiFolderSameParentLogic = false; bool useFallbackLogic = false; string? singleFolderToRename = null; List<string>? multiFoldersToMove = null; string? commonParentForMulti = null; string targetBackupPath = ""; string? customBackupBasePathValidated = null;

                if (useCustomBackupPath)
                {
                    customBackupBasePathValidated = await GetValidatedCustomPath(chkUseCustomBackupPath, txtCustomBackupPath, true, "Backup"); if (string.IsNullOrEmpty(customBackupBasePathValidated)) { LogMessage(GetLocalizedString("CustomPathReverting")); return false; }
                    targetBackupPath = Path.Combine(customBackupBasePathValidated, finalBackupFolderName); useFallbackLogic = true; LogMessage(GetLocalizedString("BackupStrategyFallback") + $" Custom Path: {targetBackupPath}");
                }
                else
                {
                    bool allAreFolders = uniqueSources.Values.All(type => type == SourceType.Directory); if (uniqueSources.Count == 1 && allAreFolders) { useSingleFolderRenameLogic = true; singleFolderToRename = uniqueSources.Keys.First(); string? parentDir = Path.GetDirectoryName(singleFolderToRename); if (parentDir == null) { LogMessage(GetLocalizedString("BackupCannotGetParent", singleFolderToRename)); return false; } targetBackupPath = Path.Combine(parentDir, finalBackupFolderName); LogMessage(GetLocalizedString("BackupStrategySingleRename", singleFolderToRename, targetBackupPath)); }
                    else if (uniqueSources.Count > 1 && allAreFolders) { commonParentForMulti = FindCommonParentDirectory(uniqueSources.Keys); if (commonParentForMulti != null) { useMultiFolderSameParentLogic = true; multiFoldersToMove = uniqueSources.Keys.ToList(); targetBackupPath = Path.Combine(commonParentForMulti, finalBackupFolderName); LogMessage(GetLocalizedString("BackupStrategyMultiFolderContainer", commonParentForMulti, targetBackupPath)); } else { useFallbackLogic = true; LogMessage(GetLocalizedString("BackupStrategyFallback") + " Reason: Multiple folders, different parents."); } }
                    else { useFallbackLogic = true; LogMessage(GetLocalizedString("BackupStrategyFallback") + " Reason: Mixed files/folders or only files."); }
                }

                if (useSingleFolderRenameLogic && singleFolderToRename != null)
                {
                    if (Directory.Exists(targetBackupPath) || File.Exists(targetBackupPath)) { LogMessage(GetLocalizedString("BackupRootExists", targetBackupPath) + " Cannot rename folder. Aborting backup."); return false; }
                    string? parentOfTarget = Path.GetDirectoryName(targetBackupPath); if (parentOfTarget != null && !Directory.Exists(parentOfTarget)) { try { Directory.CreateDirectory(parentOfTarget); } catch (Exception ex) { LogMessage(GetLocalizedString("ErrorCreatingBackupParent", parentOfTarget, ex.Message)); return false; } }
                    LogMessage(GetLocalizedString("BackupAttemptRename", singleFolderToRename, targetBackupPath));
                    try { await Task.Run(() => Directory.Move(singleFolderToRename, targetBackupPath), token); outSourceToBackupPathMap[singleFolderToRename] = targetBackupPath; LogMessage(GetLocalizedString("BackupRenameSuccess")); return true; } catch (Exception ex) { LogMessage(GetLocalizedString("BackupRenameError", singleFolderToRename, targetBackupPath, ex.Message)); return false; }
                }
                else if (useMultiFolderSameParentLogic && multiFoldersToMove != null)
                {
                    if (Directory.Exists(targetBackupPath) || File.Exists(targetBackupPath)) { LogMessage(GetLocalizedString("BackupRootExists", targetBackupPath) + " Cannot create container folder. Aborting backup."); return false; }
                    string? parentOfTarget = Path.GetDirectoryName(targetBackupPath); if (parentOfTarget != null && !Directory.Exists(parentOfTarget)) { try { Directory.CreateDirectory(parentOfTarget); } catch (Exception ex) { LogMessage(GetLocalizedString("ErrorCreatingBackupParent", parentOfTarget, ex.Message)); return false; } }
                    try { LogMessage(GetLocalizedString("BackupCreateContainer", targetBackupPath)); Directory.CreateDirectory(targetBackupPath); foreach (string folderToMove in multiFoldersToMove) { token.ThrowIfCancellationRequested(); string sourceFolderName = Path.GetFileName(folderToMove); string destinationInContainer = Path.Combine(targetBackupPath, sourceFolderName); if (Directory.Exists(destinationInContainer) || File.Exists(destinationInContainer)) { LogMessage(GetLocalizedString("BackupItemExistsInContainer", sourceFolderName, targetBackupPath)); continue; } LogMessage(GetLocalizedString("BackupMoveIntoContainer", folderToMove, targetBackupPath)); await Task.Run(() => Directory.Move(folderToMove, destinationInContainer), token); outSourceToBackupPathMap[folderToMove] = destinationInContainer; } LogMessage(GetLocalizedString("BackupMultiMoveComplete")); return true; }
                    catch (Exception ex) { LogMessage(GetLocalizedString("BackupMultiMoveError", targetBackupPath, ex.Message)); return false; }
                }
                else if (useFallbackLogic)
                {
                    bool fallbackSuccess = true; bool nestInFallbackRoot = uniqueSources.Count > 1 || useCustomBackupPath; // Nest if multiple sources OR if using custom path
                    Dictionary<string, string> fallbackRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in uniqueSources) { token.ThrowIfCancellationRequested(); string sourcePath = kvp.Key; string backupRootPath; if (useCustomBackupPath && customBackupBasePathValidated != null) { backupRootPath = targetBackupPath; if (!fallbackRoots.ContainsKey("custom")) fallbackRoots.Add("custom", backupRootPath); } else { string? parentDir = Path.GetDirectoryName(sourcePath); if (parentDir == null) { LogMessage(GetLocalizedString("BackupErrorParentRoot", sourcePath)); fallbackSuccess = false; continue; } if (!fallbackRoots.TryGetValue(parentDir, out backupRootPath!)) { backupRootPath = Path.Combine(parentDir, finalBackupFolderName); fallbackRoots.Add(parentDir, backupRootPath); if (Directory.Exists(backupRootPath) || File.Exists(backupRootPath)) { LogMessage(GetLocalizedString("BackupRootExists", backupRootPath) + " Using existing folder for fallback backup."); } else { try { Directory.CreateDirectory(backupRootPath); LogMessage(GetLocalizedString("CreatingBackupDirectory", backupRootPath)); } catch (Exception ex) { LogMessage(GetLocalizedString("BackupErrorCreateRoot", backupRootPath, ex.Message)); fallbackSuccess = false; continue; } } } } }
                    if (!fallbackSuccess) return false;
                    foreach (var kvp in uniqueSources) { token.ThrowIfCancellationRequested(); string sourcePath = kvp.Key; SourceType sourceType = kvp.Value; string sourceName = Path.GetFileName(sourcePath); string finalMoveTargetPath; string currentBackupRoot; if (useCustomBackupPath) { currentBackupRoot = fallbackRoots["custom"]; } else { string? parentDir = Path.GetDirectoryName(sourcePath); if (parentDir == null || !fallbackRoots.TryGetValue(parentDir, out currentBackupRoot!)) { LogMessage(GetLocalizedString("BackupErrorRootMapping", sourcePath)); fallbackSuccess = false; continue; } } if (nestInFallbackRoot) { finalMoveTargetPath = Path.Combine(currentBackupRoot, sourceName); } else { finalMoveTargetPath = Path.Combine(currentBackupRoot, sourceName); } if (Directory.Exists(finalMoveTargetPath) || File.Exists(finalMoveTargetPath)) { LogMessage(GetLocalizedString("BackupItemExistsInContainer", sourceName, currentBackupRoot)); continue; } LogMessage(GetLocalizedString("BackupMoveItemToCustom", sourcePath, finalMoveTargetPath)); try { if (sourceType == SourceType.Directory) { if (!Directory.Exists(sourcePath)) { LogMessage(GetLocalizedString("SourceNotFoundForMove", "directory", sourcePath)); fallbackSuccess = false; continue; } await Task.Run(() => Directory.Move(sourcePath, finalMoveTargetPath), token); } else { if (!File.Exists(sourcePath)) { LogMessage(GetLocalizedString("SourceNotFoundForMove", "file", sourcePath)); fallbackSuccess = false; continue; } string? targetDir = Path.GetDirectoryName(finalMoveTargetPath); if (targetDir != null && !Directory.Exists(targetDir)) { Directory.CreateDirectory(targetDir); } await Task.Run(() => File.Move(sourcePath, finalMoveTargetPath), token); } outSourceToBackupPathMap[sourcePath] = finalMoveTargetPath; } catch (IOException ioEx) when (IsAccessException(ioEx)) { LogMessage(GetLocalizedString("AccessDeniedMoveBackup", sourcePath, ioEx.Message)); fallbackSuccess = false; continue; } catch (OperationCanceledException) { throw; } catch (Exception ex) { LogMessage(GetLocalizedString("ErrorMoveBackupGeneral", sourcePath, finalMoveTargetPath, ex.Message)); fallbackSuccess = false; continue; } }
                    return fallbackSuccess;
                }
                else { LogMessage(GetLocalizedString("BackupErrorNoStrategy")); return false; }
            }
            catch (OperationCanceledException) { LogMessage(GetLocalizedString("BackupCancelled")); return false; }
            catch (Exception ex) { LogMessage(GetLocalizedString("BackupFatalError", ex.Message)); return false; }
        }

        // --- Helper Functions ---
        private enum SourceType { Directory, File }
        private Dictionary<string, SourceType> GetUniqueValidSources(List<string> paths) { var uniqueSources = new Dictionary<string, SourceType>(StringComparer.OrdinalIgnoreCase); foreach (string path in paths) { if (string.IsNullOrWhiteSpace(path)) continue; try { string fullPath = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); if (Directory.Exists(fullPath)) { if (!uniqueSources.ContainsKey(fullPath)) uniqueSources.Add(fullPath, SourceType.Directory); } else if (File.Exists(fullPath)) { if (!uniqueSources.ContainsKey(fullPath)) uniqueSources.Add(fullPath, SourceType.File); } else { LogMessage(GetLocalizedString("IgnoringInvalidPath", path)); } } catch (Exception ex) { LogMessage(GetLocalizedString("ErrorAddingPath", path ?? "null", ex.Message)); } } return uniqueSources; }
        private string? FindCommonParentDirectory(IEnumerable<string> paths) { string? commonParent = null; bool first = true; foreach (var path in paths) { string? parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); if (parent == null) return null; if (first) { commonParent = parent; first = false; } else if (!parent.Equals(commonParent, StringComparison.OrdinalIgnoreCase)) { return null; } } return commonParent; }
        private async Task<bool> CopyDirectoryRecursivelyAsync(string sourceDir, string destDir, CancellationToken token) { LogMessage(GetLocalizedString("CopyDirPlaceholderLog", sourceDir, destDir)); try { Directory.CreateDirectory(destDir); foreach (string filePath in Directory.GetFiles(sourceDir)) { token.ThrowIfCancellationRequested(); string destFilePath = Path.Combine(destDir, Path.GetFileName(filePath)); await Task.Run(() => File.Copy(filePath, destFilePath, true), token); } foreach (string dirPath in Directory.GetDirectories(sourceDir)) { token.ThrowIfCancellationRequested(); string destSubDir = Path.Combine(destDir, Path.GetFileName(dirPath)); if (!await CopyDirectoryRecursivelyAsync(dirPath, destSubDir, token)) { return false; } } return true; } catch (OperationCanceledException) { LogMessage(GetLocalizedString("BackupCancelled")); throw; } catch (Exception ex) { LogMessage(GetLocalizedString("CopyDirError", sourceDir, destDir, ex.Message)); return false; } }
        //private List<string> DetermineCurrentFilePaths(List<string> allUniqueOriginalFiles, List<string> originalInputPaths, bool enableBackup, Dictionary<string, string> sourceToBackupPathMap)
        //{
        //    List<string> filesToProcessNow = new List<string>(); if (enableBackup) { LogMessage(GetLocalizedString("BackupProcessingFromBackup")); foreach (string originalFile in allUniqueOriginalFiles) { string? containingOriginalSource = FindBestInputPathForFile(originalFile, originalInputPaths); string? backupLocation = null; bool foundInMap = false; if (sourceToBackupPathMap.TryGetValue(containingOriginalSource, out backupLocation)) { foundInMap = true; string relativePath = "."; try { if (!containingOriginalSource.Equals(originalFile, StringComparison.OrdinalIgnoreCase)) { relativePath = Path.GetRelativePath(containingOriginalSource, originalFile); } else { relativePath = Path.GetFileName(originalFile); } } catch (ArgumentException) { relativePath = Path.GetFileName(originalFile); } string pathInBackup; if (File.Exists(backupLocation) && backupLocation.EndsWith(Path.GetFileName(originalFile), StringComparison.OrdinalIgnoreCase)) { pathInBackup = backupLocation; } else { pathInBackup = Path.Combine(backupLocation, relativePath); } if (File.Exists(pathInBackup)) { filesToProcessNow.Add(pathInBackup); } else { LogMessage(GetLocalizedString("BackupFileInBackupNotFound", originalFile, pathInBackup)); } } else { LogMessage(GetLocalizedString("BackupMapEntryNotFound", containingOriginalSource)); if (sourceToBackupPathMap.TryGetValue(originalFile, out backupLocation)) { if (File.Exists(backupLocation)) filesToProcessNow.Add(backupLocation); else LogMessage(GetLocalizedString("BackupFileInBackupNotFound", originalFile, backupLocation)); } else { LogMessage(GetLocalizedString("BackupMapEntrySourceNotFound", originalFile)); } } } } else { filesToProcessNow = allUniqueOriginalFiles; LogMessage(GetLocalizedString("BackupProcessingOriginals")); }
        //    return filesToProcessNow;
        //}
        

        
        private bool AddInputPath(string? path) { if (string.IsNullOrWhiteSpace(path)) { LogMessage(GetLocalizedString("IgnoringInvalidPath", path ?? "null")); return false; } try { string norm = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); if (inputPaths.Any(p => { try { return Path.GetFullPath(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Equals(norm, StringComparison.OrdinalIgnoreCase); } catch { return false; } })) { LogMessage(GetLocalizedString("IgnoringDuplicateItem", norm)); return false; } if (Directory.Exists(norm)) { inputPaths.Add(norm); LogMessage(GetLocalizedString("AddedFolder", norm)); return true; } else if (File.Exists(norm)) { inputPaths.Add(norm); string ext = Path.GetExtension(norm).ToLowerInvariant(); LogMessage(supportedExtensions.Contains(ext) ? GetLocalizedString("AddedImageFile", norm) : GetLocalizedString("AddedNonImageFile", norm)); return true; } else { LogMessage(GetLocalizedString("IgnoringInvalidPath", norm)); return false; } } catch (Exception ex) { LogMessage(GetLocalizedString("ErrorAddingPath", path ?? "null", ex.Message)); return false; } }
        private void UpdateSelectionFromPath(string path) { bool added = false; try { inputPaths.Clear(); added = AddInputPath(path); if (added) LogMessage(GetLocalizedString("TextBoxPathSelected", path)); } catch (Exception ex) { LogMessage($"ERROR in UpdateSelectionFromPath: {ex.Message}"); } finally { UpdateUIState(); } }
        private void UpdateSelectionFromFiles(IEnumerable<string> filePaths) { inputPaths.Clear(); LogMessage(GetLocalizedString("ImageSelectionStart")); int added = 0; foreach (var path in filePaths) { if (AddInputPath(path)) added++; } LogMessage(GetLocalizedString("ImageSelectionComplete", added)); UpdateUIState(); }
        private void SetInitialDialogPath(FileDialog d, string? p) { if (string.IsNullOrWhiteSpace(p) || IsPlaceholderText(p) || IsSummaryText(p)) return; try { string f = Path.GetFullPath(p.Trim()); if (Directory.Exists(f)) d.InitialDirectory = f; else if (File.Exists(f)) d.InitialDirectory = Path.GetDirectoryName(f); } catch { /* Ignore IO errors */ } }
        private void SetInitialDialogPath(VistaFolderBrowserDialog d, string? p) { if (string.IsNullOrWhiteSpace(p) || IsPlaceholderText(p) || IsSummaryText(p)) return; try { string f = Path.GetFullPath(p.Trim()); if (Directory.Exists(f)) d.SelectedPath = f; else if (File.Exists(f)) d.SelectedPath = Path.GetDirectoryName(f); } catch { /* Ignore IO errors */ } }
        private void SetInitialDialogPath(VistaOpenFileDialog d, string? p) { if (string.IsNullOrWhiteSpace(p) || IsPlaceholderText(p) || IsSummaryText(p)) return; try { string f = Path.GetFullPath(p.Trim()); if (Directory.Exists(f)) d.InitialDirectory = f; else if (File.Exists(f)) d.InitialDirectory = Path.GetDirectoryName(f); } catch { /* Ignore IO errors */ } }
        private string BuildFilterString() { string i = string.Join(";", supportedExtensions.Select(e => $"*{e}")); return $"{GetLocalizedString("SupportedImageFiles")}({i})|{i}|{GetLocalizedString("AllFiles")}(*.*)|*.*"; }
        private void UpdateProgressBar(int v) => Dispatcher.InvokeAsync(() => { if (progressBar != null) { double max = progressBar.Maximum; double min = progressBar.Minimum; progressBar.Value = Math.Max(min, Math.Min((double)v, max)); progressBar.IsIndeterminate = false; } });
        private void UpdateStatusLabel(string t) => Dispatcher.InvokeAsync(() => { if (lblProcessStatus != null) lblProcessStatus.Text = t; });
        private async Task UpdateCountsAndUIAsync(CancellationToken t) { if (t.IsCancellationRequested) return; int curP = processedImages; int curF = failedImages; int curT = totalImages; int currentActiveTasks = _activeTasks; TimeSpan elapsed = TimeSpan.Zero; string elapsedFormatted = "00:00:00.0"; if (isProcessing && processStopwatch.IsRunning) { elapsed = processStopwatch.Elapsed; elapsedFormatted = FormatTimeSpan(elapsed); } bool isBurstModeActive = false; await Dispatcher.InvokeAsync(() => isBurstModeActive = chkBurstMode?.IsChecked ?? false); int displayTotal = (isBurstModeActive && totalImages == 1) ? 1 : curT; int comp = curP + curF; await Dispatcher.InvokeAsync(() => { if (t.IsCancellationRequested || progressBar == null || lblImageCount == null || lblProgressHint == null || lblElapsedTime == null || lblConcurrentTasks == null) return; progressBar.Maximum = Math.Max(1, displayTotal); progressBar.Value = Math.Max(progressBar.Minimum, Math.Min((double)comp, progressBar.Maximum)); progressBar.IsIndeterminate = false; lblImageCount.Text = GetLocalizedString("ProcessedCounts", curP, curF, displayTotal); lblProgressHint.Text = GetLocalizedString("ProgressCounts", comp, displayTotal); if (isProcessing) { lblElapsedTime.Text = $"{GetLocalizedString("StatusBar_Elapsed", "Elapsed:")} {elapsedFormatted}"; } if (lblConcurrentTasks != null) { lblConcurrentTasks.Text = $"{GetLocalizedString("StatusBar_Concurrent", "Concurrent:")} {currentActiveTasks}"; } }, DispatcherPriority.Background, t); }
        private void SetControlEnabled(UIElement? e, bool enabled) { if (e != null && e.IsEnabled != enabled) e.IsEnabled = enabled; }
        private void SetControlChecked(CheckBox? c, bool? isChecked) { if (c != null && c.IsChecked != isChecked) c.IsChecked = isChecked; }
        private bool IsSummaryText(string t) { string selPrefix = GetLocalizedString("Selected", ""); return t.StartsWith(selPrefix) || t == GetLocalizedString("InvalidItemsSelected"); }
        private bool IsPlaceholderText(string t) => t == GetLocalizedString("UnselectedState");
        private string CleanPrefix(string p) => string.IsNullOrEmpty(p) ? "" : invalidFileNameCharsRegex.Replace(p, "").Trim(' ', '.');
        private string CleanPathSegment(string p) => string.IsNullOrEmpty(p) ? "" : invalidPathCharsRegex.Replace(p, "").Trim(' ', '.');
        private HashSet<string> GetUniqueTopLevelFolders(List<string> paths) { var f = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (string path in paths) { if (string.IsNullOrWhiteSpace(path)) continue; try { string n = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); if (Directory.Exists(n)) f.Add(n); } catch (Exception ex) { LogMessage(GetLocalizedString("ErrorCheckingFolderPath", path, ex.Message)); } } return f; }

        //private string FindBestInputPathForFile(string fp, List<string> originalInputList) { string fullFp; try { fullFp = Path.GetFullPath(fp); } catch { return Path.GetDirectoryName(fp) ?? fp; } var orderedInputs = originalInputList.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => { try { return Path.GetFullPath(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); } catch { return null; } }).Where(p => p != null).OrderByDescending(p => p!.Length).ToList(); foreach (string? fullInput in orderedInputs) { if (fullInput == null || (cancellationTokenSource?.IsCancellationRequested ?? false)) break; try { if (File.Exists(fullInput) && fullFp.Equals(fullInput, StringComparison.OrdinalIgnoreCase)) { return originalInputList.First(orig => { try { return Path.GetFullPath(orig.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Equals(fullInput, StringComparison.OrdinalIgnoreCase); } catch { return false; } }); } if (Directory.Exists(fullInput)) { string dirWithSep = fullInput.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar; if (fullFp.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase)) { return originalInputList.First(orig => { try { return Path.GetFullPath(orig.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Equals(fullInput, StringComparison.OrdinalIgnoreCase); } catch { return false; } }); } } } catch (Exception ex) { LogMessage(GetLocalizedString("ErrorMatchingInputPath", fullInput, fp, ex.Message)); } } string? parent = Path.GetDirectoryName(fullFp); if (!string.IsNullOrEmpty(parent)) { try { if (Directory.Exists(parent)) return parent; } catch { /* Ignore IO error */ } } LogMessage(GetLocalizedString("ErrorNoInputContext", fp)); return fp; }
        // Inside MainWindow.xaml.cs
        private string FindBestInputPathForFile(string filePathToFindContextFor, List<string> originalInputList)
        {
            string fullFilePath;
            try
            {
                fullFilePath = Path.GetFullPath(filePathToFindContextFor);
            }
            catch (Exception ex)
            {
                LogMessage($"Error normalizing target file path '{filePathToFindContextFor}': {ex.Message}");
                return Path.GetDirectoryName(filePathToFindContextFor) ?? filePathToFindContextFor;
            }

            string? bestMatchOriginalInput = null;
            int longestMatchLength = -1;

            foreach (string originalInputRaw in originalInputList) // Iterate through the raw original list
            {
                if (string.IsNullOrWhiteSpace(originalInputRaw)) continue;

                try
                {
                    // Normalize the original input path for comparison purposes
                    string fullInputPath = Path.GetFullPath(originalInputRaw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                    // --- Check 1: Exact match ---
                    // Compare normalized paths directly
                    if (fullFilePath.Equals(fullInputPath, StringComparison.OrdinalIgnoreCase))
                    {
                        bestMatchOriginalInput = originalInputRaw; // Found exact match
                        longestMatchLength = fullInputPath.Length;
                        break; // Exact match is best
                    }

                    // --- Check 2: Directory containment ---
                    // IMPORTANT: Do NOT check Directory.Exists(fullInputPath) here!
                    // Assume it *was* a directory if it's not an exact file match found above.
                    // We only need to check if the file path logically falls under this original directory path.

                    string dirWithSep = fullInputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                    // Check if the file path starts with the *potential* original directory path + separator
                    if (fullFilePath.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
                    {
                        // This directory *would have* contained the file. Is it more specific?
                        if (fullInputPath.Length > longestMatchLength)
                        {
                            bestMatchOriginalInput = originalInputRaw; // Store the original input string
                            longestMatchLength = fullInputPath.Length;
                        }
                    }
                }
                catch (ArgumentException argEx) { LogMessage($"DEBUG: Path comparison error in FindBestInputPathForFile: {argEx.Message}"); }
                catch (Exception ex) { LogMessage(GetLocalizedString("ErrorMatchingInputPath", originalInputRaw, filePathToFindContextFor, ex.Message)); }
            }

            if (bestMatchOriginalInput != null)
            {
                return bestMatchOriginalInput;
            }

            // --- Fallback logic remains the same ---
            string? parentDir = Path.GetDirectoryName(fullFilePath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                try
                {
                    string fullParentDir = Path.GetFullPath(parentDir);
                    // Check if this parent *currently exists* and matches an original input
                    // (We might still need exists check for the fallback parent itself)
                    if (Directory.Exists(fullParentDir))
                    {
                        var matchingOriginalInput = originalInputList.FirstOrDefault(orig => {
                            try { return Path.GetFullPath(orig.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Equals(fullParentDir, StringComparison.OrdinalIgnoreCase); }
                            catch { return false; }
                        });
                        if (matchingOriginalInput != null) return matchingOriginalInput;

                        // If parent exists but wasn't an original input, return the parent path
                        LogMessage($"Warning: Using parent directory '{fullParentDir}' as context for '{filePathToFindContextFor}' (no direct input match).");
                        return fullParentDir;
                    }
                }
                catch { /* Ignore IO errors */ }
            }

            LogMessage(GetLocalizedString("ErrorNoInputContext", filePathToFindContextFor));
            return filePathToFindContextFor; // Final fallback
        }

        private string? CheckIfPathWasRenamed(string originalPath, Dictionary<string, string> renameMap) { if (renameMap.TryGetValue(originalPath, out string? exactMatch)) { return exactMatch; } string? currentParent = Path.GetDirectoryName(originalPath); string relativePart = Path.GetFileName(originalPath); var parts = new Stack<string>(); parts.Push(relativePart); while (!string.IsNullOrEmpty(currentParent)) { if (cancellationTokenSource?.IsCancellationRequested ?? false) return null; if (renameMap.TryGetValue(currentParent, out string? renamedParentPath)) { try { string finalPath = renamedParentPath; while (parts.Count > 0) { if (cancellationTokenSource?.IsCancellationRequested ?? false) return null; finalPath = Path.Combine(finalPath, parts.Pop()); } return finalPath; } catch (ArgumentException ex) { LogMessage(GetLocalizedString("RelativePathError", originalPath, currentParent, renamedParentPath ?? "null", ex.Message)); return null; } } parts.Push(Path.GetFileName(currentParent)); currentParent = Path.GetDirectoryName(currentParent); } return null; }
        private string? FindToolPath(string executableName) { string baseDir = AppDomain.CurrentDomain.BaseDirectory; string toolSubfolder; string specificBinPath = ""; switch (executableName.ToLowerInvariant()) { case "exiftool.exe": toolSubfolder = "exiftool"; break; case "magick.exe": case "convert.exe": toolSubfolder = "ImageMagick"; break; case "ffmpeg.exe": case "ffprobe.exe": toolSubfolder = "ffmpeg"; specificBinPath = Path.Combine(baseDir, toolSubfolder, "bin", executableName); break; default: toolSubfolder = ""; break; } if (!string.IsNullOrEmpty(specificBinPath) && File.Exists(specificBinPath)) { return specificBinPath; } if (!string.IsNullOrEmpty(toolSubfolder)) { string subfolderPath = Path.Combine(baseDir, toolSubfolder, executableName); if (File.Exists(subfolderPath)) { return subfolderPath; } } string basePath = Path.Combine(baseDir, executableName); if (File.Exists(basePath)) { return basePath; } string? pathVar = Environment.GetEnvironmentVariable("PATH"); if (pathVar != null) { foreach (string pathDir in pathVar.Split(Path.PathSeparator)) { try { string pathFilePath = Path.Combine(pathDir.Trim(), executableName); if (File.Exists(pathFilePath)) { return pathFilePath; } } catch { /* Ignore invalid paths in PATH */ } } } LogMessage($"Warning: Tool '{executableName}' not found in expected subdirectories ('{toolSubfolder}', '{Path.Combine(toolSubfolder, "bin")}') or system PATH."); return null; }
        private OutputFormat GetSelectedOutputFormat() { if (rbFormatMp4?.IsChecked ?? false) return OutputFormat.MP4; if (rbFormatGif?.IsChecked ?? false) return OutputFormat.GIF; return OutputFormat.MOV; }
        private bool IsAccessException(IOException ex) { const int E_ACCESSDENIED = unchecked((int)0x80070005); const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020); const int ERROR_LOCK_VIOLATION = unchecked((int)0x80070021); return ex.HResult == E_ACCESSDENIED || ex.HResult == ERROR_SHARING_VIOLATION || ex.HResult == ERROR_LOCK_VIOLATION || ex.Message.Contains("being used", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Access to the path", StringComparison.OrdinalIgnoreCase); }

        // --- Logging and Context Menu ---
        private void LogMessage(string message)
        {
            Action action = () => {
                if (TbLog != null)
                {
                    string logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"; const int MaxLogLines = 1500; // Increased limit slightly
                    if (TbLog.LineCount > MaxLogLines + 100) { try { int removeCount = TbLog.GetLineIndexFromCharacterIndex(TbLog.Text.Length) - MaxLogLines; if (removeCount > 0) { int endOfLinesToRemove = TbLog.GetCharacterIndexFromLineIndex(removeCount); TbLog.Select(0, endOfLinesToRemove); string replacement = "-- Log Trimmed --" + Environment.NewLine; TbLog.SelectedText = replacement; } else { TbLog.Clear(); TbLog.AppendText("-- Log Cleared --\n"); } } catch { try { TbLog.Clear(); TbLog.AppendText("-- Log Cleared (Catch) --\n"); } catch { Debug.WriteLine("FATAL: Could not clear or trim log TextBox."); } } }
                    TbLog.AppendText(logEntry); TbLog.ScrollToEnd();
                }
            }; var d = TbLog?.Dispatcher ?? Application.Current?.Dispatcher; if (d != null && !d.CheckAccess()) { d.BeginInvoke(action, DispatcherPriority.Background); } else if (d != null) { try { action(); } catch (Exception ex) { Debug.WriteLine($"Direct Log Err:{ex.Message}"); } } else { Debug.WriteLine($"Log fail (no dispatcher):{message}"); }
        }
        private void ClearLog_Click(object sender, RoutedEventArgs e) { if (TbLog != null) { TbLog.Clear(); LogMessage(GetLocalizedString("ClearLogMessage")); } }
        private void SaveLog_Click(object sender, RoutedEventArgs e) => SaveLog();
        private void SaveLog() { if (TbLog == null) { MessageBox.Show(this, GetLocalizedString("SaveLogDialogError"), GetLocalizedString("SaveErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error); return; } var sd = new SaveFileDialog { Filter = $"{GetLocalizedString("TextFile")}(*.txt)|*.txt|{GetLocalizedString("AllFiles")}(*.*)|*.*", Title = GetLocalizedString("SaveLog"), DefaultExt = "txt", FileName = $"ExifDog_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt", AddExtension = true, CheckPathExists = true, ValidateNames = true }; if (sd.ShowDialog(this) == true) { try { File.WriteAllText(sd.FileName, TbLog.Text, Encoding.UTF8); LogMessage(GetLocalizedString("LogSaved", sd.FileName)); } catch (Exception ex) { string msg = GetLocalizedString("ErrorSavingLog", ex.Message); LogMessage(msg); MessageBox.Show(this, msg, GetLocalizedString("SaveErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error); } } }
        private void TbLog_TextChanged(object sender, TextChangedEventArgs e) { /* Not needed */ }
        private void progressBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { /* Not needed */ }
        private void cmbOutputFps_SelectionChanged(object sender, SelectionChangedEventArgs e) { /* Not needed */ }

        private void Input_Changed(object sender, TextChangedEventArgs e)
        {

        }
    } // --- End MainWindow Class ---

    // --- Helper classes outside MainWindow ---
    public class LanguageItem { public string Code { get; set; } public string DisplayName { get; set; } public LanguageItem(string c, string d) { Code = c; DisplayName = d; } public override string ToString() => DisplayName; }
    public static class ExtensionMethods
    {
        public static TResult? Let<T, TResult>(this T? self, Func<T, TResult> func) where T : class? => self == null ? default : func(self);
        public static TResult? LetValue<T, TResult>(this T? self, Func<T, TResult> func) where T : struct => self == null ? default : func(self.Value);
        public static Task WaitForExitAsync(this Process process, CancellationToken cancellationToken = default) { if (process.HasExited) return Task.CompletedTask; var tcs = new TaskCompletionSource<object?>(); process.EnableRaisingEvents = true; process.Exited += (sender, args) => tcs.TrySetResult(null); if (cancellationToken != default) { cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { /* Ignore if already exited */ } catch (Win32Exception) { /* Ignore if access denied */ } tcs.TrySetCanceled(); }); } return tcs.Task; }
    }


    //public enum OutputFormat { MOV, MP4, GIF }
    //public enum PanDirection { None, Up, Down, Left, Right }
    //public enum ZoompanEffectType { Custom, ZoomInCenterSlow, ZoomInCenterFast, ZoomOutCenter, PanRight, PanLeft, PanUp, PanDown, ZoomInPanTopRight, ZoomInPanBottomLeft, IphoneStyle, RandomPreset }
    //public class ZoompanSettings { public ZoompanEffectType EffectType { get; set; } = ZoompanEffectType.ZoomInCenterSlow; public PanDirection PanDirection { get; set; } = PanDirection.None; public double TargetZoom { get; set; } = 1.2; public double DurationSeconds { get; set; } = 5.0; public int Fps { get; set; } = 30; public int BurstFramerate { get; set; } = 10; }

} // --- End Namespace Hui_WPF ---


// --- Other Namespaces (Ensure these exist in your project structure) ---
namespace Hui_WPF.Properties
{
    // This part depends on your project's Settings.settings file.
    // Make sure it includes UserBackupOutputPath string setting.
    internal sealed partial class Settings : System.Configuration.ApplicationSettingsBase
    {
        private static Settings defaultInstance = ((Settings)(global::System.Configuration.ApplicationSettingsBase.Synchronized(new Settings())));
        public static Settings Default { get { return defaultInstance; } }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("zh")]
        public string UserLanguage { get { try { return ((string)(this["UserLanguage"])); } catch { return "zh"; } } set { try { this["UserLanguage"] = value; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to set UserLanguage setting: {ex.Message}"); } } }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("yyyyMMdd_HHmmss_")] // Match default format
        public string UserTimestampFormat { get { try { return ((string)(this["UserTimestampFormat"])); } catch { return "yyyyMMdd_HHmmss_"; } } set { try { this["UserTimestampFormat"] = value; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to set UserTimestampFormat setting: {ex.Message}"); } } }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("True")]
        public bool UserEnableTimestamp { get { try { return ((bool)(this["UserEnableTimestamp"])); } catch { return true; } } set { try { this["UserEnableTimestamp"] = value; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to set UserEnableTimestamp setting: {ex.Message}"); } } }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("D3")]
        public string UserCounterFormat { get { try { return ((string)(this["UserCounterFormat"])); } catch { return "D3"; } } set { try { this["UserCounterFormat"] = value; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to set UserCounterFormat setting: {ex.Message}"); } } }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("1")]
        public int UserCounterStartValue { get { try { return ((int)(this["UserCounterStartValue"])); } catch { return 1; } } set { try { this["UserCounterStartValue"] = value; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to set UserCounterStartValue setting: {ex.Message}"); } } }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("True")]
        public bool UserEnableCounter { get { try { return ((bool)(this["UserEnableCounter"])); } catch { return true; } } set { try { this["UserEnableCounter"] = value; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to set UserEnableCounter setting: {ex.Message}"); } } }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("")]
        public string UserImageOutputPath { get { try { return ((string)(this["UserImageOutputPath"])); } catch { return ""; } } set { try { this["UserImageOutputPath"] = value; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to set UserImageOutputPath setting: {ex.Message}"); } } }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("")]
        public string UserVideoOutputPath { get { try { return ((string)(this["UserVideoOutputPath"])); } catch { return ""; } } set { try { this["UserVideoOutputPath"] = value; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to set UserVideoOutputPath setting: {ex.Message}"); } } }

        // Ensure this setting exists in your Settings.settings file
        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("")]
        public string UserBackupOutputPath { get { try { return ((string)(this["UserBackupOutputPath"])); } catch { return ""; } } set { try { this["UserBackupOutputPath"] = value; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to set UserBackupOutputPath setting: {ex.Message}"); } } }
    }
}

namespace Hui_WPF.utils
{
    // Basic ZipsHelper structure assumed from previous code
    internal static class ZipsHelper
    {
        private static Dictionary<string, Dictionary<string, string>> _translations = new Dictionary<string, Dictionary<string, string>>();
        private static string _currentLanguage = "zh";
        public static void Initialize(Dictionary<string, Dictionary<string, string>> translations, string currentLanguage) { _translations = translations; _currentLanguage = currentLanguage; }
        public static void SetLanguage(string language) { _currentLanguage = language; }
        public static async Task EnsureAllToolsReady(IProgress<int>? progress, IProgress<string> status, CancellationToken token) { status?.Report(GetHelperLocalizedString("CheckingTools", "Checking tools...")); try { /* Simulate check */ await Task.Delay(500, token); progress?.Report(30); /* Find ExifTool */ await Task.Delay(500, token); progress?.Report(60); /* Find FFmpeg */ await Task.Delay(500, token); progress?.Report(100); status?.Report(GetHelperLocalizedString("AllToolsReadyComplete", "[Complete] All tools are ready.")); } catch (OperationCanceledException) { status?.Report(GetHelperLocalizedString("ToolCheckCancelled", "[Cancelled] Tool check operation cancelled.")); throw; } catch (Exception ex) { status?.Report(GetHelperLocalizedString("ToolCheckError", "[Tool Check Error] {0}").Replace("{0}", ex.Message)); throw; } }
        private static string GetHelperLocalizedString(string key, string fallback) { if (_translations.TryGetValue(key, out var langDict)) { if (langDict.TryGetValue(_currentLanguage, out var translation) && !string.IsNullOrEmpty(translation)) { return translation; } if (langDict.TryGetValue("en", out var enTranslation) && !string.IsNullOrEmpty(enTranslation)) { return enTranslation; } if (langDict.TryGetValue("zh", out var zhTranslation) && !string.IsNullOrEmpty(zhTranslation)) { return zhTranslation; } } return fallback; }
    }
} // End Hui_WPF.utils namespace


//namespace Hui_WPF
//{
//    public partial class MainWindow : Window
//    {
//        //private void RefreshPreview()
//        //{
//        //    PreviewTree.Items.Clear();
//        //    string basePath = BasePathBox2.Text.Trim();
//        //    var rule = BuildRule();

//        //    foreach (var item in GeneratePreview(basePath, rule))
//        //    {
//        //        PreviewTree.Items.Add(item);
//        //    }
//        //}
//        //private void RefreshPreview()
//        //{
//        //    PreviewTree.Items.Clear();
//        //    string basePath = BasePathBox2.Text.Trim();
//        //    if (!Directory.Exists(basePath)) return;

//        //    var rule = BuildRule();
//        //    foreach (var item in GeneratePreview(basePath, rule))
//        //    {
//        //        PreviewTree.Items.Add(item);
//        //    }
//        //}



//        private void Preview_Click(object sender, RoutedEventArgs e)
//        {
//            PreviewTree.Items.Clear();
//            string basePath = BasePathBox2.Text.Trim();
//            var rule = BuildRule();

//            foreach (var item in GeneratePreview(basePath, rule))
//            {
//                PreviewTree.Items.Add(item);
//            }
//        }

//        private DirectoryRule BuildRule()
//        {
//            var root = new DirectoryRule
//            {
//                Prefix = PrefixBox.Text.Trim(),
//                Count = int.TryParse(CountBox.Text, out var count) ? count : 1,
//                Recursive = RecursiveCheck.IsChecked == true
//            };

//            if (root.Recursive)
//            {
//                root.SubRules.Add(new DirectoryRule
//                {
//                    Prefix = "RAW",
//                    Count = 2,
//                    Recursive = false
//                });
//            }

//            return root;
//        }
//        private List<TreeViewItem> GeneratePreview(string basePath, DirectoryRule rule)
//        {
//            List<TreeViewItem> items = new();
//            string date = DateTime.Now.ToString("yyyyMMdd");

//            var rootItem = new TreeViewItem { Header = GetFolderDisplayName(basePath) };
//            rootItem.IsExpanded = true;

//            for (int i = 1; i <= rule.Count; i++)
//            {
//                string name = $"{rule.Prefix}_{i:D3}_{date}";
//                string fullPath = Path.Combine(basePath, name);

//                var node = new TreeViewItem { Header = name };
//                if (i == 1) node.IsExpanded = true;

//                if (rule.Recursive && rule.SubRules.Count > 0)
//                {
//                    foreach (var subRule in rule.SubRules)
//                    {
//                        var subs = GeneratePreview(fullPath, subRule);
//                        foreach (var sub in subs)
//                            node.Items.Add(sub);
//                    }
//                }

//                rootItem.Items.Add(node);
//            }

//            items.Add(rootItem);
//            return items;
//        }

//        private object GetFolderDisplayName(string basePath)
//        {
//            throw new NotImplementedException();
//        }

//        private void SelectFolder_Click(object sender, RoutedEventArgs e)
//        {
//            using var dialog = new System.Windows.Forms.FolderBrowserDialog();
//            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
//            {
//                BasePathBox2.Text = dialog.SelectedPath;
//            }
//        }

//        //private void Input_Changed(object sender, RoutedEventArgs e)
//        //{
//        //    RefreshPreview();
//        //}

//        private void Generate_Click(object sender, RoutedEventArgs e)
//        {
//            string basePath = BasePathBox2?.Text ?? "";
//            string prefix = PrefixBox?.Text ?? "IMG";
//            bool isRecursive = RecursiveCheck?.IsChecked == true;

//            if (!int.TryParse(CountBox?.Text, out int count) || count <= 0)
//            {
//                MessageBox.Show("数量必须是正整数");
//                return;
//            }

//            if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
//            {
//                MessageBox.Show("根目录无效或不存在！");
//                return;
//            }

//            try
//            {
//                for (int i = 1; i <= count; i++)
//                {
//                    string folderName = $"{prefix}_{i:D2}";
//                    string folderPath = Path.Combine(basePath, folderName);

//                    Directory.CreateDirectory(folderPath);

//                    if (isRecursive)
//                    {
//                        for (int j = 1; j <= 3; j++)
//                        {
//                            string subPath = Path.Combine(folderPath, $"Sub_{j:D2}");
//                            Directory.CreateDirectory(subPath);
//                        }
//                    }
//                }

//                MessageBox.Show("✅ 目录创建完成！");
//                RefreshPreview();
//            }
//            catch (Exception ex)
//            {
//                MessageBox.Show("❌ 创建失败：" + ex.Message);
//            }
//        }


//        private void RefreshPreview()
//        {
//            if (PreviewTree == null) return;

//            PreviewTree.Items.Clear();

//            string basePath = BasePathBox2?.Text ?? "";
//            string prefix = PrefixBox?.Text ?? "IMG";
//            bool isRecursive = RecursiveCheck?.IsChecked == true;

//            if (!int.TryParse(CountBox?.Text, out int count) || count <= 0)
//                count = 1;

//            TreeViewItem rootItem = new TreeViewItem
//            {
//                Header = basePath,
//                IsExpanded = true
//            };

//            for (int i = 1; i <= count; i++)
//            {
//                string folderName = $"{prefix}_{i:D2}";
//                var subItem = new TreeViewItem { Header = folderName };

//                if (isRecursive)
//                {
//                    for (int j = 1; j <= 3; j++)
//                    {
//                        subItem.Items.Add(new TreeViewItem { Header = $"Sub_{j:D2}" });
//                    }
//                }

//                rootItem.Items.Add(subItem);
//            }

//            PreviewTree.Items.Add(rootItem);
//        }


//    }

//}



