using System.Configuration;
using System.Data;
using System.Drawing.Printing;
using System.Windows;
using System.Windows.Controls;

namespace Hui_WPF
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
    }

}
//< StackPanel Grid.Row = "7" Orientation = "Horizontal" HorizontalAlignment = "Right" Margin = "0,5,0,0" >
//            < !--Use DynamicResource for theme consistency if brushes are defined in Themes -->
//            <Button Content="☀ 亮色"
//                    Click="LightTheme_Click" Margin="0,0,5,0" Padding="5,2"
//                    Foreground="{DynamicResource {x:Static SystemColors.ControlTextBrushKey}}"/>
//            <Button Content="🌙 暗色"
//                    Click="DarkTheme_Click" Padding="5,2"
//                    Foreground="{DynamicResource {x:Static SystemColors.ControlTextBrushKey}}"/>
//        </StackPanel>