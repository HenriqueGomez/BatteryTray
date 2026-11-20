# Novidades do BatteryTray

Todas as mudanças de cada versão, da mais nova para a mais antiga.
Para atualizar, use **Verificar atualizações** no menu do app ou baixe a [versão mais recente](https://github.com/HenriqueGomez/BatteryTray/releases/latest).

<!--
Como usar (mantenedor):
- Enquanto trabalha, anote as mudanças na seção [Não publicado], abaixo.
- Ao rodar .\release.ps1 1.2.0, essa seção vira "[1.2.0] - data" e é usada como as notas do Release no GitHub.
- Grupos sugeridos: ### Novidades, ### Melhorias, ### Correções
-->

## [Não publicado]

## [1.8.0] - 2026-11-20

### Novidades
- **Janela Personalizar nova**, no visual das Configurações do Windows 11, com interruptores e botões de escolha.
  - Na prévia, no alto da janela, cada aparelho é um cartão. Para mudar a ordem, arraste um cartão para os lados: ele segue o mouse e os outros abrem espaço.
  - As mudanças só valem quando você clica em **Aplicar**. **Cancelar**, ou fechar a janela, descarta.
- **Ocultar aparelhos desligados**, em **Personalizar...**, vem ligado: um aparelho desligado sai da barra e volta quando liga. Ele continua no menu, com o último valor. Para ele ficar na barra, em cinza, como antes, desligue essa opção.

### Correções
- As preferências de **Personalizar...** são gravadas de um jeito mais seguro. Antes, o app apagava todas e gravava de novo a cada mudança; se ele fosse fechado bem nessa hora, elas se perdiam. Cada preferência também é lida separadamente, então um valor com problema não faz as outras voltarem ao padrão.

## [1.7.1] - 2026-11-19

### Melhorias
- Todos os aparelhos são relidos a cada 10 segundos, e não mais a cada minuto. Ligar um headset ou um mouse cujo receptor fica sempre plugado já aparece em poucos segundos, sem clicar em **Atualizar agora**.
- HyperX Cloud III Wireless, Cloud II Core Wireless, Cloud Alpha Wireless, Cloud Alpha 2 Wireless e Pulsefire Haste 2: o app escuta os avisos que o receptor manda quando o aparelho liga, desliga ou muda a bateria, e relê na hora.
- O diagnóstico lista as funções dos aparelhos Logitech que respondem mas não informam a bateria por nenhum dos caminhos conhecidos, para descobrir como ler esses modelos. O mouse "Wireless Mouse" de alguns kits teclado+mouse, por exemplo, não tem nenhuma função de bateria; o teclado do kit aparece normalmente.

## [1.7.0] - 2026-11-19

### Novidades
- **Muito mais marcas**, com protocolos documentados por projetos abertos (HeadsetControl, rivalcfg, G-Helper, ckb-next, mxw, SDL e outros). Só o HyperX Cloud III Wireless já foi testado com o BatteryTray; se algum outro não aparecer, mande o diagnóstico numa *issue*.
  - **Headsets:** HyperX (Cloud III, Cloud II, Cloud II Core, Cloud Alpha, Cloud Alpha 2, Cloud Flight, Cloud Stinger Core), SteelSeries (Arctis 1, 7, 9, Pro, 7+ e toda a linha Nova), Corsair (Void, HS70, HS80, Virtuoso, Void v2, HS80 MAX), JBL Quantum, Sony INZONE, Audeze Maxwell, ASTRO A50 e Logitech G535.
  - **Mouses:** SteelSeries Aerox, Prime e Rival, ASUS ROG e TUF, Glorious Model O/D, mouses com receptor CompX ou Areson (VXE, ATK, Pulsar, Lamzu, Redragon, Ajazz, Fantech), Corsair Dark Core, Ironclaw, Harpoon e M75, HyperX Pulsefire Dart e Haste 2, Keychron M5 e M6.
  - **Teclados:** ASUS ROG Azoth, Strix Scope II 96 e Falchion; Corsair K57, K63 e K70 Pro Mini Wireless.
  - **Controles:** DualSense, DualSense Edge, DualShock 4, controles do Switch e 8BitDo Ultimate 3. Pelo Bluetooth, os da Sony e da Nintendo só aparecem com o Steam ou um jogo aberto, que liga o modo em que o controle manda a bateria.
- O app só manda pedidos de leitura, e só para os modelos da lista. Comandos que gravam configurações ou atualizam o firmware nunca são enviados.
- O diagnóstico mostra a resposta de cada um desses aparelhos.

## [1.6.1] - 2026-11-19

### Correções
- Alguns aparelhos Logitech, como o teclado de kits teclado+mouse ("Wireless Keyboard"), apareciam com espaços sobrando no fim do nome, no menu e na dica.

### Documentação
- O README lista o headset Baseus AeQur GH02 entre os que não funcionam pelo receptor 2.4 GHz, que não transmite a bateria.

## [1.6.0] - 2026-11-19

### Melhorias
- **Atualização na hora ao ligar ou desligar aparelhos**, sem esperar a leitura de cada minuto:
  - **Razer (headsets, pelo Synapse):** o app confere o log a cada 2 segundos.
  - **Logitech:** o app escuta o receptor, que avisa quando o aparelho liga, desliga ou muda a bateria. Isso inclui começar a carregar num carregador de tomada.
  - **Mouses com receptor "2.4G 8K HS Receiver":** o app escuta o mouse o tempo todo. Ele manda a bateria a cada ~2 s e, quando para de mandar, aparece desligado em poucos segundos.
- Headsets MCHOSE e controles GameSir continuam sendo lidos a cada minuto, porque só respondem quando perguntados.

### Correções
- Headsets Razer às vezes não apareciam ligados, nem clicando em **Atualizar agora**. Enquanto o Synapse mantém o log aberto, o Windows não atualiza o tamanho do arquivo na listagem da pasta, e o app achava que não havia nada novo. Agora o tamanho é lido no próprio arquivo.

## [1.5.2] - 2026-11-19

### Melhorias
- Tamanhos do painel reorganizados: o novo **Compacto** mostra só o ícone e a bateria (`%` ou raio), sem nome curto e sem barra. O **Normal** é o antigo compacto, e o **Grande** é o antigo normal. Quem atualizar continua com o mesmo visual.

### Correções
- Mouses e teclados Logitech ligados no cabo, por exemplo para carregar, apareciam repetidos, 7 vezes. Isso acontecia porque eles respondem em todas as posições que o app pergunta. Agora só receptores são perguntados posição por posição.
- Ao ligar ou desligar um aparelho, por exemplo plugar o cabo para carregar, o painel só mudava na leitura seguinte, que acontece a cada minuto. Agora o app relê na hora (~1,5 s) e de novo alguns segundos depois.
- O mesmo aparelho Logitech pelo receptor e pelo cabo virava dois itens. Agora é um só, com a leitura de quem está ligado (no cabo, com o raio de carregando), mantendo o nome curto, a cor, a ordem e a fixação escolhidos para ele.

## [1.5.1] - 2026-11-19

### Melhorias
- No painel, cada aparelho virou uma grade: ícone e porcentagem em cima, nome curto e barra embaixo. O nome curto não fica mais por cima do ícone. Sem nome, o ícone fica centralizado; sem barra, a porcentagem também. A largura de cada aparelho se ajusta ao nome.

## [1.5.0] - 2026-11-19

### Novidades
- **Painel na barra de tarefas:** os aparelhos agora aparecem lado a lado num painel à esquerda da bandeja, cada um com o ícone colorido, a porcentagem e uma barra de bateria. Passar o mouse destaca o grupo todo e mostra os detalhes do aparelho. Clique para reler as baterias, e clique com o botão direito para abrir o menu.
- **Personalizar...**, no menu: uma janela com prévia ao vivo para escolher a cor, o nome curto (ex.: "600") e a ordem de cada aparelho. Também dá para ligar ou desligar a barra, o `%` e os nomes curtos, e escolher o tamanho (compacto, normal ou grande).
- O visual anterior, um ícone por aparelho dentro da bandeja, continua disponível em **Personalizar...**.
- Quando os apps abertos ocupam o espaço do painel, ele some até ter lugar de novo. O menu e as notificações continuam no ícone de bateria do BatteryTray, dentro da setinha `^`.

## [1.4.0] - 2026-11-17

### Novidades
- **Aviso de versão nova:** o app procura atualização logo ao abrir e mostra uma notificação do Windows quando há uma versão nova. Clicando nela, o menu abre e a atualização começa. O aviso aparece uma vez por versão e se repete a cada 3 dias enquanto você não atualizar. Se a internet ainda não estiver pronta ao ligar o PC, o app tenta de novo depois de 2 minutos.

## [1.3.4] - 2026-11-17

### Correções
- Quando a bandeja se reorganizava (por exemplo, ao tirar um print), o desenho dos aparelhos ficava até 2 segundos fora do lugar, às vezes por cima da setinha `^`. Agora o app confere a posição dos ícones 4 vezes por segundo e acompanha a mudança na hora.
- Quando o Windows não informa a posição de um ícone fixado, o desenho some até a posição voltar, em vez de ficar parado num lugar que pode estar errado.

## [1.3.3] - 2026-11-17

### Novidades
- **Aviso de bateria baixa:** uma notificação do Windows quando um aparelho chega a 15% e outra em 5%. Depois de carregar, volta a avisar. Respeita o "Não incomodar" e pode ser desligado no menu, em **Avisar bateria baixa**.
- Aparelhos desligados mostram quando foram lidos pela última vez, por exemplo "lido ontem às 22:00". Isso aparece na dica do ícone e no menu.

### Melhorias
- A busca por aparelhos Logitech ficou cerca de 8 vezes mais rápida: passou de ~10 s para ~1 s em receptores com um aparelho só. O app agora pula as posições vazias do receptor. Durante essa busca, as outras leituras e o "Atualizar agora" ficavam esperando.
- A lista de aparelhos USB do PC passou a ser montada uma vez por rodada, e não mais uma vez para cada tipo de aparelho.
- **Atualizar agora** não fecha mais o menu: ele mostra "Atualizando..." e as porcentagens mudam ali mesmo.
- Mensagens do menu mais curtas, como "Já está atualizado".
- O diagnóstico mostra, para cada aparelho, se o ícone está fixado e onde o Windows diz que ele está na barra.

### Correções
- Mouses e teclados Logitech viravam um aparelho novo quando o receptor era ligado em outra porta USB. O ícone saía da barra e precisava ser fixado de novo. Agora o aparelho continua o mesmo, e quem atualizar mantém os ícones fixados e os aparelhos ocultos.
- Às vezes os ícones fixados na barra viravam só o número, sem o desenho do aparelho e sem o "%". Isso acontecia quando o Windows deixava de informar a posição deles por um momento, por exemplo com a tela bloqueada, ao voltar da suspensão ou com um jogo em tela cheia.

## [1.3.2] - 2026-11-17

### Correções
- O controle GameSir Cyclone 2 aparecia como "Xbox 360 Controller for Windows" no modo XInput. Ele também virava outro aparelho ao trocar de modo. Agora o nome é sempre "GameSir Cyclone 2", e o ícone que você fixou na barra continua fixado.
- O headset MCHOSE às vezes ignorava o pedido de bateria e aparecia desligado. Agora o app pergunta até 3 vezes.

### Melhorias
- O mouse **Delux M900 Pro** (receptor "2.4G 8K HS Receiver") aparece com o nome dele.

## [1.3.1] - 2026-11-17

### Correções
- O controle GameSir aparecia com **0%** quando o receptor estava ligado mas o controle não estava no modo XInput. Agora ele aparece apagado, com o último valor conhecido.

### Melhorias
- Os mouses com receptor "2.4G 8K HS Receiver" aparecem com o nome do modelo (são mais de 40 modelos conhecidos).
- O diagnóstico mostra o tamanho dos relatórios de saída de cada aparelho. Também diz por que um aparelho não respondeu: o envio falhou, não veio resposta ou chegou outro tipo de resposta.
- A seção "O que o BatteryTray mostra agora" do diagnóstico ficava vazia quando ele era gerado logo depois de abrir o app.

## [1.3.0] - 2026-11-17

### Novidades
- Novos aparelhos suportados:
  - **controles GameSir** pelo receptor 2.4 GHz (ex.: Cyclone 2), no modo XInput;
  - **mouses com o receptor genérico "2.4G 8K HS Receiver"** (Beken/CompX), usado por AULA, Attack Shark, Delux, INPHIC e outras marcas;
  - **headsets MCHOSE** pelo receptor 2.4 GHz (ex.: V9 Turbo+).
- O diagnóstico mostra as respostas desses aparelhos, para conferir a leitura.

### Correções
- O controlador de LED da placa-mãe (ex.: ASUS AURA) aparecia como "controle" no diagnóstico, e receptores de mouse como "teclado".

## [1.2.0] - 2026-11-17

### Novidades
- **Gerar diagnóstico**, no menu: cria um arquivo de texto na pasta do app com tudo o que o BatteryTray encontra no seu PC (aparelhos USB e Bluetooth, controles, programas dos fabricantes e a leitura de cada um). Se um aparelho não aparecer, anexe esse arquivo ao abrir uma *issue*. O arquivo não tem números de série nem endereços Bluetooth.

### Correções
- Teclados com teclas analógicas ou com função de mouse, como o Keychron K4 HE, eram identificados como controle ou mouse.

## [1.1.0] - 2026-11-17

### Correções
- O menu (clique direito) voltou a abrir. Na v1.0.0 ele não aparecia.

## [1.0.0] - 2026-11-17

Primeira versão.

### Novidades
- A bateria de cada aparelho sem fio aparece **na barra de tarefas**, como um ícone fixado: o desenho do aparelho em cima e a porcentagem embaixo.
  - Quando o aparelho está carregando, o `%` vira um raio.
  - Desligado ou fora de alcance, o ícone fica apagado e mostra o último valor.
  - Com 15% ou menos, o texto fica vermelho.
- Passando o mouse, aparece o nome do aparelho e a porcentagem.
- Novo menu (clique direito) no estilo do Windows 11:
  - interruptores para escolher quais aparelhos aparecem na barra;
  - Atualizar agora;
  - Iniciar com o Windows;
  - Verificar atualizações.
- Atualização pelo próprio menu. O app baixa a versão nova, confere a integridade do arquivo (SHA-256) e reabre sozinho.
- Aparelhos suportados:
  - Logitech (G HUB): receptores LIGHTSPEED, Unifying e Bolt, ou aparelho no cabo;
  - headsets Razer, pelo Razer Synapse 4;
  - mouses e teclados Razer sem fio;
  - Bluetooth de qualquer marca;
  - aparelhos USB que informam a bateria no padrão HID;
  - controles XInput que informam a bateria.

### Problemas conhecidos
- Nesta versão o menu (clique direito) não abre. Corrigido na **1.1.0**.
