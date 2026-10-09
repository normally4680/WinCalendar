using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using WinCalendar.Models;

namespace WinCalendar.Services
{
    public class HolidayService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly string _cacheDir;
        private Dictionary<string, HolidayDay> _holidayDict = new();
        private HashSet<int> _loadedYears = new(); // 👈 新增：记录已加载的年份

        public HolidayService()
        {
            _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinCalendar", "HolidayCache");
            Directory.CreateDirectory(_cacheDir);
        }

        public async Task LoadYearAsync(int year)
        {
            // 👈 如果已经加载过该年份，直接返回，避免重复IO和网络请求
            if (_loadedYears.Contains(year)) return;

            string cachePath = Path.Combine(_cacheDir, $"{year}.json");
            string? json = null;

            try
            {
                string url = $"https://fastly.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{year}.json";
                var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    json = await response.Content.ReadAsStringAsync();
                    await File.WriteAllTextAsync(cachePath, json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"网络请求失败: {ex.Message}");
            }

            if (string.IsNullOrEmpty(json) && File.Exists(cachePath))
            {
                json = await File.ReadAllTextAsync(cachePath);
            }

            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var data = JsonSerializer.Deserialize<HolidayResponse>(json);
                    if (data?.Days != null)
                    {
                        foreach (var day in data.Days)
                        {
                            _holidayDict[day.Date] = day;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"JSON解析失败: {ex.Message}");
                }
            }

            _loadedYears.Add(year); // 标记为已加载
        }

        public HolidayDay? GetHoliday(string dateStr)
        {
            return _holidayDict.TryGetValue(dateStr, out var day) ? day : null;
        }
    }
}