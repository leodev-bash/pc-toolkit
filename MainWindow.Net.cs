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
    private DateTime _energyLastProcessSampleUtc;
    private bool _updatingEnergyStartup;
    private readonly Dictionary<int, (string Name, TimeSpan Cpu)> _energyProcessSnapshot = new();
    private sealed record EnergyHistoryRow(string Date, string Hours, string Kwh, string Cost, string Processes);

    private void EnergyTariff_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateEnergyCost();
    private void EnergyModel_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateEnergyCost();

    private void EnergyStartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingEnergyStartup) return;
        try
        {
            StartupManager.SetEnergyMonitorEnabled(EnergyStartWithWindows.IsChecked == true);
            EnergyStatus.Text = EnergyStartWithWindows.IsChecked == true
                ? "Monitoramento de energia configurado para iniciar com o Windows (minimizado)."
                : "Monitoramento automático ao iniciar o Windows foi desativado.";
        }
        catch (Exception ex)
        {
            _updatingEnergyStartup = true;
            EnergyStartWithWindows.IsChecked = StartupManager.IsEnergyMonitorEnabled();
            _updatingEnergyStartup = false;
            EnergyStatus.Text = "Não foi possível alterar a inicialização: " + ex.Message;
        }
    }

    private void EnergyModel_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!TryGetEnergyModel(out var tariff, out var otherWatts, out var efficiency, out var activeHours)) return;
        _settings.EnergyTariffPerKwh = tariff;
        _settings.EnergyOtherPowerWatts = otherWatts;
        _settings.EnergyPsuEfficiency = efficiency;
        _settings.EnergyActiveHoursPerDay = activeHours;
        try { _settings.Save(); } catch { }

        var ptBr = CultureInfo.GetCultureInfo("pt-BR");
        EnergyTariff.Text = tariff.ToString("0.00", ptBr);
        EnergyOtherPower.Text = otherWatts.ToString("0.#", ptBr);
        EnergyPsuEfficiency.Text = (efficiency * 100).ToString("0.#", ptBr);
        EnergyActiveHours.Text = activeHours.ToString("0.#", ptBr);
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

    private bool TryGetEnergyModel(out double tariff, out double otherWatts, out double efficiency, out double activeHours)
    {
        bool tariffOk = TryParseEnergyNumber(EnergyTariff.Text, out tariff) && double.IsFinite(tariff) && tariff > 0 && tariff <= 100;
        bool otherOk = TryParseEnergyNumber(EnergyOtherPower.Text, out otherWatts) && double.IsFinite(otherWatts) && otherWatts >= 0 && otherWatts <= 1000;
        bool efficiencyOk = TryParseEnergyNumber(EnergyPsuEfficiency.Text, out var percent) && double.IsFinite(percent) && percent >= 50 && percent <= 100;
        bool hoursOk = TryParseEnergyNumber(EnergyActiveHours.Text, out activeHours) && double.IsFinite(activeHours) && activeHours > 0 && activeHours <= 24;
        efficiency = percent / 100d;
        return tariffOk && otherOk && efficiencyOk && hoursOk;
    }

    /// <summary>Estima a potência na tomada: CPU/GPU monitoradas + componentes sem telemetria, corrigidos pela eficiência da fonte.</summary>
    private bool TryGetEstimatedWallPower(out double wallWatts, out string detail)
    {
        wallWatts = 0;
        detail = "";
        if (!TryGetEnergyModel(out _, out var otherWatts, out var efficiency, out _)) return false;

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
        TrackEnergyProcesses(day, now, nowUtc);
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

    /// <summary>Guarda os processos que realmente gastaram CPU, por diferença de tempo de processador a cada minuto.</summary>
    private void TrackEnergyProcesses(EnergyUsageDay day, DateTime now, DateTime nowUtc)
    {
        if (_energyLastProcessSampleUtc != default && (nowUtc - _energyLastProcessSampleUtc).TotalSeconds < 55) return;
        _energyLastProcessSampleUtc = nowUtc;
        var current = new Dictionary<int, (string Name, TimeSpan Cpu)>();
        var deltas = new List<(string Name, double Seconds)>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var sample = (process.ProcessName, process.TotalProcessorTime);
                current[process.Id] = sample;
                if (_energyProcessSnapshot.TryGetValue(process.Id, out var previous))
                {
                    double seconds = (sample.Item2 - previous.Cpu).TotalSeconds;
                    if (seconds > 0 && seconds < 300) deltas.Add((sample.ProcessName, seconds));
                }
            }
            catch { }
            finally { process.Dispose(); }
        }
        _energyProcessSnapshot.Clear();
        foreach (var pair in current) _energyProcessSnapshot[pair.Key] = pair.Value;
        if (deltas.Count == 0) return;

        day.Processes ??= new List<EnergyProcessUsage>();
        foreach (var group in deltas.GroupBy(x => x.Name).OrderByDescending(x => x.Sum(x => x.Seconds)).Take(6))
        {
            var entry = day.Processes.FirstOrDefault(x => x.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            if (entry == null) { entry = new EnergyProcessUsage { Name = group.Key }; day.Processes.Add(entry); }
            entry.CpuSeconds += group.Sum(x => x.Seconds);
        }
        if (day.Processes.Count > 20)
            day.Processes = day.Processes.OrderByDescending(x => x.CpuSeconds).Take(20).ToList();
    }

    private void UpdateEnergyCost()
    {
        if (!TryGetEnergyModel(out var tariff, out _, out _, out var activeHours))
        {
            EnergyCostPower.Text = "Informe tarifa, carga adicional, eficiência e horas ativas válidas.";
            EnergyCostHour.Text = EnergyCurrentDay.Text = EnergyCurrentMonth.Text = EnergyCostDay.Text = EnergyCostMonth.Text = EnergyCostProjection.Text = "—";
            return;
        }

        var culture = CultureInfo.GetCultureInfo("pt-BR");
        if (TryGetEstimatedWallPower(out var watts, out var detail))
        {
            EnergyCostPower.Text = $"Estimativa na tomada: {watts:0.#} W ({detail}) · tarifa de {tariff.ToString("C2", culture)}/kWh";
            double currentHour = watts / 1000d * tariff;
            EnergyCostHour.Text = currentHour.ToString("C2", culture);
            EnergyCurrentDay.Text = (currentHour * 24).ToString("C2", culture);
            EnergyCurrentMonth.Text = (currentHour * 24 * 30).ToString("C2", culture);
        }
        else
        {
            EnergyCostPower.Text = "Aguardando sensores de consumo da CPU ou GPU. Instale o PawnIO e abra o app como administrador para melhorar a leitura da CPU.";
            EnergyCostHour.Text = EnergyCurrentDay.Text = EnergyCurrentMonth.Text = "—";
        }

        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var todayUsage = _settings.EnergyUsageHistory.FirstOrDefault(x => x.Date.Date == today);
        var monthUsage = _settings.EnergyUsageHistory.Where(x => x.Date.Date >= monthStart && x.Date.Date <= today).ToList();
        double todayKwh = todayUsage?.Kwh ?? 0;
        double monthKwh = monthUsage.Sum(x => x.Kwh);
        double sampledHours = monthUsage.Sum(x => x.SampledHours);
        int daysRemaining = DateTime.DaysInMonth(today.Year, today.Month) - today.Day;
        // Para os dias restantes, usa a potência média de verdade observada e as horas que o usuário informou.
        // Se ainda não houver histórico, usa a leitura atual como ponto de partida.
        double avgWatts = sampledHours > 0 ? monthKwh * 1000d / sampledHours : 0;
        if (avgWatts <= 0 && TryGetEstimatedWallPower(out var currentWatts, out _)) avgWatts = currentWatts;
        double projectionKwh = monthKwh + avgWatts / 1000d * activeHours * daysRemaining;

        EnergyCostDay.Text = (todayKwh * tariff).ToString("C2", culture);
        EnergyCostMonth.Text = (monthKwh * tariff).ToString("C2", culture);
        EnergyCostProjection.Text = (projectionKwh * tariff).ToString("C2", culture);
        EnergyTrackingInfo.Text = $"Registrado neste mês: {monthKwh:0.###} kWh em {sampledHours:0.#} h acompanhadas. A previsão soma o já registrado + {daysRemaining} dias × {activeHours:0.#} h/dia na potência média observada ({avgWatts:0.#} W); não supõe uso 24 h/dia.";
        UpdateEnergyHistory(culture, tariff);
    }

    private void UpdateEnergyHistory(CultureInfo culture, double tariff)
    {
        var rows = _settings.EnergyUsageHistory.OrderByDescending(x => x.Date).Take(30).Select(day =>
        {
            var processes = (day.Processes ?? new List<EnergyProcessUsage>()).OrderByDescending(x => x.CpuSeconds).Take(3).ToList();
            string processText = processes.Count == 0
                ? "Processos serão identificados após o primeiro minuto de acompanhamento."
                : "Mais ativos: " + string.Join(", ", processes.Select(x => $"{x.Name} ({x.CpuSeconds / 60d:0.#} min CPU)"));
            return new EnergyHistoryRow(day.Date.ToString("ddd, dd/MM", culture), $"{day.SampledHours:0.#} h acompanhadas", $"{day.Kwh:0.###} kWh", (day.Kwh * tariff).ToString("C2", culture), processText);
        }).ToList();
        EnergyHistory.ItemsSource = rows;
        EnergyHistoryEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
