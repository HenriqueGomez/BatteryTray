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
