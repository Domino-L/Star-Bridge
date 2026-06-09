using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;

namespace StarBridge.Desktop;

public partial class HangarReaderWindow : Window
{
    private readonly string _language;
    private readonly ObservableCollection<OwnedShipRecord> _detectedShips = [];

    public HangarReaderWindow(string language)
    {
        InitializeComponent();
        _language = language;
        DetectedShipsList.ItemsSource = _detectedShips;
        Loaded += async (_, _) =>
        {
            try
            {
                await HangarWebView.EnsureCoreWebView2Async();
            }
            catch (Exception exception)
            {
                ReaderStatusText.Text = $"WebView2 初始化失败：{exception.Message}";
            }
        };
    }

    public IReadOnlyList<OwnedShipRecord> ImportedShips { get; private set; } = [];

    private void GoButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(AddressBox.Text.Trim(), UriKind.Absolute, out var uri))
        {
            ReaderStatusText.Text = "地址无效。";
            return;
        }

        HangarWebView.Source = uri;
    }

    private async void ReadCurrentPageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await HangarWebView.EnsureCoreWebView2Async();
            var json = await HangarWebView.ExecuteScriptAsync("""
                (() => {
                  const ships = [];
                  document.querySelectorAll('.kind').forEach(kind => {
                    const kindText = (kind.textContent || '').trim().toLowerCase();
                    if (kindText !== 'ship' && kindText !== '飞船') {
                      return;
                    }
                    const item = kind.closest('.item') || kind.parentElement;
                    const title = item && item.querySelector('.title')
                      ? item.querySelector('.title').textContent.trim()
                      : '';
                    const liner = item && item.querySelector('.liner')
                      ? item.querySelector('.liner').textContent.trim()
                      : '';
                    const manufacturer = liner.match(/\(([A-Z0-9]{3,5})\)/);
                    if (title) {
                      ships.push({
                        Title: title,
                        ManufacturerCode: manufacturer ? manufacturer[1] : ''
                      });
                    }
                  });
                  const seen = new Set();
                  return ships.filter(ship => {
                    const key = `${ship.Title}|${ship.ManufacturerCode}`;
                    if (seen.has(key)) {
                      return false;
                    }
                    seen.add(key);
                    return true;
                  });
                })();
                """);

            var candidates = JsonSerializer.Deserialize<HangarShipCandidate[]>(json) ?? [];
            var result = HangarShipImporter.ImportOfficialShipCandidates(candidates, _language);
            var added = 0;
            foreach (var ship in result.Ships)
            {
                if (_detectedShips.Any(existing => existing.Code.Equals(ship.Code, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _detectedShips.Add(ship);
                added++;
            }

            PageShipCountText.Text = $"当前页：{result.Ships.Count}";
            TotalShipCountText.Text = $"累计：{_detectedShips.Count}";
            ReaderStatusText.Text = $"读取完成：页面 Ship 条目 {result.MatchedCodes}，已识别 {result.MatchedNames}，新增 {added}。";
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = $"读取失败：{exception.Message}";
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        ImportedShips = _detectedShips.ToArray();
        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
