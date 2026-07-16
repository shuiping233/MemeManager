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
                if (_imagesCleared) return new BitmapImage();
                if (_imageSource == null && File.Exists(LocalPath))
                {
                    _imageSource = new BitmapImage();
                    _imageSource.DecodePixelWidth = 120;
                    _imageSource.UriSource = new Uri(LocalPath);
                    LiveBitmapImageCount++;
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
            if (_imageSource != null) { LiveBitmapImageCount--; _imageSource = null; }
            if (_previewSource != null) { LiveBitmapImageCount--; _previewSource = null; }
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
                if (_imagesCleared) return new BitmapImage();
                if (_previewSource == null && File.Exists(LocalPath))
                {
                    _previewSource = new BitmapImage();
                    LiveBitmapImageCount++;
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

        // 多选模式(Extended)下的选中镜像：仅作视觉指示（右上角复选框），
        // 单向由 GridView.SelectedItems 同步而来，不反向写回控件选中逻辑，
        // 避免与 WinUI 原生 shift 多选/反选冲突（见 MemeItem_Tapped 注释）。
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        // 标记位：显式清除后，getter 必须返回 null 而不重建 BitmapImage。
        // 否则 ClearImages 触发 PropertyChanged 会让绑定重新求值 getter，
        // getter 里又会 new 出一个全新的 BitmapImage（旧纹理未释放、新的又来），
        // 导致“清空”反而重新解码，内存永远下不去。
        private bool _imagesCleared;

        // 诊断用：当前进程内 MemeViewModel 还持有（已创建且未清除）的 BitmapImage 数量。
        // 隐藏/清空后应趋近于 0，否则说明仍有根引用未断开。
        public static int LiveBitmapImageCount { get; private set; }

        // 显式断开 VM 持有的图像解码资源引用（BitmapImage）。
        // 用途与注意：
        //  - 调用后必须把 _imagesCleared 置 true，且**不要触发 PropertyChanged**：
        //    否则绑定重新求值 ImageSource getter 时会再次 new 出 BitmapImage，
        //    旧纹理未释放、新的又来，等于“清空反而重建”，内存永远下不去。
        //  - 当前隐藏路径统一走极简的 ItemsSource=null + GC（框架自动释放纹理），
        //    非复用模式不调用本方法；本方法保留供复用模式在需要手动释放时调用。
        //  - 注意：仅把 VM 字段置 null 不足以释放纹理，真正让 WinUI 回收 GPU 纹理的
        //    是“Image 控件从可视化树移除”（即 GridView.ItemsSource=null 后容器被卸载）。
        public void ClearImages()
        {
            _imagesCleared = true;
            if (_imageSource != null) { LiveBitmapImageCount--; _imageSource = null; }
            if (_previewSource != null) { LiveBitmapImageCount--; _previewSource = null; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
