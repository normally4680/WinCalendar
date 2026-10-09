using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace WinCalendar.Models
{
    public class HolidayResponse
    {
        [JsonPropertyName("year")]
        public int Year { get; set; }

        [JsonPropertyName("days")]
        public List<HolidayDay> Days { get; set; } = new();
    }

    public class HolidayDay
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("isOffDay")]
        public bool IsOffDay { get; set; }
    }
}