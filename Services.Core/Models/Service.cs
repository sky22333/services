using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Services.Core.Models
{
    public class Service : INotifyPropertyChanged
    {
        private string _status = "未知";
        private int _pid;

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ExePath { get; set; } = string.Empty;
        public string? Args { get; set; }
        public string? WorkingDir { get; set; }

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                }
            }
        }

        public int Pid
        {
            get => _pid;
            set
            {
                if (_pid != value)
                {
                    _pid = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool AutoStart { get; set; }
        public bool AutoRestart { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class ServiceConfig
    {
        public string Name { get; set; } = string.Empty;
        public string ExePath { get; set; } = string.Empty;
        public string? Args { get; set; }
        public string? WorkingDir { get; set; }
        public bool AutoRestart { get; set; }
        public ServiceStartupType StartupType { get; set; } = ServiceStartupType.Auto;
    }

    public enum ServiceStartupType
    {
        Auto = 2,
        Manual = 3
    }
}
