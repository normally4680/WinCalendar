using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinCalendar.Models
{
    /// <summary>
    /// 日历网格中的一个格子。
    ///
    /// ⚠️ 关键约定：所有会影响 UI 的属性都必须在赋值时触发 PropertyChanged。
    /// 因为"胶片式滚动"依赖【复用同一批 DayItem 实例】——只改属性、不重建集合，
    /// 这样每次滚动就不会重建 49 个 Button 视觉树，动画才能真正丝滑。
    /// </summary>
    public class DayItem : INotifyPropertyChanged
    {
        private DateTime _date = DateTime.Today;
        private bool _isCurrentMonth;
        private bool _isWeekend;
        private bool _isHoliday;
        private bool _isWorkday;
        private bool _isToday;
        private bool _isSelected;
        private string _lunarDate = string.Empty;
        private string _holidayName = string.Empty;

        public DateTime Date
        {
            get => _date;
            set
            {
                if (_date == value) return;
                _date = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DayNumber)); // 派生属性需要手动通知
            }
        }

        /// <summary>派生属性：跟随 Date 变化</summary>
        public int DayNumber => _date.Day;

        public bool IsCurrentMonth { get => _isCurrentMonth; set => Set(ref _isCurrentMonth, value); }
        public bool IsWeekend { get => _isWeekend; set => Set(ref _isWeekend, value); }
        public bool IsHoliday { get => _isHoliday; set => Set(ref _isHoliday, value); }
        public bool IsWorkday { get => _isWorkday; set => Set(ref _isWorkday, value); }
        public bool IsToday { get => _isToday; set => Set(ref _isToday, value); }
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
        public string LunarDate { get => _lunarDate; set => Set(ref _lunarDate, value); }
        public string HolidayName { get => _holidayName; set => Set(ref _holidayName, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 值真正变化时才通知 —— 滚动时绝大多数属性值不变，
        /// 于是绑定的更新量极小，这是"零重排"刷新的关键。
        /// </summary>
        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            OnPropertyChanged(propertyName);
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
