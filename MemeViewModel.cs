using System;
using System.IO;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media.Imaging;
using MemeManager.Models;

namespace MemeManager.ViewModels
{
    public class MemeViewModel : INotifyPropertyChanged
    {
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

        // 缩略图：Avalonia 的 Bitmap 直接按文件路径解码，可被图像缓存复用。
        private Bitmap? _imageSource;
        public Bitmap? ImageSource
        {
            get
            {
                if (_imagesCleared) return null;
                if (_imageSource == null && File.Exists(LocalPath))
                {
                    try
                    {
                        _imageSource = new Bitmap(LocalPath);
                        LiveBitmapImageCount++;
                    }
                    catch { return null; }
                }
                return _imageSource;
            }
        }

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

        private Bitmap? _previewSource;
        // 放大预览图：超过配置上限则等比压缩，未超过不缩放。
        public Bitmap? PreviewSource
        {
            get
            {
                if (_imagesCleared) return null;
                if (_previewSource == null && File.Exists(LocalPath))
                {
                    try
                    {
                        var max = GetPreviewMaxSize();
                        var (w, h) = GetNativePixelSize();
                        var (dw, dh) = Utils.FitWithin(w, h, max.width, max.height);
                        // 仅当超过上限时才限制解码尺寸，避免无谓缩小
                        if (dw < w || dh < h)
                        {
                            var destW = (int)Math.Round(dw);
                            var destH = (int)Math.Round(dh);
                            using var original = new Bitmap(LocalPath);
                            _previewSource = original.CreateScaledBitmap(new PixelSize(destW, destH));
                        }
                        else
                        {
                            _previewSource = new Bitmap(LocalPath);
                        }
                        LiveBitmapImageCount++;
                    }
                    catch { return null; }
                }
                return _previewSource;
            }
        }

        // 读取原图像素尺寸（不解码整图，只取元数据）。失败时回退为 0。
        private (double width, double height) GetNativePixelSize()
        {
            try
            {
                using var bmp = new Bitmap(LocalPath);
                return (bmp.PixelSize.Width, bmp.PixelSize.Height);
            }
            catch
            {
                return (0, 0);
            }
        }

        private (double width, double height)? _nativeSizeCache;
        public (double width, double height) GetPreviewNaturalSize()
            => _nativeSizeCache ??= GetNativePixelSize();

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

        private bool _imagesCleared;

        public static int LiveBitmapImageCount { get; private set; }

        public void ClearImages()
        {
            _imagesCleared = true;
            if (_imageSource != null) { LiveBitmapImageCount--; _imageSource = null; }
            if (_previewSource != null) { LiveBitmapImageCount--; _previewSource = null; }
        }

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

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
