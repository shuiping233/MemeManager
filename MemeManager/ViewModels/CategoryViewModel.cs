using System.ComponentModel;
using MemeManager.Infrastructure;

namespace MemeManager.ViewModels
{
    public class CategoryViewModel : INotifyPropertyChanged
    {
        private string _name;
        private int _count;
        private bool _isSelected;

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); }
            }
        }

        public int Count
        {
            get => _count;
            set
            {
                if (_count != value) { _count = value; OnPropertyChanged(nameof(Count)); }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
            }
        }

        // 下拉/列表显示名：空名(Name=="")代表“全部表情”虚拟项，统一显示 Category_AllMemes；
        // 否则显示真实分类名。供 Mini 的 ComboBox 等直接绑定。
        public string DisplayText =>
            string.IsNullOrEmpty(_name) ? Localization.Get("Category_AllMemes") : _name;

        public CategoryViewModel(string name, int count)
        {
            _name = name;
            _count = count;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
