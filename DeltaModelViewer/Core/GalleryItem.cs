using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace DeltaModelViewer.Core;

/// <summary>模型库条目：相对路径 + 显示名 + 懒加载的缩略图。</summary>
public sealed class GalleryItem : INotifyPropertyChanged
{
    /// <summary>相对模型根目录的路径（作为加载与缩略图缓存的唯一标识，支持子文件夹）。</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>显示名（文件夹名）。</summary>
    public string FolderName { get; init; } = "";

    private ImageSource? _thumbnail;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
