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
        public string Title => Model.Title;
        public string Category => Model.Category;
        public string FileName => Model.FileName;

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
                    OnPropertyChanged(nameof(CheckBoxVisibility));
                    OnPropertyChanged(nameof(BadgeVisibility));
                }
            }
        }

        public Visibility CheckBoxVisibility => (_showSelectionUI && !_isSelected) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility BadgeVisibility => (_showSelectionUI && _isSelected) ? Visibility.Visible : Visibility.Collapsed;

        private bool _showSelectionUI;
        public bool ShowSelectionUI
        {
            get => _showSelectionUI;
            set
            {
                if (_showSelectionUI != value)
                {
                    _showSelectionUI = value;
                    OnPropertyChanged(nameof(ShowSelectionUI));
                    OnPropertyChanged(nameof(CheckBoxVisibility));
                    OnPropertyChanged(nameof(BadgeVisibility));
                }
            }
        }

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

        public MemeViewModel(MemeModel model)
        {
            Model = model;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
