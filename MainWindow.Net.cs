using System.Windows;
using System.Windows.Threading;

using System.Globalization;

namespace ResetExplode;

/// <summary>Páginas Energia (bateria e fonte) e Rede (diagnóstico de lag).</summary>
public partial class MainWindow
{
    // ================= Energia =================

    private bool _energyBusy;

    private async void RefreshEnergy()
    {
        if (_energyBusy) return;
        _energyBusy = true;
        BtnEnergyRefresh.IsEnabled = false;
        EnergyStatus.Text = "Lendo bateria e energia…";
        try
        {
            var sensors = _monitor.Rows.ToList();
            var cards = await Task.Run(() => BatteryInfo.Read(sensors));
            EnergyCards.ItemsSource = cards;
            UpdateEnergyCost();
            bool hasBattery = cards.Count > 1;
            EnergyStatus.Text = (hasBattery ? "Bateria detectada" : "Nenhuma bateria (PC de mesa)") + $" · lido às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { EnergyStatus.Text = "Falhou: " + ex.Message; }
        finally
        {
            _energyBusy = false;
            BtnEnergyRefresh.IsEnabled = true;
        }
    }

    private void BtnEnergyRefresh_Click(object sender, RoutedEventArgs e) => RefreshEnergy();

    private void EnergyTariff_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateEnergyCost();

    private void EnergyTariff_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!TryGetEnergyTariff(out var tariff)) return;
        _settings.EnergyTariffPerKwh = tariff;
        try { _settings.Save(); } catch { }
        EnergyTariff.Text = tariff.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR"));
    }

    private bool TryGetEnergyTariff(out double tariff)
    {
        var text = EnergyTariff.Text.Trim();
        var ptBr = CultureInfo.GetCultureInfo("pt-BR");
        // Aceita tanto 1,25 (formato brasileiro) quanto 1.25; no pt-BR, "1.25" seria interpretado como 125.
        bool dotDecimal = text.Contains('.') && !text.Contains(',');
        bool parsed = dotDecimal
            ? double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out tariff)
            : double.TryParse(text, NumberStyles.Number, ptBr, out tariff)
              || double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out tariff);
        return parsed && double.IsFinite(tariff) && tariff > 0 && tariff <= 100;
    }

    /// <summary>Atualiza o custo usando a potência do pacote da CPU e da GPU principal, sem inventar o consumo da tomada.</summary>
    private void UpdateEnergyCost()
    {
        if (!TryGetEnergyTariff(out var tariff))
        {
            EnergyCostPower.Text = "Informe uma tarifa válida entre R$ 0,01 e R$ 100,00 por kWh.";
            EnergyCostHour.Text = EnergyCostDay.Text = EnergyCostMonth.Text = "—";
            return;
        }

        var parts = new List<string>();
        double watts = 0;
        if (_monitor.CpuPower is float cpu && cpu > 0)
        {
            watts += cpu;
            parts.Add($"CPU {cpu:0.#} W");
        }
        if (MainGpuPower() is float gpu && gpu > 0)
        {
            watts += gpu;
            parts.Add($"GPU {gpu:0.#} W");
        }

        if (parts.Count == 0)
        {
            EnergyCostPower.Text = "Aguardando sensores de consumo da CPU ou GPU. Instale o PawnIO e abra o app como administrador para melhorar a leitura da CPU.";
            EnergyCostHour.Text = EnergyCostDay.Text = EnergyCostMonth.Text = "—";
            return;
        }

        var culture = CultureInfo.GetCultureInfo("pt-BR");
        double hourly = watts / 1000d * tariff;
        EnergyCostPower.Text = $"Consumo monitorado: {watts:0.#} W ({string.Join(" + ", parts)}) · tarifa de {tariff.ToString("C2", culture)}/kWh";
        EnergyCostHour.Text = hourly.ToString("C2", culture);
        EnergyCostDay.Text = (hourly * 24).ToString("C2", culture);
        EnergyCostMonth.Text = (hourly * 24 * 30).ToString("C2", culture);
    }

    // ================= Rede =================

    private CancellationTokenSource? _netCts;
    private bool _netRunning;
    private List<DiskHealthCard>? _lastNet;

    private async void BtnNetTest_Click(object sender, RoutedEventArgs e)
    {
        if (_netRunning) { _netCts?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { NetStatus.Text = "Termine o outro teste antes."; return; }

        _netRunning = true;
        _netCts = new CancellationTokenSource();
        var ct = _netCts.Token;
        BtnNetTest.Content = "CANCELAR";
        NetCards.ItemsSource = null;
        NetProgress.ScaleX = 0;
        try
        {
            var cards = await NetworkTest.Run((text, frac) => Dispatcher.BeginInvoke(new Action(() =>
            {
                NetStatus.Text = text;
                NetProgress.ScaleX = Math.Clamp(frac, 0, 1);
            })), ct);
            _lastNet = cards;
            NetCards.ItemsSource = cards;
            NetStatus.Text = $"Concluído às {DateTime.Now:HH:mm:ss}. O resultado entra no relatório exportado.";
            NetProgress.ScaleX = 1;
        }
        catch (OperationCanceledException) { NetStatus.Text = "Teste cancelado."; NetProgress.ScaleX = 0; }
        catch (Exception ex) { NetStatus.Text = "Falhou: " + ex.Message; NetProgress.ScaleX = 0; }
        finally
        {
            _netRunning = false;
            _netCts?.Dispose();
            _netCts = null;
            BtnNetTest.Content = "TESTAR";
        }
    }

    // ================= Tema (escuro / claro) =================

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(!ThemeManager.IsLight);
        _settings.LightTheme = ThemeManager.IsLight;
        try { _settings.Save(); } catch { }
        UpdateThemeButton();
    }

    /// <summary>Mostra o ícone do modo para onde o clique leva (sol = ir para o claro, lua = ir para o escuro) e troca o logo.</summary>
    private void UpdateThemeButton()
    {
        bool light = ThemeManager.IsLight;
        BtnTheme.Content = light ? "\uE708" : "\uE706";
        BtnTheme.ToolTip = light ? "Modo escuro" : "Modo claro";
        TitleLogo.Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri(light ? "pack://application:,,,/Assets/logo-dark.png" : "pack://application:,,,/Assets/logo.png"));
    }
}
