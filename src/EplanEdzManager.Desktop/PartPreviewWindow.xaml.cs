using System.Windows;
using EplanEdzManager.Desktop.ViewModels;

namespace EplanEdzManager.Desktop;

public partial class PartPreviewWindow : Window
{
    public PartPreviewWindow(PartPreviewResult preview)
    {
        InitializeComponent();
        Title = "部件预览 · " + preview.PartDisplayName;
        PartTitleText.Text = preview.PartDisplayName;
        ResourceText.Text = preview.Resource is null
            ? "没有可显示的内嵌图片"
            : $"预览资源：{preview.Resource.DisplayName}  ·  {preview.Resource.SizeText}";
        MessageText.Text = preview.Message;
        PreviewImage.Source = preview.Image;
        PreviewImage.Visibility = preview.Image is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyMessageText.Text = preview.Image is null ? preview.Message : string.Empty;
        EmptyMessageText.Visibility = preview.Image is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
