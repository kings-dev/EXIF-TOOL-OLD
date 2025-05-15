using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows; // Required for Binding.DoNothing

namespace Hui_WPF // 确保命名空间正确
{
    //public class ProgressToBrushConverter : IValueConverter
    //{
    //    // 定义不同进度区间的颜色 (可以设为属性以便在XAML中设置)
    //    public Brush LowBrush { get; set; } = new SolidColorBrush(Color.FromArgb(255, 255, 100, 100)); // Light Red/Orange
    //    public Brush MediumBrush { get; set; } = new SolidColorBrush(Color.FromArgb(255, 255, 193, 7));    // Amber/Gold
    //    public Brush HighBrush { get; set; } = new SolidColorBrush(Color.FromArgb(255, 76, 175, 80));    // Green
    //    public Brush CompleteBrush { get; set; } = new SolidColorBrush(Color.FromArgb(255, 0, 200, 83));   // Bright Green for 100%

    //    // 定义区间的阈值 (也可以设为属性)
    //    public double MediumThreshold { get; set; } = 40.0; // 值 >= 40 算中等
    //    public double HighThreshold { get; set; } = 80.0;   // 值 >= 80 算高
    //    public double CompleteThreshold { get; set; } = 100.0; // 值 >= 100 算完成

    //    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    //    {
    //        // 检查输入值是否为 double 类型 (ProgressBar 的 Value 是 double)
    //        if (value is double progressValue)
    //        {
    //            // 检查是否完成 (处理等于 100 的情况)
    //            // 使用 >= 确保 100 也匹配
    //            if (progressValue >= CompleteThreshold)
    //            {
    //                return CompleteBrush;
    //            }
    //            // 检查高区间
    //            if (progressValue >= HighThreshold)
    //            {
    //                return HighBrush;
    //            }
    //            // 检查中等区间
    //            if (progressValue >= MediumThreshold)
    //            {
    //                return MediumBrush;
    //            }
    //            // 否则认为是低区间
    //            return LowBrush;
    //        }

    //        // 如果输入值不是 double，则不进行转换或返回默认值
    //        return Binding.DoNothing; // 或者 return Brushes.Gray;
    //    }

    //    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    //    {
    //        // ConvertBack 通常不需要实现，因为我们只从进度值单向转换为颜色
    //        throw new NotSupportedException("Cannot convert Brush back to progress value.");
    //    }
    //}
}