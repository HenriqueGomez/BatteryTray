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
