// MainWindow.DirectoryPreview.cs
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Diagnostics;

namespace Hui_WPF // 确保命名空间与 MainWindow.xaml.cs 中的一致
{
    public partial class MainWindow : Window // 关键：使用 partial 关键字
    {
        // --- 目录配置与预览相关方法 ---

        private void SelectFolder_Click(object sender, RoutedEventArgs e)
        {
            if (BasePathBox == null) // BasePathBox 是在 XAML 中定义的，应由 InitializeComponent() 初始化
            {
                Debug.WriteLine("SelectFolder_Click: BasePathBox is null!");
                // 如果 GetLocalizedString 已经可以在此时安全调用
                // MessageBox.Show(this, GetLocalizedString("ErrorControlNotReady", "UI控件未就绪。"), GetLocalizedString("ErrorTitle", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = GetLocalizedString("DialogTitleSelectRootDir", "选择根目录"),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            if (dialog.ShowDialog() == true)
            {
                BasePathBox.Text = dialog.FolderName;
                // BasePathBox.TextChanged 事件会自动调用 DirectoryConfig_TextChanged, 进而调用 UpdatePreview
            }
        }

        // 处理 CheckBox 的 Checked/Unchecked 事件
        private void DirectoryConfig_InputChanged(object sender, RoutedEventArgs e)
        {
            // 确保窗口已加载且 PreviewTree 控件已初始化
            if (!this.IsLoaded || PreviewTree == null)
            {
                Debug.WriteLine("DirectoryConfig_InputChanged: Not loaded or PreviewTree is null.");
                return;
            }
            UpdatePreview(); // 调用 UpdatePreview 方法
        }

        // 处理 TextBox 的 TextChanged 事件
        private void DirectoryConfig_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 确保窗口已加载且 PreviewTree 控件已初始化
            if (!this.IsLoaded || PreviewTree == null)
            {
                Debug.WriteLine("DirectoryConfig_TextChanged: Not loaded or PreviewTree is null.");
                return;
            }
            UpdatePreview(); // 调用 UpdatePreview 方法
        }

