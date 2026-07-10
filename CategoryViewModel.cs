using System.ComponentModel;

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
