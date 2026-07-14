using System;
using System.IO;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using MemeManager.Models;

namespace MemeManager.ViewModels
{
    public class MemeViewModel : INotifyPropertyChanged
    {
        // 支持复用：切分类/刷新时复用同一 VM 实例仅替换 Model（见 UpdateModel），
        // 避免每次重建一批 BitmapImage 导致 WinUI 非托管纹理/解码资源累积泄漏。
        private MemeModel _model;
        public MemeModel Model
        {
            get => _model;
            private set => _model = value;
        }

        public string Hash => _model.Hash;
        public string LocalPath => _model.LocalPath;
        public string Category => _model.Category;
        public string FileName => _model.FileName;

        private string _title;
        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged(nameof(Title));
                }
            }
        }

        // 缩略图：URI 绑定（文档推荐，可跨多处复用同一解码结果，避免重复解码）。
        // 复用 VM 时 UpdateModel 会清掉 _imageSource 并通知，Image 重新按新 URI
        // 解码，旧图引用断开后 URI 图像缓存条目可被 LRU 淘汰，避免累积泄漏。
        private BitmapImage? _imageSource;
        public BitmapImage ImageSource
        {
            get
            {
                if (_imageSource == null && File.Exists(LocalPath))
                {
                    _imageSource = new BitmapImage();
                    _imageSource.DecodePixelWidth = 120;
                    _imageSource.UriSource = new Uri(LocalPath);
                }
                return _imageSource ?? new BitmapImage();
            }
        }

        // 复用 VM 替换底层 Model：重置缓存的缩略图/预览图，并通知所有依赖属性变更，
        // 使绑定的 Image 重新按新 LocalPath 解码（就地换源，旧纹理被替换释放）。
        public void UpdateModel(MemeModel model)
        {
            _model = model;
            _title = model.Title;
            _imageSource = null;
            _previewSource = null;
            OnPropertyChanged(nameof(Hash));
            OnPropertyChanged(nameof(LocalPath));
            OnPropertyChanged(nameof(Category));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ImageSource));
        }

        private BitmapImage? _previewSource;
        // 放大预览图：超过配置上限则等比压缩，未超过不缩放。
        public BitmapImage PreviewSource
        {
            get
            {
                if (_previewSource == null && File.Exists(LocalPath))
                {
                    _previewSource = new BitmapImage();
                    var max = GetPreviewMaxSize();
                    var (w, h) = Utils.FitWithin(
                        GetNativePixelSize().width, GetNativePixelSize().height,
                        max.width, max.height);
                    // 仅当超过上限时才设解码上限，避免无谓缩小
                    if (w < GetNativePixelSize().width || h < GetNativePixelSize().height)
                    {
                        // 以较长边作为 DecodePixel 上限，保持宽高比
                        if (w >= h)
                            _previewSource.DecodePixelWidth = (int)Math.Round(w);
                        else
                            _previewSource.DecodePixelHeight = (int)Math.Round(h);
                    }
                    _previewSource.UriSource = new Uri(LocalPath);
                }
                return _previewSource ?? new BitmapImage();
            }
        }

        // 预览图最大分辨率：优先取配置，配置缺失时回退到 Utils 默认常量。
        private (double width, double height) GetPreviewMaxSize()
        {
            double w = Utils.PreviewMaxWidth;
            double h = Utils.PreviewMaxHeight;
            try
            {
                var cfg = App.DataEngine.Config;
                if (cfg != null && cfg.PreviewMaxWidth > 0 && cfg.PreviewMaxHeight > 0)
                {
                    w = cfg.PreviewMaxWidth;
                    h = cfg.PreviewMaxHeight;
                }
            }
            catch { }
            return (w, h);
        }

        // 读取原图像素尺寸（不解码整图，只取元数据）。失败时回退为 0。
        private (double width, double height) GetNativePixelSize()
        {
            try
            {
                using var stream = File.OpenRead(LocalPath);
                var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream()).AsTask().Result;
                return (decoder.PixelWidth, decoder.PixelHeight);
            }
            catch
            {
                return (0, 0);
            }
        }

        // 供日志使用的原图尺寸（缓存首次读取结果）
        private (double width, double height)? _nativeSizeCache;
        public (double width, double height) GetPreviewNaturalSize()
            => _nativeSizeCache ??= GetNativePixelSize();

        // 实际输出（解码）分辨率：未超过配置上限取原图，否则取压缩后尺寸。
        public (double width, double height) GetPreviewOutputSize()
        {
            var (nw, nh) = GetPreviewNaturalSize();
            var max = GetPreviewMaxSize();
            var (w, h) = Utils.FitWithin(nw, nh, max.width, max.height);
            if (w >= nw && h >= nh) return (nw, nh);
            return (Math.Round(w), Math.Round(h));
        }

        public MemeViewModel(MemeModel model)
        {
            _model = model;
            _title = model.Title;
        }

        // 主窗口隐藏/关闭时调用：断开所有图像解码资源引用。
        // 置 null 后，配合 GridView 的 ItemsSource=null 卸载容器，
        // WinUI 才能把对应的 BitmapImage 从图像缓存移除并释放非托管纹理/解码内存，
        // 否则常驻的 VM 集合会一直持有这些资源导致后台进程内存无法回落。
        public void ClearImages()
        {
            _imageSource = null;
            _previewSource = null;
            OnPropertyChanged(nameof(ImageSource));
            OnPropertyChanged(nameof(PreviewSource));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
