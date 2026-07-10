using System;
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;
using MemeManager.Models;

namespace MemeManager.ViewModels
{
    public class MemeViewModel
    {
        // 保留对底层原始数据的引用
        public MemeModel Model { get; }

        // 快捷提供哈希和路径
        public string Hash => Model.Hash;
        public string LocalPath => Model.LocalPath;

        // 🎯 核心优化：懒加载、异步渲染的图片源
        // 只有当 GridView 滚到这一帧、需要显示时，才会触发 WinUI 的底层硬件加速解码
        private BitmapImage? _imageSource;
        public BitmapImage ImageSource
        {
            get
            {
                if (_imageSource == null && File.Exists(LocalPath))
                {
                    _imageSource = new BitmapImage();
                    // 启用软解码和缓存优化
                    _imageSource.DecodePixelWidth = 120; // 紧凑模式下限制解码宽度，极大地压榨并节省内存开销
                    _imageSource.UriSource = new Uri(LocalPath);
                }
                return _imageSource ?? new BitmapImage();
            }
        }

        public MemeViewModel(MemeModel model)
        {
            Model = model;
        }
    }
}