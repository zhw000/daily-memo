using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using DailyMemo.Core;

namespace DailyMemo.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private PageViewModel _selected;
    private string _status = "";
    private bool _hasErrors;
    private bool _isRefreshing;

    public MainViewModel(AppHost host)
    {
        Today = new TodayViewModel();
        Upcoming = new UpcomingViewModel();
        Lists = new ListsViewModel();
        Settings = new SettingsViewModel(host);
        Pages = new ObservableCollection<PageViewModel> { Today, Upcoming, Lists };
        _selected = Today;

        RefreshCommand = new AsyncCommand(() => host.Agenda.RefreshAsync());
        ToggleWidgetCommand = new RelayCommand(host.ToggleWidget);
        OpenSettingsCommand = new RelayCommand(() => Selected = Settings);
    }

    public TodayViewModel Today { get; }
    public UpcomingViewModel Upcoming { get; }
    public ListsViewModel Lists { get; }
    public SettingsViewModel Settings { get; }
    public ObservableCollection<PageViewModel> Pages { get; }

    public PageViewModel Selected
    {
        get => _selected;
        set
        {
            if (value == null || !Set(ref _selected, value)) return;
            value.Rebuild();
            OnPropertyChanged(nameof(IsSettingsSelected));
        }
    }

    public bool IsSettingsSelected => _selected == Settings;

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool HasErrors { get => _hasErrors; private set => Set(ref _hasErrors, value); }
    public bool IsRefreshing { get => _isRefreshing; private set => Set(ref _isRefreshing, value); }

    public ICommand RefreshCommand { get; }
    public ICommand ToggleWidgetCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    public void Navigate(string key)
    {
        Selected = key switch
        {
            "settings" => Settings,
            "upcoming" => Upcoming,
            "lists" => Lists,
            _ => Today,
        };
    }

    public void Rebuild()
    {
        var agenda = AppHost.Current.Agenda;
        IsRefreshing = agenda.IsRefreshing;
        var errors = agenda.Errors.ToList();
        HasErrors = errors.Count > 0;
        Status = agenda.IsRefreshing ? "正在同步…"
            : errors.Count > 0 ? "同步出错，点这里查看"
            : agenda.LastRefresh.HasValue ? $"已同步 · {agenda.LastRefresh:HH:mm}"
            : "尚未同步";

        // 今天页的角标总是要更新
        Today.Rebuild();
        if (_selected != Today) _selected.Rebuild();
    }
}
