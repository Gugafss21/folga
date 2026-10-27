# Folga

Gerenciador de desempenho para jogos no Windows. Quando um jogo do seu perfil abre (ou qualquer app em primeiro plano que fique em tela cheia desenhando com DirectX, que a detecção automática acrescenta à lista sozinha), o Folga dá prioridade a ele e segura o resto (navegador, Discord, launchers): prioridade, núcleos (CPU Sets), EcoQoS, prioridade de I/O e de memória, orçamento de RAM e limpeza da standby list. Na rede, limita o upload dos apps de fundo (QoS do Windows), pausa o Windows Update e pode marcar o tráfego do jogo com DSCP 46. Mostra FPS, 1% low e frametime (eventos de Present do DirectX via ETW, sem injeção), uso, VRAM e temperatura da GPU (NVML na NVIDIA) e o ping até o servidor da partida, também dentro do jogo pelo overlay do RTSS (o Folga escreve o texto na memória compartilhada do RTSS; itens, rótulos, cores, tamanho e canto configuráveis na tela Overlay). Quando o jogo fecha, tudo é desfeito.

**Nada é injetado no jogo.** Tudo passa por APIs do Windows, por fora do processo. Se o anti-cheat negar acesso, a prioridade vai para o IFEO do registro e vale a partir da próxima abertura.

## Rodar

```
dotnet run --project src/Folga.App
```

O app pede administrador (UAC), fica na bandeja e grava:

| O quê | Onde |
| --- | --- |
| Perfis e regras | `%AppData%\Folga\config.json` |
| Diário de alterações (para desfazer após crash) | `%ProgramData%\Folga\journal.json` |
| Log | `%LocalAppData%\Folga\logs` |

Argumentos: `--minimized` abre direto na bandeja; `--config <arquivo>` usa outro config (testes).

## Testes e build

```
dotnet test --project tests/Folga.Core.Tests
powershell -ExecutionPolicy Bypass -File tools\teste-rede.ps1   # como administrador: QoS, Windows Update e diário no PC real
dotnet publish src/Folga.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none -o publish
```

## Estrutura

- `src/Folga.Core`: motor sem interface (detecção por ETW, perfis, aplicadores, diário, memória, rede, telemetria).
- `src/Folga.App`: WPF (telas Agora, Jogos, Segundo plano, Rede; ícone na bandeja).
- `tests/Folga.Core.Tests`: testes do motor com processos falsos e contra um processo filho real.