        // 更新 TreeView 预览的方法
        private void UpdatePreview()
        {
            // 安全检查：确保所有必要的目录配置 UI 控件都已加载
            if (PreviewTree == null || BasePathBox == null || PrefixBox == null || CountBox == null || RecursiveCheck == null)
            {
                Debug.WriteLine("UpdatePreview called but one or more directory config UI controls are null!");
                // 如果 PreviewTree 存在，但其他控件可能不存在，仍然尝试显示错误
                if (PreviewTree != null)
                {
                    // 假设 GetLocalizedString 此时可用，否则使用硬编码的英文
                    AddErrorToPreview(GetLocalizedString("ErrorUIControlsNotReadyForPreview", "UI controls not fully loaded for preview."));
                }
                return;
            }

            PreviewTree.Items.Clear();
            string basePathText = BasePathBox.Text;
            string prefixText = PrefixBox.Text;
            string countText = CountBox.Text;
            bool recursiveChecked = RecursiveCheck.IsChecked == true;

            // 检查是否是初始启动状态（或用户清空了路径），需要显示示例预览
            // "此电脑" 是 BasePathBox 在 XAML 中的初始值
            // GetLocalizedString("UnselectedState") 是主界面 txtFolderPath 的常用占位符
            bool showExamplePreview = basePathText.Equals("此电脑", StringComparison.OrdinalIgnoreCase) ||
                                      (txtFolderPath != null && basePathText.Equals(GetLocalizedString("UnselectedState", "未选择文件夹/源图片"), StringComparison.OrdinalIgnoreCase)) ||
                                      string.IsNullOrWhiteSpace(basePathText);

            if (showExamplePreview)
            {
                // --- 生成并显示示例预览 ---
                string exampleBasePath = GetLocalizedString("ExampleBasePathPreview", "D:\\OutputTest (示例预览)");
                string examplePrefix = !string.IsNullOrWhiteSpace(prefixText) ? prefixText : "RAW"; // 使用当前输入框的值，或默认"RAW"

                if (!int.TryParse(countText, out int exampleCount) || exampleCount <= 0)
                {
                    exampleCount = 2; // 与 CountBox 在 XAML 中的初始值 "2" 对应
                }
                bool exampleRecursive = recursiveChecked; // 使用当前 CheckBox 的状态
                string exampleDate = "20250506"; // 固定示例日期以匹配描述

                try
                {
                    TreeViewItem rootExampleItem = new TreeViewItem
                    {
                        Header = exampleBasePath,
                        IsExpanded = true,
                        Foreground = System.Windows.Media.Brushes.DarkGray // 示例使用深灰色文本
                    };
                    PreviewTree.Items.Add(rootExampleItem);

                    for (int i = 1; i <= exampleCount; i++)
                    {
                        string rootFolderName = $"{examplePrefix}_{i:D3}_{exampleDate}";
                        TreeViewItem mainFolderItem = new TreeViewItem
                        {
                            Header = rootFolderName,
                            IsExpanded = (i == 1) // 只展开第一个主文件夹
                        };
                        rootExampleItem.Items.Add(mainFolderItem);

                        if (exampleRecursive)
                        {
                            // 第一个子目录：与父目录同名
                            string subFolder1Name = rootFolderName;
                            TreeViewItem subItem1 = new TreeViewItem { Header = subFolder1Name };
                            mainFolderItem.Items.Add(subItem1);

                            // 第二个子目录：编号+1 (基于父目录的i)
                            string subFolder2Name = $"{examplePrefix}_{(i + 1):D3}_{exampleDate}";
                            TreeViewItem subItem2 = new TreeViewItem { Header = subFolder2Name };
                            mainFolderItem.Items.Add(subItem2);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddErrorToPreview(GetLocalizedString("ErrorExamplePreviewGenerationFailed", $"示例预览生成失败: {ex.Message}"));
                }
                return; // 显示完示例后返回，不执行后续的真实路径检查
            }

            // --- 如果不是显示示例，则执行正常的预览逻辑 ---
            string basePath = basePathText;
            string prefix = prefixText;

            if (!int.TryParse(countText, out int count) || count <= 0)
            {
                AddErrorToPreview(GetLocalizedString("ErrorCountPositiveInteger", "数量必须是正整数。"));
                return;
            }
            // "此电脑" 和空路径的情况已在 showExamplePreview 中处理
            // if (string.IsNullOrWhiteSpace(basePath)) ...

            if (string.IsNullOrWhiteSpace(prefix))
            {
                AddErrorToPreview(GetLocalizedString("ErrorEnterPrefix", "请输入根目录前缀。"));
                return;
            }

            bool generateSubDirs = recursiveChecked;
            string currentDate = DateTime.Now.ToString("yyyyMMdd");

            try
            {
                TreeViewItem rootPreviewItem = new TreeViewItem
                {
                    Header = basePath, // 真实路径
                    IsExpanded = true
                };
                PreviewTree.Items.Add(rootPreviewItem);

                for (int i = 1; i <= count; i++)
                {
                    string rootFolderName = $"{prefix}_{i:D3}_{currentDate}";
                    TreeViewItem mainFolderItem = new TreeViewItem
                    {
                        Header = rootFolderName,
                        IsExpanded = (i == 1)
                    };
                    rootPreviewItem.Items.Add(mainFolderItem);

                    if (generateSubDirs)
                    {
                        string subFolder1Name = rootFolderName;
                        TreeViewItem subItem1 = new TreeViewItem { Header = subFolder1Name };
                        mainFolderItem.Items.Add(subItem1);

                        string subFolder2Name = $"{prefix}_{(i + 1):D3}_{currentDate}";
                        TreeViewItem subItem2 = new TreeViewItem { Header = subFolder2Name };
                        mainFolderItem.Items.Add(subItem2);
                    }
                }
            }
            catch (ArgumentException ex) // Path.Combine 可能因无效字符抛出异常
            {
                AddErrorToPreview(GetLocalizedString("ErrorInvalidCharsPreview", $"路径或前缀包含无效字符: {ex.Message}"));
            }
            catch (Exception ex) // 其他通用异常
            {
                AddErrorToPreview(GetLocalizedString("ErrorPreviewGenerationFailed", $"预览生成失败: {ex.Message}"));
            }
        }

        // 向 TreeView 添加错误信息的方法
        private void AddErrorToPreview(string errorMessage)
        {
            if (PreviewTree == null)
            {
                Debug.WriteLine("AddErrorToPreview: PreviewTree is null!");
                return;
            }
            PreviewTree.Items.Clear();
            PreviewTree.Items.Add(new TreeViewItem { Header = errorMessage, Foreground = System.Windows.Media.Brushes.Red });
        }

        // "生成目录" 按钮的点击事件处理
        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            // 安全检查：确保所有必要的目录配置 UI 控件都已加载
            if (BasePathBox == null || PrefixBox == null || CountBox == null || RecursiveCheck == null || PreviewTree == null)
            {
                MessageBox.Show(this, GetLocalizedString("ErrorUIControlsNotReadyForGeneration", "UI 控件未准备好进行生成。"), GetLocalizedString("ErrorTitle", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 检查是否正在显示示例预览，如果是，则阻止生成
            string currentBasePathText = BasePathBox.Text;
            bool isShowingExample = currentBasePathText.Equals("此电脑", StringComparison.OrdinalIgnoreCase) ||
                                   (txtFolderPath != null && currentBasePathText.Equals(GetLocalizedString("UnselectedState", "未选择文件夹/源图片"), StringComparison.OrdinalIgnoreCase)) ||
                                   string.IsNullOrWhiteSpace(currentBasePathText) ||
                                   (PreviewTree.Items.Count > 0 && PreviewTree.Items[0] is TreeViewItem rootItem && rootItem.Header.ToString().Contains(GetLocalizedString("ExamplePreviewSuffix", "(示例预览)")));
            // (示例预览) 这个文本应该与 UpdatePreview 中 exampleBasePath 的文本匹配

            if (isShowingExample)
            {
                MessageBox.Show(this, GetLocalizedString("ErrorCannotGenerateFromExample", "不能基于示例预览生成目录。请先选择一个有效的根目录。"), GetLocalizedString("TipTitle", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string basePath = BasePathBox.Text; // 使用最新的文本框内容
            string prefix = PrefixBox.Text;

            // 重新进行输入验证，因为用户可能在预览后更改了内容
            if (string.IsNullOrWhiteSpace(basePath))
            {
                MessageBox.Show(this, GetLocalizedString("ErrorValidBasePathRequired", "请输入有效的根目录路径！"), GetLocalizedString("ErrorTitle", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (string.IsNullOrWhiteSpace(prefix))
            {
                MessageBox.Show(this, GetLocalizedString("ErrorPrefixRequired", "请输入根目录前缀！"), GetLocalizedString("ErrorTitle", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!int.TryParse(CountBox.Text, out int count) || count <= 0)
            {
                MessageBox.Show(this, GetLocalizedString("ErrorCountPositiveIntegerRequired", "数量必须是一个正整数！"), GetLocalizedString("ErrorTitle", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            bool generateSubDirs = RecursiveCheck.IsChecked == true;
            string currentDate = DateTime.Now.ToString("yyyyMMdd");
            int createdCount = 0;

            try
            {
                if (!Directory.Exists(basePath))
                {
                    Directory.CreateDirectory(basePath);
                }

                for (int i = 1; i <= count; i++)
                {
                    string rootFolderName = $"{prefix}_{i:D3}_{currentDate}";
                    string fullRootFolderPath = Path.Combine(basePath, rootFolderName);

                    Directory.CreateDirectory(fullRootFolderPath);
                    createdCount++;

                    if (generateSubDirs)
                    {
                        // 第一个子目录：与父目录同名
                        string subFolder1Path = Path.Combine(fullRootFolderPath, rootFolderName);
                        Directory.CreateDirectory(subFolder1Path);

                        // 第二个子目录：编号+1
                        string subFolder2Name = $"{prefix}_{(i + 1):D3}_{currentDate}";
                        string subFolder2Path = Path.Combine(fullRootFolderPath, subFolder2Name);
                        Directory.CreateDirectory(subFolder2Path);
                    }
                }
                MessageBox.Show(this, GetLocalizedString("SuccessGeneration", $"成功生成 {createdCount} 个主目录及其子目录（如果勾选）。"), GetLocalizedString("SuccessTitle", "成功"), MessageBoxButton.OK, MessageBoxImage.Information);

                // 可选：生成成功后，如果当前 BasePathBox 的路径与生成的路径一致，可以刷新预览
                if (BasePathBox.Text.Equals(basePath, StringComparison.OrdinalIgnoreCase))
                {
                    UpdatePreview();
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                MessageBox.Show(this, GetLocalizedString("ErrorAuthGeneration", $"创建目录时权限不足：{ex.Message}"), GetLocalizedString("ErrorAuthTitle", "权限错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (IOException ex)
            {
                MessageBox.Show(this, GetLocalizedString("ErrorIOGeneration", $"创建目录时发生IO错误：{ex.Message}"), GetLocalizedString("ErrorIOTitle", "IO错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, GetLocalizedString("ErrorUnknownGeneration", $"发生未知错误：{ex.Message}"), GetLocalizedString("ErrorTitle", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}