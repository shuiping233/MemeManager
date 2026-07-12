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
        public MemeModel Model { get; }

        public string Hash => Model.Hash;
        public string LocalPath => Model.LocalPath;
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
        public string Category => Model.Category;
        public string FileName => Model.FileName;

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

        private BitmapImage? _previewSource;
        // 放大预览图：≤800x600 不缩放，超出则等比压缩到 800x600 内。
        public BitmapImage PreviewSource
        {
            get
            {
                if (_previewSource == null && File.Exists(LocalPath))
                {
                    _previewSource = new BitmapImage();
                    var (w, h) = Utils.FitWithin(
                        GetNativePixelSize().width, GetNativePixelSize().height,
                        Utils.PreviewMaxWidth, Utils.PreviewMaxHeight);
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

        // 实际输出（解码）分辨率：≤800x600 不缩放取原图，否则取压缩后尺寸。
        public (double width, double height) GetPreviewOutputSize()
        {
            var (nw, nh) = GetPreviewNaturalSize();
            var (w, h) = Utils.FitWithin(nw, nh, Utils.PreviewMaxWidth, Utils.PreviewMaxHeight);
            if (w >= nw && h >= nh) return (nw, nh);
            return (Math.Round(w), Math.Round(h));
        }

        public MemeViewModel(MemeModel model)
        {
            Model = model;
            _title = model.Title;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
