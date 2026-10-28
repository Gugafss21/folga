# Folga

App de desempenho para jogos no Windows (C# .NET 10, WPF). Quando um jogo abre, dá prioridade a ele e segura o resto; quando fecha, desfaz tudo. Mede FPS, travadas e ping por fora do jogo. Usuário principal: PC com Ryzen 5 5500X3D, GTX 1060 6 GB, 16 GB, Arc Raiders (Easy Anti-Cheat).

Toda conversa, comentário, texto de tela e mensagem de commit em português do Brasil, com acentos.

## Comandos

O `dotnet` do PATH é x86 sem SDK; use o caminho completo:

```
& "C:\Program Files\dotnet\dotnet.exe" build Folga.sln -c Release
& "C:\Program Files\dotnet\dotnet.exe" test --project tests/Folga.Core.Tests -c Release
```

Pronto = build com 0 avisos e todos os testes verdes.

## Regras

**Segurança do PC e do jogo** (porquês em [docs/seguranca.md](docs/seguranca.md)):
- Tudo por APIs do Windows, por fora do processo do jogo. Leitura de hardware só com o que o Windows já expõe para programas comuns.
- Toda mudança no sistema passa pelo diário (`ChangeJournal`) e volta sozinha quando o jogo fecha, quando o Folga cai e em "Restaurar tudo".
- Limpeza de disco só em pastas fixas de cache e temporários; arquivos pessoais ficam intocados.
- Atualização só com SHA-256 conferido.
- Guardrails: nunca driver de kernel, nunca injetar nada no jogo, nunca prioridade Tempo real.

**Leveza:** o Folga roda junto com o jogo, então cada recurso novo prova que é barato.
- Monitores e amostragens ligam só com a tela à vista ou com um jogo aberto.
- Contadores pelo `PdhCounterArray`. O `PerformanceCounter` gerenciado, o WMI e a compressão do single-file custam de 14 a 90 MB e ficam de fora.
- Fechar a janela libera as telas da memória; o Folga fica só na bandeja.
- Animações dentro do orçamento de [docs/interface.md](docs/interface.md).

**Interface:** todo controle novo leva uma dica completa: o que faz, quando ajuda, o risco e se precisa reiniciar. O estilo é um só, descrito em [docs/interface.md](docs/interface.md).

**Código:** siga o idioma do arquivo vizinho. Comentários explicam o porquê. P/Invoke com `LibraryImport`. Arquivos em UTF-8 sem BOM.

**Git e versões:** commit, push e release só quando o usuário pedir. Cada publicação sobe só o terceiro número (0.1.3 → 0.1.4); mudar o do meio é decisão do usuário. Passo a passo em [docs/publicacao.md](docs/publicacao.md).

## Índice

- [docs/arquitetura.md](docs/arquitetura.md): onde fica cada parte (motor, telemetria, diário, telas). Leia antes de criar arquivo, mover código ou ligar uma tela nova ao motor.
- [docs/interface.md](docs/interface.md): cores, controles, animações, textos e ícones. Leia antes de mexer em qualquer tela, texto de tela, dica ou animação.
- [docs/seguranca.md](docs/seguranca.md): por que cada regra de segurança existe e como o código a cumpre. Leia antes de mexer em processos, registro, serviços, rede, limpeza de disco ou leitura de hardware.
- [docs/testes.md](docs/testes.md): como testar e ver as telas neste ambiente (sem admin, com o Folga do usuário aberto, AppData virtualizado). Leia antes de testar no app real ou concluir algo sobre o PC do usuário.
- [docs/publicacao.md](docs/publicacao.md): versão, commit, tag, release e assinatura. Leia antes de commitar ou publicar.
