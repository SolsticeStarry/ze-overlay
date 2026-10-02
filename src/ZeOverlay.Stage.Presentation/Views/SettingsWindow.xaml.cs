using System.Globalization;
using System.Windows;

using ZeOverlay.Shared;
using ZeOverlay.Infrastructure;

namespace ZeOverlay.Stage.Presentation;

public partial class SettingsWindow : Window
{
    private List<string> _names;
    private readonly IReadOnlyList<string> _recentNames;

    public WatchlistConfig Configuration { get; private set; }

    public SettingsWindow(WatchlistConfig configuration, Func<IReadOnlyList<string>> recentNames)
    {
        InitializeComponent();
        _names = configuration.Names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(WatchlistConfig.MaxNames)
            .ToList();
        List<string> configuredOrder = configuration.SortOrder
            .Where(_names.Contains)
            .ToList();
        configuredOrder.AddRange(_names.Where(name => !configuredOrder.Contains(name, StringComparer.Ordinal)));
        _names = configuredOrder;
        _recentNames = recentNames();
        Configuration = new WatchlistConfig
        {
            Version = configuration.Version,
            Names = _names.ToList(),
            SortOrder = configuration.SortOrder.Where(_names.Contains).ToList(),
            MatchThreshold = configuration.MatchThreshold,
        };
        RefreshLists();
        ThresholdTextBox.Text = configuration.MatchThreshold.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void RefreshLists()
    {
        NamesList.ItemsSource = null;
        NamesList.ItemsSource = _names;
        OrderList.ItemsSource = null;
        OrderList.ItemsSource = _names;
        RecentList.ItemsSource = null;
        RecentList.ItemsSource = _recentNames.Where(name => !_names.Contains(name, StringComparer.Ordinal)).ToList();
    }

    public IReadOnlyList<string> OrderedNames => _names;

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        string name = NameTextBox.Text.Trim();
        if (name.Length == 0 || _names.Contains(name, StringComparer.Ordinal)) return;
        if (_names.Count >= WatchlistConfig.MaxNames)
        {
            MessageBox.Show("关注名单最多 10 项。", "ZeOverlay", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _names.Add(name);
        NameTextBox.Clear();
        RefreshLists();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (NamesList.SelectedItem is string name)
        {
            _names.Remove(name);
            RefreshLists();
        }
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        int index = OrderList.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _names.Count) return;
        (_names[index], _names[target]) = (_names[target], _names[index]);
        RefreshLists();
        OrderList.SelectedIndex = target;
    }

    private void OnAddRecentClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not string name) return;
        if (_names.Count >= WatchlistConfig.MaxNames)
        {
            MessageBox.Show("关注名单最多 10 项。", "ZeOverlay", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _names.Add(name);
        RefreshLists();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ThresholdTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold)
            || threshold is < 0.5 or > 1.0)
        {
            MessageBox.Show("阈值必须是 0.50 到 1.00 之间的数字。", "ZeOverlay", MessageBoxButton.OK, MessageBoxImage.Warning);
            ThresholdTextBox.Focus();
            return;
        }
        Configuration = new WatchlistConfig
        {
            Version = Configuration.Version,
            Names = _names.ToList(),
            SortOrder = _names.ToList(),
            MatchThreshold = threshold,
        };
        DialogResult = true;
    }
}
