# Publicação

Commit, push, tag e release só quando o usuário pedir, e só o que ele pediu ("só commitar" = commit local, sem push).

## Versão

`<Version>` em `Directory.Build.props`. Cada publicação sobe o terceiro número (0.1.3 → 0.1.4); o do meio (0.2.0) só sobe quando o usuário decidir. A tag é `vX.Y.Z`, só com números: o `Updater` compara por `Version.TryParse`. Uma instalação com versão maior que a nova não se atualiza sozinha (aconteceu na volta de 0.9.0 para 0.1.0).

## Commit

1. Mensagem num arquivo do rascunho, gravada em UTF-8 sem BOM. Título `vX.Y.Z: resumo` em release; corpo com o que muda para quem usa e o número de testes.
2. Termina com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
3. `git commit -F <arquivo>`.

## Release

1. Subir a versão, build e testes verdes, commit.
2. `git push origin main`, `git tag -a vX.Y.Z -m "..."`, `git push origin vX.Y.Z`.
3. O workflow `.github/workflows/build.yml` testa, publica o exe único, gera `Folga.exe.sha256` e cria a release. Acompanhe com `gh run watch <id> -R Gugafss21/folga --exit-status`.
4. Confira: a release é a Latest e o digest do `Folga.exe` bate com o `.sha256`.

## Assinatura (SignPath Foundation)

O workflow assina sozinho em tag quando existirem as variáveis `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG` e o segredo `SIGNPATH_API_TOKEN` no repositório; sem elas, a release sai sem assinatura. O pedido em signpath.org, o 2FA e o token são do usuário. Requisitos que o repositório mantém: licença MIT, `CODE_SIGNING.md` com papéis, privacidade e o que o Folga muda, e o link da política no README e no texto da release.
