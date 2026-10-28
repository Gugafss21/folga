# Arquitetura

Três projetos: `src/Folga.Core` (motor, sem WPF), `src/Folga.App` (WPF, assembly `Folga`) e `tests/Folga.Core.Tests` (xUnit v3). O Core não conhece a interface; o App monta tudo em `AppServices.Create`.

## Core

| Pasta | O que tem |
| --- | --- |
| `Engine/BoostEngine.cs` | O coração: sessão de jogo, aplicar e desfazer ajustes por processo, ordem dos jogos (`_gameOrder`; o primeiro jogo manda nos ajustes por jogo). |
| `Journal/ChangeJournal.cs` | Diário em `%ProgramData%\Folga\journal.json`. Toda alteração entra antes de ser aplicada e sai ao desfazer. |
| `Processes/` | `IProcessControl` (prioridade, CPU Sets, EcoQoS, I/O, memória, suspender) e o IFEO do registro. |
| `Detection/` | Processos abrindo e fechando (ETW com admin) e a detecção automática de jogo (tela cheia + DirectX). |
| `Telemetry/` | FPS por ETW do DXGI (`EtwPresentMonitor`), GPU (NVML ou PDH), ping, histórico de partidas (`SessionLog`), diagnóstico das travadas (`HitchDiagnosis`, `SystemProbe`) e o uso dos núcleos pelo jogo (`CoreUsageMonitor`). |
| `Network/` | QoS de upload, pausa do Windows Update, ETW de rede. |
| `Memory/` | RAM, standby list, orçamento de RAM dos apps de fundo. |
| `Cpu/CpuTopology.cs` | Núcleos a partir dos CPU Sets; quais vão para o fundo e para o jogo. |
| `Diagnostics/` | Ficha do hardware (`HardwareInfo`: SMBIOS, registro, consultas de disco) e o resumo da RAM. |
| `Cleanup/DiskCleaner.cs` | Limpeza de disco por lista fixa de pastas. |
| `SystemSettings/` | Ajustes do Windows (paginação, Modo de Jogo, energia…), instalação, inicialização, atualização pelo GitHub. |
| `Profiles/` | `FolgaConfig` (o que vai para `%AppData%\Folga\config.json`) e o `ConfigStore`. |
| `Native/` | P/Invoke escrito à mão e o `PdhCounterArray`. |

## App

- `App.xaml.cs`: instância única (mutex `Global\Folga.SingleInstance`; segunda abertura traz a janela pelo evento `Local\Folga.Show`), bandeja, criação da janela sob demanda e liberação dela ao fechar.
- `MainWindow`: menu lateral agrupado (Jogo, Ajustes, Seu PC) e a tela atual. As telas vêm de `MainViewModel.Pages` (`NavPage`: grupo, ícone, título, view model, dica); cada view model vira view por `DataTemplate` em `App.xaml`.
- `ViewModels/`: um por tela, com CommunityToolkit.Mvvm. Telas com amostragem têm `IsVisible`, ligado pelo `MainViewModel.UpdateVisibility`, e só trabalham com ele verdadeiro.
- `Views/`: uma `UserControl` por tela. `Theme/Tokens.xaml` e `Theme/Controls.xaml` são o tema inteiro (ver [interface.md](interface.md)).
- `Assets/Folga.ico`: ícone do exe, da janela e da bandeja.

## Tela nova

1. View model em `ViewModels/`, recebendo só o que usa: um `Func<...>` em vez de `AppServices` quando der, o que também deixa a tela desenhável no harness de [testes.md](testes.md).
2. View em `Views/` com o cabeçalho padrão de [interface.md](interface.md).
3. `DataTemplate` em `App.xaml` e uma `NavPage` em `MainViewModel.Pages`, no grupo certo.
4. Com amostragem: `IsVisible` em `UpdateVisibility`.
