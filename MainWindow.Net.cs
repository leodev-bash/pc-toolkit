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

    private DateTime _energyLastSampleUtc;
    private DateTime _energyLastSaveUtc;

    private void EnergyTariff_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateEnergyCost();
    private void EnergyModel_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateEnergyCost();

    private void EnergyModel_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!TryGetEnergyModel(out var tariff, out var otherWatts, out var efficiency)) return;
        _settings.EnergyTariffPerKwh = tariff;
        _settings.EnergyOtherPowerWatts = otherWatts;
        _settings.EnergyPsuEfficiency = efficiency;
        try { _settings.Save(); } catch { }

        var ptBr = CultureInfo.GetCultureInfo("pt-BR");
        EnergyTariff.Text = tariff.ToString("0.00", ptBr);
        EnergyOtherPower.Text = otherWatts.ToString("0.#", ptBr);
        EnergyPsuEfficiency.Text = (efficiency * 100).ToString("0.#", ptBr);
    }

    private static bool TryParseEnergyNumber(string input, out double value)
    {
        var text = input.Trim();
        var ptBr = CultureInfo.GetCultureInfo("pt-BR");
        // Em pt-BR, "1.25" seria interpretado como 125: trate ponto isolado como decimal.
        bool dotDecimal = text.Contains('.') && !text.Contains(',');
        return dotDecimal
            ? double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
            : double.TryParse(text, NumberStyles.Number, ptBr, out value)
              || double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private bool TryGetEnergyModel(out double tariff, out double otherWatts, out double efficiency)
    {
        bool tariffOk = TryParseEnergyNumber(EnergyTariff.Text, out tariff) && double.IsFinite(tariff) && tariff > 0 && tariff <= 100;
        bool otherOk = TryParseEnergyNumber(EnergyOtherPower.Text, out otherWatts) && double.IsFinite(otherWatts) && otherWatts >= 0 && otherWatts <= 1000;
        bool efficiencyOk = TryParseEnergyNumber(EnergyPsuEfficiency.Text, out var percent) && double.IsFinite(percent) && percent >= 50 && percent <= 100;
        efficiency = percent / 100d;
        return tariffOk && otherOk && efficiencyOk;
    }

    /// <summary>Estima a potência na tomada: CPU/GPU monitoradas + componentes sem telemetria, corrigidos pela eficiência da fonte.</summary>
    private bool TryGetEstimatedWallPower(out double wallWatts, out string detail)
    {
        wallWatts = 0;
        detail = "";
        if (!TryGetEnergyModel(out _, out var otherWatts, out var efficiency)) return false;

        var parts = new List<string>();
        double internalWatts = otherWatts;
        if (_monitor.CpuPower is float cpu && cpu > 0)
        {
            internalWatts += cpu;
            parts.Add($"CPU {cpu:0.#} W");
        }
        if (MainGpuPower() is float gpu && gpu > 0)
        {
            internalWatts += gpu;
            parts.Add($"GPU {gpu:0.#} W");
        }
        if (parts.Count == 0) return false;

        parts.Add($"outros {otherWatts:0.#} W");
        wallWatts = internalWatts / efficiency;
        detail = string.Join(" + ", parts);
        return true;
    }

    /// <summary>Acumula apenas o tempo realmente observado pelo aplicativo; pausas longas não são preenchidas por suposição.</summary>
    private void TrackEnergyUsage()
    {
        var nowUtc = DateTime.UtcNow;
        if (_energyLastSampleUtc == default)
        {
            _energyLastSampleUtc = nowUtc;
            return;
        }

        double seconds = (nowUtc - _energyLastSampleUtc).TotalSeconds;
        _energyLastSampleUtc = nowUtc;
        if (seconds <= 0 || seconds > 10 || !TryGetEstimatedWallPower(out var watts, out _)) return;

        var now = DateTime.Now;
        var day = _settings.EnergyUsageHistory.FirstOrDefault(x => x.Date.Date == now.Date);
        if (day == null)
        {
            day = new EnergyUsageDay { Date = now.Date };
            _settings.EnergyUsageHistory.Add(day);
        }
        day.Kwh += watts / 1000d * seconds / 3600d;
        day.SampledHours += seconds / 3600d;
        _settings.EnergyUsageHistory.RemoveAll(x => x.Date.Date < now.Date.AddDays(-370));

        if (_energyLastSaveUtc == default || (nowUtc - _energyLastSaveUtc).TotalMinutes >= 5)
        {
            SaveEnergyUsage();
            _energyLastSaveUtc = nowUtc;
        }
    }

    private void SaveEnergyUsage()
    {
        try { _settings.Save(); } catch { }
    }

    private void UpdateEnergyCost()
    {
        if (!TryGetEnergyModel(out var tariff, out _, out _))
        {
            EnergyCostPower.Text = "Informe uma tarifa, carga adicional e eficiência válidas.";
            EnergyCostHour.Text = EnergyCostDay.Text = EnergyCostMonth.Text = EnergyCostProjection.Text = "—";
            return;
        }

        var culture = CultureInfo.GetCultureInfo("pt-BR");
        if (TryGetEstimatedWallPower(out var watts, out var detail))
        {
            EnergyCostPower.Text = $"Estimativa na tomada: {watts:0.#} W ({detail}) · tarifa de {tariff.ToString("C2", culture)}/kWh";
            EnergyCostHour.Text = (watts / 1000d * tariff).ToString("C2", culture);
        }
        else
        {
            EnergyCostPower.Text = "Aguardando sensores de consumo da CPU ou GPU. Instale o PawnIO e abra o app como administrador para melhorar a leitura da CPU.";
            EnergyCostHour.Text = "—";
        }

        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var todayUsage = _settings.EnergyUsageHistory.FirstOrDefault(x => x.Date.Date == today);
        var monthUsage = _settings.EnergyUsageHistory.Where(x => x.Date.Date >= monthStart && x.Date.Date <= today).ToList();
        double todayKwh = todayUsage?.Kwh ?? 0;
        double monthKwh = monthUsage.Sum(x => x.Kwh);
        double sampledHours = monthUsage.Sum(x => x.SampledHours);
        int daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        // Média dos dias de calendário já passados: nenhum trecho de 24/7 é inventado.
        double projectionKwh = today.Day > 0 ? monthKwh / today.Day * daysInMonth : 0;

        EnergyCostDay.Text = (todayKwh * tariff).ToString("C2", culture);
        EnergyCostMonth.Text = (monthKwh * tariff).ToString("C2", culture);
        EnergyCostProjection.Text = (projectionKwh * tariff).ToString("C2", culture);
        EnergyTrackingInfo.Text = $"Registrado neste mês: {monthKwh:0.###} kWh em {sampledHours:0.#} h acompanhadas. A projeção usa a média dos {today.Day} dias já passados, sem supor uso 24 h/dia. Para máxima precisão, compare com um medidor de tomada.";
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
