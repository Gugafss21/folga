# Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

O `Folga.exe` publicado nas [releases](https://github.com/Gugafss21/folga/releases) é compilado pelo
[workflow do GitHub Actions](.github/workflows/build.yml) a partir deste repositório e só é assinado depois
que um aprovador confere o pedido de assinatura na SignPath. Nada compilado fora do workflow é assinado.
Até o projeto ser aceito pela SignPath Foundation, as releases saem sem assinatura (só com o SHA-256).

## Papéis

| Papel | Quem |
| --- | --- |
| Autores (mudam o código sem revisão extra) | [Gugafss21](https://github.com/Gugafss21) |
| Revisores (revisam toda mudança de quem não é autor, por pull request) | [Gugafss21](https://github.com/Gugafss21) |
| Aprovadores (aprovam cada pedido de assinatura) | [Gugafss21](https://github.com/Gugafss21) |

Quem tem acesso ao repositório: [colaboradores](https://github.com/Gugafss21/folga/graphs/contributors).
Todos usam verificação em duas etapas no GitHub e na SignPath.

## Privacidade

O Folga não coleta nem envia dados pessoais. Ele só fala com a rede em dois casos:

- **Procurar atualização:** 1 minuto depois de abrir e a cada 12 horas, consulta a
  [API de releases do GitHub](https://docs.github.com/rest/releases/releases) deste repositório. A consulta
  leva só a versão do Folga (no User-Agent). Dá para desligar em **Sistema > Instalação e atualizações**;
  baixar e instalar uma versão nova é sempre com um clique da pessoa.
- **Medir o ping:** com um jogo aberto, manda ping (ICMP) para o servidor com que o próprio jogo está
  conectado, para mostrar a latência. Nenhum dado vai junto.

Configuração, log e histórico de partidas ficam só no PC (`%AppData%\Folga`, `%LocalAppData%\Folga` e
`%ProgramData%\Folga`).

## O que o Folga muda no Windows

Todas as mudanças são feitas por APIs do Windows, por fora dos processos, sem injetar nada nos jogos. Cada
opção explica na tela o que faz e quando precisa reiniciar. As mudanças da sessão de jogo (prioridade,
núcleos, limites de upload, pausa do Windows Update, plano de energia) são desfeitas quando o jogo fecha e
anotadas num diário para serem desfeitas mesmo depois de uma queda. A desinstalação (em **Sistema** ou em
**Configurações > Aplicativos instalados**) desfaz tudo antes de remover o programa.
