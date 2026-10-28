# Segurança

O Folga roda como administrador ao lado de jogos com anti-cheat e mexe em processos, registro, serviços e disco. As regras do `CLAUDE.md` existem por estes motivos.

## Por fora do jogo

- Prioridade, CPU Sets, EcoQoS e suspensão por `OpenProcess` com o menor acesso que serve (`PROCESS_QUERY_LIMITED_INFORMATION`, `PROCESS_SET_LIMITED_INFORMATION`). FPS e uso de núcleos por ETW, sem tocar no processo.
- O Easy Anti-Cheat pode negar acesso. Para a prioridade há o caminho do IFEO (`PerfOptions\CpuPriorityClass` no registro), que o próprio Windows aplica na próxima abertura. Para CPU Sets não há: a nota do jogo na tela Agora diz que foi negado.
- O máximo é a prioridade Alta. Tempo real põe o jogo na frente do mouse, do áudio e do disco do Windows e trava o PC.

## Driver de kernel

O CL em uso da memória e outros registradores só saem com driver. Os drivers prontos (WinRing0 e parecidos) deixam qualquer programa ler e escrever a memória física: é a porta usada em ataques "BYOVD", estão na lista de bloqueio da Microsoft e podem barrar o jogo no anti-cheat. Driver próprio exige certificado EV e assinatura da Microsoft. Decisão do usuário (2026-10-28): sem driver; a tela Hardware mostra o que o Windows expõe e aponta o ZenTimings para o resto.

## Desfazer sempre

- `BoostEngine` grava cada alteração no `ChangeJournal` antes de aplicar, com o valor original. Fechar o jogo, desligar o boost, sair, desligar o PC e "Restaurar tudo" desfazem pelo diário.
- Se o Folga cair, a próxima abertura (`RecoverFromCrash`) desfaz o que sobrou. O IFEO fica de propósito (vale nas próximas aberturas) e sai em "Restaurar tudo" ou na desinstalação.
- Ajuste novo que muda o sistema entra no diário com um `ChangeKind` próprio e um teste de ida e volta.

## Limpeza de disco

`DiskCleaner` só apaga dentro de uma lista fixa de pastas de cache e temporários. Travas:
- `IsSafeRoot` recusa a raiz do disco, a pasta do usuário, as do Windows e as pessoais (Documentos, Downloads, Área de Trabalho…), mesmo que alguém as ponha na lista.
- Junções e links não são seguidos; arquivo em uso é pulado; temporários e downloads do Windows Update só com mais de 1 dia.
- O bloqueio (admin, atualização esperando reiniciar) é conferido de novo na hora de apagar.
- Nada com jogo aberto. Caches de shaders vêm desmarcados (apagar traz travadas).
- A Lixeira fica de fora: esvaziá-la apaga arquivos pessoais.

## Atualização e assinatura

O `Updater` baixa do GitHub Releases público, confere o SHA-256 (digest da release ou `Folga.exe.sha256`) e troca o exe com volta automática se falhar. A assinatura pela SignPath Foundation está preparada no workflow e espera a aprovação do projeto ([publicacao.md](publicacao.md)); quando a primeira versão assinada sair, o `Updater` passa a exigir o mesmo assinante no exe novo.
