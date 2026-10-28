# Interface

O Folga é escuro, calmo e direto: a cor aparece só onde importa. Tema próprio em `src/Folga.App/Theme/` (sem o tema Fluent do WPF, que é pesado). `Tokens.xaml` é a fonte única de cores, fontes, tamanhos e durações; `Controls.xaml` tem o estilo de cada controle. Use os recursos pelo nome, sem cor ou tamanho soltos na tela.

## Cores

| Recurso | Uso |
| --- | --- |
| `BackgroundBrush` / `SidebarBrush` | Fundo da tela / do menu lateral. |
| `PanelBrush` + `LineBrush` | Cartões (estilo `Panel`: canto 10, linha de 1 px). |
| `RaisedBrush` / `LineStrongBrush` | Campos, listas suspensas, botões comuns, dicas e menus. |
| `TextPrimaryBrush` / `TextSecondaryBrush` / `TextTertiaryBrush` | Valor e título / texto corrido / rótulo e legenda. |
| `AccentBrush` (âmbar `#F2B13D`) | A marca: ação principal, item selecionado, valor fora do padrão, aviso. Uma ação âmbar por tela. |
| `OkBrush` | Só "ligado/ativo" (boost, sessão). |
| `DangerBrush` | Erro de verdade. |

## Texto

- Estilos: `Text.Headline` (título da tela), `Text.Section` (título de bloco), `Text.Body` (texto corrido), `Text.Label` (rótulos e legendas), `Text.Number` (números grandes, tabulares), `Text.Mono`, `Text.Icon`.
- Voz: segunda pessoa ("seu PC", "você"), frases curtas, o resultado antes do mecanismo ("O ping sobe quando…", não "O Folga usa QoS para…"). Termo técnico só com a explicação junto.
- Cabeçalho de toda tela: `Text.Headline` com o nome do menu e uma ou duas linhas de `Text.Body` dizendo para que a tela serve.
- Dica (`ToolTip`) em todo controle e em toda coluna (`v:Hint.Text`): o que faz, quando ajuda, o risco e se precisa reiniciar. Opções de lista suspensa (`Choice`) explicam a si mesmas pelo `Hint`.

## Controles

- Botões: `Button.Primary` para a ação principal da tela (Salvar, Aplicar, Limpar…), `Button.Plain` (o padrão) para as outras, `Button.Link` para ações discretas no meio do texto.
- `BoostToggle`: o botão grande da tela Agora.
- Tabelas: `Style="{StaticResource Table}"`. Colunas de caixa de seleção com `ElementStyle="{StaticResource GridCheck}"` e `EditingElementStyle="{StaticResource GridCheckEdit}"`; colunas de texto editáveis com `EditingElementStyle="{StaticResource GridText}"`. A tabela troca o estilo das células, e sem isso elas aparecem brancas.
- Listas suspensas com explicação por opção: `ChoiceCombo`.
- Ícones: fonte `IconFont` (Segoe Fluent Icons, com Segoe MDL2 de reserva), pelo código do caractere (`` no C#, `&#xE7FC;` no XAML). Confira o desenho num harness ([testes.md](testes.md)): código errado vira quadrado.
- Marca: bola preta com o F amarelo (`Assets/Folga.ico`, 16 a 256 px). No menu, a mesma bola desenhada em vetor.

## Animações

Bonitas e baratas: dão retorno a um gesto e terminam.
- Duram 0,12 s (`FastDuration`) ou 0,18 s (`MediumDuration`), com `EaseOut`, animando só cor, opacidade e transformação (escala, rotação, deslocamento). Isso roda na thread de renderização, sem refazer o layout.
- Disparam por evento: passar o mouse, clicar, selecionar, abrir a janela, trocar de tela, ligar o boost. Ao terminar, o custo volta a zero.
- Efeitos maiores (giro do logo ao abrir, entrada com rebote, falso 3D por escala e rotação) cabem quando tocam uma vez por evento.
- Animação contínua só com a janela à vista e o motivo na tela (ex.: algo carregando), com `Timeline.DesiredFrameRate` de no máximo 30.
- Para animar uma cor no template, dê nome ao `SolidColorBrush` local e anime `Color`. Estado de clique por `Setter` de `RenderTransform` no elemento raiz (um `Setter` não alcança uma transformação nomeada).
- Fora do orçamento: `DropShadowEffect`, `BlurEffect` e `Viewport3D`, que caem para renderização por software ou ocupam a GPU o tempo todo.
