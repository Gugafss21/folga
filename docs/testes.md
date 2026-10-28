# Testes

## Testes automáticos

`tests/Folga.Core.Tests` cobre o Core. Lógica nova fica numa classe pura testável (ex.: `HitchProfiler`, `CoreUsageCounter`, `DiskCleaner` com pastas de teste) e a parte do Windows numa casca fina. Testes que leem o PC real toleram VM sem dados (`Assert.SkipWhen`): o CI do GitHub roda numa VM.

## Este ambiente

- **Sem admin.** O terminal do agente não é elevado: ETW, IFEO, QoS, serviços e o uso de núcleos pelo jogo só rodam no PC do usuário. Diga o que ficou sem teste elevado.
- **O Folga do usuário costuma estar aberto (elevado).** A instância única faz qualquer Folga aberto pelo agente sair na hora; ele também não fecha o do usuário. Para ver o app real, peça ao usuário para sair pela bandeja.
- **AppData virtualizado.** O app do Claude é MSIX: o que o terminal lê em `%LocalAppData%\Folga` e `%AppData%\Folga` pode ser a cópia em `%LocalAppData%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\...`, e não o arquivo do usuário. O log e o config que aparecem ali são só dos Folga abertos de dentro do app do Claude; a chave de Aplicativos instalados no HKCU também não aparece. `sessions.json` e `%ProgramData%` são reais. Confira com o usuário antes de concluir algo pelo log dele.
- **Rodar o app sem admin:** `$env:__COMPAT_LAYER="RunAsInvoker"` e `--config <arquivo>` com um config de teste. Navegue pelas telas com UI Automation (os itens do menu são `ListItem`; o nome traz `Title = <tela>,`).
- **Capturar só a janela do Folga**, com `PrintWindow` e a flag `PW_RENDERFULLCONTENT` (2). Copiar a área da tela pega o que estiver por cima, inclusive janelas pessoais do usuário; se isso acontecer, apague a imagem sem abrir.
- **Medir memória** por `PrivateMemorySize64`, comparando: aberto com `--minimized` (sem janela), com a janela aberta e alguns segundos depois de fechá-la. O RTSS aberto soma uns 50 MB ao Folga.

## Ver as telas sem o app

Um harness num projeto de rascunho, que referencia `src/Folga.App/Folga.App.csproj`, desenha a tela de verdade num PNG:

1. Thread STA, `new Application { ShutdownMode = OnExplicitShutdown }` (não o `App` do Folga, que sobe o motor) e o tema por `pack://application:,,,/Folga;component/Theme/Tokens.xaml` e `.../Controls.xaml`, mais `BoolToVisibility` e os `DataTemplate` das telas usadas.
2. Tudo dentro de `app.Startup` com `app.Run()`, para ter o ciclo de mensagens: fora dele, async e recursos falham.
3. View model real quando ele recebe só `Func`s (Hardware, Limpeza); senão, um falso com as mesmas propriedades (com setter quando a tela grava nelas).
4. Mostre a janela fora da tela, espere os dados e grave com `RenderTargetBitmap`.

Nunca chame no harness algo que apaga ou muda o PC (ex.: `Clean`); `Scan` só lê.
